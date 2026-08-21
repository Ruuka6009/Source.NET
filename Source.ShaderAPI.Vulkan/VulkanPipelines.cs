using Silk.NET.Vulkan;

using Source.Common;
using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;

using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Everything a snapshot needs on this key must be here: two identical keys must be
/// interchangeable pipelines. Manual equality - GraphicsBoardState contains bools/enums only,
/// but struct padding makes memcmp-style comparison unsafe, so fields are compared explicitly.
/// </summary>
public struct VulkanPipelineKey : IEquatable<VulkanPipelineKey>
{
	public GraphicsBoardState State;
	public nint VertexShader;
	public nint PixelShader;
	public VertexFormat Format;
	public MaterialPrimitiveType Topology;
	public VkFormat ColorFormat;
	public VkFormat DepthFormat;

	public readonly bool Equals(VulkanPipelineKey other) {
		ref readonly GraphicsBoardState a = ref State;
		ref readonly GraphicsBoardState b = ref other.State;
		return VertexShader == other.VertexShader
			&& PixelShader == other.PixelShader
			&& Format == other.Format
			&& Topology == other.Topology
			&& ColorFormat == other.ColorFormat
			&& DepthFormat == other.DepthFormat
			&& a.Blending == b.Blending
			&& a.SourceBlend == b.SourceBlend
			&& a.DestinationBlend == b.DestinationBlend
			&& a.BlendOperation == b.BlendOperation
			&& a.AlphaSeparateBlend == b.AlphaSeparateBlend
			&& a.AlphaSourceBlend == b.AlphaSourceBlend
			&& a.AlphaDestinationBlend == b.AlphaDestinationBlend
			&& a.AlphaBlendOperation == b.AlphaBlendOperation
			&& a.DepthTest == b.DepthTest
			&& a.ColorWrite == b.ColorWrite
			&& a.AlphaWrite == b.AlphaWrite
			&& a.DepthWrite == b.DepthWrite
			&& a.CullEnable == b.CullEnable
			&& a.AlphaToCoverage == b.AlphaToCoverage
			&& a.DepthFunc == b.DepthFunc
			&& a.FillMode == b.FillMode
			&& a.ZBias == b.ZBias;
	}

	public override readonly bool Equals(object? obj) => obj is VulkanPipelineKey key && Equals(key);

	public override readonly int GetHashCode() {
		HashCode hash = new();
		hash.Add(VertexShader);
		hash.Add(PixelShader);
		hash.Add((ulong)Format);
		hash.Add(Topology);
		hash.Add(ColorFormat);
		hash.Add(DepthFormat);
		hash.Add(State.Blending);
		hash.Add(State.SourceBlend);
		hash.Add(State.DestinationBlend);
		hash.Add(State.DepthTest);
		hash.Add(State.DepthWrite);
		hash.Add(State.DepthFunc);
		hash.Add(State.CullEnable);
		return hash.ToHashCode();
	}
}

/// <summary>
/// The descriptor layout contract (set 0 = dynamic-offset UBO ring, set 1 = material textures,
/// push constant = int flags - see common_vk13.glsl), the per-frame uniform ring buffers, the
/// placeholder white texture, and the VkPipeline cache keyed on <see cref="VulkanPipelineKey"/>.
/// </summary>
public unsafe class VulkanPipelineSystem : IDisposable
{
	public enum UniformBlock
	{
		Matrices,       // binding 0: view/projection/model
		VertexShared,   // binding 2: numBones
		PixelShared,    // binding 3: alpha test state
		Bones,          // binding 4: mat4[MAXSTUDIOBONES]
		VsConstants,    // binding 5: vec4[256]
		PsConstants,    // binding 6: vec4[256]
		Count
	}

	public static readonly uint[] BlockBindings = [0, 2, 3, 4, 5, 6];
	public static readonly int[] BlockSizes = [3 * 64, 16, 16, Studio.MAXSTUDIOBONES * 64, 256 * 16, 256 * 16];

	const ulong RingSize = 16UL * 1024 * 1024;
	/// <summary>set 1: basetexture, envmap, envmapmask, lightmaptexture, bumpmap, basetexture2.</summary>
	public const int TextureBindingCount = 6;
	const uint SetsPerPool = 512;

	readonly VulkanCore core;
	readonly VulkanMemoryAllocator allocator;
	readonly int framesInFlight;

	DescriptorSetLayout set0Layout;
	DescriptorSetLayout set1Layout;
	public PipelineLayout PipelineLayout { get; private set; }
	DescriptorPool descriptorPool;

	VulkanBufferResource[] ringBuffers = [];
	DescriptorSet[] set0Sets = [];
	public DescriptorSet Set1WhiteSet { get; private set; }

	VulkanBufferResource? zeroVertexBuffer;
	public VkBuffer ZeroVertexBuffer => zeroVertexBuffer!.Handle;

	Image whiteImage;
	ImageView whiteImageView;
	VkSampler whiteSampler;
	VulkanMemoryAllocator.Allocation whiteImageAlloc;

	/// <summary>1x1 opaque white - the fallback for any set-1 slot a material does not bind.</summary>
	public ImageView WhiteImageView => whiteImageView;
	public VkSampler WhiteSampler => whiteSampler;

	ulong uniformAlignment = 256;
	ulong ringHead;
	int currentFrame;
	bool warnedRingOverflow;

	readonly Dictionary<VulkanPipelineKey, Pipeline> pipelines = [];
	VulkanPipelineKey lastKey;
	Pipeline lastPipeline;
	bool haveLastPipeline;

	public VulkanPipelineSystem(VulkanCore core, VulkanMemoryAllocator allocator, int framesInFlight) {
		this.core = core;
		this.allocator = allocator;
		this.framesInFlight = framesInFlight;
	}

	public bool Init() {
		Vk vk = core.Vk;

		vk.GetPhysicalDeviceProperties(core.PhysicalDevice, out PhysicalDeviceProperties props);
		uniformAlignment = Math.Max(props.Limits.MinUniformBufferOffsetAlignment, 16);

		// --- set 0: dynamic-offset UBOs ---
		DescriptorSetLayoutBinding* set0Bindings = stackalloc DescriptorSetLayoutBinding[(int)UniformBlock.Count];
		for (int i = 0; i < (int)UniformBlock.Count; i++) {
			set0Bindings[i] = new DescriptorSetLayoutBinding {
				Binding = BlockBindings[i],
				DescriptorType = DescriptorType.UniformBufferDynamic,
				DescriptorCount = 1,
				StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
			};
		}
		DescriptorSetLayoutCreateInfo set0Info = new() {
			SType = StructureType.DescriptorSetLayoutCreateInfo,
			BindingCount = (uint)UniformBlock.Count,
			PBindings = set0Bindings
		};
		if (vk.CreateDescriptorSetLayout(core.Device, &set0Info, null, out set0Layout) != Result.Success)
			return Fail("set 0 layout");

		// --- set 1: material textures ---
		DescriptorSetLayoutBinding* set1Bindings = stackalloc DescriptorSetLayoutBinding[TextureBindingCount];
		for (int i = 0; i < TextureBindingCount; i++) {
			set1Bindings[i] = new DescriptorSetLayoutBinding {
				Binding = (uint)i,
				DescriptorType = DescriptorType.CombinedImageSampler,
				DescriptorCount = 1,
				StageFlags = ShaderStageFlags.FragmentBit
			};
		}
		DescriptorSetLayoutCreateInfo set1Info = new() {
			SType = StructureType.DescriptorSetLayoutCreateInfo,
			BindingCount = TextureBindingCount,
			PBindings = set1Bindings
		};
		if (vk.CreateDescriptorSetLayout(core.Device, &set1Info, null, out set1Layout) != Result.Success)
			return Fail("set 1 layout");

		// --- pipeline layout: set0 + set1 + int flags push constant ---
		PushConstantRange pushRange = new() {
			StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
			Offset = 0,
			Size = sizeof(int)
		};
		DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2] { set0Layout, set1Layout };
		PipelineLayoutCreateInfo layoutInfo = new() {
			SType = StructureType.PipelineLayoutCreateInfo,
			SetLayoutCount = 2,
			PSetLayouts = setLayouts,
			PushConstantRangeCount = 1,
			PPushConstantRanges = &pushRange
		};
		if (vk.CreatePipelineLayout(core.Device, &layoutInfo, null, out PipelineLayout pipelineLayout) != Result.Success)
			return Fail("pipeline layout");
		PipelineLayout = pipelineLayout;

		// --- descriptor pool + sets ---
		if (!AddDescriptorPool())
			return Fail("descriptor pool");

		ringBuffers = new VulkanBufferResource[framesInFlight];
		set0Sets = new DescriptorSet[framesInFlight];
		DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[(int)UniformBlock.Count];
		WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[(int)UniformBlock.Count];
		for (int frame = 0; frame < framesInFlight; frame++) {
			ringBuffers[frame] = VulkanBufferResource.Create(core, allocator, RingSize, BufferUsageFlags.UniformBufferBit);

			if (!AllocateSet(set0Layout, out set0Sets[frame]))
				return Fail("set 0 descriptor set");

			for (int i = 0; i < (int)UniformBlock.Count; i++) {
				bufferInfos[i] = new DescriptorBufferInfo(ringBuffers[frame].Handle, 0, (ulong)BlockSizes[i]);
				writes[i] = new WriteDescriptorSet {
					SType = StructureType.WriteDescriptorSet,
					DstSet = set0Sets[frame],
					DstBinding = BlockBindings[i],
					DescriptorCount = 1,
					DescriptorType = DescriptorType.UniformBufferDynamic,
					PBufferInfo = &bufferInfos[i]
				};
			}
			vk.UpdateDescriptorSets(core.Device, (uint)UniformBlock.Count, writes, 0, null);
		}

		// --- zero vertex buffer (source for attributes absent from a vertex format) ---
		zeroVertexBuffer = VulkanBufferResource.Create(core, allocator, 64, BufferUsageFlags.VertexBufferBit);
		new Span<byte>(zeroVertexBuffer.Mapped, 64).Clear();

		if (!CreateWhiteTexture())
			return false;

		return true;
	}

	static bool Fail(string what) {
		Warning($"Vulkan: pipeline system init failed ({what})\n");
		return false;
	}

	readonly List<DescriptorPool> descriptorPools = [];

	bool AddDescriptorPool() {
		DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[2] {
			new DescriptorPoolSize(DescriptorType.UniformBufferDynamic, (uint)((int)UniformBlock.Count * framesInFlight)),
			new DescriptorPoolSize(DescriptorType.CombinedImageSampler, TextureBindingCount * SetsPerPool)
		};
		DescriptorPoolCreateInfo poolInfo = new() {
			SType = StructureType.DescriptorPoolCreateInfo,
			MaxSets = SetsPerPool + (uint)framesInFlight,
			PoolSizeCount = 2,
			PPoolSizes = poolSizes
		};
		if (core.Vk.CreateDescriptorPool(core.Device, &poolInfo, null, out DescriptorPool pool) != Result.Success)
			return false;

		descriptorPools.Add(pool);
		descriptorPool = pool;
		return true;
	}

	/// <summary>Allocates one set of the given layout, adding a pool when the current one is full.</summary>
	bool AllocateSet(DescriptorSetLayout layout, out DescriptorSet set) {
		DescriptorSetAllocateInfo allocInfo = new() {
			SType = StructureType.DescriptorSetAllocateInfo,
			DescriptorPool = descriptorPool,
			DescriptorSetCount = 1,
			PSetLayouts = &layout
		};
		Result result = core.Vk.AllocateDescriptorSets(core.Device, &allocInfo, out set);
		if (result == Result.Success)
			return true;

		if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
			return false;

		if (!AddDescriptorPool())
			return false;

		allocInfo.DescriptorPool = descriptorPool;
		return core.Vk.AllocateDescriptorSets(core.Device, &allocInfo, out set) == Result.Success;
	}

	public void BeginFrame(int frameIndex) {
		currentFrame = frameIndex;
		ringHead = 0;
	}

	public DescriptorSet CurrentSet0 => set0Sets[currentFrame];

	/// <summary>Copies one uniform block's bytes into the current frame's ring; returns the dynamic offset.</summary>
	public uint AllocUniform(ReadOnlySpan<byte> data) {
		ulong aligned = (ringHead + uniformAlignment - 1) / uniformAlignment * uniformAlignment;
		if (aligned + (ulong)data.Length > RingSize) {
			if (!warnedRingOverflow) {
				warnedRingOverflow = true;
				Warning($"Vulkan: uniform ring overflow ({RingSize} bytes/frame) - wrapping, expect one glitched frame\n");
			}
			aligned = 0;
		}
		data.CopyTo(new Span<byte>((byte*)ringBuffers[currentFrame].Mapped + aligned, data.Length));
		ringHead = aligned + (ulong)data.Length;
		return (uint)aligned;
	}

	bool CreateWhiteTexture() {
		Vk vk = core.Vk;

		ImageCreateInfo imageInfo = new() {
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = VkFormat.R8G8B8A8Unorm,
			Extent = new Extent3D(1, 1, 1),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined
		};
		if (vk.CreateImage(core.Device, &imageInfo, null, out whiteImage) != Result.Success)
			return Fail("white image");

		vk.GetImageMemoryRequirements(core.Device, whiteImage, out MemoryRequirements reqs);
		whiteImageAlloc = allocator.Allocate(in reqs, MemoryPropertyFlags.DeviceLocalBit);
		vk.BindImageMemory(core.Device, whiteImage, whiteImageAlloc.Memory, whiteImageAlloc.Offset);

		// Staging + one-shot upload
		using VulkanBufferResource staging = VulkanBufferResource.Create(core, allocator, 4, BufferUsageFlags.TransferSrcBit);
		new Span<byte>(staging.Mapped, 4).Fill(0xFF);

		CommandPoolCreateInfo poolInfo = new() {
			SType = StructureType.CommandPoolCreateInfo,
			Flags = CommandPoolCreateFlags.TransientBit,
			QueueFamilyIndex = core.GraphicsQueueFamily
		};
		if (vk.CreateCommandPool(core.Device, &poolInfo, null, out CommandPool pool) != Result.Success)
			return Fail("upload pool");

		try {
			CommandBufferAllocateInfo cmdAlloc = new() {
				SType = StructureType.CommandBufferAllocateInfo,
				CommandPool = pool,
				Level = CommandBufferLevel.Primary,
				CommandBufferCount = 1
			};
			vk.AllocateCommandBuffers(core.Device, &cmdAlloc, out CommandBuffer cmd);

			CommandBufferBeginInfo begin = new() {
				SType = StructureType.CommandBufferBeginInfo,
				Flags = CommandBufferUsageFlags.OneTimeSubmitBit
			};
			vk.BeginCommandBuffer(cmd, &begin);

			ImageMemoryBarrier2 toTransfer = new() {
				SType = StructureType.ImageMemoryBarrier2,
				SrcStageMask = PipelineStageFlags2.TopOfPipeBit,
				DstStageMask = PipelineStageFlags2.TransferBit,
				DstAccessMask = AccessFlags2.TransferWriteBit,
				OldLayout = ImageLayout.Undefined,
				NewLayout = ImageLayout.TransferDstOptimal,
				SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
				DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
				Image = whiteImage,
				SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
			};
			DependencyInfo dep = new() { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &toTransfer };
			vk.CmdPipelineBarrier2(cmd, &dep);

			BufferImageCopy region = new() {
				ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
				ImageExtent = new Extent3D(1, 1, 1)
			};
			vk.CmdCopyBufferToImage(cmd, staging.Handle, whiteImage, ImageLayout.TransferDstOptimal, 1, &region);

			ImageMemoryBarrier2 toSampled = toTransfer with {
				SrcStageMask = PipelineStageFlags2.TransferBit,
				SrcAccessMask = AccessFlags2.TransferWriteBit,
				DstStageMask = PipelineStageFlags2.FragmentShaderBit,
				DstAccessMask = AccessFlags2.ShaderReadBit,
				OldLayout = ImageLayout.TransferDstOptimal,
				NewLayout = ImageLayout.ShaderReadOnlyOptimal
			};
			DependencyInfo dep2 = new() { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &toSampled };
			vk.CmdPipelineBarrier2(cmd, &dep2);

			vk.EndCommandBuffer(cmd);

			SubmitInfo submit = new() {
				SType = StructureType.SubmitInfo,
				CommandBufferCount = 1,
				PCommandBuffers = &cmd
			};
			vk.QueueSubmit(core.GraphicsQueue, 1, &submit, default);
			vk.QueueWaitIdle(core.GraphicsQueue);
		}
		finally {
			vk.DestroyCommandPool(core.Device, pool, null);
		}

		ImageViewCreateInfo viewInfo = new() {
			SType = StructureType.ImageViewCreateInfo,
			Image = whiteImage,
			ViewType = ImageViewType.Type2D,
			Format = VkFormat.R8G8B8A8Unorm,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
		};
		if (vk.CreateImageView(core.Device, &viewInfo, null, out whiteImageView) != Result.Success)
			return Fail("white image view");

		SamplerCreateInfo samplerInfo = new() {
			SType = StructureType.SamplerCreateInfo,
			MagFilter = Filter.Linear,
			MinFilter = Filter.Linear,
			MipmapMode = SamplerMipmapMode.Linear,
			AddressModeU = SamplerAddressMode.Repeat,
			AddressModeV = SamplerAddressMode.Repeat,
			AddressModeW = SamplerAddressMode.Repeat,
			MaxLod = Vk.LodClampNone
		};
		if (vk.CreateSampler(core.Device, &samplerInfo, null, out whiteSampler) != Result.Success)
			return Fail("white sampler");

		if (!AllocateSet(set1Layout, out DescriptorSet whiteSet))
			return Fail("white descriptor set");
		Set1WhiteSet = whiteSet;

		DescriptorImageInfo imageDescriptor = new(whiteSampler, whiteImageView, ImageLayout.ShaderReadOnlyOptimal);
		WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[TextureBindingCount];
		for (int i = 0; i < TextureBindingCount; i++) {
			writes[i] = new WriteDescriptorSet {
				SType = StructureType.WriteDescriptorSet,
				DstSet = Set1WhiteSet,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &imageDescriptor
			};
		}
		vk.UpdateDescriptorSets(core.Device, TextureBindingCount, writes, 0, null);
		return true;
	}

	/// <summary>
	/// The six (view, sampler) pairs a draw samples from. Descriptor sets are immutable once
	/// written, so one set is cached per distinct combination - materials reuse the same handful.
	/// </summary>
	public struct TextureSetKey : IEquatable<TextureSetKey>
	{
		public ulong View0, View1, View2, View3, View4, View5;
		public ulong Sampler0, Sampler1, Sampler2, Sampler3, Sampler4, Sampler5;

		public void Set(int index, ImageView view, VkSampler sampler) {
			ulong v = view.Handle, s = sampler.Handle;
			switch (index) {
				case 0: View0 = v; Sampler0 = s; break;
				case 1: View1 = v; Sampler1 = s; break;
				case 2: View2 = v; Sampler2 = s; break;
				case 3: View3 = v; Sampler3 = s; break;
				case 4: View4 = v; Sampler4 = s; break;
				case 5: View5 = v; Sampler5 = s; break;
			}
		}

		public readonly (ulong View, ulong Sampler) Get(int index) => index switch {
			0 => (View0, Sampler0),
			1 => (View1, Sampler1),
			2 => (View2, Sampler2),
			3 => (View3, Sampler3),
			4 => (View4, Sampler4),
			_ => (View5, Sampler5)
		};

		public readonly bool Equals(TextureSetKey other) =>
			View0 == other.View0 && View1 == other.View1 && View2 == other.View2 &&
			View3 == other.View3 && View4 == other.View4 && View5 == other.View5 &&
			Sampler0 == other.Sampler0 && Sampler1 == other.Sampler1 && Sampler2 == other.Sampler2 &&
			Sampler3 == other.Sampler3 && Sampler4 == other.Sampler4 && Sampler5 == other.Sampler5;

		public override readonly bool Equals(object? obj) => obj is TextureSetKey key && Equals(key);

		public override readonly int GetHashCode() {
			HashCode hash = new();
			hash.Add(View0); hash.Add(View1); hash.Add(View2);
			hash.Add(View3); hash.Add(View4); hash.Add(View5);
			hash.Add(Sampler0); hash.Add(Sampler3);
			return hash.ToHashCode();
		}
	}

	readonly Dictionary<TextureSetKey, DescriptorSet> textureSets = [];
	bool warnedTextureSetLimit;

	public DescriptorSet GetTextureSet(in TextureSetKey key) {
		if (textureSets.TryGetValue(key, out DescriptorSet cached))
			return cached;

		if (!AllocateSet(set1Layout, out DescriptorSet set)) {
			if (!warnedTextureSetLimit) {
				warnedTextureSetLimit = true;
				Warning("Vulkan: ran out of descriptor sets for material textures; falling back to white\n");
			}
			return Set1WhiteSet;
		}

		DescriptorImageInfo* images = stackalloc DescriptorImageInfo[TextureBindingCount];
		WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[TextureBindingCount];
		for (int i = 0; i < TextureBindingCount; i++) {
			(ulong view, ulong sampler) = key.Get(i);
			images[i] = new DescriptorImageInfo(
				new VkSampler(sampler == 0 ? whiteSampler.Handle : sampler),
				new ImageView(view == 0 ? whiteImageView.Handle : view),
				ImageLayout.ShaderReadOnlyOptimal);
			writes[i] = new WriteDescriptorSet {
				SType = StructureType.WriteDescriptorSet,
				DstSet = set,
				DstBinding = (uint)i,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &images[i]
			};
		}
		core.Vk.UpdateDescriptorSets(core.Device, TextureBindingCount, writes, 0, null);

		textureSets[key] = set;
		return set;
	}

	/// <summary>
	/// Drops cached sets that reference a view being destroyed - a stale view handle in a
	/// descriptor set is a use-after-free the moment something draws with it.
	/// </summary>
	public void InvalidateTextureSets(ImageView view) {
		if (view.Handle == 0 || textureSets.Count == 0)
			return;

		List<TextureSetKey>? doomed = null;
		foreach (TextureSetKey key in textureSets.Keys) {
			for (int i = 0; i < TextureBindingCount; i++) {
				if (key.Get(i).View == view.Handle) {
					(doomed ??= []).Add(key);
					break;
				}
			}
		}
		if (doomed == null)
			return;

		// The sets themselves are left allocated (their pool is only reset at shutdown); dropping
		// the cache entry is enough to stop anything binding them again.
		foreach (TextureSetKey key in doomed)
			textureSets.Remove(key);
	}

	public Pipeline GetPipeline(in VulkanPipelineKey key) {
		if (haveLastPipeline && key.Equals(lastKey))
			return lastPipeline;

		if (!pipelines.TryGetValue(key, out Pipeline pipeline)) {
			pipeline = CreatePipeline(in key);
			pipelines[key] = pipeline;
		}

		lastKey = key;
		lastPipeline = pipeline;
		haveLastPipeline = true;
		return pipeline;
	}

	static CompareOp MapDepthFunc(ShaderDepthFunc func) => func switch {
		ShaderDepthFunc.Never => CompareOp.Never,
		ShaderDepthFunc.Nearer => CompareOp.Less,
		ShaderDepthFunc.Equal => CompareOp.Equal,
		ShaderDepthFunc.NearerOrEqual => CompareOp.LessOrEqual,
		ShaderDepthFunc.Farther => CompareOp.Greater,
		ShaderDepthFunc.NotEqual => CompareOp.NotEqual,
		ShaderDepthFunc.FartherOrEqual => CompareOp.GreaterOrEqual,
		_ => CompareOp.Always
	};

	static BlendFactor MapBlendFactor(ShaderBlendFactor factor) => factor switch {
		ShaderBlendFactor.Zero => BlendFactor.Zero,
		ShaderBlendFactor.One => BlendFactor.One,
		ShaderBlendFactor.SrcColor => BlendFactor.SrcColor,
		ShaderBlendFactor.OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
		ShaderBlendFactor.SrcAlpha => BlendFactor.SrcAlpha,
		ShaderBlendFactor.OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
		ShaderBlendFactor.DstAlpha => BlendFactor.DstAlpha,
		ShaderBlendFactor.OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
		ShaderBlendFactor.DstColor => BlendFactor.DstColor,
		ShaderBlendFactor.OneMinusDstColor => BlendFactor.OneMinusDstColor,
		ShaderBlendFactor.SrcAlphaSat => BlendFactor.SrcAlphaSaturate,
		ShaderBlendFactor.BothSrcAlpha => BlendFactor.SrcAlpha,
		ShaderBlendFactor.BothInvSrcAlpha => BlendFactor.OneMinusSrcAlpha,
		_ => BlendFactor.One
	};

	static BlendOp MapBlendOp(ShaderBlendOp op) => op switch {
		ShaderBlendOp.Add => BlendOp.Add,
		ShaderBlendOp.Subtract => BlendOp.Subtract,
		ShaderBlendOp.RevSubtract => BlendOp.ReverseSubtract,
		ShaderBlendOp.Min => BlendOp.Min,
		ShaderBlendOp.Max => BlendOp.Max,
		_ => BlendOp.Add
	};

	bool warnedFillMode;

	Pipeline CreatePipeline(in VulkanPipelineKey key) {
		Vk vk = core.Vk;
		ref readonly GraphicsBoardState state = ref key.State;

		byte* entryPoint = stackalloc byte[5] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
		PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2] {
			new PipelineShaderStageCreateInfo {
				SType = StructureType.PipelineShaderStageCreateInfo,
				Stage = ShaderStageFlags.VertexBit,
				Module = new ShaderModule((ulong)key.VertexShader),
				PName = entryPoint
			},
			new PipelineShaderStageCreateInfo {
				SType = StructureType.PipelineShaderStageCreateInfo,
				Stage = ShaderStageFlags.FragmentBit,
				Module = new ShaderModule((ulong)key.PixelShader),
				PName = entryPoint
			}
		};

		Span<VertexInputAttributeDescription> attributes = stackalloc VertexInputAttributeDescription[VulkanVertexLayout.LocationCount];
		uint stride = VulkanVertexLayout.BuildAttributes(key.Format, attributes);

		VertexInputBindingDescription* bindings = stackalloc VertexInputBindingDescription[2] {
			new VertexInputBindingDescription(VulkanVertexLayout.VertexDataBinding, stride, VertexInputRate.Vertex),
			// Stride 0 = every vertex reads the same 64 zero bytes; feeds attributes the format lacks.
			new VertexInputBindingDescription(VulkanVertexLayout.ZeroBufferBinding, 0, VertexInputRate.Vertex)
		};

		fixed (VertexInputAttributeDescription* attributesPtr = attributes) {
			PipelineVertexInputStateCreateInfo vertexInput = new() {
				SType = StructureType.PipelineVertexInputStateCreateInfo,
				VertexBindingDescriptionCount = 2,
				PVertexBindingDescriptions = bindings,
				VertexAttributeDescriptionCount = VulkanVertexLayout.LocationCount,
				PVertexAttributeDescriptions = attributesPtr
			};

			PipelineInputAssemblyStateCreateInfo inputAssembly = new() {
				SType = StructureType.PipelineInputAssemblyStateCreateInfo,
				Topology = VulkanVertexLayout.Topology(key.Topology)
			};

			PipelineViewportStateCreateInfo viewportState = new() {
				SType = StructureType.PipelineViewportStateCreateInfo,
				ViewportCount = 1,
				ScissorCount = 1
			};

			PolygonMode polygonMode = state.FillMode switch {
				ShaderPolyMode.Point => PolygonMode.Point,
				ShaderPolyMode.Line => PolygonMode.Line,
				_ => PolygonMode.Fill
			};
			if (polygonMode != PolygonMode.Fill && !core.SupportsFillModeNonSolid) {
				if (!warnedFillMode) {
					warnedFillMode = true;
					Warning("Vulkan: fillModeNonSolid not supported; wireframe/point fill clamped to solid\n");
				}
				polygonMode = PolygonMode.Fill;
			}

			bool depthBias = state.ZBias != PolygonOffsetMode.Disable && state.FillMode == ShaderPolyMode.Fill;
			PipelineRasterizationStateCreateInfo rasterization = new() {
				SType = StructureType.PipelineRasterizationStateCreateInfo,
				PolygonMode = polygonMode,
				CullMode = state.CullEnable ? CullModeFlags.BackBit : CullModeFlags.None,
				// GL backend runs glFrontFace(GL_CW); the negative-height viewport keeps winding GL-compatible.
				FrontFace = FrontFace.Clockwise,
				LineWidth = 1.0f,
				DepthBiasEnable = depthBias,
				DepthBiasConstantFactor = state.ZBias == PolygonOffsetMode.ShadowBias ? -1.0f : 0.0f,
				DepthBiasSlopeFactor = state.ZBias == PolygonOffsetMode.ShadowBias ? -1.0f : 0.0f
			};

			PipelineMultisampleStateCreateInfo multisample = new() {
				SType = StructureType.PipelineMultisampleStateCreateInfo,
				RasterizationSamples = SampleCountFlags.Count1Bit,
				AlphaToCoverageEnable = state.AlphaToCoverage
			};

			PipelineDepthStencilStateCreateInfo depthStencil = new() {
				SType = StructureType.PipelineDepthStencilStateCreateInfo,
				DepthTestEnable = state.DepthTest,
				DepthWriteEnable = state.DepthWrite,
				DepthCompareOp = MapDepthFunc(state.DepthFunc)
			};

			ColorComponentFlags writeMask = 0;
			if (state.ColorWrite)
				writeMask |= ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit;
			if (state.AlphaWrite)
				writeMask |= ColorComponentFlags.ABit;

			PipelineColorBlendAttachmentState blendAttachment = new() {
				BlendEnable = state.Blending,
				SrcColorBlendFactor = MapBlendFactor(state.SourceBlend),
				DstColorBlendFactor = MapBlendFactor(state.DestinationBlend),
				ColorBlendOp = MapBlendOp(state.BlendOperation),
				SrcAlphaBlendFactor = state.AlphaSeparateBlend ? MapBlendFactor(state.AlphaSourceBlend) : MapBlendFactor(state.SourceBlend),
				DstAlphaBlendFactor = state.AlphaSeparateBlend ? MapBlendFactor(state.AlphaDestinationBlend) : MapBlendFactor(state.DestinationBlend),
				AlphaBlendOp = state.AlphaSeparateBlend ? MapBlendOp(state.AlphaBlendOperation) : MapBlendOp(state.BlendOperation),
				ColorWriteMask = writeMask
			};
			PipelineColorBlendStateCreateInfo colorBlend = new() {
				SType = StructureType.PipelineColorBlendStateCreateInfo,
				AttachmentCount = 1,
				PAttachments = &blendAttachment
			};

			DynamicState* dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
			PipelineDynamicStateCreateInfo dynamic = new() {
				SType = StructureType.PipelineDynamicStateCreateInfo,
				DynamicStateCount = 2,
				PDynamicStates = dynamicStates
			};

			VkFormat colorFormat = key.ColorFormat;
			PipelineRenderingCreateInfo rendering = new() {
				SType = StructureType.PipelineRenderingCreateInfo,
				ColorAttachmentCount = 1,
				PColorAttachmentFormats = &colorFormat,
				DepthAttachmentFormat = key.DepthFormat
			};

			GraphicsPipelineCreateInfo pipelineInfo = new() {
				SType = StructureType.GraphicsPipelineCreateInfo,
				PNext = &rendering,
				StageCount = 2,
				PStages = stages,
				PVertexInputState = &vertexInput,
				PInputAssemblyState = &inputAssembly,
				PViewportState = &viewportState,
				PRasterizationState = &rasterization,
				PMultisampleState = &multisample,
				PDepthStencilState = &depthStencil,
				PColorBlendState = &colorBlend,
				PDynamicState = &dynamic,
				Layout = PipelineLayout
			};

			if (vk.CreateGraphicsPipelines(core.Device, default, 1, &pipelineInfo, null, out Pipeline pipeline) != Result.Success) {
				Warning("Vulkan: vkCreateGraphicsPipelines failed\n");
				return default;
			}
			return pipeline;
		}
	}

	public void Dispose() {
		Vk vk = core.Vk;
		vk.DeviceWaitIdle(core.Device);

		foreach (Pipeline pipeline in pipelines.Values)
			if (pipeline.Handle != 0)
				vk.DestroyPipeline(core.Device, pipeline, null);
		pipelines.Clear();
		haveLastPipeline = false;

		if (whiteSampler.Handle != 0) vk.DestroySampler(core.Device, whiteSampler, null);
		if (whiteImageView.Handle != 0) vk.DestroyImageView(core.Device, whiteImageView, null);
		if (whiteImage.Handle != 0) {
			vk.DestroyImage(core.Device, whiteImage, null);
			allocator.Free(in whiteImageAlloc);
		}

		zeroVertexBuffer?.Dispose();
		foreach (VulkanBufferResource ring in ringBuffers)
			ring.Dispose();
		ringBuffers = [];

		textureSets.Clear();
		foreach (DescriptorPool pool in descriptorPools)
			vk.DestroyDescriptorPool(core.Device, pool, null);
		descriptorPools.Clear();
		descriptorPool = default;
		if (PipelineLayout.Handle != 0) vk.DestroyPipelineLayout(core.Device, PipelineLayout, null);
		if (set0Layout.Handle != 0) vk.DestroyDescriptorSetLayout(core.Device, set0Layout, null);
		if (set1Layout.Handle != 0) vk.DestroyDescriptorSetLayout(core.Device, set1Layout, null);
	}
}
