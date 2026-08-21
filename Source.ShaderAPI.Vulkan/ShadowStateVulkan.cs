using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Shared uniforms every vertex shader can use (set 0 binding 2). Layout must match
/// source_base_vertex in the *_vk13 shaders (std140: int + pad to 16).
/// </summary>
public struct VertexSharedStateVulkan
{
	public int NumBones;
}

/// <summary>
/// Shared uniforms every pixel shader can use (set 0 binding 3). Layout must match
/// source_pixel_sharedUBO in the *_vk13 shaders.
/// </summary>
public struct PixelSharedStateVulkan
{
	public int IsAlphaTesting;
	public int AlphaTestFunc;
	public float AlphaTestRef;
}

/// <summary>
/// Snapshot ("shadow") state. Pure data: the full <see cref="GraphicsBoardState"/> (the
/// pipeline-key half) plus the shared shader uniforms (the UBO half). Activate() publishes it to
/// the shader API; the VkPipeline itself is resolved per draw from the pipeline cache.
/// </summary>
public class ShadowStateVulkan : IShaderShadow
{
	readonly ShaderAPIVulkan shaderAPI;
	public readonly string? Name;

	public GraphicsBoardState State;
	public VertexSharedStateVulkan Vertex;
	public PixelSharedStateVulkan Pixel;

	ShaderFlags flags;
	VertexFormat vertexFormat;

	internal VertexShaderHandle VertexShader = VertexShaderHandle.INVALID;
	internal PixelShaderHandle PixelShader = PixelShaderHandle.INVALID;

	readonly List<IMaterialVar> shaderUniforms = [];

	public ShadowStateVulkan(ShaderAPIVulkan shaderAPI, ReadOnlySpan<char> name) {
		this.shaderAPI = shaderAPI;
		Name = name.IsEmpty ? null : new(name);
	}

	public void DepthFunc(ShaderDepthFunc depthFunc) => State.DepthFunc = depthFunc;
	public void EnableDepthWrites(bool enable) => State.DepthWrite = enable;
	public void EnableDepthTest(bool enable) => State.DepthTest = enable;
	public void EnablePolyOffset(PolygonOffsetMode offsetMode) => State.ZBias = offsetMode;
	public void EnableColorWrites(bool enable) => State.ColorWrite = enable;
	public void EnableAlphaWrites(bool enable) => State.AlphaWrite = enable;
	public void EnableBlending(bool enable) => State.Blending = enable;

	public void BlendFunc(ShaderBlendFactor srcFactor, ShaderBlendFactor dstFactor) {
		State.SourceBlend = srcFactor;
		State.DestinationBlend = dstFactor;
	}

	public void BlendOp(ShaderBlendOp blendOp) => State.BlendOperation = blendOp;
	public void EnableBlendingSeparateAlpha(bool enable) => State.AlphaSeparateBlend = enable;

	public void BlendFuncSeparateAlpha(ShaderBlendFactor srcFactor, ShaderBlendFactor dstFactor) {
		State.AlphaSourceBlend = srcFactor;
		State.AlphaDestinationBlend = dstFactor;
	}

	public void BlendOpSeparateAlpha(ShaderBlendOp blendOp) => State.AlphaBlendOperation = blendOp;

	public void PolyMode(ShaderPolyModeFace face, ShaderPolyMode polyMode) {
		if (face == ShaderPolyModeFace.Back)
			return;
		State.FillMode = polyMode;
	}

	public void EnableCulling(bool enable) => State.CullEnable = enable;
	public void EnableAlphaToCoverage(bool enable) => State.AlphaToCoverage = enable;

	public void EnableAlphaTest(bool enable) => Pixel.IsAlphaTesting = enable ? 1 : 0;

	public void AlphaFunc(ShaderAlphaFunc alphaFunc, float alphaRef) {
		Pixel.AlphaTestFunc = (int)alphaFunc;
		Pixel.AlphaTestRef = alphaRef;
	}

	public void SetShaderUniform(IMaterialVar textureVar) => shaderUniforms.Add(textureVar);

	public void VertexShaderVertexFormat(VertexFormat format, int texCoordCount, Span<int> texCoordDimensions, int userDataSize)
		=> vertexFormat = format;

	public void SetVertexShader(ReadOnlySpan<char> fileName, int staticIndex = 0) {
		VertexShader = shaderAPI.ShaderLoader.LoadVertexShader($"{fileName}_{GetDriver().Extension(ShaderType.Vertex)}");
	}

	public void SetPixelShader(ReadOnlySpan<char> fileName, int staticIndex = 0) {
		PixelShader = shaderAPI.ShaderLoader.LoadPixelShader($"{fileName}_{GetDriver().Extension(ShaderType.Pixel)}");
	}

	// Combos are ignored for now; needs the vertexlitgeneric rework.
	public int GetStaticComboScale(ShaderType type, ReadOnlySpan<char> fileName, ReadOnlySpan<char> name) => 1;

	public void Activate() {
		shaderAPI.SetCurrentShadow(this);
		shaderAPI.BindVertexShader(in VertexShader);
		shaderAPI.BindPixelShader(in PixelShader);

		foreach (IMaterialVar var in shaderUniforms)
			shaderAPI.SetShaderUniform(var);
	}

	public void SetDefaultState() {
		DepthFunc(ShaderDepthFunc.NearerOrEqual);
		EnableColorWrites(true);
		EnableAlphaWrites(true);
		EnableDepthWrites(true);
		EnableDepthTest(true);
		EnableBlending(false);
		EnableCulling(true);
		PolyMode(ShaderPolyModeFace.FrontAndBack, ShaderPolyMode.Fill);
		BlendFunc(ShaderBlendFactor.One, ShaderBlendFactor.Zero);
		BlendOp(ShaderBlendOp.Add);
		EnableBlendingSeparateAlpha(false);
		BlendFuncSeparateAlpha(ShaderBlendFactor.One, ShaderBlendFactor.Zero);
		BlendOpSeparateAlpha(ShaderBlendOp.Add);
		EnablePolyOffset(PolygonOffsetMode.Disable);
	}

	public void EnableConstantColor(bool enable) => throw new NotImplementedException();
	public void EnableVertexBlend(bool enable) => throw new NotImplementedException();
	public void OverbrightValue(TextureStage stage, float value) => throw new NotImplementedException();

	readonly bool[] samplerState = new bool[(int)Sampler.MaxSamplers];
	public void EnableTexture(Sampler sampler, bool enable) {
		if ((int)sampler < samplerState.Length)
			samplerState[(int)sampler] = enable;
		else
			Warning($"Attempting to bind a texture to an invalid sampler {(int)sampler}!\n");
	}

	public void EnableTexGen(TextureStage stage, bool enable) => throw new NotImplementedException();
	public void TexGen(TextureStage stage, ShaderTexGenParam param) => throw new NotImplementedException();
	public void EnableCustomPixelPipe(bool enable) => throw new NotImplementedException();
	public void CustomTextureStages(int stageCount) => throw new NotImplementedException();
	public void CustomTextureOperation(TextureStage stage, ShaderTexChannel channel, ShaderTexOp op, ShaderTexArg arg1, ShaderTexArg arg2) => throw new NotImplementedException();
	public void EnableAlphaPipe(bool enable) => throw new NotImplementedException();
	public void EnableConstantAlpha(bool enable) => throw new NotImplementedException();
	public void EnableVertexAlpha(bool enable) => throw new NotImplementedException();
	public void EnableTextureAlpha(TextureStage stage, bool enable) => throw new NotImplementedException();
	public void FogMode(ShaderFogMode fogMode) => throw new NotImplementedException();
	public void SetDiffuseMaterialSource(ShaderMaterialSource materialSource) => throw new NotImplementedException();
	public void DisableFogGammaCorrection(bool bDisable) => throw new NotImplementedException();
	public void SetShadowDepthFiltering(Sampler stage) => throw new NotImplementedException();

	public GraphicsDriver GetDriver() => shaderAPI.GetDriver();
	public VertexFormat GetVertexFormat() => vertexFormat;
	public ShaderFlags GetFlags() => flags;
	public void SetFlags(ShaderFlags newFlags) => flags = newFlags;
}
