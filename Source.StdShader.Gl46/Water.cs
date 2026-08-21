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

	protected override void OnInitShaderParams(IMaterialVar[] vars, ReadOnlySpan<char> materialName) {
		// Water is its own lighting model; the world's fog would double up on $fogcolor.
		SetFlags(vars, MaterialVarFlags.NoFog);
		SetFlags(vars, MaterialVarFlags.Translucent);
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

			// ps_const[0] = fog colour + reflect amount, ps_const[1].x = ripple strength
			Span<float> fog = stackalloc float[4];
			vars[FOGCOLOR].GetVecValue(fog[..3]);

			// With no cubemap bound the backends substitute a 1x1 white texture, and blending
			// toward that turns the whole surface white. Reflect nothing instead, so the water
			// shows its own colour.
			fog[3] = vars[ENVMAP].IsTexture() ? vars[REFLECTAMOUNT].GetFloatValue() : 0.0f;
			shaderAPI.SetPixelShaderConstant(0, fog);

			// Ripple strength stays 0 while no normal map is loaded (see OnInitShaderInstance).
			Span<float> ripple = [0, 0, 0, 0];
			shaderAPI.SetPixelShaderConstant(1, ripple);
		}
		Draw();
	}
}
