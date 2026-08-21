using Source.Common.MaterialSystem;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

/// <summary>
/// Lightmapped world surfaces with a reflection term. The reflection needs a render target the
/// backends do not fill yet, so this falls back to the plain lightmapped path - the surface still
/// gets its base texture and lightmap, just no reflection.
/// </summary>
public class LightmappedReflective : BaseVSShader
{
	public static string HelpString = "Help for LightmappedReflective";
	public static int Flags = 0;

	protected override void OnInitShaderParams(IMaterialVar[] vars, ReadOnlySpan<char> materialName) { }

	public override string? GetFallbackShader(IMaterialVar[] vars) => "LightmappedGeneric";

	protected override void OnInitShaderInstance(IMaterialVar[] vars, ReadOnlySpan<char> materialName) { }

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) { }
}
