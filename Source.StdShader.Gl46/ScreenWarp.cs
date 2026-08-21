using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

/// <summary>
/// Draws a captured frame back over the screen with its UVs rippled, which is how the underwater
/// distortion is done: copy the frame into _rt_FullFrameFB, then draw it through this.
///
/// Opaque and depth-test free, because it replaces the whole screen rather than compositing over
/// it. Anything that should stay sharp - the rest of the HUD - simply draws afterwards.
/// </summary>
public class ScreenWarp : BaseVSShader
{
	public static string HelpString = "Help for ScreenWarp";
	public static int Flags = 0;
	public static List<ShaderParam> ShaderParams = [];

	public class ShaderParam
	{
		public readonly ShaderParamInfo Info;
		public readonly int Index;
		public ShaderParam(string name, ShaderParamType type, ReadOnlySpan<char> defaultParam, ReadOnlySpan<char> help, int flags = 0) {
			Info.Name = name;
			Info.Type = type;
			Info.DefaultValue = new(defaultParam);
			Info.Help = new(help);
			Info.Flags = (ShaderParamFlags)flags;
			Index = (int)ShaderMaterialVars.Count + ShaderParams.Count;
			ShaderParams.Add(this);
		}
		public static implicit operator int(ShaderParam param) => param.Index;
		public ReadOnlySpan<char> GetName() => Info.Name;
		public ShaderParamType GetType() => Info.Type;
		public ReadOnlySpan<char> GetDefaultValue() => Info.DefaultValue;
		public int GetFlags() => (int)Info.Flags;
		public ReadOnlySpan<char> GetHelp() => Info.Help;
	}

	public static readonly ShaderParam WARPAMOUNT = new("$warpamount", ShaderParamType.Float, "1", "how far the ripples bend the frame");
	public static readonly ShaderParam TINTAMOUNT = new("$tintamount", ShaderParamType.Float, "0.55", "how strongly the water colour takes over");
	public static readonly ShaderParam TINTCOLOR = new("$tintcolor", ShaderParamType.Color, "[0.25 0.55 0.65]", "water colour");
	public static readonly ShaderParam EDGEDARKEN = new("$edgedarken", ShaderParamType.Float, "0.55", "falloff toward the edges of vision");

	public override int GetFlags() => Flags;
	public override int GetNumParams() => base.GetNumParams() + ShaderParams.Count;
	public override ReadOnlySpan<char> GetParamName(int i) {
		int b = base.GetNumParams();
		return i < b ? base.GetParamName(i) : ShaderParams[i - b].GetName();
	}
	public override ReadOnlySpan<char> GetParamHelp(int i) {
		int b = base.GetNumParams();
		return i < b ? base.GetParamHelp(i) : ShaderParams[i - b].GetHelp();
	}
	public override ShaderParamType GetParamType(int i) {
		int b = base.GetNumParams();
		return i < b ? base.GetParamType(i) : ShaderParams[i - b].GetType();
	}
	public override ReadOnlySpan<char> GetParamDefault(int i) {
		int b = base.GetNumParams();
		return i < b ? base.GetParamDefault(i) : ShaderParams[i - b].GetDefaultValue();
	}

	public override string? GetFallbackShader(IMaterialVar[] vars) => null;

	// Its own clock, like Water - the engine's SetFrameTime is a stub.
	static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
	static float Time => (float)clock.Elapsed.TotalSeconds;

	protected override void OnInitShaderParams(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		SetFlags(vars, MaterialVarFlags.NoFog);

		// Undefined params are zeroed rather than taking the declared default, so set them here.
		if (!vars[WARPAMOUNT].IsDefined()) vars[WARPAMOUNT].SetFloatValue(1.0f);
		if (!vars[TINTAMOUNT].IsDefined()) vars[TINTAMOUNT].SetFloatValue(0.55f);
		if (!vars[EDGEDARKEN].IsDefined()) vars[EDGEDARKEN].SetFloatValue(0.55f);
		if (!vars[TINTCOLOR].IsDefined()) vars[TINTCOLOR].SetVecValue(0.25f, 0.55f, 0.65f);
	}

	protected override void OnInitShaderInstance(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		if (vars[(int)ShaderMaterialVars.BaseTexture].IsDefined())
			LoadTexture((int)ShaderMaterialVars.BaseTexture);
	}

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) {
		if (IsSnapshotting()) {
			ShaderShadow!.EnableTexture(Sampler.Sampler0, true);

			ShaderShadow!.EnableDepthTest(false);
			ShaderShadow!.EnableDepthWrites(false);
			ShaderShadow!.EnableBlending(false);
			ShaderShadow!.EnableCulling(false);

			ShaderShadow!.VertexShaderVertexFormat(VertexFormat.Position | VertexFormat.TexCoord2D_0, 1, null, 0);

			ShaderShadow.SetVertexShader("screenwarp");
			ShaderShadow.SetPixelShader("screenwarp");

			SetStandardShaderUniforms();
		}
		else {
			BindTexture(Sampler.Sampler0, (int)ShaderMaterialVars.BaseTexture, (int)ShaderMaterialVars.Frame);

			Span<float> parms = [
				Time,
				vars[WARPAMOUNT].GetFloatValue(),
				vars[TINTAMOUNT].GetFloatValue(),
				vars[EDGEDARKEN].GetFloatValue()
			];
			shaderAPI.SetPixelShaderConstant(0, parms);

			Span<float> tint = stackalloc float[4];
			vars[TINTCOLOR].GetVecValue(tint[..3]);
			tint[3] = 1.0f;
			shaderAPI.SetPixelShaderConstant(1, tint);
		}
		Draw();
	}
}
