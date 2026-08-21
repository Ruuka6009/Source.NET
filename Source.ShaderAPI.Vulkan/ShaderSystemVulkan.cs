using Microsoft.Extensions.DependencyInjection;

using Source.Common.Filesystem;
using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;
using Source.Common.ShaderLib;
using Source.MaterialSystem;

using System.Numerics;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Vulkan shader system. The material-facing logic mirrors the Gl46 ShaderSystem; the actual
/// shader loading (SPIR-V from disk, per Phase 5 of VULKAN_TODO.md) is stubbed to INVALID so
/// std shaders can run their snapshot/draw passes against the stub states without a real pipeline.
/// </summary>
public interface IShaderSystemInternalVulkan : IShaderInit, IShaderSystem;

public class ShaderSystemVulkan(IServiceProvider services, IFileSystem fileSystem) : IShaderSystemInternalVulkan
{
	readonly List<IShaderDLL> ShaderDLLs = [];
	IShaderShadow? RenderState;
	private IMaterialSystem? _MaterialSystem;
	private IShaderAPI? _ShaderAPI;

	private IMaterialSystem MaterialSystem => _MaterialSystem ??= Singleton<IMaterialSystem>();
	private IShaderAPI ShaderAPI => _ShaderAPI ??= Singleton<IShaderAPI>();
	IShaderDevice? _ShaderDevice;
	IShaderDevice? ShaderDevice => _ShaderDevice ??= Singleton<IShaderDevice>();
	IMaterialSystemHardwareConfig? _HardwareConfig;
	IMaterialSystemHardwareConfig HardwareConfig => _HardwareConfig ??= Singleton<IMaterialSystemHardwareConfig>();

	public void Init() { }

	public void BindTexture(Sampler sampler, ITexture texture, int frame) {
		if (texture == null) return;
		((ITextureInternal)texture).Bind(sampler, frame);
	}

	public void ResetShaderState() { }

	public void DrawElements(IShader shader, IMaterialVar[] parms, IShaderShadow renderState, VertexCompressionType vertexCompression, uint materialVarTimeStamp) {
		ShaderAPI.InvalidateDelayedShaderConstants();

		int materialVarFlags = parms[(int)ShaderMaterialVars.Flags].GetIntValue();
		if (((materialVarFlags & (int)MaterialVarFlags.Model) != 0) || (IsFlag2Set(parms, MaterialVarFlags2.SupportsHardwareSkinning) && (ShaderAPI.GetCurrentNumBones() > 0)))
			ShaderAPI.SetSkinningMatrices();

		if ((materialVarFlags & (uint)MaterialVarFlags.Flat) > 0)
			ShaderAPI.ShadeMode(ShadeMode.Flat);

		PrepForShaderDraw(shader, parms, renderState);
		shader.DrawElements(parms, null, ShaderAPI, vertexCompression);
		DoneWithShaderDraw();
	}

	public IShader? FindShader(ReadOnlySpan<char> shaderName) {
		foreach (var shaderDLL in ShaderDLLs) {
			foreach (var shader in shaderDLL.GetShaders()) {
				if (shaderName.Equals(shader.GetName(), StringComparison.OrdinalIgnoreCase))
					return shader;
			}
		}
		return null;
	}

	public IEnumerable<IShader> GetShaders() {
		foreach (var shaderDLL in ShaderDLLs) {
			foreach (var shader in shaderDLL.GetShaders())
				yield return shader;
		}
	}

	public bool LoadShaderDLL<T>(T shaderAPI) where T : IShaderDLL {
		ShaderDLLs.Add(shaderAPI);
		return true;
	}

	public void LoadAllShaderDLLs() {
		foreach (var dll in services.GetServices<IShaderDLL>())
			LoadShaderDLL(dll);
	}

	static readonly string?[] shaderStateStrings = [
		"$debug",
		"$no_fullbright",
		"$no_draw",
		"$use_in_fillrate_mode",

		"$vertexcolor",
		"$vertexalpha",
		"$selfillum",
		"$additive",
		"$alphatest",
		"$multipass",
		"$znearer",
		"$model",
		"$flat",
		"$nocull",
		"$nofog",
		"$ignorez",
		"$decal",
		"$envmapsphere",
		"$noalphamod",
		"$envmapcameraspace",
		"$basealphaenvmapmask",
		"$translucent",
		"$normalmapalphaenvmapmask",
		"$softwareskin",
		"$opaquetexture",
		"$envmapmode",
		"$nodecal",
		"$halflambert",
		"$wireframe",
		"$allowalphatocoverage",
		null
	];

	public ReadOnlySpan<char> ShaderStateString(int i) => shaderStateStrings[i];

	public void InitShaderParameters(IShader shader, IMaterialVar[] vars, ReadOnlySpan<char> materialName, ReadOnlySpan<char> textureGroupName) {
		PrepForShaderDraw(shader, vars, null);
		shader.InitShaderParams(vars, ShaderAPI, materialName);
		DoneWithShaderDraw();

		if (!vars[(int)ShaderMaterialVars.Color].IsDefined())
			vars[(int)ShaderMaterialVars.Color].SetVecValue(1, 1, 1);

		if (!vars[(int)ShaderMaterialVars.Alpha].IsDefined())
			vars[(int)ShaderMaterialVars.Alpha].SetFloatValue(1);

		int i;
		for (i = shader.GetNumParams(); --i >= 0;) {
			if (vars[i].IsDefined())
				continue;
			ShaderParamType type = shader.GetParamType(i);
			switch (type) {
				case ShaderParamType.Texture:
				case ShaderParamType.String:
					// Do nothing; we'll be loading in a string later
					break;
				case ShaderParamType.Material:
					vars[i].SetMaterialValue(null);
					break;
				case ShaderParamType.Bool:
				case ShaderParamType.Integer:
					vars[i].SetIntValue(0);
					break;
				case ShaderParamType.Color:
					vars[i].SetVecValue(1.0f, 1.0f, 1.0f);
					break;
				case ShaderParamType.Vec2:
					vars[i].SetVecValue(0.0f, 0.0f);
					break;
				case ShaderParamType.Vec3:
					vars[i].SetVecValue(0.0f, 0.0f, 0.0f);
					break;
				case ShaderParamType.Vec4:
					vars[i].SetVecValue(0.0f, 0.0f, 0.0f, 0.0f);
					break;
				case ShaderParamType.Float:
					vars[i].SetFloatValue(0);
					break;
				case ShaderParamType.FourCC:
					vars[i].SetFourCCValue(0, 0);
					break;
				case ShaderParamType.Matrix:
				case ShaderParamType.Matrix4x2: {
						Matrix4x4 identity = Matrix4x4.Identity;
						vars[i].SetMatrixValue(identity);
					}
					break;
				default:
					Dbg.Assert(false);
					break;
			}
		}
	}

	public void InitShaderInstance(IShader shader, IMaterialVar[]? shaderParams, ReadOnlySpan<char> materialName, ReadOnlySpan<char> textureGroupName) {
		PrepForShaderDraw(shader, shaderParams, null);
		shader.InitShaderInstance(shaderParams, ShaderAPI, this, materialName, textureGroupName);
		DoneWithShaderDraw();
	}

	public bool InitRenderState(IShader shader, IMaterialVar[] shaderParams, ref IShaderShadow renderState, ReadOnlySpan<char> materialName) {
		Assert(RenderState == null);
		InitState(shader, shaderParams, ref renderState);
		ComputeRenderStateFlagsFromSnapshot(renderState);
		return true;
	}

	private void ComputeRenderStateFlagsFromSnapshot(IShaderShadow renderState) {
		if (ShaderAPI.IsTranslucent(renderState))
			renderState.SetFlags(renderState.GetFlags() | ShaderFlags.OpacityTranslucent);
		else {
			if (ShaderAPI.IsAlphaTested(renderState))
				renderState.SetFlags(renderState.GetFlags() | ShaderFlags.OpacityAlphaTest);
			else
				renderState.SetFlags(renderState.GetFlags() | ShaderFlags.OpacityOpaque);
		}
	}

	private void InitState(IShader shader, IMaterialVar[] shaderParams, ref IShaderShadow renderState) {
		PrepForShaderDraw(shader, shaderParams, renderState);
		shader.DrawElements(shaderParams, renderState, null, VertexCompressionType.None);
		DoneWithShaderDraw();
	}

	private void DoneWithShaderDraw() {
		RenderState = null;
	}

	private void PrepForShaderDraw(IShader shader, Span<IMaterialVar> vars, IShaderShadow? renderState) {
		Assert(RenderState == null);
		RenderState = renderState;
		renderState?.Activate();
	}

	public void Draw(bool makeActualDrawCall = true) {
		Assert(RenderState);

		if (makeActualDrawCall)
			ShaderAPI.RenderPass();

		ShaderAPI.InvalidateDelayedShaderConstants();
	}

	public void LoadCubeMap(IMaterialVar[] parms, IMaterialVar textureVar, int additionalCreationFlags = 0) {
		if (!HardwareConfig.SupportsCubeMaps())
			return;

		if (textureVar.GetVarType() != MaterialVarType.String) {
			if (textureVar.GetVarType() != MaterialVarType.Texture)
				textureVar.SetTextureValue(MaterialSystem.GetErrorTexture());
			return;
		}

		if (stricmp(textureVar.GetStringValue(), "env_cubemap") == 0) {
			textureVar.SetTextureValue(ITextureInternal.EnvCubemap);
			SetFlags2(parms, MaterialVarFlags2.UsesEnvCubemap);
			return;
		}

		string textureName = textureVar.GetStringValue();
		if (HardwareConfig.GetHDRType() != HDRType.None)
			textureName += ".hdr";

		ITexture texture = MaterialSystem.FindTexture(textureName, TEXTURE_GROUP_CUBE_MAP, false, additionalCreationFlags)
			?? MaterialSystem.GetErrorTexture();

		textureVar.SetTextureValue(texture);
	}

	public void LoadTexture(IMaterialVar textureVar, ReadOnlySpan<char> textureGroupName, int additionalCreationFlags = 0) {
		if (textureVar.GetVarType() != MaterialVarType.String) {
			if (textureVar.GetVarType() != MaterialVarType.Texture)
				textureVar.SetTextureValue(MaterialSystem.GetErrorTexture());
			return;
		}

		ReadOnlySpan<char> name = textureVar.GetStringValue();
		if (name[0] == Path.PathSeparator || name[1] == Path.PathSeparator)
			name = name[1..];

		ITexture texture = MaterialSystem.FindTexture(name, textureGroupName, false, additionalCreationFlags);

		if (texture == null) {
			if (!ShaderDevice!.IsUsingGraphics())
				Warning($"Shader_t::LoadTexture: texture \"{name}.vtf\" doesn't exist\n");
			texture = MaterialSystem.GetErrorTexture();
		}

		textureVar.SetTextureValue(texture);
	}

	readonly Dictionary<ulong, VertexShaderHandle> vshs = [];
	readonly Dictionary<ulong, PixelShaderHandle> pshs = [];
	readonly HashSet<ulong> warnedShaders = [];

	static ulong ComboSymbol(ReadOnlySpan<char> name, ReadOnlySpan<char> defines) {
		ulong symbol = name.Hash();
		if (!defines.IsEmpty)
			symbol ^= defines.Hash();
		return symbol;
	}

	void WarnOnce(ReadOnlySpan<char> name, string message) {
		if (warnedShaders.Add(name.Hash()))
			Warning($"Vulkan: {message}\n");
	}

	/// <summary>Reads shaders/{name} (a .spv produced by the Game.Assets build) and wraps it in a VkShaderModule.</summary>
	nint LoadShaderModule(ReadOnlySpan<char> name, string typeName) {
		using IFileHandle? handle = fileSystem.Open($"shaders/{name}", FileOpenOptions.Read, "game");
		if (handle == null) {
			WarnOnce(name, $"{typeName} shader 'shaders/{name}' not found (is the Vulkan SDK installed so the build can produce SPIR-V?)");
			return 0;
		}

		byte[] spirv = new byte[handle.Stream.Length];
		handle.Stream.ReadExactly(spirv);

		nint module = ((ShaderAPIVulkan)ShaderAPI).CreateShaderModule(spirv);
		if (module == 0)
			WarnOnce(name, $"{typeName} shader module creation failed for '{name}'");
		else
			Msg($"Loaded SPIR-V shader: {name} ({typeName})\n");
		return module;
	}

	public VertexShaderHandle LoadVertexShader(ReadOnlySpan<char> name, ReadOnlySpan<char> defines = default) {
		ulong symbol = ComboSymbol(name, defines);
		if (vshs.TryGetValue(symbol, out VertexShaderHandle cached))
			return cached;

		nint module = LoadShaderModule(name, "Vertex");
		if (module == 0)
			return VertexShaderHandle.INVALID; // not cached so a later device/file appearance can retry

		VertexShaderHandle vsh = new(module);
		vshs[symbol] = vsh;
		return vsh;
	}

	public PixelShaderHandle LoadPixelShader(ReadOnlySpan<char> name, ReadOnlySpan<char> defines = default) {
		ulong symbol = ComboSymbol(name, defines);
		if (pshs.TryGetValue(symbol, out PixelShaderHandle cached))
			return cached;

		nint module = LoadShaderModule(name, "Pixel");
		if (module == 0)
			return PixelShaderHandle.INVALID;

		PixelShaderHandle psh = new(module);
		pshs[symbol] = psh;
		return psh;
	}
}
