using Source.Common.Filesystem;

using System.Text;

namespace Source.ShaderAPI.Vulkan;

public readonly record struct ShaderComboVulkan(string Name, int Min, int Range);

/// <summary>
/// The GL backend compiles one shader variant per combo combination. Vulkan compiles a single
/// variant that branches on push constants instead, so the combo values have to survive the trip
/// from the shader classes to the GPU rather than being baked in.
///
/// The shader classes hand the backend a mixed-radix index built from
/// <c>GetStaticComboScale</c>/<c>GetDynamicComboScale</c>. This parses the same
/// <c>// STATIC:</c>/<c>// DYNAMIC:</c> declarations the GL backend reads so those scales come out
/// right, then decodes the resulting index back into named values and repacks them into the flat
/// bit layout <c>common_vk13.glsl</c> expects.
/// </summary>
public class VulkanShaderCombos(IFileSystem fileSystem)
{
	/// <summary>Must match the COMBO_* defines in common_vk13.glsl.</summary>
	static int ComboBit(ReadOnlySpan<char> name) {
		if (name.SequenceEqual("VERTEXCOLOR")) return 1 << 0;
		if (name.SequenceEqual("CUBEMAP")) return 1 << 1;
		if (name.SequenceEqual("HALFLAMBERT")) return 1 << 2;
		if (name.SequenceEqual("SELFILLUM")) return 1 << 3;
		if (name.SequenceEqual("ENVMAPMASK")) return 1 << 4;
		if (name.SequenceEqual("BASEALPHAENVMAPMASK")) return 1 << 5;
		if (name.SequenceEqual("NORMALMAPALPHAENVMAPMASK")) return 1 << 6;
		if (name.SequenceEqual("DYNAMIC_LIGHT")) return 1 << 7;
		if (name.SequenceEqual("STATIC_LIGHT")) return 1 << 8;
		return 0;
	}

	const int NumLightsShift = 9;

	readonly Dictionary<string, (List<ShaderComboVulkan> Static, List<ShaderComboVulkan> Dynamic)> cache = [];

	/// <summary>
	/// Combo declarations for a shader. <paramref name="name"/> is the .spv name the backend loads;
	/// the declarations live in the GLSL source next to it.
	/// </summary>
	public (List<ShaderComboVulkan> Static, List<ShaderComboVulkan> Dynamic) Get(ReadOnlySpan<char> name) {
		string key = new(name);
		if (cache.TryGetValue(key, out var cached))
			return cached;

		List<ShaderComboVulkan> statics = [];
		List<ShaderComboVulkan> dynamics = [];

		string sourceName = key.EndsWith(".spv", StringComparison.OrdinalIgnoreCase) ? key[..^4] : key;
		using IFileHandle? handle = fileSystem.Open($"shaders/{sourceName}", FileOpenOptions.Read, "game");
		if (handle != null) {
			byte[] bytes = new byte[handle.Stream.Length];
			handle.Stream.ReadExactly(bytes);
			Parse(Encoding.ASCII.GetString(bytes), statics, dynamics);
		}

		var result = (statics, dynamics);
		cache[key] = result;
		return result;
	}

	static void Parse(string source, List<ShaderComboVulkan> statics, List<ShaderComboVulkan> dynamics) {
		foreach (string rawLine in source.Split('\n')) {
			string line = rawLine.Trim();
			if (!line.StartsWith("//"))
				continue;

			ReadOnlySpan<char> body = line.AsSpan(2).TrimStart();
			List<ShaderComboVulkan>? list =
				body.StartsWith("STATIC:") ? statics :
				body.StartsWith("DYNAMIC:") ? dynamics : null;
			if (list == null)
				continue;

			int q1 = line.IndexOf('"');
			if (q1 < 0)
				continue;
			int q2 = line.IndexOf('"', q1 + 1);
			if (q2 < 0)
				continue;
			string comboName = line[(q1 + 1)..q2];

			int min = 0, max = 1;
			int q3 = line.IndexOf('"', q2 + 1);
			int q4 = q3 < 0 ? -1 : line.IndexOf('"', q3 + 1);
			if (q4 >= 0) {
				ReadOnlySpan<char> range = line[(q3 + 1)..q4];
				int dots = range.IndexOf("..");
				if (dots >= 0 && int.TryParse(range[..dots], out int parsedMin) && int.TryParse(range[(dots + 2)..], out int parsedMax)) {
					min = parsedMin;
					max = parsedMax;
				}
			}

			list.Add(new ShaderComboVulkan(comboName, min, max - min + 1));
		}
	}

	/// <summary>
	/// Scale a combo contributes to the packed index: the product of the ranges before it, so the
	/// index is mixed-radix exactly as the GL backend builds it. 0 for names this shader lacks.
	/// </summary>
	public static int Scale(List<ShaderComboVulkan> combos, ReadOnlySpan<char> name) {
		int scale = 1;
		for (int i = 0; i < combos.Count; i++) {
			if (name.SequenceEqual(combos[i].Name))
				return scale;
			scale *= combos[i].Range;
		}
		return 0;
	}

	/// <summary>Unpacks a mixed-radix index into the bit layout the shaders branch on.</summary>
	public static int Unpack(List<ShaderComboVulkan> combos, int index) {
		int bits = 0;
		int scale = 1;
		foreach (ShaderComboVulkan combo in combos) {
			int value = combo.Min + (index / scale) % combo.Range;
			scale *= combo.Range;

			if (combo.Name == "NUM_LIGHTS")
				bits |= (value & 7) << NumLightsShift;
			else if (value != 0)
				bits |= ComboBit(combo.Name);
		}
		return bits;
	}
}
