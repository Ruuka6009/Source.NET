using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Snapshot ("shadow") state stub. Phase 3 turns this into the pipeline description that keys
/// the VkPipeline cache; for the boot shim it just records the few bits the material system
/// reads back (flags, vertex format, blending for translucency queries).
/// </summary>
public class ShadowStateVulkan(ShaderAPIVulkan shaderAPI, ReadOnlySpan<char> name) : IShaderShadow
{
	public readonly string Name = new(name);
	ShaderFlags flags;
	VertexFormat vertexFormat;

	internal bool Blending;
	internal bool AlphaTesting;

	public void DepthFunc(ShaderDepthFunc depthFunc) { }
	public void EnableDepthWrites(bool enable) { }
	public void EnableDepthTest(bool enable) { }
	public void EnablePolyOffset(PolygonOffsetMode offsetMode) { }
	public void EnableColorWrites(bool enable) { }
	public void EnableAlphaWrites(bool enable) { }
	public void EnableBlending(bool enable) => Blending = enable;
	public void BlendFunc(ShaderBlendFactor srcFactor, ShaderBlendFactor dstFactor) { }
	public void EnableAlphaTest(bool enable) => AlphaTesting = enable;
	public void AlphaFunc(ShaderAlphaFunc alphaFunc, float alphaRef) { }
	public void PolyMode(ShaderPolyModeFace face, ShaderPolyMode polyMode) { }
	public void EnableCulling(bool enable) { }
	public void EnableConstantColor(bool enable) { }
	public void VertexShaderVertexFormat(VertexFormat format, int texCoordCount, Span<int> texCoordDimensions, int userDataSize)
		=> vertexFormat = format;
	public void SetVertexShader(ReadOnlySpan<char> fileName, int staticIndex = 0) { }
	public void SetPixelShader(ReadOnlySpan<char> fileName, int staticIndex = 0) { }
	public int GetStaticComboScale(ShaderType type, ReadOnlySpan<char> fileName, ReadOnlySpan<char> name) => 1;
	public void EnableVertexBlend(bool enable) { }
	public void OverbrightValue(TextureStage stage, float value) { }
	public void EnableTexture(Sampler sampler, bool enable) { }
	public void EnableTexGen(TextureStage stage, bool enable) { }
	public void TexGen(TextureStage stage, ShaderTexGenParam param) { }
	public void EnableCustomPixelPipe(bool enable) { }
	public void CustomTextureStages(int stageCount) { }
	public void CustomTextureOperation(TextureStage stage, ShaderTexChannel channel, ShaderTexOp op, ShaderTexArg arg1, ShaderTexArg arg2) { }
	public void EnableAlphaPipe(bool enable) { }
	public void EnableConstantAlpha(bool enable) { }
	public void EnableVertexAlpha(bool enable) { }
	public void EnableTextureAlpha(TextureStage stage, bool enable) { }
	public void EnableBlendingSeparateAlpha(bool enable) { }
	public void BlendFuncSeparateAlpha(ShaderBlendFactor srcFactor, ShaderBlendFactor dstFactor) { }
	public void FogMode(ShaderFogMode fogMode) { }
	public void SetDiffuseMaterialSource(ShaderMaterialSource materialSource) { }
	public void SetShaderUniform(IMaterialVar textureVar) { }
	public void DisableFogGammaCorrection(bool bDisable) { }
	public void EnableAlphaToCoverage(bool enable) { }
	public void SetShadowDepthFiltering(Sampler stage) { }
	public void BlendOp(ShaderBlendOp blendOp) { }
	public void BlendOpSeparateAlpha(ShaderBlendOp blendOp) { }
	public GraphicsDriver GetDriver() => shaderAPI.GetDriver();
	public void SetDefaultState() { }
	public VertexFormat GetVertexFormat() => vertexFormat;
	public void Activate() { }
	public ShaderFlags GetFlags() => flags;
	public void SetFlags(ShaderFlags newFlags) => flags = newFlags;
}
