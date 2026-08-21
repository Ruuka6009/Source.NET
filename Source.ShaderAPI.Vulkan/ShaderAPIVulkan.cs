using Microsoft.Extensions.DependencyInjection;

using Silk.NET.Vulkan;

using Source.Bitmap;
using Source.Common;
using Source.Common.Bitmap;
using Source.Common.Launcher;
using Source.Common.MaterialSystem;
using Source.Common.Mathematics;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;

using System.Numerics;
using System.Runtime.InteropServices;

using Sampler = Source.Common.MaterialSystem.Sampler;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Vulkan implementation of IShaderAPI/IShaderDevice. Pipelines are resolved from shadow-state
/// snapshots; see VULKAN_TODO.md for what is and isn't implemented.
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
	VulkanTextureManager? textureManager;
	MeshMgrVulkan MeshMgr = null!;

	internal GraphicsDriver Driver = GraphicsDriver.Vulkan13;
	ShaderDeviceInfo PresentParameters;

	float clearR = 0.10f, clearG = 0.20f, clearB = 0.40f;

	ILauncherManager LauncherManager => services.GetRequiredService<ILauncherManager>();

	internal VulkanCore? Core => core;
	internal VulkanMemoryAllocator? Allocator => allocator;

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

		textureManager = new VulkanTextureManager(core, allocator);
		if (!textureManager.Init())
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

		Msg("Vulkan: device is up (pipelines, meshes, uniform ring, textures)\n");
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

	// These free device-reset-sensitive resources; they must not tear down the device.
	public void ReleaseResources() { }
	public void ReacquireResources() { }

	void DestroyDevice() {
		foreach (VulkanTexture texture in textures.Values)
			textureManager?.Destroy(texture);
		textures.Clear();
		textureManager?.Dispose();
		textureManager = null;
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

		// Copies can't be recorded inside a rendering pass.
		textureManager?.Flush();

		if (!frameLoop.BeginFrame())
			return false;
		OnFrameBegun();
		return true;
	}

	/// <summary>Frame started and a rendering pass open on the current render target.</summary>
	bool EnsureRendering() {
		if (!EnsureFrameStarted())
			return false;
		if (frameLoop!.RenderingActive && !renderTargetDirty)
			return true;

		OpenRenderPass();
		return frameLoop.RenderingActive;
	}

	void OnFrameBegun() {
		TickRetiredBuffers();
		textureManager!.TickRetiredViews();
		pipelines!.BeginFrame(frameLoop!.FrameIndex);

		for (int i = 0; i < (int)VulkanPipelineSystem.UniformBlock.Count; i++)
			uniformDirty[i] = true;
		descriptorsBound = false;
		boundPipeline = default;
		boundTextureSet = default;
		pushConstantsDirty = true;
		backbufferNeedsClear = true;
		renderTargetDirty = true;
	}

	public void Present() {
		FlushBufferedPrimitives();

		if (frameLoop == null || swapchain == null)
			return;

		if (!frameLoop.FrameActive && frameLoop.BeginFrame())
			OnFrameBegun();

		SetRenderTargetEx(0);
		EnsureRendering();

		frameLoop.EndFrameAndPresent();

		if (frameLoop.NeedsRecreate || (Device?.VSyncChanged ?? false)) {
			LauncherManager.DisplayedSize(out int width, out int height);
			if (width > 0 && height > 0 && swapchain.Recreate((uint)width, (uint)height, Device?.VSync ?? true))
				frameLoop.OnSwapchainRecreated();
			Device?.AcknowledgeVSyncChange();
		}

		MeshMgr.DiscardVertexBuffers();
	}

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
		if (!EnsureRendering())
			return;

		// -1/-1 clears everything; anything else is scissored to the viewport.
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

	public IShaderShadow NewShaderShadow(ReadOnlySpan<char> materialName) => new ShadowStateVulkan(this, materialName);
	public bool IsTranslucent(IShaderShadow renderState) => ((ShadowStateVulkan)renderState).State.Blending;
	public bool IsAlphaTested(IShaderShadow renderState) => ((ShadowStateVulkan)renderState).Pixel.IsAlphaTesting != 0;

	ShadowStateVulkan? currentShadow;
	GraphicsBoardState currentBoardState;

	internal void SetCurrentShadow(ShadowStateVulkan shadow) {
		currentShadow = shadow;
		currentBoardState = shadow.State;

		// Each snapshot re-declares its own routing; don't let the last material's leak in.
		Array.Fill(samplerForBinding, -1);
		lastTextureSetValid = false;
		pushConstantsDirty = true;
	}

	public void InitRenderState() {
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

	static readonly int BlockCount = (int)VulkanPipelineSystem.UniformBlock.Count;
	readonly byte[][] uniformBlocks = CreateUniformBlocks();
	readonly bool[] uniformDirty = new bool[BlockCount];
	readonly uint[] uniformOffsets = new uint[BlockCount];
	bool descriptorsBound;

	static byte[][] CreateUniformBlocks() {
		byte[][] blocks = new byte[(int)VulkanPipelineSystem.UniformBlock.Count][];
		for (int i = 0; i < blocks.Length; i++)
			blocks[i] = new byte[VulkanPipelineSystem.BlockSizes[i]];

		Span<Matrix4x4> bones = MemoryMarshal.Cast<byte, Matrix4x4>(blocks[(int)VulkanPipelineSystem.UniformBlock.Bones].AsSpan());
		for (int i = 0; i < bones.Length; i++)
			bones[i] = Matrix4x4.Identity;
		return blocks;
	}

	void MarkDirty(VulkanPipelineSystem.UniformBlock block) => uniformDirty[(int)block] = true;

	Span<T> Block<T>(VulkanPipelineSystem.UniformBlock block) where T : unmanaged =>
		MemoryMarshal.Cast<byte, T>(uniformBlocks[(int)block].AsSpan());

	MaterialMatrixMode currentMatrixMode;
	readonly Matrix4x4[] Matrices = [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];

	public void MatrixMode(MaterialMatrixMode mode) => currentMatrixMode = mode;

	// Deliberately does not flush buffered primitives, matching the GL backend.
	public void LoadMatrix(in Matrix4x4 matrix) {
		Matrices[(int)currentMatrixMode] = matrix;
		UploadMatrices();
		if (currentMatrixMode == MaterialMatrixMode.View)
			UpdateWorldSpaceCameraPosition();
	}

	public void LoadIdentity() {
		Matrices[(int)currentMatrixMode] = Matrix4x4.Identity;
		UploadMatrices();
		if (currentMatrixMode == MaterialMatrixMode.View)
			UpdateWorldSpaceCameraPosition();
	}

	/// <summary>
	/// Camera position in world space, which shaders read from vs_const[CameraPos] for view
	/// vectors (cubemap reflections, water fresnel). GL derives this whenever the view matrix is
	/// loaded; without it the constant stays zero and every view vector points at the origin.
	/// </summary>
	void UpdateWorldSpaceCameraPosition() {
		ref Matrix4x4 view = ref Matrices[(int)MaterialMatrixMode.View];

		float x = -(view.M41 * view.M11 + view.M42 * view.M12 + view.M43 * view.M13);
		float y = -(view.M41 * view.M21 + view.M42 * view.M22 + view.M43 * view.M23);
		float z = -(view.M41 * view.M31 + view.M42 * view.M32 + view.M43 * view.M33);

		// Some pixel shaders divide by z, so keep it away from zero (GL does the same).
		if (MathF.Abs(z) <= 0.00001f)
			z = 0.01f;

		Span<float> cameraPos = [x, y, z, 0.0f];
		SetVertexShaderConstant(VertexShaderConst.CameraPos, cameraPos);
	}

	public void GetMatrix(MaterialMatrixMode matrixMode, out Matrix4x4 dst) => dst = Matrices[(int)matrixMode];

	public void PushMatrix() {
		FlushBufferedPrimitives();
	}

	public void PopMatrix() {
		FlushBufferedPrimitives();
	}

	/// <summary>
	/// Remaps GL clip space (z in [-1,1]) to Vulkan's [0,1]. Matrices are transposed on upload,
	/// so the clip z row is M31..M34 and w is M41..M44. Y is handled by the negative viewport.
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
		// Transposed on upload, as in ShaderAPIGl46.LoadMatrix.
		Span<Matrix4x4> block = Block<Matrix4x4>(VulkanPipelineSystem.UniformBlock.Matrices);
		block[(int)MaterialMatrixMode.View] = Matrix4x4.Transpose(Matrices[(int)MaterialMatrixMode.View]);
		block[(int)MaterialMatrixMode.Projection] = Matrix4x4.Transpose(FixupProjection(in Matrices[(int)MaterialMatrixMode.Projection]));
		block[(int)MaterialMatrixMode.Model] = Matrix4x4.Transpose(Matrices[(int)MaterialMatrixMode.Model]);
		MarkDirty(VulkanPipelineSystem.UniformBlock.Matrices);
	}

	int numBones;

	public void SetNumBoneWeights(int numBones) => this.numBones = numBones;
	public int GetCurrentNumBones() => numBones;

	public void LoadBoneMatrix(int boneIndex, in Matrix3x4 matrix) {
		if (IsDeactivated() || boneIndex < 0 || boneIndex >= Studio.MAXSTUDIOBONES)
			return;
		Matrix4x4 transposed = Matrix4x4.Transpose(matrix);
		Block<Matrix4x4>(VulkanPipelineSystem.UniformBlock.Bones)[boneIndex] = transposed;
		MarkDirty(VulkanPipelineSystem.UniformBlock.Bones);

		// GL parity: bone 0 doubles as the model matrix, which is what the unskinned path in the
		// vertex shaders transforms by. Without this, models draw at the wrong place.
		if (boneIndex == 0) {
			MatrixMode(MaterialMatrixMode.Model);
			LoadMatrix(matrix);
		}
	}

	public void SetSkinningMatrices() {
		if (numBones == 0)
			return;

		GetMatrix(MaterialMatrixMode.Model, out Matrix4x4 modelMatrix);
		Block<Matrix4x4>(VulkanPipelineSystem.UniformBlock.Bones)[0] = Matrix4x4.Transpose(modelMatrix);
		MarkDirty(VulkanPipelineSystem.UniformBlock.Bones);
	}

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

	int pushFlags;
	bool pushConstantsDirty = true;
	const int UniformFlags = 0;
	const int UniformTextureBase = 0x1000;

	/// <summary>set-1 binding for a texture name, matching the *_vk13 shaders.</summary>
	static int TextureBindingFor(ReadOnlySpan<char> name) {
		if (name.SequenceEqual("basetexture")) return 0;
		if (name.SequenceEqual("envmap")) return 1;
		if (name.SequenceEqual("envmapmask")) return 2;
		if (name.SequenceEqual("lightmaptexture")) return 3;
		if (name.SequenceEqual("bumpmap")) return 4;
		if (name.SequenceEqual("basetexture2")) return 5;
		return -1;
	}

	/// <summary>Which sampler unit currently feeds each set-1 binding; -1 when the shader did not say.</summary>
	readonly int[] samplerForBinding = new int[VulkanPipelineSystem.TextureBindingCount];

	public int LocateShaderUniform(ReadOnlySpan<char> name) {
		if (name.Length > 0 && name[0] == '$')
			name = name[1..];

		if (name.SequenceEqual("flags"))
			return UniformFlags;

		// The vk13 shaders have fixed bindings, so this runs in reverse of GL: the shader tells
		// us which unit it put a texture on, and we route that unit to the binding it samples.
		int binding = TextureBindingFor(name);
		return binding >= 0 ? UniformTextureBase + binding : -1;
	}

	public void SetShaderUniform(int uniform, int integer) {
		if (uniform == UniformFlags) {
			if (pushFlags != integer) {
				pushFlags = integer;
				pushConstantsDirty = true;
			}
			return;
		}

		if (uniform >= UniformTextureBase) {
			int binding = uniform - UniformTextureBase;
			if (binding < samplerForBinding.Length && samplerForBinding[binding] != integer) {
				samplerForBinding[binding] = integer;
				lastTextureSetValid = false;
			}
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

	VertexShaderHandle activeVertexShader = VertexShaderHandle.INVALID;
	PixelShaderHandle activePixelShader = PixelShaderHandle.INVALID;

	public void BindVertexShader(in VertexShaderHandle vertexShader) => activeVertexShader = vertexShader;
	public void BindPixelShader(in PixelShaderHandle pixelShader) => activePixelShader = pixelShader;

	int dynamicComboBits;

	public void SetVertexShaderIndex(int index) => SetDynamicComboIndex(ShaderType.Vertex, index);
	public void SetPixelShaderIndex(int index) => SetDynamicComboIndex(ShaderType.Pixel, index);

	void SetDynamicComboIndex(ShaderType type, int index) {
		if (currentShadow == null)
			return;

		int bits = currentShadow.UnpackDynamic(type, index);
		if (bits != dynamicComboBits) {
			dynamicComboBits = bits;
			pushConstantsDirty = true;
		}
	}

	public int GetDynamicComboScale(ShaderType type, ReadOnlySpan<char> name) =>
		currentShadow?.GetDynamicComboScale(type, name) ?? 0;

	internal VulkanShaderCombos Combos => ((ShaderSystemVulkan)ShaderManager).Combos;
	public nint GetCurrentProgram() => 0;
	public void SetVertexShaderStateAmbientLightCube() {
		SetVertexShaderConstant(VertexShaderConst.AmbientLight, MemoryMarshal.Cast<Vector4, float>(ambientLightCube.AsSpan()));
	}

	/// <summary>
	/// Packs the enabled lights into vs_const at VertexShaderConst.Lights, five vec4s each, in the
	/// layout common_vs_vk13.glsl reads back (colour+directional flag, direction+spot flag,
	/// position, spot params, attenuation).
	/// </summary>
	public void CommitVertexShaderLighting() {
		if (!lightingDirty)
			return;
		lightingDirty = false;

		SetVertexShaderStateAmbientLightCube();

		Span<Vector4> lightState = stackalloc Vector4[5];
		int slot = 0;
		for (int i = 0; i < MaxNumLights; i++) {
			if (!lightEnabled[i])
				continue;

			ref LightDesc light = ref lightDescs[i];

			float w = light.Type == LightType.Directional ? 1.0f : 0.0f;
			lightState[0] = new Vector4(light.Color, w);

			w = light.Type == LightType.Spot ? 1.0f : 0.0f;
			lightState[1] = new Vector4(light.Direction, w);

			lightState[2] = new Vector4(light.Position, 1.0f);

			if (light.Type == LightType.Spot) {
				float stopDot = MathF.Cos(light.Theta * 0.5f);
				float stopDot2 = MathF.Cos(light.Phi * 0.5f);
				float ooDot = stopDot > stopDot2 ? 1.0f / (stopDot - stopDot2) : 0.0f;
				lightState[3] = new Vector4(light.Falloff, stopDot, stopDot2, ooDot);
			}
			else {
				lightState[3] = new Vector4(0, 1, 1, 1);
			}

			lightState[4] = new Vector4(light.Attenuation0, light.Attenuation1, light.Attenuation2, 0.0f);

			SetVertexShaderConstant(VertexShaderConst.Lights + slot * 5, MemoryMarshal.Cast<Vector4, float>(lightState));
			slot++;
		}
	}
	public void InvalidateDelayedShaderConstants() { }

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
			readyToDraw = PrepareDraw(RenderMesh.GetVertexFormat(), RenderMesh.GetPrimitiveType(), RenderMesh.GetColorMeshStride());
			RenderMesh.RenderPass();
			readyToDraw = false;
		}
	}

	public void FlushBufferedPrimitives() {
		Assert(RenderMesh == null);
		MeshMgr?.Flush();
	}

	unsafe bool PrepareDraw(VertexFormat format, MaterialPrimitiveType topology, int colorMeshStride) {
		if (!EnsureRendering())
			return false;

		if (!activeVertexShader.IsValid() || activeVertexShader.Handle == 0 ||
			!activePixelShader.IsValid() || activePixelShader.Handle == 0) {
			if (!warnedInvalidShaders) {
				warnedInvalidShaders = true;
				Warning("Vulkan: draw skipped - material has no loaded SPIR-V shaders (more will be skipped silently)\n");
			}
			return false;
		}

		// A texture uploaded mid-frame still has its copy unsubmitted.
		if (textureManager!.HasPendingWork)
			textureManager.Flush();

		Vk vk = core!.Vk;
		CommandBuffer cmd = frameLoop!.Cmd;

		VulkanPipelineKey key = new() {
			State = currentBoardState,
			VertexShader = activeVertexShader.Handle,
			PixelShader = activePixelShader.Handle,
			Format = format,
			Topology = topology,
			ColorFormat = currentColorFormat,
			DepthFormat = currentDepthFormat,
			ColorMeshStride = colorMeshStride,
			StencilEnable = stencilEnable,
			StencilFail = stencilFail,
			StencilZFail = stencilZFail,
			StencilPass = stencilPass,
			StencilFunc = stencilFunc
		};
		Pipeline pipeline = pipelines!.GetPipeline(in key);
		if (pipeline.Handle == 0)
			return false;
		if (pipeline.Handle != boundPipeline.Handle) {
			vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
			boundPipeline = pipeline;
		}

		UpdateSharedBlocksFromShadow();

		bool offsetsChanged = false;
		for (int i = 0; i < BlockCount; i++) {
			if (!uniformDirty[i])
				continue;
			uniformOffsets[i] = pipelines.AllocUniform(uniformBlocks[i]);
			uniformDirty[i] = false;
			offsetsChanged = true;
		}

		DescriptorSet textureSet = ResolveTextureSet();
		if (offsetsChanged || !descriptorsBound || textureSet.Handle != boundTextureSet.Handle) {
			DescriptorSet* sets = stackalloc DescriptorSet[2] { pipelines.CurrentSet0, textureSet };
			fixed (uint* offsets = uniformOffsets)
				vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, pipelines.PipelineLayout, 0, 2, sets, (uint)BlockCount, offsets);
			descriptorsBound = true;
			boundTextureSet = textureSet;
		}

		if (pushConstantsDirty) {
			int* push = stackalloc int[2] { pushFlags, (currentShadow?.StaticComboBits ?? 0) | dynamicComboBits };
			vk.CmdPushConstants(cmd, pipelines.PipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, sizeof(int) * 2, push);
			pushConstantsDirty = false;
		}

		ApplyViewportAndScissor(vk, cmd);

		vk.CmdSetStencilReference(cmd, StencilFaceFlags.FaceFrontAndBack, stencilReference);
		vk.CmdSetStencilCompareMask(cmd, StencilFaceFlags.FaceFrontAndBack, stencilTestMask);
		vk.CmdSetStencilWriteMask(cmd, StencilFaceFlags.FaceFrontAndBack, stencilWriteMask);
		return true;
	}

	VulkanPipelineSystem.TextureSetKey lastTextureSetKey;
	DescriptorSet lastTextureSet;
	DescriptorSet boundTextureSet;
	bool lastTextureSetValid;

	/// <summary>
	/// Builds set 1 from the textures the shader routed to each binding: the shader told us which
	/// sampler unit it put each named texture on (see <see cref="SetShaderUniform(int, int)"/>),
	/// and <see cref="BindTexture"/> recorded what is on each unit. Bindings the material never
	/// routed fall back to the 1x1 white texture.
	/// </summary>
	DescriptorSet ResolveTextureSet() {
		if (lastTextureSetValid)
			return lastTextureSet;

		VulkanPipelineSystem.TextureSetKey key = default;
		for (int binding = 0; binding < samplerForBinding.Length; binding++) {
			int sampler = samplerForBinding[binding];
			if (sampler < 0 || sampler >= boundTextures.Length)
				continue;

			ShaderAPITextureHandle_t handle = boundTextures[sampler];
			if (!textures.TryGetValue(handle, out VulkanTexture? texture) || !texture.HasContent)
				continue;

			// Binding 1 is declared samplerCube; a 2D view there is a type mismatch, so leave the
			// white cube in place rather than handing the shader something it cannot sample.
			if ((binding == 1) != texture.IsCubeMap)
				continue;

			ImageView view = textureManager!.GetView(texture);
			if (view.Handle == 0)
				continue;

			key.Set(binding, view, textureManager.GetSampler(in texture.Sampler));
		}

		lastTextureSetKey = key;
		lastTextureSet = pipelines!.GetTextureSet(in key);
		lastTextureSetValid = true;
		return lastTextureSet;
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
		CurrentTargetSize(out int maxWidth, out int maxHeight);

		int x = currentViewport.TopLeftX, y = currentViewport.TopLeftY;
		int width = currentViewport.Width, height = currentViewport.Height;
		if (width <= 0 || height <= 0) {
			x = y = 0;
			width = maxWidth;
			height = maxHeight;
		}
		// A viewport larger than the attachment is invalid in Vulkan; GL silently clamped.
		width = Math.Min(width, Math.Max(maxWidth - x, 0));
		height = Math.Min(height, Math.Max(maxHeight - y, 0));
		if (width <= 0 || height <= 0)
			return;

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

	/// <summary>Dimensions of whatever is currently being rendered into.</summary>
	void CurrentTargetSize(out int width, out int height) {
		if (usingTextureRenderTarget) {
			width = viewportMaxWidth;
			height = viewportMaxHeight;
			return;
		}
		width = (int)(swapchain?.Extent.Width ?? 0);
		height = (int)(swapchain?.Extent.Height ?? 0);
	}

	internal unsafe bool BindMeshBuffers(VertexBufferVulkan vertexBuffer, IndexBufferVulkan indexBuffer,
		VertexBufferVulkan? colorMesh = null, int colorMeshOffset = 0) {
		if (!readyToDraw || frameLoop is not { FrameActive: true })
			return false;

		VulkanBufferResource? vb = vertexBuffer.Buffer;
		VulkanBufferResource? ib = indexBuffer.Buffer;
		if (vb == null || ib == null)
			return false;

		Vk vk = core!.Vk;
		CommandBuffer cmd = frameLoop.Cmd;

		VulkanBufferResource? colorBuffer = colorMesh?.Buffer;
		uint count = colorBuffer != null ? 3u : 2u;

		VkBuffer* buffers = stackalloc VkBuffer[3] { vb.Handle, pipelines!.ZeroVertexBuffer, colorBuffer?.Handle ?? default };
		ulong* offsets = stackalloc ulong[3] { 0, 0, (ulong)colorMeshOffset };
		vk.CmdBindVertexBuffers(cmd, 0, count, buffers, offsets);
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

	readonly Dictionary<ShaderAPITextureHandle_t, VulkanTexture> textures = [];
	ShaderAPITextureHandle_t nextTextureHandle = 1;
	ShaderAPITextureHandle_t modifyTextureHandle = INVALID_SHADERAPI_TEXTURE_HANDLE;

	VulkanTexture? ModifyTarget =>
		textures.TryGetValue(modifyTextureHandle, out VulkanTexture? texture) ? texture : null;

	public ShaderAPITextureHandle_t CreateTexture(int width, int height, int depth, ImageFormat imageFormat, ushort mipCount, int copies,
		CreateTextureFlags creationFlags, ReadOnlySpan<char> debugName, ReadOnlySpan<char> textureGroup) {
		ShaderAPITextureHandle_t handle = nextTextureHandle++;

		VulkanTexture? texture = textureManager?.Create(width, height, depth, imageFormat, mipCount, copies, creationFlags, debugName);
		if (texture != null)
			textures[handle] = texture;

		return handle;
	}

	public void CreateTextures(Span<ShaderAPITextureHandle_t> handles, int count, int width, int height, int depth, ImageFormat imageFormat,
		ushort mipCount, int copies, CreateTextureFlags creationFlags, ReadOnlySpan<char> debugName, ReadOnlySpan<char> textureGroup) {
		for (int i = 0; i < count; i++)
			handles[i] = CreateTexture(width, height, depth, imageFormat, mipCount, copies, creationFlags, debugName, textureGroup);
	}

	public ShaderAPITextureHandle_t CreateDepthTexture(ImageFormat imageFormat, ushort width, ushort height, Span<char> debugName, bool texture)
		=> CreateTexture(width, height, 1, imageFormat, 1, 1, CreateTextureFlags.DepthBuffer, debugName, default);

	public bool IsTexture(ShaderAPITextureHandle_t handle) => textures.ContainsKey(handle);

	public void DeleteTexture(ShaderAPITextureHandle_t handle) {
		if (!textures.Remove(handle, out VulkanTexture? texture))
			return;

		// Anything still pointing at this texture's views must stop doing so before they die.
		foreach (ImageView view in texture.Views)
			pipelines?.InvalidateTextureSets(view);
		lastTextureSetValid = false;

		for (int i = 0; i < boundTextures.Length; i++)
			if (boundTextures[i] == handle)
				boundTextures[i] = INVALID_SHADERAPI_TEXTURE_HANDLE;

		core?.Vk.DeviceWaitIdle(core.Device);
		textureManager?.Destroy(texture);
	}

	public ImageFormat GetNearestSupportedFormat(ImageFormat fmt, bool filteringRequired = true) => fmt;
	public bool CanDownloadTextures() => IsActive();

	public void ModifyTexture(int handle) {
		modifyTextureHandle = handle;

		// GL advances to the next copy when a texture that is still in flight is rewritten.
		if (textures.TryGetValue(handle, out VulkanTexture? texture) && texture.SwitchNeeded) {
			if (texture.Images.Length > 1) {
				texture.CurrentCopy = (texture.CurrentCopy + 1) % texture.Images.Length;
				lastTextureSetValid = false;
			}
			texture.SwitchNeeded = false;
		}
	}

	public void TexImage2D(int mip, int face, ImageFormat dstFormat, int zOffset, int width, int height, ImageFormat srcFormat, bool srcIsTiled, Span<byte> imageData) {
		VulkanTexture? texture = ModifyTarget;
		if (texture == null)
			return;

		// GL's BlitSurfaceBits uploads (width >> mip) x (height >> mip): the caller passes the
		// level-0 size and lets the mip shift do the work.
		textureManager?.Upload(texture, mip, face, srcFormat, Math.Max(width >> mip, 1), Math.Max(height >> mip, 1), imageData);
		texture.SwitchNeeded = true;
	}

	public void TexSubImage2D(int mip, int face, int x, int y, int z, int width, int height, ImageFormat srcFormat, int srcStride, Span<byte> imageData) {
		VulkanTexture? texture = ModifyTarget;
		if (texture == null)
			return;

		int mipWidth = Math.Max(width >> mip, 1);
		int mipHeight = Math.Max(height >> mip, 1);

		if (x == 0 && y == 0 && mipWidth == Math.Max(texture.Width >> mip, 1) && mipHeight == Math.Max(texture.Height >> mip, 1))
			textureManager?.Upload(texture, mip, face, srcFormat, mipWidth, mipHeight, imageData, srcStride);
		else
			textureManager?.UploadSubRect(texture, mip, face, x, y, mipWidth, mipHeight, srcFormat, imageData, srcStride);

		texture.SwitchNeeded = true;
	}

	public void TexImageFromVTF(IVTFTexture? vtf, int vtfFrame) {
		VulkanTexture? texture = ModifyTarget;
		if (vtf == null || texture == null)
			return;

		if (vtf.Depth() > 1) {
			Warning($"Vulkan: volume textures are not supported ('{texture.DebugName}')\n");
			return;
		}

		ImageFormat srcFormat = vtf.Format();
		int mipCount = Math.Min(vtf.MipCount(), texture.MipCount);

		if (vtf.IsCubeMap() && texture.IsCubeMap) {
			int faces = Math.Min(vtf.FaceCount(), 6);
			if (faces < 6) {
				Warning($"Vulkan: cubemap '{texture.DebugName}' has only {faces} faces\n");
				return;
			}
			for (int mip = 0; mip < mipCount; mip++) {
				vtf.ComputeMipLevelDimensions(mip, out int w, out int h, out _);
				for (int face = 0; face < 6; face++)
					textureManager?.Upload(texture, mip, face, srcFormat, w, h, vtf.ImageData(vtfFrame, face, mip));
			}
		}
		else {
			for (int mip = 0; mip < mipCount; mip++) {
				vtf.ComputeMipLevelDimensions(mip, out int w, out int h, out _);
				textureManager?.Upload(texture, mip, 0, srcFormat, w, h, vtf.ImageData(vtfFrame, 0, mip));
			}
		}

		texture.SwitchNeeded = true;
	}

	public void TexWrap(TexCoordComponent coord, TexWrapMode wrapMode) {
		VulkanTexture? texture = ModifyTarget;
		if (texture == null)
			return;

		SamplerAddressMode mode = wrapMode switch {
			TexWrapMode.Clamp => SamplerAddressMode.ClampToEdge,
			TexWrapMode.Repeat => SamplerAddressMode.Repeat,
			TexWrapMode.Border => SamplerAddressMode.ClampToBorder,
			_ => SamplerAddressMode.Repeat
		};

		switch (coord) {
			case TexCoordComponent.S: texture.Sampler.AddressU = mode; break;
			case TexCoordComponent.T: texture.Sampler.AddressV = mode; break;
			case TexCoordComponent.U: texture.Sampler.AddressW = mode; break;
			default: Warning("Vulkan: TexWrap with an unknown coordinate\n"); return;
		}
		lastTextureSetValid = false;
	}

	public void TexMinFilter(TexFilterMode mode) {
		VulkanTexture? texture = ModifyTarget;
		if (texture == null)
			return;

		ref SamplerDesc sampler = ref texture.Sampler;
		sampler.Anisotropic = false;
		switch (mode) {
			case TexFilterMode.Nearest: sampler.MinFilter = Filter.Nearest; sampler.MipmapMode = SamplerMipmapMode.Nearest; break;
			case TexFilterMode.Linear: sampler.MinFilter = Filter.Linear; sampler.MipmapMode = SamplerMipmapMode.Nearest; break;
			case TexFilterMode.NearestMipmapNearest: sampler.MinFilter = Filter.Nearest; sampler.MipmapMode = SamplerMipmapMode.Nearest; break;
			case TexFilterMode.LinearMipmapNearest: sampler.MinFilter = Filter.Linear; sampler.MipmapMode = SamplerMipmapMode.Nearest; break;
			case TexFilterMode.NearestMipmapLinear: sampler.MinFilter = Filter.Nearest; sampler.MipmapMode = SamplerMipmapMode.Linear; break;
			case TexFilterMode.LinearMipmapLinear: sampler.MinFilter = Filter.Linear; sampler.MipmapMode = SamplerMipmapMode.Linear; break;
			case TexFilterMode.Anisotropic:
				sampler.MinFilter = Filter.Linear;
				sampler.MipmapMode = SamplerMipmapMode.Linear;
				sampler.Anisotropic = true;
				break;
		}
		lastTextureSetValid = false;
	}

	public void TexMagFilter(TexFilterMode mode) {
		VulkanTexture? texture = ModifyTarget;
		if (texture == null)
			return;

		texture.Sampler.MagFilter = mode == TexFilterMode.Nearest ? Filter.Nearest : Filter.Linear;
		lastTextureSetValid = false;
	}

	// TexLock hands the caller a CPU buffer to paint into; TexUnlock uploads it. Unlike GL this
	// does not read the existing contents back (no host-visible copy exists), so callers that
	// only touch part of the rect will write zeroes over the rest - the same rects the engine
	// locks are fully rewritten in practice.
	byte[] lockBuffer = new byte[2048 * 2048 * 4];
	bool lockActive;
	int lockMip, lockFace, lockX, lockY, lockWidth, lockHeight;
	ImageFormat lockFormat;
	ShaderAPITextureHandle_t lockHandle;

	bool BeginTexLock(int level, int cubeFaceID, int xOffset, int yOffset, int width, int height, out Memory<byte> buffer) {
		buffer = default;
		VulkanTexture? texture = ModifyTarget;
		if (texture == null || lockActive || width <= 0 || height <= 0)
			return false;

		ImageFormat format = texture.StorageFormat;
		if (format.IsCompressed()) {
			Warning($"Vulkan: TexLock on compressed texture '{texture.DebugName}' is not supported\n");
			return false;
		}

		int size = ImageLoader.SizeInBytes(format) * width * height;
		if (size > lockBuffer.Length)
			lockBuffer = new byte[MathLib.CeilPow2(size)];
		Array.Clear(lockBuffer, 0, size);

		lockActive = true;
		lockMip = level;
		lockFace = cubeFaceID;
		lockX = xOffset;
		lockY = yOffset;
		lockWidth = width;
		lockHeight = height;
		lockFormat = format;
		lockHandle = modifyTextureHandle;

		buffer = lockBuffer.AsMemory(0, size);
		return true;
	}

	public bool TexLock(int level, int cubeFaceID, int xOffset, int yOffset, int width, int height, ref PixelWriter writer) {
		if (!BeginTexLock(level, cubeFaceID, xOffset, yOffset, width, height, out Memory<byte> buffer))
			return false;
		writer.SetPixelMemory(lockFormat, buffer.Span, width * ImageLoader.SizeInBytes(lockFormat));
		return true;
	}

	public bool TexLock(int level, int cubeFaceID, int xOffset, int yOffset, int width, int height, ref PixelWriterMem writer) {
		if (!BeginTexLock(level, cubeFaceID, xOffset, yOffset, width, height, out Memory<byte> buffer))
			return false;
		writer.SetPixelMemory(lockFormat, buffer, width * ImageLoader.SizeInBytes(lockFormat));
		return true;
	}

	public void TexUnlock() {
		if (!lockActive)
			return;
		lockActive = false;

		if (textures.TryGetValue(lockHandle, out VulkanTexture? texture)) {
			int size = ImageLoader.SizeInBytes(lockFormat) * lockWidth * lockHeight;
			textureManager?.UploadSubRect(texture, lockMip, lockFace, lockX, lockY, lockWidth, lockHeight,
				lockFormat, lockBuffer.AsSpan(0, size));
			texture.SwitchNeeded = true;
		}
	}

	// --- binding ---

	readonly ShaderAPITextureHandle_t[] boundTextures = CreateBoundTextureTable();

	static ShaderAPITextureHandle_t[] CreateBoundTextureTable() {
		ShaderAPITextureHandle_t[] table = new ShaderAPITextureHandle_t[(int)Sampler.MaxSamplers];
		Array.Fill(table, INVALID_SHADERAPI_TEXTURE_HANDLE);
		return table;
	}

	public void BindTexture(Sampler sampler, ShaderAPITextureHandle_t textureHandle) {
		if (textureHandle == INVALID_SHADERAPI_TEXTURE_HANDLE || (int)sampler >= boundTextures.Length)
			return;

		if (boundTextures[(int)sampler] != textureHandle) {
			boundTextures[(int)sampler] = textureHandle;
			lastTextureSetValid = false;
		}
	}

	public void BindStandardTexture(Sampler sampler, StandardTextureId id) => ShaderUtil.BindStandardTexture(sampler, id);
	public void SetStandardTextureHandle(StandardTextureId id, int handle) { }

	VulkanTexture? renderTargetColor;   // null = the swapchain backbuffer
	VulkanTexture? renderTargetDepth;   // null = the frame loop's shared depth buffer
	bool renderTargetNoDepth;
	bool renderTargetDirty = true;
	bool backbufferNeedsClear = true;
	bool usingTextureRenderTarget;
	int viewportMaxWidth, viewportMaxHeight;

	public bool DoRenderTargetsNeedSeparateDepthBuffer() => false;
	public void EnableLinearColorSpaceFrameBuffer(bool v) { }

	public void SetRenderTargetEx(int rt,
		ShaderAPITextureHandle_t colorTextureHandle = (ShaderAPITextureHandle_t)ShaderRenderTarget.Backbuffer,
		ShaderAPITextureHandle_t depthTextureHandle = (ShaderAPITextureHandle_t)ShaderRenderTarget.Depthbuffer) {
		// Only one colour attachment is wired up; MRT would need the extra views threaded through
		// the pipeline key as well.
		if (rt != 0)
			return;

		FlushBufferedPrimitives();

		VulkanTexture? color = colorTextureHandle >= 0 && textures.TryGetValue(colorTextureHandle, out VulkanTexture? c) ? c : null;
		VulkanTexture? depth = depthTextureHandle >= 0 && textures.TryGetValue(depthTextureHandle, out VulkanTexture? d) ? d : null;
		bool noDepth = depthTextureHandle == (ShaderAPITextureHandle_t)ShaderRenderTarget.None;

		if (color == renderTargetColor && depth == renderTargetDepth && noDepth == renderTargetNoDepth)
			return;

		renderTargetColor = color;
		renderTargetDepth = depth;
		renderTargetNoDepth = noDepth;
		renderTargetDirty = true;

		usingTextureRenderTarget = color != null;
		if (color != null) {
			viewportMaxWidth = color.Width;
			viewportMaxHeight = color.Height;
		}
	}

	/// <summary>
	/// Closes any open pass, moves the attachments into the layouts they need, and opens a pass on
	/// the current target. A texture that was being rendered into goes back to shader-read so it
	/// can be sampled by whatever comes next.
	/// </summary>
	unsafe void OpenRenderPass() {
		frameLoop!.EndRendering();
		renderTargetDirty = false;

		CommandBuffer cmd = frameLoop.Cmd;

		foreach (VulkanTexture texture in colorAttachmentsInUse) {
			if (texture != renderTargetColor)
				TransitionTexture(cmd, texture, ImageLayout.ShaderReadOnlyOptimal);
		}
		colorAttachmentsInUse.RemoveAll(t => t != renderTargetColor);

		VulkanFrameLoop.RenderPassTarget target;
		bool clear = false;
		bool discard = false;

		if (renderTargetColor == null) {
			target = frameLoop.SwapchainTarget;
			clear = backbufferNeedsClear;
			backbufferNeedsClear = false;
		}
		else {
			ImageView colorView = textureManager!.GetView(renderTargetColor);
			if (colorView.Handle == 0)
				return;

			discard = !renderTargetColor.RenderedTo;
			TransitionTexture(cmd, renderTargetColor, ImageLayout.ColorAttachmentOptimal);
			renderTargetColor.RenderedTo = true;
			if (!colorAttachmentsInUse.Contains(renderTargetColor))
				colorAttachmentsInUse.Add(renderTargetColor);

			target = new VulkanFrameLoop.RenderPassTarget {
				ColorView = colorView,
				ColorFormat = renderTargetColor.VkFormat,
				Extent = new Extent2D((uint)renderTargetColor.Width, (uint)renderTargetColor.Height)
			};
		}

		if (renderTargetNoDepth) {
			target.DepthView = default;
			target.DepthFormat = Format.Undefined;
		}
		else if (renderTargetDepth != null) {
			ImageView depthView = textureManager!.GetView(renderTargetDepth);
			TransitionTexture(cmd, renderTargetDepth, DepthLayoutFor(renderTargetDepth.VkFormat));
			target.DepthView = depthView;
			target.DepthFormat = renderTargetDepth.VkFormat;
			target.Extent = MinExtent(target.Extent, new Extent2D((uint)renderTargetDepth.Width, (uint)renderTargetDepth.Height));
		}
		else if (renderTargetColor != null) {
			// Sharing the backbuffer's depth buffer: the pass can only cover what both can hold.
			target.DepthView = frameLoop.SharedDepthView;
			target.DepthFormat = frameLoop.DepthFormat;
			target.Extent = MinExtent(target.Extent, frameLoop.SharedDepthExtent);
		}

		currentColorFormat = target.ColorFormat;
		currentDepthFormat = target.DepthView.Handle != 0 ? target.DepthFormat : Format.Undefined;

		frameLoop.BeginRendering(in target, clear, clearR, clearG, clearB, discard);
	}

	static ImageLayout DepthLayoutFor(Format format) => VulkanFrameLoop.FormatHasStencil(format)
		? ImageLayout.DepthStencilAttachmentOptimal
		: ImageLayout.DepthAttachmentOptimal;

	static Extent2D MinExtent(Extent2D a, Extent2D b) =>
		new(Math.Min(a.Width, b.Width), Math.Min(a.Height, b.Height));

	readonly List<VulkanTexture> colorAttachmentsInUse = [];
	Format currentColorFormat;
	Format currentDepthFormat;

	void TransitionTexture(CommandBuffer cmd, VulkanTexture texture, ImageLayout newLayout) {
		int copy = texture.CurrentCopy;
		ImageLayout old = texture.Layouts[copy];
		if (old == newLayout)
			return;

		bool depth = newLayout is ImageLayout.DepthAttachmentOptimal or ImageLayout.DepthStencilAttachmentOptimal || texture.IsDepth;
		ImageAspectFlags aspect = !depth ? ImageAspectFlags.ColorBit
			: VulkanFrameLoop.FormatHasStencil(texture.VkFormat)
				? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
				: ImageAspectFlags.DepthBit;

		(PipelineStageFlags2 srcStage, AccessFlags2 srcAccess) = LayoutAccess(old, texture.IsDepth);
		(PipelineStageFlags2 dstStage, AccessFlags2 dstAccess) = LayoutAccess(newLayout, texture.IsDepth);

		frameLoop!.TransitionImage(cmd, texture.Images[copy], aspect, old, newLayout,
			srcStage, srcAccess, dstStage, dstAccess, texture.MipCount, texture.FaceCount);

		texture.Layouts[copy] = newLayout;
	}

	static (PipelineStageFlags2, AccessFlags2) LayoutAccess(ImageLayout layout, bool isDepth) => layout switch {
		ImageLayout.Undefined => (PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None),
		ImageLayout.TransferDstOptimal => (PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit),
		ImageLayout.ColorAttachmentOptimal => (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
		ImageLayout.DepthAttachmentOptimal or ImageLayout.DepthStencilAttachmentOptimal => (
			PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
			AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit),
		ImageLayout.ShaderReadOnlyOptimal => (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderReadBit),
		_ => (PipelineStageFlags2.AllCommandsBit, AccessFlags2.None)
	};

	public bool SupportsShadowDepthTextures() => true;
	public ImageFormat GetShadowDepthTextureFormat() => ImageFormat.NV_DST24;
	public ImageFormat GetNullTextureFormat() => ImageFormat.NV_NULL;

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

	bool stencilEnable;
	StencilOperation stencilFail = StencilOperation.Keep;
	StencilOperation stencilZFail = StencilOperation.Keep;
	StencilOperation stencilPass = StencilOperation.Keep;
	StencilComparisonFunction stencilFunc = StencilComparisonFunction.Always;
	uint stencilReference;
	uint stencilTestMask = 0xFFFFFFFF;
	uint stencilWriteMask = 0xFFFFFFFF;

	public void SetStencilEnable(bool onoff) {
		if (stencilEnable == onoff)
			return;
		FlushBufferedPrimitives();
		stencilEnable = onoff;
	}

	public void SetStencilFailOperation(StencilOperation op) {
		if (stencilFail == op)
			return;
		FlushBufferedPrimitives();
		stencilFail = op;
	}

	public void SetStencilZFailOperation(StencilOperation op) {
		if (stencilZFail == op)
			return;
		FlushBufferedPrimitives();
		stencilZFail = op;
	}

	public void SetStencilPassOperation(StencilOperation op) {
		if (stencilPass == op)
			return;
		FlushBufferedPrimitives();
		stencilPass = op;
	}

	public void SetStencilCompareFunction(StencilComparisonFunction cmpfn) {
		if (stencilFunc == cmpfn)
			return;
		FlushBufferedPrimitives();
		stencilFunc = cmpfn;
	}

	public void SetStencilReferenceValue(int reference) {
		if (stencilReference == (uint)reference)
			return;
		FlushBufferedPrimitives();
		stencilReference = (uint)reference;
	}

	public void SetStencilTestMask(uint msk) {
		if (stencilTestMask == msk)
			return;
		FlushBufferedPrimitives();
		stencilTestMask = msk;
	}

	public void SetStencilWriteMask(uint msk) {
		if (stencilWriteMask == msk)
			return;
		FlushBufferedPrimitives();
		stencilWriteMask = msk;
	}

	public const int MaxNumLights = 4;

	readonly Vector4[] ambientLightCube = new Vector4[6];
	readonly LightDesc[] lightDescs = new LightDesc[MaxNumLights];
	readonly bool[] lightEnabled = new bool[MaxNumLights];
	int numLights;
	bool lightingDirty = true;

	public void SetAmbientLightCube(ReadOnlySpan<Vector4> cube) {
		if (cube.Length < 6 || cube.SequenceEqual(ambientLightCube))
			return;
		cube[..6].CopyTo(ambientLightCube);
		lightingDirty = true;
	}

	Vector3 lightingOrigin;
	public void SetLightingOrigin(Vector3 origin) {
		if (origin != lightingOrigin) {
			FlushBufferedPrimitives();
			lightingOrigin = origin;
		}
	}

	public void SetAmbientLight(float r, float g, float b) { }

	public void SetLight(int lightNum, in LightDesc desc) {
		if (lightNum < 0 || lightNum >= MaxNumLights)
			return;

		FlushBufferedPrimitives();
		lightDescs[lightNum] = desc;
		lightEnabled[lightNum] = desc.Type != LightType.Disable;
		RecountLights();
		lightingDirty = true;
	}

	public void DisableAllLocalLights() {
		for (int i = 0; i < MaxNumLights; i++) {
			if (!lightEnabled[i])
				continue;
			FlushBufferedPrimitives();
			lightDescs[i].Type = LightType.Disable;
			lightEnabled[i] = false;
		}
		RecountLights();
		lightingDirty = true;
	}

	void RecountLights() {
		numLights = 0;
		for (int i = 0; i < MaxNumLights; i++)
			if (lightEnabled[i])
				numLights++;
	}

	public int GetMaxLights() => MaxNumLights;

	public void GetLightState(out LightState state) {
		state = default;

		foreach (Vector4 entry in ambientLightCube) {
			if (entry.X != 0 || entry.Y != 0 || entry.Z != 0) {
				state.AmbientLight = true;
				break;
			}
		}

		state.NumLights = numLights;
		state.StaticLightVertex = RenderMesh?.HasColorMesh() ?? false;
	}
	public void SetFlashlightStateEx(in FlashlightState state, in Matrix4x4 worldToTexture, ITexture? flashlightDepthTexture) { }
	public bool InFlashlightMode() => false;
	public MaterialFogMode GetSceneFogMode() => MaterialFogMode.None;

	public float LinearToGamma_HardwareSpecific(float fLookupResult) => fLookupResult;
	public void SetLinearToGammaConversionTextures(int linearToGammaTableTextureHandle, int linearToGammaIdentityTableTextureHandle) { }

	public void EnableDebugTextureList(bool enable) { }
	public void EnableGetAllTextures(bool enable) { }
	public Source.Common.Formats.Keyvalues.KeyValues? GetDebugTextureList() => null;
	public int GetTextureMemoryUsed(TextureMemoryType textureMemory) => 0;
	public bool IsDebugTextureListFresh(int numFramesAllowed = 1) => false;
	public bool SetDebugTextureRendering(bool enable) => false;
}
