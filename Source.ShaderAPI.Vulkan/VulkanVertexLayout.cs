using Silk.NET.Vulkan;

using Source.Common.MaterialSystem;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Maps Source's <see cref="VertexFormat"/> onto Vulkan vertex input state. The attribute
/// locations are the GL backend's shader input contract (OpenGL_ShaderInputAttribute), which the
/// *_vk13 shaders reuse via layout(location = N). Element offsets follow the exact order of
/// VertexBufferGl46.ComputeVertexDescription so CPU writes and GPU reads agree.
///
/// Attributes a format lacks are sourced from a zero buffer at binding 1 with stride 0 (legal in
/// Vulkan: every vertex reads the same zeros). GL's disabled-attribute default is (0,0,0,1) — the
/// alpha differs, accepted for bring-up.
/// </summary>
public static class VulkanVertexLayout
{
	// Shader input locations (mirror of OpenGL_ShaderInputAttribute).
	public const uint LocPosition = 0;
	public const uint LocNormal = 1;
	public const uint LocColor = 2;
	public const uint LocSpecular = 3;
	public const uint LocTangentS = 4;
	public const uint LocTangentT = 5;
	public const uint LocWrinkle = 6;
	public const uint LocBoneIndex = 7;
	public const uint LocBoneWeights = 8;
	public const uint LocUserData = 9;
	public const uint LocTexCoord0 = 10;
	public const int LocationCount = 18; // TexCoord0..7 inclusive

	public const uint VertexDataBinding = 0;
	public const uint ZeroBufferBinding = 1;

	static Format FloatFormat(int count) => count switch {
		1 => Format.R32Sfloat,
		2 => Format.R32G32Sfloat,
		3 => Format.R32G32B32Sfloat,
		4 => Format.R32G32B32A32Sfloat,
		_ => throw new NotSupportedException()
	};

	/// <summary>Format used when the vertex format does not carry this location (zero-buffer source).</summary>
	static Format AbsentFormat(uint location) => location switch {
		LocColor or LocSpecular => Format.R8G8B8A8Unorm,
		LocBoneIndex => Format.R8G8B8A8Uint,
		LocWrinkle => Format.R32Sfloat,
		_ => Format.R32G32B32A32Sfloat
	};

	/// <summary>
	/// Fills all <see cref="LocationCount"/> attribute descriptions for the given format and
	/// returns the vertex stride. Attributes present in the format read binding 0 at their
	/// computed offset; absent ones read the zero buffer at binding 1.
	/// </summary>
	public static uint BuildAttributes(VertexFormat format, Span<VertexInputAttributeDescription> attributes) {
		for (uint loc = 0; loc < LocationCount; loc++)
			attributes[(int)loc] = new VertexInputAttributeDescription(loc, ZeroBufferBinding, AbsentFormat(loc), 0);

		uint offset = 0;
		void Place(Span<VertexInputAttributeDescription> attributes, uint location, Format vkFormat, uint size) {
			attributes[(int)location] = new VertexInputAttributeDescription(location, VertexDataBinding, vkFormat, offset);
			offset += size;
		}

		// Same element order as ComputeVertexDescription / RecomputeVAO.
		if ((format & VertexFormat.Position) != 0)
			Place(attributes, LocPosition, Format.R32G32B32Sfloat, 12);

		if ((format & VertexFormat.BoneIndex) != 0) {
			int numBoneWeights = format.GetBoneWeightsSize();
			if (numBoneWeights > 0)
				Place(attributes, LocBoneWeights, FloatFormat(numBoneWeights), (uint)(numBoneWeights * 4));
			Place(attributes, LocBoneIndex, Format.R8G8B8A8Uint, 4);
		}

		if ((format & VertexFormat.Normal) != 0)
			Place(attributes, LocNormal, Format.R32G32B32Sfloat, 12);

		if ((format & VertexFormat.Color) != 0)
			Place(attributes, LocColor, Format.R8G8B8A8Unorm, 4);

		if ((format & VertexFormat.Specular) != 0)
			Place(attributes, LocSpecular, Format.R8G8B8A8Unorm, 4);

		for (int i = 0; i < IMesh.VERTEX_MAX_TEXTURE_COORDINATES; i++) {
			int texCoordSize = format.GetTexCoordDimensionSize(i);
			if (texCoordSize > 0)
				Place(attributes, (uint)(LocTexCoord0 + i), FloatFormat(texCoordSize), (uint)(texCoordSize * 4));
		}

		if ((format & VertexFormat.TangentS) != 0)
			Place(attributes, LocTangentS, Format.R32G32B32Sfloat, 12);

		if ((format & VertexFormat.TangentT) != 0)
			Place(attributes, LocTangentT, Format.R32G32B32Sfloat, 12);

		int userDataSize = format.GetUserDataSize();
		if (userDataSize > 0)
			Place(attributes, LocUserData, FloatFormat(userDataSize), (uint)(userDataSize * 4));

		// Wrinkle is not part of the GL vertex layout either (RecomputeVAO disables it).
		return offset;
	}

	public static PrimitiveTopology Topology(MaterialPrimitiveType type) => type switch {
		MaterialPrimitiveType.Points => PrimitiveTopology.PointList,
		MaterialPrimitiveType.Lines => PrimitiveTopology.LineList,
		MaterialPrimitiveType.Triangles => PrimitiveTopology.TriangleList,
		MaterialPrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
		MaterialPrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
		_ => throw new NotSupportedException($"Vulkan: unsupported primitive type {type}")
	};
}
