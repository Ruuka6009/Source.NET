using Microsoft.Extensions.DependencyInjection;

using Source.Common;
using Source.Common.Bitmap;
using Source.Common.Launcher;
using Source.Common.MaterialSystem;
using Source.Common.Mathematics;
using Source.Common.ShaderAPI;

using System.Numerics;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Vulkan IShaderAPI/IShaderDevice. Currently a boot shim (Phase 2 of VULKAN_TODO.md): brings the
/// device up, clears the screen every present, and swallows all draw/texture/state calls so the
/// engine can run against it. Each subsystem gets replaced by real Vulkan work in Phases 3-5.
/// </summary>
public class ShaderAPIVulkan : IShaderAPI, IShaderDevice, IDebugTextureInfo
{
	public static void DLLInit(IServiceCollection services) {
		services.AddSingleton(x => (IDebugTextureInfo)(ShaderAPIVulkan)x.GetRequiredService<IShaderAPI>());
		services.AddSingleton(x => x.GetRequiredService<IShaderAPI>().GetShaderDevice());
		services.AddSingleton<IMeshMgr, MeshMgrVulkan>();
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
	internal GraphicsDriver Driver = GraphicsDriver.Vulkan13;
	ShaderDeviceInfo PresentParameters;

	// Distinctive default so a running Vulkan backend is unmistakable on screen
	float clearR = 0.10f, clearG = 0.20f, clearB = 0.40f;

	ILauncherManager LauncherManager => services.GetRequiredService<ILauncherManager>();

	// ------------------------------------------------------------------
	// Boot / device
	// ------------------------------------------------------------------

	public void PreInit(IShaderUtil shaderUtil, IServiceProvider services) {
		this.services = services;
		ShaderUtil = shaderUtil;
		ShaderManager = services.GetRequiredService<IShaderSystem>();
	}

	public bool SetMode(IWindow window, in ShaderDeviceInfo info) {
		if (IsActive())
			ReleaseResources();

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

		swapchain = new VulkanSwapchain(core);
		if (!swapchain.Create((uint)deviceInfo.DisplayMode.Width, (uint)deviceInfo.DisplayMode.Height, deviceInfo.WaitForVSync))
			return false;

		frameLoop = new VulkanFrameLoop(core, swapchain);
		if (!frameLoop.Init())
			return false;

		Device = new VulkanGraphicsContext(core, swapchain);
		Driver = deviceInfo.Driver;
		return true;
	}

	public bool OnDeviceInit() {
		Msg("Vulkan: device is up; running as a clear-screen shim (no real rendering yet)\n");
		return true;
	}

	public void Present() {
		if (frameLoop == null || swapchain == null)
			return;

		frameLoop.DrawClearFrame(clearR, clearG, clearB);

		if (frameLoop.NeedsRecreate || (Device?.VSyncChanged ?? false)) {
			LauncherManager.DisplayedSize(out int width, out int height);
			if (width > 0 && height > 0)
				swapchain.Recreate((uint)width, (uint)height, Device?.VSync ?? true);
			Device?.AcknowledgeVSyncChange();
		}
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

	public void ReleaseResources() {
		frameLoop?.Dispose();
		frameLoop = null;
		swapchain?.Dispose();
		swapchain = null;
		core?.Dispose();
		core = null;
		Device = null;
	}

	public void ReacquireResources() { }

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
	public ReadOnlySpan<char> GetDriverVersionString() => core?.DeviceDescription ?? "Vulkan (no device)";

	public int GetCurrentAdapter() => LauncherManager.GetCurrentDisplayIndex();
	public int GetModeCount(int adapter) => LauncherManager.GetDisplayModeCount(adapter);
	public void GetModeInfo(int adapter, int mode, out ShaderDisplayMode info) => LauncherManager.GetDisplayMode(adapter, mode, out info);

	// ------------------------------------------------------------------
	// Frame / clear
	// ------------------------------------------------------------------

	public void BeginFrame() { }
	public void EndFrame() { }

	public void ClearColor3ub(byte r, byte g, byte b) => ClearColor4ub(r, g, b, 255);
	public void ClearColor4ub(byte r, byte g, byte b, byte a) {
		clearR = r / 255.0f;
		clearG = g / 255.0f;
		clearB = b / 255.0f;
	}
	public void ClearBuffers(bool bClearColor, bool bClearDepth, bool bClearStencil, int renderTargetWidth, int renderTargetHeight) {
		// The clear happens as part of Present()'s render pass for now.
	}

	public void GetBackBufferDimensions(out int width, out int height) {
		width = PresentParameters.DisplayMode.Width;
		height = PresentParameters.DisplayMode.Height;
	}
	public ImageFormat GetBackBufferFormat() => ImageFormat.RGBA8888;

	// ------------------------------------------------------------------
	// Snapshots / shaders
	// ------------------------------------------------------------------

	public IShaderShadow NewShaderShadow(ReadOnlySpan<char> materialName) => new ShadowStateVulkan(this, materialName);
	public bool IsTranslucent(IShaderShadow renderState) => ((ShadowStateVulkan)renderState).Blending;
	public bool IsAlphaTested(IShaderShadow renderState) => ((ShadowStateVulkan)renderState).AlphaTesting;

	public void InitRenderState() { }
	public void SetDefaultState() { }
	public bool SetBoardState(in GraphicsBoardState state) => true;
	public void RenderPass() { }
	public void FlushBufferedPrimitives() { }
	public void DrawMesh(IMesh mesh) { }
	public void Bind(IMaterial? material) { }
	public void InvalidateDelayedShaderConstants() { }
	public void SetSkinningMatrices() { }
	public void ShadeMode(ShadeMode mode) { }

	// ------------------------------------------------------------------
	// Meshes
	// ------------------------------------------------------------------

	static readonly DummyMeshVulkan dummyMesh = new();

	public IMesh CreateStaticMesh(VertexFormat format, ReadOnlySpan<char> textureGroup, IMaterial? material) => dummyMesh;
	public void DestroyStaticMesh(IMesh mesh) { }
	public IMesh GetDynamicMesh(IMaterial material, int nCurrentBoneCount, bool buffered, IMesh? vertexOverride, IMesh? indexOverride) => dummyMesh;
	public int GetCurrentDynamicVBSize() => IMesh.DYNAMIC_VERTEX_BUFFER_MEMORY;
	public int GetMaxVerticesToRender(IMaterial material) => 32767;
	public int GetMaxIndicesToRender() => IMesh.INDEX_BUFFER_SIZE;

	// ------------------------------------------------------------------
	// Textures
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

	public bool DoRenderTargetsNeedSeparateDepthBuffer() => false;
	public void EnableLinearColorSpaceFrameBuffer(bool v) { }
	public void SetRenderTargetEx(int rt,
		ShaderAPITextureHandle_t colorTextureHandle = (ShaderAPITextureHandle_t)ShaderRenderTarget.Backbuffer,
		ShaderAPITextureHandle_t depthTextureHandle = (ShaderAPITextureHandle_t)ShaderRenderTarget.Depthbuffer) { }
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
	// Lighting / bones / fog
	// ------------------------------------------------------------------

	public void LoadBoneMatrix(int boneIndex, in Matrix3x4 matrix) { }
	public void SetNumBoneWeights(int numBones) { }
	public int GetCurrentNumBones() => 0;
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
	// Matrices
	// ------------------------------------------------------------------

	MaterialMatrixMode currentMatrixMode;
	readonly Matrix4x4[] Matrices = [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];

	public void MatrixMode(MaterialMatrixMode mode) => currentMatrixMode = mode;
	public void LoadMatrix(in Matrix4x4 transposeTop) => Matrices[(int)currentMatrixMode] = transposeTop;
	public void LoadIdentity() => Matrices[(int)currentMatrixMode] = Matrix4x4.Identity;
	public void GetMatrix(MaterialMatrixMode matrixMode, out Matrix4x4 dst) => dst = Matrices[(int)matrixMode];
	public void PushMatrix() { }
	public void PopMatrix() { }

	// ------------------------------------------------------------------
	// Dynamic shader state
	// ------------------------------------------------------------------

	public bool InEditorMode() => false;
	public void BindVertexShader(in VertexShaderHandle vertexShader) { }
	public void BindPixelShader(in PixelShaderHandle pixelShader) { }
	public void SetVertexShaderIndex(int index) { }
	public void SetPixelShaderIndex(int index) { }
	public int GetDynamicComboScale(ShaderType type, ReadOnlySpan<char> name) => 1;
	public int LocateShaderUniform(ReadOnlySpan<char> name) => -1;
	public void SetShaderUniform(int uniform, int integer) { }
	public void SetShaderUniform(int uniform, float fl) { }
	public void SetShaderUniform(int uniform, ReadOnlySpan<float> flConsts) { }
	public void SetShaderUniform(IMaterialVar variable) { }
	public nint GetCurrentProgram() => 0;
	public void SetVertexShaderConstant(int var, Span<float> vec) { }
	public void SetPixelShaderConstant(int var, Span<float> vec) { }
	public void SetVertexShaderStateAmbientLightCube() { }
	public void CommitVertexShaderLighting() { }

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
