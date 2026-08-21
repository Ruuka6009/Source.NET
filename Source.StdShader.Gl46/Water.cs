using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;

namespace Source.StdShader.Gl46;

/// <summary>
/// Water surfaces.
///
/// Source's real water renders the scene twice into reflection and refraction render targets and
/// samples those. Neither exists here yet - there is no water view in the engine - so this shades
/// water from what is available: a normal map for ripples, the env cubemap for reflection, and
/// $fogcolor for the water body, blended by fresnel so it reflects at grazing angles and shows the
/// water colour when looked straight down. Translucent, so whatever is behind still reads through.
/// </summary>
public class Water : BaseVSShader
{
	public static string HelpString = "Help for Water";
	public static int Flags = 0;
	public static List<ShaderParam> ShaderParams = [];
	public static ShaderParam[] ShaderParamOverrides = new ShaderParam[(int)ShaderMaterialVars.Count];

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

	public static readonly ShaderParam NORMALMAP = new("$normalmap", ShaderParamType.Texture, "dev/bump_normal", "water normal map");
	public static readonly ShaderParam BUMPMAP = new("$bumpmap", ShaderParamType.Texture, "dev/bump_normal", "water normal map");
	public static readonly ShaderParam BUMPFRAME = new("$bumpframe", ShaderParamType.Integer, "0", "");
	public static readonly ShaderParam ENVMAP = new("$envmap", ShaderParamType.Texture, "env_cubemap", "reflection cubemap");
	public static readonly ShaderParam ENVMAPFRAME = new("$envmapframe", ShaderParamType.Integer, "0", "");
	public static readonly ShaderParam FOGCOLOR = new("$fogcolor", ShaderParamType.Color, "[0.2 0.35 0.4]", "water body colour");
	public static readonly ShaderParam FOGENABLE = new("$fogenable", ShaderParamType.Bool, "1", "");
	public static readonly ShaderParam REFLECTAMOUNT = new("$reflectamount", ShaderParamType.Float, "0.8", "how strongly the surface reflects");
	public static readonly ShaderParam REFRACTAMOUNT = new("$refractamount", ShaderParamType.Float, "0.2", "ripple strength");
	public static readonly ShaderParam WAVESCALE = new("$wavescale", ShaderParamType.Float, "0.06", "world units to wave space");
	public static readonly ShaderParam WAVESTRENGTH = new("$wavestrength", ShaderParamType.Float, "0.055", "how far the ripples tilt the surface");
	public static readonly ShaderParam SUNDIR = new("$sundir", ShaderParamType.Vec3, "[0.4 0.3 0.55]", "direction the sun glitter comes from");
	public static readonly ShaderParam SUNAMOUNT = new("$sunamount", ShaderParamType.Float, "1.6", "sun glitter intensity");
	public static readonly ShaderParam ABOVEWATER = new("$abovewater", ShaderParamType.Bool, "1", "");
	public static readonly ShaderParam BOTTOMMATERIAL = new("$bottommaterial", ShaderParamType.String, "", "");
	public static readonly ShaderParam SCALE = new("$scale", ShaderParamType.Vec2, "[1 1]", "");

	public override int GetFlags() => Flags;
	public override int GetNumParams() => base.GetNumParams() + ShaderParams.Count;
	public override ReadOnlySpan<char> GetParamName(int paramIndex) {
		int baseCount = base.GetNumParams();
		return paramIndex < baseCount ? base.GetParamName(paramIndex) : ShaderParams[paramIndex - baseCount].GetName();
	}
	public override ReadOnlySpan<char> GetParamHelp(int paramIndex) {
		int baseCount = base.GetNumParams();
		return paramIndex < baseCount ? base.GetParamHelp(paramIndex) : ShaderParams[paramIndex - baseCount].GetHelp();
	}
	public override ShaderParamType GetParamType(int paramIndex) {
		int baseCount = base.GetNumParams();
		return paramIndex < baseCount ? base.GetParamType(paramIndex) : ShaderParams[paramIndex - baseCount].GetType();
	}
	public override ReadOnlySpan<char> GetParamDefault(int paramIndex) {
		int baseCount = base.GetNumParams();
		return paramIndex < baseCount ? base.GetParamDefault(paramIndex) : ShaderParams[paramIndex - baseCount].GetDefaultValue();
	}

	public override string? GetFallbackShader(IMaterialVar[] vars) => null;

	// The engine's SetFrameTime is a stub, so the animation keeps its own monotonic clock. Water
	// ripples are ambient - they do not need to be in step with game time.
	static readonly System.Diagnostics.Stopwatch waveClock = System.Diagnostics.Stopwatch.StartNew();
	static float WaveTime => (float)waveClock.Elapsed.TotalSeconds;

	protected override void OnInitShaderParams(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		// Water is its own lighting model; the world's fog would double up on $fogcolor.
		SetFlags(vars, MaterialVarFlags.NoFog);
		SetFlags(vars, MaterialVarFlags.Translucent);

		// Anything still undefined after this gets zeroed by InitShaderParameters - it does not
		// fall back to the declared default string. Zero wave strength is a mirror-flat surface
		// and zero sun amount is no glitter, so the tuning values have to be set here.
		if (!vars[WAVESCALE].IsDefined()) vars[WAVESCALE].SetFloatValue(0.048f);
		if (!vars[WAVESTRENGTH].IsDefined()) vars[WAVESTRENGTH].SetFloatValue(0.095f);
		if (!vars[SUNAMOUNT].IsDefined()) vars[SUNAMOUNT].SetFloatValue(1.1f);
		if (!vars[SUNDIR].IsDefined()) vars[SUNDIR].SetVecValue(0.40f, 0.30f, 0.55f);
		if (!vars[REFLECTAMOUNT].IsDefined()) vars[REFLECTAMOUNT].SetFloatValue(0.8f);
		if (!vars[FOGCOLOR].IsDefined()) vars[FOGCOLOR].SetVecValue(0.11f, 0.26f, 0.29f);
	}

	protected override void OnInitShaderInstance(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		// $normalmap on water is usually a dU/dV map (UV88). Nothing loads those yet - Texture.cs
		// asserts on UV88/UVWQ8888/UVLX8888, and ImageFormatGl46 has no entry for them at all, so
		// loading one would take GL down. Ripples stay off until those formats are supported.
		if (vars[ENVMAP].IsDefined())
			LoadCubeMap(ENVMAP);
	}

	protected override void OnDrawElements(IMaterialVar[] vars, IShaderDynamicAPI shaderAPI, VertexCompressionType vertexCompression) {
		if (IsSnapshotting()) {
			ShaderShadow!.EnableTexture(Sampler.Sampler0, true);   // envmap

			ShaderShadow!.EnableDepthWrites(false);
			EnableAlphaBlending(ShaderBlendFactor.SrcAlpha, ShaderBlendFactor.OneMinusSrcAlpha);

			ShaderShadow!.VertexShaderVertexFormat(VertexFormat.Position | VertexFormat.Normal | VertexFormat.TexCoord2D_0, 1, null, 0);

			ShaderShadow.SetVertexShader("water");
			ShaderShadow.SetPixelShader("water");

			SetStandardShaderUniforms();
		}
		else {
			if (vars[ENVMAP].IsTexture())
				BindTexture(Sampler.Sampler0, ENVMAP, ENVMAPFRAME);

			SetVertexShaderTextureTransform(48, (int)ShaderMaterialVars.BaseTextureTransform);

			bool hasEnvmap = vars[ENVMAP].IsTexture();

			Span<float> fog = stackalloc float[4];
			vars[FOGCOLOR].GetVecValue(fog[..3]);
			// Reflection always contributes: a real cubemap when the material has one, the
			// shader's procedural sky otherwise. Only the 1x1 white fallback is worth avoiding.
			fog[3] = vars[REFLECTAMOUNT].GetFloatValue();
			shaderAPI.SetPixelShaderConstant(0, fog);

			Span<float> wave = [
				WaveTime,
				vars[WAVESCALE].GetFloatValue(),
				vars[WAVESTRENGTH].GetFloatValue(),
				hasEnvmap ? 1.0f : 0.0f
			];
			shaderAPI.SetPixelShaderConstant(1, wave);

			Span<float> sun = stackalloc float[4];
			vars[SUNDIR].GetVecValue(sun[..3]);
			sun[3] = vars[SUNAMOUNT].GetFloatValue();
			shaderAPI.SetPixelShaderConstant(2, sun);
		}
		Draw();
	}
}
