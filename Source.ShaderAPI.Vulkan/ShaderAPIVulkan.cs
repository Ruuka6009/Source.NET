using Microsoft.Extensions.DependencyInjection;

using Silk.NET.Vulkan;

using Source.Common;
using Source.Common.Bitmap;
using Source.Common.Launcher;
using Source.Common.MaterialSystem;
using Source.Common.Mathematics;
using Source.Common.ShaderAPI;

using System.Numerics;
using System.Runtime.InteropServices;

using Sampler = Source.Common.MaterialSystem.Sampler;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Vulkan IShaderAPI/IShaderDevice. Phases 3+4 of VULKAN_TODO.md: real pipelines resolved from
/// shadow-state snapshots, real VkBuffer meshes, dynamic-offset uniform ring, depth buffer.
/// Textures are still a 1x1 white placeholder (set 1) - geometry renders flat-colored until the
/// texture phase lands.
/// </summary>
public class ShaderAPIVulkan : IShaderAPI, IShaderDevice, IDebugTextureInfo
{
	public static void DLLInit(IServiceCollection services) {
		services.AddSingleton(x => (IDebugTextureInfo)(ShaderAPIVulkan)x.GetRequiredService<IShaderAPI>());
		services.AddSingleton(x => x.GetRequiredService<IShaderAPI>().GetShaderDevice());
		services.AddSingleton<MeshMgrVulkan>();
		services.AddSingleton<IMeshMgr>(x => x.GetRequiredService<MeshMgrVulkan>());
		services.AddSingleton<IMaterialSystemHardwareConfig, HardwareConfigVulkan>();
		services.AddSingleton<IShaderSystem, ShaderSystemVulkan>();
	}

	internal IServiceProvider services = null!;
	IShaderUtil ShaderUtil = null!;
	IShaderSystem ShaderManager = null!;

	VulkanCore? core;
	VulkanSwapchain? swapchain;
	VulkanFrameLoop? frameLoop;
	VulkanGraphicsContext? Device;
	VulkanMemoryAllocator? allocator;
	VulkanPipelineSystem? pipelines;
	MeshMgrVulkan MeshMgr = null!;

	internal GraphicsDriver Driver = GraphicsDriver.Vulkan13;
	ShaderDeviceInfo PresentParameters;

	// Distinctive default so a running Vulkan backend is unmistakable on screen
	float clearR = 0.10f, clearG = 0.20f, clearB = 0.40f;

	ILauncherManager LauncherManager => services.GetRequiredService<ILauncherManager>();

	internal VulkanCore? Core => core;
	internal VulkanMemoryAllocator? Allocator => allocator;

	// ------------------------------------------------------------------
	// Boot / device
	// ------------------------------------------------------------------

	public void PreInit(IShaderUtil shaderUtil, IServiceProvider services) {
		this.services = services;
		ShaderUtil = shaderUtil;
		ShaderManager = services.GetRequiredService<IShaderSystem>();
		MeshMgr = services.GetRequiredService<MeshMgrVulkan>();
	}

	public bool SetMode(IWindow window, in ShaderDeviceInfo info) {
		if (IsActive())
			DestroyDevice();

		if (!InitDevice(window, in info))
			return false;

		return OnDeviceInit();
	}

	bool InitDevice(IWindow window, in ShaderDeviceInfo deviceInfo) {
		IGraphicsProvider graphics = services.GetRequiredService<IGraphicsProvider>();
		PresentParameters = deviceInfo;

		core = new VulkanCore(graphics);
		if (!core.Init(window)) {
			core.Dispose();
			core = null;
			return false;
		}

		allocator = new VulkanMemoryAllocator(core);

		swapchain = new VulkanSwapchain(core);
		if (!swapchain.Create((uint)deviceInfo.DisplayMode.Width, (uint)deviceInfo.DisplayMode.Height, deviceInfo.WaitForVSync))
			return false;

		frameLoop = new VulkanFrameLoop(core, swapchain, allocator);
		if (!frameLoop.Init())
			return false;

		pipelines = new VulkanPipelineSystem(core, allocator, VulkanFrameLoop.FramesInFlight);
		if (!pipelines.Init())
			return false;

		Device = new VulkanGraphicsContext(core, swapchain);
		Driver = deviceInfo.Driver;
		return true;
	}

	public bool OnDeviceInit() {
		MeshMgr.MaterialSystem = Singleton<IMaterialSystem>();
		MeshMgr.ShaderAPI = this;
		MeshMgr.Init();

		InitRenderState();

		Msg("Vulkan: device is up (pipelines + meshes + uniform ring; textures are still the white placeholder)\n");
		return true;
	}

	public bool IsActive() => core != null;
	public bool IsUsingGraphics() => IsActive();
	public bool IsDeactivated() => !IsActive();

	public bool ChangeVideoMode(in ShaderDeviceInfo info) {
		if (!info.Windowed) {
			LauncherManager.SetWindowFullScreen(true, info.DisplayMode.Width, info.DisplayMode.Height);
		}
		else {
			if (LauncherManager.IsWindowFullScreen())
				LauncherManager.SetWindowFullScreen(false, info.DisplayMode.Width, info.DisplayMode.Height);
			else
				LauncherManager.SizeWindow(info.DisplayMode.Width, info.DisplayMode.Height);

			LauncherManager.SetWindowBordered(!info.Borderless);
		}

		PresentParameters = info;
		InvokeModeChangeCallbacks();
		return true;
	}

	// Release/ReacquireResources are the engine's lightweight "free device-reset-sensitive
	// resources" pair - they must NOT tear down the device.
	public void ReleaseResources() { }
	public void ReacquireResources() { }

	void DestroyDevice() {
		pipelines?.Dispose();
		pipelines = null;
		foreach ((VulkanBufferResource buffer, _) in retiredBuffers)
			buffer.Dispose();
		retiredBuffers.Clear();
		frameLoop?.Dispose();
		frameLoop = null;
		swapchain?.Dispose();
		swapchain = null;
		allocator?.Dispose();
		allocator = null;
		core?.Dispose();
		core = null;
		Device = null;
	}

	readonly List<Action> ModeChangeCallbacks = [];
	public void AddModeChangeCallBack(Action func) {
		if (!ModeChangeCallbacks.Contains(func))
			ModeChangeCallbacks.Add(func);
	}
	void InvokeModeChangeCallbacks() {
		foreach (Action func in ModeChangeCallbacks)
			func();
	}

	public IShaderDevice GetShaderDevice() => this;
	public GraphicsDriver GetDriver() => Driver;

	internal IShaderInit ShaderLoader => (IShaderInit)ShaderManager;

	/// <summary>Wraps vkCreateShaderModule; 0 on failure or before device init.</summary>
	internal unsafe nint CreateShaderModule(ReadOnlySpan<byte> spirv) {
		if (core == null) {
			Warning("Vulkan: shader module requested before device init\n");
			return 0;
		}
		fixed (byte* code = spirv) {
			ShaderModuleCreateInfo info = new() {
				SType = StructureType.ShaderModuleCreateInfo,
				CodeSize = (nuint)spirv.Length,
				PCode = (uint*)code
			};
			if (core.Vk.CreateShaderModule(core.Device, &info, null, out ShaderModule module) != Result.Success) {
				Warning("Vulkan: vkCreateShaderModule failed\n");
				return 0;
			}
			return unchecked((nint)module.Handle);
		}
	}

	public ReadOnlySpan<char> GetDriverVersionString() => core?.DeviceDescription ?? "Vulkan (no device)";

	public int GetCurrentAdapter() => LauncherManager.GetCurrentDisplayIndex();
	public int GetModeCount(int adapter) => LauncherManager.GetDisplayModeCount(adapter);
	public void GetModeInfo(int adapter, int mode, out ShaderDisplayMode info) => LauncherManager.GetDisplayMode(adapter, mode, out info);

	// ------------------------------------------------------------------
	// Retired buffer queue (GL orphaning replacement)
	// ------------------------------------------------------------------

	readonly List<(VulkanBufferResource Buffer, int FramesLeft)> retiredBuffers = [];

	internal void RetireBuffer(VulkanBufferResource buffer) =>
		retiredBuffers.Add((buffer, VulkanFrameLoop.FramesInFlight + 1));

	void TickRetiredBuffers() {
		for (int i = retiredBuffers.Count - 1; i >= 0; i--) {
			(VulkanBufferResource buffer, int framesLeft) = retiredBuffers[i];
			framesLeft--;
			if (framesLeft <= 0) {
				buffer.Dispose();
				retiredBuffers.RemoveAt(i);
			}
			else
				retiredBuffers[i] = (buffer, framesLeft);
		}
	}

	// ------------------------------------------------------------------
	// Frame lifecycle
	// ------------------------------------------------------------------

	public void BeginFrame() { }
	public void EndFrame() { }

	/// <summary>Lazily opens the frame on the first draw/clear; resets per-frame GPU-visible state.</summary>
	bool EnsureFrameStarted() {
		if (frameLoop == null)
			return false;
		if (frameLoop.FrameActive)
			return true;
		if (frameLoop.NeedsRecreate)
			return false; // handled at Present
		if (!frameLoop.BeginFrame(clearR, clearG, clearB))
			return false;
		OnFrameBegun();
		return true;
	}

	void OnFrameBegun() {
		TickRetiredBuffers();
		pipelines!.BeginFrame(frameLoop!.FrameIndex);

		for (int i = 0; i < (int)VulkanPipelineSystem.UniformBlock.Count; i++)
			uniformDirty[i] = true;
		descriptorsBound = false;
		boundPipeline = default;
		pushFlagsDirty = true;
	}

	public void Present() {
		FlushBufferedPrimitives();

		if (frameLoop == null || swapchain == null)
			return;

		// A frame with no draws (boot, loading) still presents the clear colour.
		if (!frameLoop.FrameActive && frameLoop.BeginFrame(clearR, clearG, clearB))
			OnFrameBegun();

		frameLoop.EndFrameAndPresent();

		if (frameLoop.NeedsRecreate || (Device?.VSyncChanged ?? false)) {
			LauncherManager.DisplayedSize(out int width, out int height);
			if (width > 0 && height > 0 && swapchain.Recreate((uint)width, (uint)height, Device?.VSync ?? true))
				frameLoop.OnSwapchainRecreated();
			Device?.AcknowledgeVSyncChange();
		}

		MeshMgr.DiscardVertexBuffers();
	}

	// ------------------------------------------------------------------
	// Clears
	// ------------------------------------------------------------------

	public void ClearColor3ub(byte r, byte g, byte b) => ClearColor4ub(r, g, b, 255);
	public void ClearColor4ub(byte r, byte g, byte b, byte a) {
		clearR = r / 255.0f;
		clearG = g / 255.0f;
		clearB = b / 255.0f;
	}

	public void ClearBuffers(bool bClearColor, bool bClearDepth, bool bClearStencil, int renderTargetWidth, int renderTargetHeight) {
		if (IsDeactivated())
			return;

		FlushBufferedPrimitives();
		if (!EnsureFrameStarted())
			return;

		// Mirror the GL semantics: -1/-1 (or a match with the viewport) clears everything,
		// otherwise the clear is scissored to the viewport.
		Rect2D rect = default;
		if (renderTargetWidth != -1 || renderTargetHeight != -1) {
			rect = new Rect2D(
				new Offset2D(Math.Max(currentViewport.TopLeftX, 0), Math.Max(currentViewport.TopLeftY, 0)),
				new Extent2D((uint)Math.Max(currentViewport.Width, 0), (uint)Math.Max(currentViewport.Height, 0)));
		}
		frameLoop!.ClearAttachments(bClearColor, bClearDepth, clearR, clearG, clearB, rect);
	}

	public void GetBackBufferDimensions(out int width, out int height) {
		width = PresentParameters.DisplayMode.Width;
		height = PresentParameters.DisplayMode.Height;
	}
	public ImageFormat GetBackBufferFormat() => ImageFormat.RGBA8888;

	// ------------------------------------------------------------------
	// Snapshots
	// ------------------------------------------------------------------

	public IShaderShadow NewShaderShadow(ReadOnlySpan<char> materialName) => new ShadowStateVulkan(this, materialName);
	public bool IsTranslucent(IShaderShadow renderState) => ((ShadowStateVulkan)renderState).State.Blending;
	public bool IsAlphaTested(IShaderShadow renderState) => ((ShadowStateVulkan)renderState).Pixel.IsAlphaTesting != 0;

	ShadowStateVulkan? currentShadow;
	GraphicsBoardState currentBoardState;

	internal void SetCurrentShadow(ShadowStateVulkan shadow) {
		currentShadow = shadow;
		currentBoardState = shadow.State;
	}

	public void InitRenderState() {
		// Board-state defaults (what a GL context effectively starts as + shadow SetDefaultState).
		currentBoardState = default;
		currentBoardState.DepthFunc = ShaderDepthFunc.NearerOrEqual;
		currentBoardState.ColorWrite = true;
		currentBoardState.AlphaWrite = true;
		currentBoardState.DepthWrite = true;
		currentBoardState.DepthTest = true;
		currentBoardState.CullEnable = true;
		currentBoardState.FillMode = ShaderPolyMode.Fill;
		currentBoardState.SourceBlend = ShaderBlendFactor.One;
		currentBoardState.DestinationBlend = ShaderBlendFactor.Zero;
		currentBoardState.BlendOperation = ShaderBlendOp.Add;
		currentBoardState.AlphaSourceBlend = ShaderBlendFactor.One;
		currentBoardState.AlphaDestinationBlend = ShaderBlendFactor.Zero;
		currentBoardState.AlphaBlendOperation = ShaderBlendOp.Add;

		LauncherManager.DisplayedSize(out int width, out int height);
		ShaderViewport viewport = new(0, 0, width, height);
		SetViewports(new(ref viewport));
	}

	public void SetDefaultState() { }
	public bool SetBoardState(in GraphicsBoardState state) {
		currentBoardState = state;
		return true;
	}

	// ------------------------------------------------------------------
	// Uniform blocks (CPU side of the ring)
	// ------------------------------------------------------------------

	static readonly int BlockCount = (int)VulkanPipelineSystem.UniformBlock.Count;
	readonly byte[][] uniformBlocks = CreateUniformBlocks();
	readonly bool[] uniformDirty = new bool[BlockCount];
	readonly uint[] uniformOffsets = new uint[BlockCount];
	bool descriptorsBound;

	static byte[][] CreateUniformBlocks() {
		byte[][] blocks = new byte[(int)VulkanPipelineSystem.UniformBlock.Count][];
		for (int i = 0; i < blocks.Length; i++)
			blocks[i] = new byte[VulkanPipelineSystem.BlockSizes[i]];

		// Bones start as identity, like the GL backend's bone UBO.
		Span<Matrix4x4> bones = MemoryMarshal.Cast<byte, Matrix4x4>(blocks[(int)VulkanPipelineSystem.UniformBlock.Bones].AsSpan());
		for (int i = 0; i < bones.Length; i++)
			bones[i] = Matrix4x4.Identity;
		return blocks;
	}

	void MarkDirty(VulkanPipelineSystem.UniformBlock block) => uniformDirty[(int)block] = true;

	Span<T> Block<T>(VulkanPipelineSystem.UniformBlock block) where T : unmanaged =>
		MemoryMarshal.Cast<byte, T>(uniformBlocks[(int)block].AsSpan());

	// ------------------------------------------------------------------
	// Matrices
	// ------------------------------------------------------------------

	MaterialMatrixMode currentMatrixMode;
	readonly Matrix4x4[] Matrices = [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];

	public void MatrixMode(MaterialMatrixMode mode) => currentMatrixMode = mode;

	// Like the GL backend, loading a matrix does NOT flush buffered primitives - the UBO contents
	// are only read at draw time, and SyncMatrices before each lock keeps batches coherent.
	public void LoadMatrix(in Matrix4x4 matrix) {
		Matrices[(int)currentMatrixMode] = matrix;
		UploadMatrices();
	}

	public void LoadIdentity() {
		Matrices[(int)currentMatrixMode] = Matrix4x4.Identity;
		UploadMatrices();
	}

	public void GetMatrix(MaterialMatrixMode matrixMode, out Matrix4x4 dst) => dst = Matrices[(int)matrixMode];

	public void PushMatrix() {
		FlushBufferedPrimitives();
	}

	public void PopMatrix() {
		FlushBufferedPrimitives();
	}

	/// <summary>
	/// GL clip space has z in [-1,1], Vulkan [0,1]. The engine's matrices are GL-flavoured, and
	/// the upload convention (matching GL) is transpose-then-upload, after which the GLSL-side
	/// math matrix M satisfies M[row][col] == Matrix4x4.M(row+1)(col+1). The clip z row is
	/// therefore fields M31..M34 and the w row M41..M44; remap z' = 0.5*(z + w) there.
	/// Y stays untouched - the negative-height viewport handles it.
	/// </summary>
	static Matrix4x4 FixupProjection(in Matrix4x4 p) {
		Matrix4x4 m = p;
		m.M31 = 0.5f * (p.M31 + p.M41);
		m.M32 = 0.5f * (p.M32 + p.M42);
		m.M33 = 0.5f * (p.M33 + p.M43);
		m.M34 = 0.5f * (p.M34 + p.M44);
		return m;
	}

	void UploadMatrices() {
		// GL parity: matrices are transposed on their way into the UBO (see ShaderAPIGl46.LoadMatrix).
		Span<Matrix4x4> block = Block<Matrix4x4>(VulkanPipelineSystem.UniformBlock.Matrices);
		block[(int)MaterialMatrixMode.View] = Matrix4x4.Transpose(Matrices[(int)MaterialMatrixMode.View]);
		block[(int)MaterialMatrixMode.Projection] = Matrix4x4.Transpose(FixupProjection(in Matrices[(int)MaterialMatrixMode.Projection]));
		block[(int)MaterialMatrixMode.Model] = Matrix4x4.Transpose(Matrices[(int)MaterialMatrixMode.Model]);
		MarkDirty(VulkanPipelineSystem.UniformBlock.Matrices);
	}

	// ------------------------------------------------------------------
	// Bones
	// ------------------------------------------------------------------

	int numBones;

	public void SetNumBoneWeights(int numBones) => this.numBones = numBones;
	public int GetCurrentNumBones() => numBones;

	public void LoadBoneMatrix(int boneIndex, in Matrix3x4 matrix) {
		if (IsDeactivated() || boneIndex < 0 || boneIndex >= Studio.MAXSTUDIOBONES)
			return;
		Matrix4x4 transposed = Matrix4x4.Transpose(matrix);
		Block<Matrix4x4>(VulkanPipelineSystem.UniformBlock.Bones)[boneIndex] = transposed;
		MarkDirty(VulkanPipelineSystem.UniformBlock.Bones);
	}

	public void SetSkinningMatrices() {
		if (numBones == 0)
			return;

		GetMatrix(MaterialMatrixMode.Model, out Matrix4x4 modelMatrix);
		Block<Matrix4x4>(VulkanPipelineSystem.UniformBlock.Bones)[0] = Matrix4x4.Transpose(modelMatrix);
		MarkDirty(VulkanPipelineSystem.UniformBlock.Bones);
	}

	// ------------------------------------------------------------------
	// Shader constants
	// ------------------------------------------------------------------

	void SetShaderConstantInternal(VulkanPipelineSystem.UniformBlock block, int var, Span<float> vec) {
		Span<float> dst = Block<float>(block);
		int start = var * 4;
		if (start + vec.Length > dst.Length) {
			Assert(false);
			return;
		}
		Span<float> target = dst.Slice(start, vec.Length);
		if (vec.SequenceEqual(target))
			return;
		vec.CopyTo(target);
		MarkDirty(block);
	}

	public void SetVertexShaderConstant(int var, Span<float> vec) =>
		SetShaderConstantInternal(VulkanPipelineSystem.UniformBlock.VsConstants, var, vec);

	public void SetPixelShaderConstant(int var, Span<float> vec) =>
		SetShaderConstantInternal(VulkanPipelineSystem.UniformBlock.PsConstants, var, vec);

	// ------------------------------------------------------------------
	// Push constants ("flags")
	// ------------------------------------------------------------------

	int pushFlags;
	bool pushFlagsDirty = true;
	const int UniformFlags = 0;

	public int LocateShaderUniform(ReadOnlySpan<char> name) {
		if (name.Length > 0 && name[0] == '$')
			name = name[1..];
		// The only loose uniform the vk13 shaders have is the push-constant flags; GL's sampler
		// unit assignments (e.g. "lightmaptexture" -> 1) are fixed set-1 bindings here.
		return name.SequenceEqual("flags") ? UniformFlags : -1;
	}

	public void SetShaderUniform(int uniform, int integer) {
		if (uniform != UniformFlags)
			return;
		if (pushFlags != integer) {
			pushFlags = integer;
			pushFlagsDirty = true;
		}
	}

	public void SetShaderUniform(int uniform, float fl) { }
	public void SetShaderUniform(int uniform, ReadOnlySpan<float> flConsts) { }

	public void SetShaderUniform(IMaterialVar variable) {
		int uniform = LocateShaderUniform(variable.GetName());
		if (uniform == -1)
			return;
		switch (variable.GetVarType()) {
			case MaterialVarType.Int: SetShaderUniform(uniform, variable.GetIntValue()); break;
			case MaterialVarType.Float: SetShaderUniform(uniform, variable.GetFloatValue()); break;
		}
	}

	// ------------------------------------------------------------------
	// Shader binding
	// ------------------------------------------------------------------

	VertexShaderHandle activeVertexShader = VertexShaderHandle.INVALID;
	PixelShaderHandle activePixelShader = PixelShaderHandle.INVALID;

	public void BindVertexShader(in VertexShaderHandle vertexShader) => activeVertexShader = vertexShader;
	public void BindPixelShader(in PixelShaderHandle pixelShader) => activePixelShader = pixelShader;
	public void SetVertexShaderIndex(int index) { } // combos not implemented (Phase 5)
	public void SetPixelShaderIndex(int index) { }
	public int GetDynamicComboScale(ShaderType type, ReadOnlySpan<char> name) => 1;
	public nint GetCurrentProgram() => 0;
	public void SetVertexShaderStateAmbientLightCube() { }
	public void CommitVertexShaderLighting() { }
	public void InvalidateDelayedShaderConstants() { }

	// ------------------------------------------------------------------
	// Draw path
	// ------------------------------------------------------------------

	MeshVulkan? RenderMesh;
	IMaterialInternal? Material;
	Pipeline boundPipeline;
	bool readyToDraw;
	bool warnedInvalidShaders;

	public void DrawMesh(IMesh imesh) {
		MeshVulkan mesh = (MeshVulkan)imesh;
		RenderMesh = mesh;
		Material!.DrawMesh(VertexCompressionType.None);
		RenderMesh = null;
	}

	public void Bind(IMaterial? material) {
		IMaterialInternal? matInt = (IMaterialInternal?)material;

		bool materialChanged;
		if (Material != null && matInt != null && Material.InMaterialPage() && matInt.InMaterialPage()) {
			materialChanged = Material.GetMaterialPage() != matInt.GetMaterialPage();
		}
		else {
			materialChanged = (Material != matInt) || (Material != null && Material.InMaterialPage()) || (matInt != null && matInt.InMaterialPage());
		}

		if (materialChanged) {
			FlushBufferedPrimitives();
			Material = matInt;
		}
	}

	public void RenderPass() {
		if (IsDeactivated())
			return;

		if (RenderMesh != null) {
			readyToDraw = PrepareDraw(RenderMesh.GetVertexFormat(), RenderMesh.GetPrimitiveType());
			RenderMesh.RenderPass();
			readyToDraw = false;
		}
	}

	public void FlushBufferedPrimitives() {
		Assert(RenderMesh == null);
		MeshMgr?.Flush();
	}

	unsafe bool PrepareDraw(VertexFormat format, MaterialPrimitiveType topology) {
		if (!EnsureFrameStarted())
			return false;

		if (!activeVertexShader.IsValid() || activeVertexShader.Handle == 0 ||
			!activePixelShader.IsValid() || activePixelShader.Handle == 0) {
			if (!warnedInvalidShaders) {
				warnedInvalidShaders = true;
				Warning("Vulkan: draw skipped - material has no loaded SPIR-V shaders (more will be skipped silently)\n");
			}
			return false;
		}

		Vk vk = core!.Vk;
		CommandBuffer cmd = frameLoop!.Cmd;

		// Pipeline
		VulkanPipelineKey key = new() {
			State = currentBoardState,
			VertexShader = activeVertexShader.Handle,
			PixelShader = activePixelShader.Handle,
			Format = format,
			Topology = topology,
			ColorFormat = swapchain!.ImageFormat,
			DepthFormat = VulkanFrameLoop.DepthFormat
		};
		Pipeline pipeline = pipelines!.GetPipeline(in key);
		if (pipeline.Handle == 0)
			return false;
		if (pipeline.Handle != boundPipeline.Handle) {
			vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
			boundPipeline = pipeline;
		}

		// Shared uniform blocks come from the active snapshot
		UpdateSharedBlocksFromShadow();

		// Upload dirty blocks into the ring, rebind descriptor sets when offsets moved
		bool offsetsChanged = false;
		for (int i = 0; i < BlockCount; i++) {
			if (!uniformDirty[i])
				continue;
			uniformOffsets[i] = pipelines.AllocUniform(uniformBlocks[i]);
			uniformDirty[i] = false;
			offsetsChanged = true;
		}

		if (offsetsChanged || !descriptorsBound) {
			DescriptorSet* sets = stackalloc DescriptorSet[2] { pipelines.CurrentSet0, pipelines.Set1WhiteSet };
			fixed (uint* offsets = uniformOffsets)
				vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, pipelines.PipelineLayout, 0, 2, sets, (uint)BlockCount, offsets);
			descriptorsBound = true;
		}

		// Push constants
		if (pushFlagsDirty) {
			int flags = pushFlags;
			vk.CmdPushConstants(cmd, pipelines.PipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, sizeof(int), &flags);
			pushFlagsDirty = false;
		}

		ApplyViewportAndScissor(vk, cmd);
		return true;
	}

	void UpdateSharedBlocksFromShadow() {
		VertexSharedStateVulkan vertexShared = currentShadow?.Vertex ?? default;
		vertexShared.NumBones = numBones;
		Span<VertexSharedStateVulkan> vertexBlock = Block<VertexSharedStateVulkan>(VulkanPipelineSystem.UniformBlock.VertexShared);
		if (!vertexBlock[0].Equals(vertexShared)) {
			vertexBlock[0] = vertexShared;
			MarkDirty(VulkanPipelineSystem.UniformBlock.VertexShared);
		}

		PixelSharedStateVulkan pixelShared = currentShadow?.Pixel ?? default;
		Span<PixelSharedStateVulkan> pixelBlock = Block<PixelSharedStateVulkan>(VulkanPipelineSystem.UniformBlock.PixelShared);
		if (!pixelBlock[0].Equals(pixelShared)) {
			pixelBlock[0] = pixelShared;
			MarkDirty(VulkanPipelineSystem.UniformBlock.PixelShared);
		}
	}

	unsafe void ApplyViewportAndScissor(Vk vk, CommandBuffer cmd) {
		swapchainExtent(out uint maxWidth, out uint maxHeight);

		int x = currentViewport.TopLeftX, y = currentViewport.TopLeftY;
		int width = currentViewport.Width, height = currentViewport.Height;
		if (width <= 0 || height <= 0) {
			x = y = 0;
			width = (int)maxWidth;
			height = (int)maxHeight;
		}

		// Negative height = GL-style clip space (Y up), rectangle anchored at the top-left position.
		Viewport viewport = new() {
			X = x,
			Y = y + height,
			Width = width,
			Height = -height,
			MinDepth = currentViewport.MinZ,
			MaxDepth = currentViewport.MaxZ
		};
		vk.CmdSetViewport(cmd, 0, 1, &viewport);

		Rect2D scissor = new(new Offset2D(Math.Max(x, 0), Math.Max(y, 0)), new Extent2D((uint)width, (uint)height));
		vk.CmdSetScissor(cmd, 0, 1, &scissor);
	}

	void swapchainExtent(out uint width, out uint height) {
		width = swapchain?.Extent.Width ?? 0;
		height = swapchain?.Extent.Height ?? 0;
	}

	internal unsafe bool BindMeshBuffers(VertexBufferVulkan vertexBuffer, IndexBufferVulkan indexBuffer) {
		if (!readyToDraw || frameLoop is not { FrameActive: true })
			return false;

		VulkanBufferResource? vb = vertexBuffer.Buffer;
		VulkanBufferResource? ib = indexBuffer.Buffer;
		if (vb == null || ib == null)
			return false;

		Vk vk = core!.Vk;
		CommandBuffer cmd = frameLoop.Cmd;

		VkBuffer* buffers = stackalloc VkBuffer[2] { vb.Handle, pipelines!.ZeroVertexBuffer };
		ulong* offsets = stackalloc ulong[2] { 0, 0 };
		vk.CmdBindVertexBuffers(cmd, 0, 2, buffers, offsets);
		vk.CmdBindIndexBuffer(cmd, ib.Handle, 0, IndexType.Uint16);
		return true;
	}

	internal void DrawIndexed(int indexCount, int firstIndex) {
		if (!readyToDraw || frameLoop is not { FrameActive: true })
			return;
		core!.Vk.CmdDrawIndexed(frameLoop.Cmd, (uint)indexCount, 1, (uint)firstIndex, 0, 0);
	}

	public void ShadeMode(ShadeMode mode) { }
	public bool InEditorMode() => false;

	// ------------------------------------------------------------------
	// Meshes
	// ------------------------------------------------------------------

	public IMesh CreateStaticMesh(VertexFormat format, ReadOnlySpan<char> textureGroup, IMaterial? material) =>
		MeshMgr.CreateStaticMesh(format, textureGroup, material);
	public void DestroyStaticMesh(IMesh mesh) => MeshMgr.DestroyStaticMesh(mesh);
	public IMesh GetDynamicMesh(IMaterial material, int nCurrentBoneCount, bool buffered, IMesh? vertexOverride, IMesh? indexOverride) {
		Assert(material == null || material.IsRealTimeVersion());
		return MeshMgr.GetDynamicMesh(material, 0, nCurrentBoneCount, buffered, vertexOverride, indexOverride);
	}
	public int GetCurrentDynamicVBSize() => (1024 + 512) * 1024;
	public int GetMaxVerticesToRender(IMaterial material) => MeshMgr.GetMaxVerticesToRender(material);
	public int GetMaxIndicesToRender() => MeshMgr.GetMaxIndicesToRender();

	// ------------------------------------------------------------------
	// Textures (still placeholder - Phase 4 continues next session)
	// ------------------------------------------------------------------

	ShaderAPITextureHandle_t nextTextureHandle = 1;
	readonly HashSet<ShaderAPITextureHandle_t> textureHandles = [];

	public ShaderAPITextureHandle_t CreateTexture(int width, int height, int depth, ImageFormat imageFormat, ushort mipCount, int copies,
		CreateTextureFlags creationFlags, ReadOnlySpan<char> debugName, ReadOnlySpan<char> textureGroup) {
		ShaderAPITextureHandle_t handle = nextTextureHandle++;
		textureHandles.Add(handle);
		return handle;
	}

	public void CreateTextures(Span<ShaderAPITextureHandle_t> handles, int count, int width, int height, int depth, ImageFormat imageFormat,
		ushort mipCount, int copies, CreateTextureFlags creationFlags, ReadOnlySpan<char> debugName, ReadOnlySpan<char> textureGroup) {
		for (int i = 0; i < count; i++)
			handles[i] = CreateTexture(width, height, depth, imageFormat, mipCount, copies, creationFlags, debugName, textureGroup);
	}

	public ShaderAPITextureHandle_t CreateDepthTexture(ImageFormat imageFormat, ushort width, ushort height, Span<char> debugName, bool texture)
		=> CreateTexture(width, height, 1, imageFormat, 1, 1, CreateTextureFlags.DepthBuffer, debugName, default);

	public bool IsTexture(ShaderAPITextureHandle_t handle) => textureHandles.Contains(handle);
	public void DeleteTexture(ShaderAPITextureHandle_t handle) => textureHandles.Remove(handle);
	public ImageFormat GetNearestSupportedFormat(ImageFormat fmt, bool filteringRequired = true) => fmt;
	public bool CanDownloadTextures() => IsActive();

	public void ModifyTexture(int handle) { }
	public void TexImage2D(int mip, int face, ImageFormat dstFormat, int zOffset, int width, int height, ImageFormat srcFormat, bool srcIsTiled, Span<byte> imageData) { }
	public void TexSubImage2D(int mip, int face, int x, int y, int z, int width, int height, ImageFormat srcFormat, int srcStride, Span<byte> imageData) { }
	public void TexImageFromVTF(IVTFTexture? vtfTexture, int i) { }
	public void TexWrap(TexCoordComponent coord, TexWrapMode wrapMode) { }
	public void TexMinFilter(TexFilterMode mode) { }
	public void TexMagFilter(TexFilterMode mode) { }
	public bool TexLock(int level, int cubeFaceID, int xOffset, int yOffset, int width, int height, ref PixelWriter writer) => false;
	public bool TexLock(int level, int cubeFaceID, int xOffset, int yOffset, int width, int height, ref PixelWriterMem writer) => false;
	public void TexUnlock() { }
	public void BindTexture(Sampler sampler, ShaderAPITextureHandle_t textureHandle) { }
	public void BindStandardTexture(Sampler sampler, StandardTextureId id) { }
	public void SetStandardTextureHandle(StandardTextureId id, int handle) { }

	// ------------------------------------------------------------------
	// Render targets
	// ------------------------------------------------------------------

	bool warnedRenderTargets;

	public bool DoRenderTargetsNeedSeparateDepthBuffer() => false;
	public void EnableLinearColorSpaceFrameBuffer(bool v) { }
	public void SetRenderTargetEx(int rt,
		ShaderAPITextureHandle_t colorTextureHandle = (ShaderAPITextureHandle_t)ShaderRenderTarget.Backbuffer,
		ShaderAPITextureHandle_t depthTextureHandle = (ShaderAPITextureHandle_t)ShaderRenderTarget.Depthbuffer) {
		if (colorTextureHandle >= 0 || depthTextureHandle >= 0) {
			if (!warnedRenderTargets) {
				warnedRenderTargets = true;
				Warning("Vulkan: texture render targets not implemented yet; drawing to the backbuffer instead\n");
			}
		}
	}
	public bool SupportsShadowDepthTextures() => true;
	public ImageFormat GetShadowDepthTextureFormat() => ImageFormat.NV_DST24;
	public ImageFormat GetNullTextureFormat() => ImageFormat.NV_NULL;

	// ------------------------------------------------------------------
	// Viewports / scissor / stencil
	// ------------------------------------------------------------------

	ShaderViewport currentViewport;
	public void SetViewports(ReadOnlySpan<ShaderViewport> viewports) {
		if (viewports.Length > 0)
			currentViewport = viewports[0];
	}
	public void GetViewports(Span<ShaderViewport> viewports) {
		if (viewports.Length > 0)
			viewports[0] = currentViewport;
	}
	public void SetScissorRect(int left, int top, int right, int bottom, bool enableScissor) { }

	public void SetStencilEnable(bool onoff) { }
	public void SetStencilFailOperation(StencilOperation op) { }
	public void SetStencilZFailOperation(StencilOperation op) { }
	public void SetStencilPassOperation(StencilOperation op) { }
	public void SetStencilCompareFunction(StencilComparisonFunction cmpfn) { }
	public void SetStencilReferenceValue(int reference) { }
	public void SetStencilTestMask(uint msk) { }
	public void SetStencilWriteMask(uint msk) { }

	// ------------------------------------------------------------------
	// Lighting / fog
	// ------------------------------------------------------------------

	public void SetAmbientLightCube(ReadOnlySpan<Vector4> cube) { }
	public void SetLightingOrigin(Vector3 lightingOrigin) { }
	public void SetAmbientLight(float r, float g, float b) { }
	public void SetLight(int lightNum, in LightDesc desc) { }
	public void DisableAllLocalLights() { }
	public int GetMaxLights() => 4;
	public void GetLightState(out LightState state) => state = default;
	public void SetFlashlightStateEx(in FlashlightState state, in Matrix4x4 worldToTexture, ITexture? flashlightDepthTexture) { }
	public bool InFlashlightMode() => false;
	public MaterialFogMode GetSceneFogMode() => MaterialFogMode.None;

	public float LinearToGamma_HardwareSpecific(float fLookupResult) => fLookupResult;
	public void SetLinearToGammaConversionTextures(int linearToGammaTableTextureHandle, int linearToGammaIdentityTableTextureHandle) { }

	// ------------------------------------------------------------------
	// IDebugTextureInfo
	// ------------------------------------------------------------------

	public void EnableDebugTextureList(bool enable) { }
	public void EnableGetAllTextures(bool enable) { }
	public Source.Common.Formats.Keyvalues.KeyValues? GetDebugTextureList() => null;
	public int GetTextureMemoryUsed(TextureMemoryType textureMemory) => 0;
	public bool IsDebugTextureListFresh(int numFramesAllowed = 1) => false;
	public bool SetDebugTextureRendering(bool enable) => false;
}
