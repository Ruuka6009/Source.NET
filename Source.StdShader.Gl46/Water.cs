using Source.Common.MaterialSystem;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

/// <summary>
/// Water surfaces. Real water needs reflection and refraction render targets, which the backends
/// do not provide yet, so this takes the same route Source itself takes on hardware that cannot
/// manage them: fall back to an unlit base-texture pass. Without this, water materials resolve to
/// nothing and draw as the error checkerboard.
/// </summary>
public class Water : BaseVSShader
{
	public static string HelpString = "Help for Water";
	public static int Flags = 0;

	protected override void OnInitShaderParams(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		SetFlags(vars, MaterialVarFlags.NoFog);
	}

	public override string? GetFallbackShader(IMaterialVar[] vars) => "UnlitGeneric";

	protected override void OnInitShaderInstance(IMaterialVar[] vars, ReadOnlySpan<char> materialName) { }

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) { }
}
