using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;

using System.Runtime.InteropServices;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// No-op mesh for the boot shim. Hands MeshBuilder dummy pointers with all element sizes zero,
/// so writes land harmlessly in scratch memory and never advance. Phase 4 replaces this with
/// real VkBuffer-backed meshes.
/// </summary>
public class DummyMeshVulkan : IMesh
{
	static unsafe ushort* dummyIndices;
	static unsafe float* dummyFloat;
	static unsafe byte* dummyByte;
	static unsafe void AllocDummyBuffers() {
		if (dummyIndices == null) dummyIndices = (ushort*)Marshal.AllocHGlobal(6 * sizeof(ushort));
		if (dummyFloat == null) dummyFloat = (float*)Marshal.AllocHGlobal(32 * sizeof(float));
		if (dummyByte == null) dummyByte = (byte*)Marshal.AllocHGlobal(32);
	}

	public void BeginCastBuffer(VertexFormat format) { }
	public void BeginCastBuffer(MaterialIndexFormat format) { }
	public void Draw(int firstIndex = -1, int indexCount = 0) { }
	public void Draw(ReadOnlySpan<PrimList> lists, int numLists) { }
	public void EndCastBuffer() { }
	public int GetRoomRemaining() => 1;
	public VertexFormat GetVertexFormat() => VertexFormat.Position;
	public int IndexCount() => 0;
	public MaterialIndexFormat IndexFormat() => MaterialIndexFormat.x16;
	public bool IsDynamic() => false;

	public unsafe bool Lock(int vertexCount, bool append, ref VertexDesc desc) {
		AllocDummyBuffers();
		memreset(ref desc);
		desc.Position = dummyFloat;
		desc.BoneWeight = dummyFloat;
		desc.BoneMatrixIndex = dummyByte;
		desc.Normal = dummyFloat;
		desc.Color = dummyByte;
		desc.Specular = dummyByte;
		for (int i = 0; i < IMesh.VERTEX_MAX_TEXTURE_COORDINATES; i++)
			desc.SetTexCoord(i, dummyFloat);
		desc.TangentS = dummyFloat;
		desc.TangentT = dummyFloat;
		desc.Wrinkle = dummyFloat;
		desc.UserData = dummyFloat;
		desc.FirstVertex = 0;
		desc.OffsetVertex = 0;
		return true;
	}

	public unsafe int Lock(bool readOnly, int firstIndex, int indexCount, ref IndexDesc desc) {
		AllocDummyBuffers();
		desc.Indices = dummyIndices;
		desc.IndexSize = 0;
		desc.FirstIndex = 0;
		desc.OffsetIndex = 0;
		return 0;
	}

	public void LockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		Lock(vertexCount, false, ref desc.Vertex);
		Lock(false, 0, indexCount, ref desc.Index);
	}

	public unsafe void ModifyBegin(int firstVertex, int vertexCount, int firstIndex, int indexCount, ref MeshDesc desc) {
		Lock(vertexCount, false, ref desc.Vertex);
		Lock(false, 0, indexCount, ref desc.Index);
	}

	public void ModifyEnd(ref MeshDesc desc) { }
	public void MarkAsDrawn() { }
	public void SetColorMesh(IMesh colorMesh, int vertexOffset) { }
	public void SetPrimitiveType(MaterialPrimitiveType type) { }
	public bool Unlock(int vertexCount, ref VertexDesc desc) => true;
	public bool Unlock(int writtenIndexCount, ref IndexDesc desc) => true;
	public void UnlockMesh(int vertexCount, int indexCount, ref MeshDesc desc) { }
	public int VertexCount() => 0;
}

public class MeshMgrVulkan : IMeshMgr
{
}
