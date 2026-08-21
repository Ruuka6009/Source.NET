using Silk.NET.Vulkan;

using Source.Bitmap;
using Source.Common.Bitmap;
using Source.Common.MaterialSystem;
using Source.Common.Mathematics;
using Source.Common.ShaderAPI;

using VkFormat = Silk.NET.Vulkan.Format;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Per-texture sampler state. GL sets these on the texture object; in Vulkan samplers are
/// separate objects, so the state is recorded here and resolved through a cache at bind time.
/// </summary>
public struct SamplerDesc : IEquatable<SamplerDesc>
{
	public SamplerAddressMode AddressU;
	public SamplerAddressMode AddressV;
	public SamplerAddressMode AddressW;
	public Filter MinFilter;
	public Filter MagFilter;
	public SamplerMipmapMode MipmapMode;
	public bool Anisotropic;

	public static SamplerDesc Default => new() {
		AddressU = SamplerAddressMode.Repeat,
		AddressV = SamplerAddressMode.Repeat,
		AddressW = SamplerAddressMode.Repeat,
		MinFilter = Filter.Linear,
		MagFilter = Filter.Linear,
		MipmapMode = SamplerMipmapMode.Linear
	};

	public readonly bool Equals(SamplerDesc other) =>
		AddressU == other.AddressU && AddressV == other.AddressV && AddressW == other.AddressW &&
		MinFilter == other.MinFilter && MagFilter == other.MagFilter &&
		MipmapMode == other.MipmapMode && Anisotropic == other.Anisotropic;

	public override readonly bool Equals(object? obj) => obj is SamplerDesc desc && Equals(desc);
	public override readonly int GetHashCode() =>
		HashCode.Combine(AddressU, AddressV, AddressW, MinFilter, MagFilter, MipmapMode, Anisotropic);
}

/// <summary>
/// One texture as the material system sees it. May hold several GPU copies (GL's NumCopies);
/// <see cref="CurrentCopy"/> selects the one reads and writes land on.
/// </summary>
public unsafe class VulkanTexture
{
	public int Width;
	public int Height;
	public int Depth = 1;
	public int MipCount = 1;
	public int FaceCount = 1;
	/// <summary>Format the pixels are stored in on the GPU (post-conversion).</summary>
	public ImageFormat StorageFormat;
	public VkFormat VkFormat;
	public CreateTextureFlags CreationFlags;
	public string DebugName = "";
	public bool IsCubeMap;
	public bool IsDepth;

	public Image[] Images = [];
	public ImageView[] Views = [];
	public VulkanMemoryAllocator.Allocation[] Allocations = [];
	public ImageLayout[] Layouts = [];
	/// <summary>Highest mip level written so far per copy, -1 when nothing has been uploaded yet.</summary>
	public int[] TopMipUploaded = [];

	public int CurrentCopy;
	public bool SwitchNeeded;
	/// <summary>Set once the texture has been used as a render target, so it is sampleable.</summary>
	public bool RenderedTo;

	public SamplerDesc Sampler = SamplerDesc.Default;

	public bool HasContent => RenderedTo || (TopMipUploaded.Length > 0 && TopMipUploaded[CurrentCopy] >= 0);
	public Image CurrentImage => Images[CurrentCopy];
}

/// <summary>
/// Creates VkImages for the material system's textures and streams pixels into them. Uploads are
/// recorded into a dedicated command buffer - not the frame's, which is inside a rendering pass
/// where copies are illegal - and flushed with a fence wait when the staging ring fills, before
/// each frame, and before any draw that would sample a pending upload.
/// </summary>
public unsafe class VulkanTextureManager : IDisposable
{
	const ulong StagingSize = 32UL * 1024 * 1024;

	readonly VulkanCore core;
	readonly VulkanMemoryAllocator allocator;

	CommandPool uploadPool;
	CommandBuffer uploadCmd;
	Fence uploadFence;
	bool recording;

	VulkanBufferResource? staging;
	ulong stagingHead;
	readonly List<VulkanTexture> touched = [];
	readonly Dictionary<SamplerDesc, VkSampler> samplers = [];

	byte[] conversionBuffer = new byte[1024 * 1024];

	public VulkanTextureManager(VulkanCore core, VulkanMemoryAllocator allocator) {
		this.core = core;
		this.allocator = allocator;
	}

	public bool Init() {
		Vk vk = core.Vk;

		CommandPoolCreateInfo poolInfo = new() {
			SType = StructureType.CommandPoolCreateInfo,
			Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
			QueueFamilyIndex = core.GraphicsQueueFamily
		};
		if (vk.CreateCommandPool(core.Device, &poolInfo, null, out uploadPool) != Result.Success) {
			Warning("Vulkan: texture upload command pool creation failed\n");
			return false;
		}

		CommandBufferAllocateInfo allocInfo = new() {
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = uploadPool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = 1
		};
		if (vk.AllocateCommandBuffers(core.Device, &allocInfo, out uploadCmd) != Result.Success) {
			Warning("Vulkan: texture upload command buffer allocation failed\n");
			return false;
		}

		FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
		if (vk.CreateFence(core.Device, &fenceInfo, null, out uploadFence) != Result.Success) {
			Warning("Vulkan: texture upload fence creation failed\n");
			return false;
		}

		staging = VulkanBufferResource.Create(core, allocator, StagingSize, BufferUsageFlags.TransferSrcBit);
		return true;
	}

	public VulkanTexture? Create(int width, int height, int depth, ImageFormat format, int mipCount, int copies,
		CreateTextureFlags flags, ReadOnlySpan<char> debugName) {
		Vk vk = core.Vk;

		// NV_NULL takes no video memory; anything binding it falls back to white.
		if (format == ImageFormat.NV_NULL)
			return null;

		ImageFormat storage = ImageFormatVulkan.GetStorageFormat(format);
		// SRGB is ignored for parity with GL, which never enables GL_FRAMEBUFFER_SRGB. The
		// swapchain is UNORM to match, so an _Srgb view would gamma-correct one time too many.
		VkFormat vkFormat = ImageFormatVulkan.ToVkFormat(storage);
		if (vkFormat == VkFormat.Undefined) {
			Warning($"Vulkan: unsupported texture format {format} for '{debugName}'\n");
			return null;
		}

		bool isCube = (flags & CreateTextureFlags.Cubemap) != 0;
		bool isDepth = ImageFormatVulkan.IsDepthFormat(storage);
		copies = Math.Max(copies, 1);
		mipCount = Math.Max(mipCount, 1);

		VulkanTexture texture = new() {
			Width = Math.Max(width, 1),
			Height = Math.Max(height, 1),
			Depth = Math.Max(depth, 1),
			MipCount = mipCount,
			FaceCount = isCube ? 6 : 1,
			StorageFormat = storage,
			VkFormat = vkFormat,
			CreationFlags = flags,
			DebugName = new string(debugName).SliceNullTerminatedString().ToString(),
			IsCubeMap = isCube,
			IsDepth = isDepth,
			Images = new Image[copies],
			Views = new ImageView[copies],
			Allocations = new VulkanMemoryAllocator.Allocation[copies],
			Layouts = new ImageLayout[copies],
			TopMipUploaded = new int[copies]
		};

		ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
		if ((flags & CreateTextureFlags.RenderTarget) != 0)
			usage |= isDepth ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit;
		if (isDepth)
			usage |= ImageUsageFlags.DepthStencilAttachmentBit;

		for (int i = 0; i < copies; i++) {
			ImageCreateInfo imageInfo = new() {
				SType = StructureType.ImageCreateInfo,
				Flags = isCube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
				ImageType = ImageType.Type2D,
				Format = vkFormat,
				Extent = new Extent3D((uint)texture.Width, (uint)texture.Height, 1),
				MipLevels = (uint)mipCount,
				ArrayLayers = (uint)texture.FaceCount,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				Usage = usage,
				SharingMode = SharingMode.Exclusive,
				InitialLayout = ImageLayout.Undefined
			};

			if (vk.CreateImage(core.Device, &imageInfo, null, out Image image) != Result.Success) {
				Warning($"Vulkan: vkCreateImage failed for '{texture.DebugName}' ({width}x{height} {format})\n");
				Destroy(texture);
				return null;
			}

			vk.GetImageMemoryRequirements(core.Device, image, out MemoryRequirements reqs);
			VulkanMemoryAllocator.Allocation alloc = allocator.Allocate(in reqs, MemoryPropertyFlags.DeviceLocalBit);
			vk.BindImageMemory(core.Device, image, alloc.Memory, alloc.Offset);

			texture.Images[i] = image;
			texture.Allocations[i] = alloc;
			texture.Layouts[i] = ImageLayout.Undefined;
			texture.TopMipUploaded[i] = -1;
		}

		return texture;
	}

	public void Destroy(VulkanTexture texture) {
		Vk vk = core.Vk;
		touched.Remove(texture);

		for (int i = 0; i < texture.Images.Length; i++) {
			if (texture.Views[i].Handle != 0) {
				vk.DestroyImageView(core.Device, texture.Views[i], null);
				texture.Views[i] = default;
			}
			if (texture.Images[i].Handle != 0) {
				vk.DestroyImage(core.Device, texture.Images[i], null);
				allocator.Free(in texture.Allocations[i]);
				texture.Images[i] = default;
			}
		}
	}

	/// <summary>
	/// Covers only the mips actually uploaded; a VTF with fewer levels than the image was created
	/// with would otherwise sample uninitialised memory at the small end.
	/// </summary>
	public ImageView GetView(VulkanTexture texture) {
		int copy = texture.CurrentCopy;
		if (texture.Views[copy].Handle != 0)
			return texture.Views[copy];

		int levels = Math.Clamp(texture.TopMipUploaded[copy] + 1, 1, texture.MipCount);
		ImageViewCreateInfo viewInfo = new() {
			SType = StructureType.ImageViewCreateInfo,
			Image = texture.Images[copy],
			ViewType = texture.IsCubeMap ? ImageViewType.TypeCube : ImageViewType.Type2D,
			Format = texture.VkFormat,
			SubresourceRange = new ImageSubresourceRange(
				texture.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit,
				0, (uint)levels, 0, (uint)texture.FaceCount)
		};
		if (core.Vk.CreateImageView(core.Device, &viewInfo, null, out ImageView view) != Result.Success) {
			Warning($"Vulkan: image view creation failed for '{texture.DebugName}'\n");
			return default;
		}
		texture.Views[copy] = view;
		return view;
	}

	void InvalidateView(VulkanTexture texture, int copy) {
		if (texture.Views[copy].Handle != 0) {
			// A previous frame may still reference this view from a descriptor set.
			retiredViews.Add((texture.Views[copy], VulkanFrameLoop.FramesInFlight + 1));
			texture.Views[copy] = default;
		}
	}

	readonly List<(ImageView View, int FramesLeft)> retiredViews = [];

	public void TickRetiredViews() {
		for (int i = retiredViews.Count - 1; i >= 0; i--) {
			(ImageView view, int framesLeft) = retiredViews[i];
			if (--framesLeft <= 0) {
				core.Vk.DestroyImageView(core.Device, view, null);
				retiredViews.RemoveAt(i);
			}
			else
				retiredViews[i] = (view, framesLeft);
		}
	}

	public VkSampler GetSampler(in SamplerDesc desc) {
		if (samplers.TryGetValue(desc, out VkSampler existing))
			return existing;

		bool aniso = desc.Anisotropic && core.SupportsAnisotropy;
		SamplerCreateInfo info = new() {
			SType = StructureType.SamplerCreateInfo,
			MagFilter = desc.MagFilter,
			MinFilter = desc.MinFilter,
			MipmapMode = desc.MipmapMode,
			AddressModeU = desc.AddressU,
			AddressModeV = desc.AddressV,
			AddressModeW = desc.AddressW,
			AnisotropyEnable = aniso,
			MaxAnisotropy = aniso ? core.MaxAnisotropy : 1.0f,
			MinLod = 0,
			MaxLod = Vk.LodClampNone,
			BorderColor = BorderColor.FloatOpaqueBlack
		};
		if (core.Vk.CreateSampler(core.Device, &info, null, out VkSampler sampler) != Result.Success) {
			Warning("Vulkan: sampler creation failed\n");
			return default;
		}
		samplers[desc] = sampler;
		return sampler;
	}

	/// <summary>
	/// Copies one mip of one cube face. <paramref name="srcStride"/> is the source row pitch in
	/// bytes, or 0 when the rows are tightly packed.
	/// </summary>
	public void Upload(VulkanTexture texture, int mip, int face, ImageFormat srcFormat,
		int width, int height, ReadOnlySpan<byte> data, int srcStride = 0) {
		if (data.IsEmpty || width <= 0 || height <= 0)
			return;
		if (mip < 0 || mip >= texture.MipCount)
			return;

		ImageFormat storage = texture.StorageFormat;
		int dstPitch = ImageFormatVulkan.RowPitch(storage, width);
		int rows = storage.IsCompressed() ? Math.Max(1, (height + 3) / 4) : height;
		int dstSize = dstPitch * rows;

		ReadOnlySpan<byte> upload = data;
		if (ImageFormatVulkan.RequiresConversion(srcFormat)) {
			if (conversionBuffer.Length < dstSize)
				conversionBuffer = new byte[MathLib.CeilPow2(dstSize)];
			ImageFormatVulkan.Convert(srcFormat, data, conversionBuffer.AsSpan(0, dstSize));
			upload = conversionBuffer.AsSpan(0, dstSize);
			srcStride = 0;
		}

		// Staging is tightly packed; strided sources are de-strided row by row.
		int srcPitch = srcStride > 0 ? srcStride : dstPitch;
		if (!ReserveStaging(dstSize, out ulong stagingOffset)) {
			Warning($"Vulkan: texture upload of {dstSize} bytes exceeds the {StagingSize} byte staging buffer; skipping '{texture.DebugName}'\n");
			return;
		}

		byte* dst = (byte*)staging!.Mapped + stagingOffset;
		if (srcPitch == dstPitch) {
			int copyBytes = Math.Min(dstSize, upload.Length);
			upload[..copyBytes].CopyTo(new Span<byte>(dst, copyBytes));
		}
		else {
			for (int y = 0; y < rows; y++) {
				int srcOffset = y * srcPitch;
				if (srcOffset + dstPitch > upload.Length)
					break;
				upload.Slice(srcOffset, dstPitch).CopyTo(new Span<byte>(dst + y * dstPitch, dstPitch));
			}
		}

		BeginRecording();
		int copy = texture.CurrentCopy;
		TransitionForUpload(texture, copy);

		BufferImageCopy region = new() {
			BufferOffset = stagingOffset,
			BufferRowLength = 0,
			BufferImageHeight = 0,
			ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, (uint)Math.Clamp(face, 0, texture.FaceCount - 1), 1),
			ImageOffset = new Offset3D(0, 0, 0),
			ImageExtent = new Extent3D((uint)width, (uint)height, 1)
		};
		core.Vk.CmdCopyBufferToImage(uploadCmd, staging.Handle, texture.Images[copy], ImageLayout.TransferDstOptimal, 1, &region);

		if (mip > texture.TopMipUploaded[copy]) {
			texture.TopMipUploaded[copy] = mip;
			InvalidateView(texture, copy);
		}
	}

	/// <summary>Sub-rectangle update (procedural textures, TexSubImage2D/TexUnlock).</summary>
	public void UploadSubRect(VulkanTexture texture, int mip, int face, int x, int y, int width, int height,
		ImageFormat srcFormat, ReadOnlySpan<byte> data, int srcStride = 0) {
		if (data.IsEmpty || width <= 0 || height <= 0)
			return;

		ImageFormat storage = texture.StorageFormat;
		if (storage.IsCompressed()) {
			Warning($"Vulkan: sub-rect upload into compressed texture '{texture.DebugName}' is not supported\n");
			return;
		}

		int dstPitch = ImageFormatVulkan.RowPitch(storage, width);
		int dstSize = dstPitch * height;

		ReadOnlySpan<byte> upload = data;
		if (ImageFormatVulkan.RequiresConversion(srcFormat)) {
			if (conversionBuffer.Length < dstSize)
				conversionBuffer = new byte[MathLib.CeilPow2(dstSize)];
			ImageFormatVulkan.Convert(srcFormat, data, conversionBuffer.AsSpan(0, dstSize));
			upload = conversionBuffer.AsSpan(0, dstSize);
			srcStride = 0;
		}

		int srcPitch = srcStride > 0 ? srcStride : dstPitch;
		if (!ReserveStaging(dstSize, out ulong stagingOffset)) {
			Warning($"Vulkan: sub-rect upload too large for staging; skipping '{texture.DebugName}'\n");
			return;
		}

		byte* dst = (byte*)staging!.Mapped + stagingOffset;
		for (int row = 0; row < height; row++) {
			int srcOffset = row * srcPitch;
			if (srcOffset + dstPitch > upload.Length)
				break;
			upload.Slice(srcOffset, dstPitch).CopyTo(new Span<byte>(dst + row * dstPitch, dstPitch));
		}

		BeginRecording();
		int copy = texture.CurrentCopy;
		TransitionForUpload(texture, copy);

		BufferImageCopy region = new() {
			BufferOffset = stagingOffset,
			ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, (uint)Math.Clamp(face, 0, texture.FaceCount - 1), 1),
			ImageOffset = new Offset3D(x, y, 0),
			ImageExtent = new Extent3D((uint)width, (uint)height, 1)
		};
		core.Vk.CmdCopyBufferToImage(uploadCmd, staging.Handle, texture.Images[copy], ImageLayout.TransferDstOptimal, 1, &region);

		if (texture.TopMipUploaded[copy] < mip) {
			texture.TopMipUploaded[copy] = mip;
			InvalidateView(texture, copy);
		}
	}

	bool ReserveStaging(int size, out ulong offset) {
		if ((ulong)size > StagingSize) {
			offset = 0;
			return false;
		}
		ulong aligned = (stagingHead + 15) / 16 * 16;
		if (aligned + (ulong)size > StagingSize) {
			Flush();
			aligned = 0;
		}
		offset = aligned;
		stagingHead = aligned + (ulong)size;
		return true;
	}

	void BeginRecording() {
		if (recording)
			return;

		Vk vk = core.Vk;
		vk.ResetCommandBuffer(uploadCmd, 0);
		CommandBufferBeginInfo begin = new() {
			SType = StructureType.CommandBufferBeginInfo,
			Flags = CommandBufferUsageFlags.OneTimeSubmitBit
		};
		vk.BeginCommandBuffer(uploadCmd, &begin);
		recording = true;
	}

	void TransitionForUpload(VulkanTexture texture, int copy) {
		if (texture.Layouts[copy] == ImageLayout.TransferDstOptimal) {
			if (!touched.Contains(texture))
				touched.Add(texture);
			return;
		}

		Barrier(uploadCmd, texture.Images[copy], texture.FaceCount, texture.MipCount,
			texture.Layouts[copy], ImageLayout.TransferDstOptimal,
			PipelineStageFlags2.AllCommandsBit, AccessFlags2.None,
			PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

		texture.Layouts[copy] = ImageLayout.TransferDstOptimal;
		if (!touched.Contains(texture))
			touched.Add(texture);
	}

	/// <summary>True when copies have been recorded but not yet submitted.</summary>
	public bool HasPendingWork => recording;

	/// <summary>Submits any recorded copies and leaves every touched texture sampleable.</summary>
	public void Flush() {
		if (!recording) {
			stagingHead = 0;
			return;
		}

		Vk vk = core.Vk;

		foreach (VulkanTexture texture in touched) {
			for (int copy = 0; copy < texture.Images.Length; copy++) {
				if (texture.Layouts[copy] != ImageLayout.TransferDstOptimal)
					continue;
				Barrier(uploadCmd, texture.Images[copy], texture.FaceCount, texture.MipCount,
					ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
					PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit,
					PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderReadBit);
				texture.Layouts[copy] = ImageLayout.ShaderReadOnlyOptimal;
			}
		}
		touched.Clear();

		vk.EndCommandBuffer(uploadCmd);
		recording = false;

		CommandBuffer cmd = uploadCmd;
		SubmitInfo submit = new() {
			SType = StructureType.SubmitInfo,
			CommandBufferCount = 1,
			PCommandBuffers = &cmd
		};
		vk.ResetFences(core.Device, 1, in uploadFence);
		vk.QueueSubmit(core.GraphicsQueue, 1, &submit, uploadFence);
		vk.WaitForFences(core.Device, 1, in uploadFence, true, ulong.MaxValue);

		stagingHead = 0;
	}

	void Barrier(CommandBuffer cmd, Image image, int faces, int mips,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess) {
		ImageMemoryBarrier2 barrier = new() {
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = srcStage,
			SrcAccessMask = srcAccess,
			DstStageMask = dstStage,
			DstAccessMask = dstAccess,
			OldLayout = oldLayout,
			NewLayout = newLayout,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, (uint)mips, 0, (uint)faces)
		};
		DependencyInfo dependency = new() {
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier
		};
		core.Vk.CmdPipelineBarrier2(cmd, &dependency);
	}

	public void Dispose() {
		Vk vk = core.Vk;
		vk.DeviceWaitIdle(core.Device);

		foreach ((ImageView view, _) in retiredViews)
			vk.DestroyImageView(core.Device, view, null);
		retiredViews.Clear();

		foreach (VkSampler sampler in samplers.Values)
			vk.DestroySampler(core.Device, sampler, null);
		samplers.Clear();

		staging?.Dispose();
		staging = null;

		if (uploadFence.Handle != 0) vk.DestroyFence(core.Device, uploadFence, null);
		if (uploadPool.Handle != 0) vk.DestroyCommandPool(core.Device, uploadPool, null);
	}
}
