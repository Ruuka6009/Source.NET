using Source.Common.MaterialSystem;
using Source.Common.ShaderAPI;

using System.Runtime.InteropServices;

namespace Source.ShaderAPI.Vulkan;

public struct PrimListVulkan
{
	public int FirstIndex;
	public int NumIndices;
}

/// <summary>
/// Port of MeshGl46. Same lock/unlock and prim-list flow; RenderPass
/// records vkCmdBindVertexBuffers/vkCmdBindIndexBuffer/vkCmdDrawIndexed through the shader API,
/// which has already resolved the pipeline for this mesh's vertex format and topology.
/// </summary>
public unsafe class MeshVulkan : IMesh
{
	public ShaderAPIVulkan ShaderAPI = null!;
	public IShaderUtil ShaderUtil = null!;
	public MeshMgrVulkan MeshMgr = null!;
	public IShaderDevice ShaderDevice = null!;

	protected VertexBufferVulkan? VertexBuffer;
	protected IndexBufferVulkan? IndexBuffer;

	protected VertexFormat VertexFormat;
	protected IMaterialInternal? Material;
	protected MaterialPrimitiveType Type = MaterialPrimitiveType.Triangles;
	protected bool IsDrawing;
	public bool Locked;

	protected static PrimListVulkan* s_Prims;
	protected static int s_PrimsCount;
	protected static uint s_FirstVertex;
	protected static uint s_NumVertices;

	public VertexBufferVulkan? GetVertexBuffer() => VertexBuffer;
	public IndexBufferVulkan? GetIndexBuffer() => IndexBuffer;

	public virtual void BeginCastBuffer(VertexFormat format) => throw new NotImplementedException();
	public virtual void BeginCastBuffer(MaterialIndexFormat format) => throw new NotImplementedException();
	public virtual void EndCastBuffer() => throw new NotImplementedException();
	public virtual int GetRoomRemaining() => throw new NotImplementedException();
	public virtual int IndexCount() => throw new NotImplementedException();
	public virtual MaterialIndexFormat IndexFormat() => throw new NotImplementedException();
	public virtual bool IsDynamic() => throw new NotImplementedException();

	public void DrawMesh() {
		Assert(!IsDrawing);
		IsDrawing = true;

		ShaderAPI.DrawMesh(this);

		IsDrawing = false;
	}

	public virtual unsafe void Draw(int firstIndex = -1, int indexCount = 0) {
		Assert(VertexBuffer != null);
		if (VertexBuffer == null)
			return;

		if (!ShaderUtil.OnDrawMesh(this, firstIndex, indexCount)) {
			MarkAsDrawn();
			return;
		}

		PrimListVulkan* primList = stackalloc PrimListVulkan[1];
		if (firstIndex == -1 || indexCount == 0) {
			primList->FirstIndex = 0;
			primList->NumIndices = NumIndices;
		}
		else {
			primList->FirstIndex = firstIndex;
			primList->NumIndices = indexCount;
		}
		DrawInternal(primList, 1);
	}

	public virtual void Draw(ReadOnlySpan<Source.Common.MaterialSystem.PrimList> lists, int numLists) {
		Assert(VertexBuffer != null);
		if (VertexBuffer == null)
			return;

		if (!ShaderUtil.OnDrawMesh(this, -1, 0)) {
			MarkAsDrawn();
			return;
		}

		fixed (PrimListVulkan* p = MemoryMarshal.Cast<Source.Common.MaterialSystem.PrimList, PrimListVulkan>(lists))
			DrawInternal(p, numLists);
	}

	private unsafe void DrawInternal(PrimListVulkan* primList, int lists) {
		int i;
		for (i = 0; i < lists; i++) {
			if (primList[i].NumIndices > 0)
				break;
		}

		if (i == lists)
			return;

		if (ShaderDevice.IsDeactivated())
			return;

		s_Prims = primList;
		s_PrimsCount = lists;
		s_FirstVertex = 0;
		s_NumVertices = (uint)VertexBuffer!.VertexCount;

		DrawMesh();

		s_Prims = null;
		s_PrimsCount = 0;
	}

	public virtual VertexFormat GetVertexFormat() => VertexFormat;

	public virtual bool Lock(int vertexCount, bool append, ref VertexDesc desc) {
		if (VertexBuffer == null) {
			int size = MeshMgr.VertexFormatSize(VertexFormat);
			VertexBuffer = new VertexBufferVulkan(ShaderAPI, VertexFormat, size, vertexCount, false);
		}

		byte* vertexMemory = VertexBuffer.Lock(vertexCount, out desc.FirstVertex);
		VertexBufferVulkan.ComputeVertexDescription(vertexMemory, VertexFormat, ref desc);

		return true;
	}

	bool IsIBLocked;
	static readonly ushort* ScratchIndexBuffer = (ushort*)NativeMemory.Alloc(6 * sizeof(ushort));

	public virtual int Lock(bool readOnly, int firstIndex, int indexCount, ref IndexDesc desc) {
		if (ShaderDevice.IsDeactivated() || indexCount == 0) {
			desc.Indices = ScratchIndexBuffer;
			desc.IndexSize = 0;
			return 0;
		}

		IndexBuffer ??= new IndexBufferVulkan(ShaderAPI, indexCount, false);

		desc.Indices = (ushort*)IndexBuffer.Lock(readOnly, indexCount, out int startIndex, firstIndex);
		if (desc.Indices == null) {
			desc.IndexSize = 0;
			Assert(false);
			Warning("Failed to lock index buffer...\n");
			return 0;
		}

		desc.IndexSize = 1;
		IsIBLocked = true;
		return startIndex;
	}

	public virtual void LockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		ShaderUtil.SyncMatrices();

		Lock(vertexCount, false, ref desc.Vertex);
		if (Type != MaterialPrimitiveType.Points)
			Lock(false, -1, indexCount, ref desc.Index);
		else {
			desc.Index.Indices = ScratchIndexBuffer;
			desc.Index.IndexSize = 0;
		}

		Locked = true;
	}

	public virtual void MarkAsDrawn() { }

	int modifyVertexCount;
	int modifyFirstIndex;
	int modifyIndexCount;

	public virtual void ModifyBegin(int firstVertex, int vertexCount, int firstIndex, int indexCount, ref MeshDesc desc) {
		Assert(VertexBuffer != null);

		byte* vertexMemory = VertexBuffer!.ModifyLock(firstVertex, vertexCount, out desc.Vertex.FirstVertex);
		VertexBufferVulkan.ComputeVertexDescription(vertexMemory, VertexFormat, ref desc.Vertex);

		if (indexCount > 0) {
			IndexBuffer ??= new IndexBufferVulkan(ShaderAPI, indexCount, false);
			desc.Index.Indices = (ushort*)IndexBuffer.ModifyLock(firstIndex, indexCount, out _);
			desc.Index.IndexSize = 1;
			IsIBLocked = true;
		}
		else {
			desc.Index.Indices = ScratchIndexBuffer;
			desc.Index.IndexSize = 0;
		}

		modifyVertexCount = vertexCount;
		modifyFirstIndex = firstIndex;
		modifyIndexCount = indexCount;
		Locked = true;
	}

	public virtual void ModifyEnd(ref MeshDesc desc) {
		Assert(Locked);

		if (IsIBLocked) {
			IndexBuffer!.ModifyUnlock(modifyFirstIndex, modifyIndexCount);
			IsIBLocked = false;
		}

		VertexBuffer!.ModifyUnlock(modifyVertexCount);
		Locked = false;
	}

	IMesh? ColorMesh;
	static bool warnedColorMesh;

	public virtual void SetColorMesh(IMesh colorMesh, int vertexOffset) {
		ColorMesh = colorMesh;
		if (colorMesh != null && !warnedColorMesh) {
			warnedColorMesh = true;
			Warning("Vulkan: color meshes (static prop lighting streams) are not implemented yet; ignoring\n");
		}
	}

	public virtual MaterialPrimitiveType GetPrimitiveType() => Type;

	public virtual void SetPrimitiveType(MaterialPrimitiveType type) {
		if (!ShaderUtil.OnSetPrimitiveType(this, type))
			return;

		Type = type;
	}

	public virtual bool Unlock(int vertexCount, ref VertexDesc desc) {
		VertexBuffer!.Unlock(vertexCount);
		return true;
	}

	public virtual bool Unlock(int indexCount, ref IndexDesc desc) {
		if (!IsIBLocked)
			return true;
		IndexBuffer!.Unlock(indexCount);
		IsIBLocked = false;
		return true;
	}

	int NumVertices;
	int NumIndices;

	public virtual void UnlockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		Assert(Locked);

		Unlock(vertexCount, ref desc.Vertex);
		if (Type != MaterialPrimitiveType.Points)
			Unlock(indexCount, ref desc.Index);

		NumVertices = vertexCount;
		NumIndices = indexCount;
		Locked = false;
	}

	public virtual int VertexCount() => NumVertices;

	public virtual void SetMaterial(IMaterialInternal matInternal) => Material = matInternal;

	public virtual void SetVertexFormat(VertexFormat fmt) => VertexFormat = fmt;

	public virtual unsafe void RenderPass() {
		Assert(Type != MaterialPrimitiveType.Heterogenous);

		if (VertexBuffer == null || IndexBuffer == null)
			return;

		bool bound = false;
		for (int iPrim = 0; iPrim < s_PrimsCount; iPrim++) {
			PrimListVulkan* pPrim = &s_Prims[iPrim];

			if (pPrim->NumIndices == 0)
				continue;

			if (Type == MaterialPrimitiveType.Points || Type == MaterialPrimitiveType.InstancedQuads)
				throw new NotImplementedException();

			if (!bound) {
				if (!ShaderAPI.BindMeshBuffers(VertexBuffer, IndexBuffer))
					return;
				bound = true;
			}
			ShaderAPI.DrawIndexed(pPrim->NumIndices, pPrim->FirstIndex);
		}
	}

	public virtual bool NeedsVertexFormatReset(VertexFormat fmt) => VertexFormat != fmt;

	public virtual bool HasEnoughRoom(int vertexCount, int indexCount) => true;

	public virtual void PreLock() { }

	internal bool HasColorMesh() => ColorMesh != null;
	internal bool HasFlexMesh() => false;

	public virtual void UseVertexBuffer(VertexBufferVulkan vertexBuffer) => VertexBuffer = vertexBuffer;
	public virtual void UseIndexBuffer(IndexBufferVulkan indexBuffer) => IndexBuffer = indexBuffer;
}

/// <summary>Port of DynamicMeshGl46 - dynamic buffer sharing + partial draws.</summary>
public unsafe class DynamicMeshVulkan : MeshVulkan
{
	bool HasDrawn;
	bool VertexOverride;
	bool IndexOverride;

	int TotalVertices;
	int TotalIndices;
	int FirstVertex;
	int FirstIndex;

	int BufferId;

	public void Init(int bufferId) => BufferId = bufferId;

	public void ResetVertexAndIndexCounts() {
		TotalVertices = TotalIndices = 0;
		FirstIndex = FirstVertex = -1;
		HasDrawn = false;
	}

	public override void PreLock() {
		if (HasDrawn)
			ResetVertexAndIndexCounts();
	}

	internal void OverrideVertexBuffer(VertexBufferVulkan? vertexBuffer) {
		if (vertexBuffer != null) {
			UseVertexBuffer(vertexBuffer);
			VertexOverride = true;
		}
	}

	internal void OverrideIndexBuffer(IndexBufferVulkan? indexBuffer) {
		if (indexBuffer != null) {
			UseIndexBuffer(indexBuffer);
			IndexOverride = true;
		}
	}

	public override bool NeedsVertexFormatReset(VertexFormat fmt) {
		return VertexOverride || IndexOverride || base.NeedsVertexFormatReset(fmt);
	}

	public override bool HasEnoughRoom(int vertexCount, int indexCount) {
		if (ShaderDevice.IsDeactivated())
			return false;
		Assert(VertexBuffer != null);
		return VertexBuffer!.HasEnoughRoom(vertexCount) && IndexBuffer!.HasEnoughRoom(indexCount);
	}

	public override void LockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		PreLock();

		if (VertexOverride)
			vertexCount = 0;

		if (IndexOverride)
			indexCount = 0;

		Lock(vertexCount, false, ref desc.Vertex);

		if (FirstVertex < 0)
			FirstVertex = desc.Vertex.FirstVertex;

		if (IndexOverride || HasFlexMesh())
			desc.Vertex.FirstVertex -= FirstVertex;

		int firstIndex = Lock(false, -1, indexCount, ref desc.Index);
		if (FirstIndex < 0)
			FirstIndex = firstIndex;

		Locked = true;
	}

	public override void SetVertexFormat(VertexFormat format) {
		if (ShaderDevice.IsDeactivated())
			return;

		if (format != VertexFormat || VertexOverride || IndexOverride) {
			VertexFormat = format;
			UseVertexBuffer(MeshMgr.FindOrCreateVertexBuffer(BufferId, format));

			if (BufferId == 0)
				UseIndexBuffer(MeshMgr.GetDynamicIndexBuffer());

			VertexOverride = IndexOverride = false;
		}
	}

	public override void UnlockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		TotalVertices += vertexCount;
		TotalIndices += indexCount;
		base.UnlockMesh(vertexCount, indexCount, ref desc);
	}

	public override void Draw(int firstIndex = -1, int indexCount = 0) {
		if (!ShaderUtil.OnDrawMesh(this, firstIndex, indexCount)) {
			MarkAsDrawn();
			return;
		}

		HasDrawn = true;

		if (IndexOverride || VertexOverride || TotalVertices > 0 && (TotalIndices > 0 || Type == MaterialPrimitiveType.Points || Type == MaterialPrimitiveType.InstancedQuads)) {
			Assert(!IsDrawing);

			// only have a non-zero first vertex when we are using static indices
			int nFirstVertex = VertexOverride ? 0 : FirstVertex;
			int actualFirstVertex = IndexOverride ? nFirstVertex : 0;
			int baseIndex = IndexOverride ? 0 : FirstIndex;

			// Overriding with the dynamic index buffer, preserve state!
			if (IndexOverride && IndexBuffer == MeshMgr.GetDynamicIndexBuffer())
				baseIndex = FirstIndex;

			if (ShaderDevice.IsDeactivated())
				return;

			int numVertices = VertexOverride ? VertexBuffer!.VertexCount : TotalVertices;
			if (firstIndex != -1 && indexCount != 0) {
				firstIndex += baseIndex;
			}
			else {
				firstIndex = baseIndex;
				if (IndexOverride) {
					indexCount = IndexBuffer!.IndexCount;
					Assert(indexCount != 0);
				}
				else {
					indexCount = TotalIndices;

					if (Type == MaterialPrimitiveType.Points || Type == MaterialPrimitiveType.InstancedQuads)
						indexCount = TotalVertices;

					Assert(indexCount != 0);
				}
			}

			if (!HasFlexMesh())
				actualFirstVertex = nFirstVertex - actualFirstVertex;

			s_FirstVertex = (uint)actualFirstVertex;
			s_NumVertices = (uint)numVertices;

			PrimListVulkan* prim = stackalloc PrimListVulkan[1];
			prim->FirstIndex = firstIndex;
			prim->NumIndices = indexCount;
			Assert(indexCount != 0);
			s_Prims = prim;
			s_PrimsCount = 1;

			DrawMesh();

			s_Prims = null;
			s_PrimsCount = 0;
		}
	}
}

/// <summary>Port of BufferedMeshGl46 - batches consecutive small locks into one draw.</summary>
public unsafe class BufferedMeshVulkan : MeshVulkan
{
	MeshVulkan? Mesh;
	ushort LastIndex;
	ushort ExtraIndices;
	bool IsFlushing;
	bool WasRendered = true;
	bool FlushNeeded;

	public void ResetRendered() => WasRendered = false;
	public bool WasNotRendered() => !WasRendered;

	public void Flush() {
		if (Mesh != null && !IsFlushing && FlushNeeded) {
			IsFlushing = true;
			((IMesh)Mesh!).Draw();
			IsFlushing = false;
			FlushNeeded = false;
		}
	}

	public void SetMesh(MeshVulkan? mesh) {
		if (Mesh != mesh) {
			ShaderAPI.FlushBufferedPrimitives();
			Mesh = mesh;
		}
	}

	public override void SetMaterial(IMaterialInternal matInternal) {
		Assert(Mesh != null);
		Mesh!.SetMaterial(matInternal);
	}

	public override void SetVertexFormat(VertexFormat fmt) {
		Assert(Mesh != null);
		if (Mesh!.NeedsVertexFormatReset(fmt)) {
			ShaderAPI.FlushBufferedPrimitives();
			Mesh.SetVertexFormat(fmt);
		}
	}

	public override MaterialPrimitiveType GetPrimitiveType() => Mesh!.GetPrimitiveType();

	public override void SetPrimitiveType(MaterialPrimitiveType type) {
		if (type != GetPrimitiveType()) {
			ShaderAPI.FlushBufferedPrimitives();
			Mesh!.SetPrimitiveType(type);
		}
	}

	public override void LockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		ShaderUtil.SyncMatrices();

		Assert(Mesh != null);
		Assert(WasRendered);

		Mesh!.PreLock();

		if (!Mesh.HasEnoughRoom(vertexCount, indexCount))
			ShaderAPI.FlushBufferedPrimitives();

		WasRendered = false;

		Mesh.LockMesh(vertexCount, indexCount, ref desc);
	}

	public override void UnlockMesh(int vertexCount, int indexCount, ref MeshDesc desc) {
		if (Mesh!.GetPrimitiveType() == MaterialPrimitiveType.TriangleStrip && desc.Index.IndexSize > 0) {
			if (ExtraIndices > 0)
				*(desc.Index.Indices - 1) = *desc.Index.Indices;

			LastIndex = desc.Index.Indices[indexCount - 1];
			indexCount += ExtraIndices;
		}

		Mesh.UnlockMesh(vertexCount, indexCount, ref desc);
	}

	public override void Draw(int firstIndex = -1, int indexCount = 0) {
		if (!ShaderUtil.OnDrawMesh(this, firstIndex, indexCount)) {
			WasRendered = true;
			MarkAsDrawn();
			return;
		}

		Assert(!IsFlushing && !WasRendered);
		Assert(firstIndex == -1 && indexCount == 0);

		WasRendered = true;
		FlushNeeded = true;
	}
}

/// <summary>Port of MeshMgr: owns the dynamic/buffered meshes and the shared dynamic buffers.</summary>
public class MeshMgrVulkan : IMeshMgr
{
	internal IMaterialSystem MaterialSystem = null!;
	internal ShaderAPIVulkan ShaderAPI = null!;

	public const int VERTEX_BUFFER_SIZE = 32768;

	bool BufferedMode;

	readonly List<VertexBufferVulkan> DynamicVertexBuffers = [];
	IndexBufferVulkan? DynamicIndexBuffer;

	BufferedMeshVulkan BufferedMesh = null!;
	DynamicMeshVulkan DynamicMesh = null!;

	internal void Init() {
		BufferedMesh = InitMesh<BufferedMeshVulkan>();
		DynamicMesh = InitMesh<DynamicMeshVulkan>();
		DynamicMesh.Init(0);
		CreateDynamicIndexBuffer();
		BufferedMode = true;
	}

	private TMesh InitMesh<TMesh>() where TMesh : MeshVulkan, new() {
		TMesh ret = new TMesh();
		ret.ShaderAPI = ShaderAPI;
		ret.ShaderUtil = MaterialSystem.GetShaderUtil();
		ret.MeshMgr = this;
		ret.ShaderDevice = ShaderAPI.GetShaderDevice();
		return ret;
	}

	internal void Flush() {
		if (IsPC())
			BufferedMesh?.Flush();
	}

	internal void DiscardVertexBuffers() {
		for (int i = 0; i < DynamicVertexBuffers.Count; i++)
			DynamicVertexBuffers[i].FlushASAP();
		DynamicIndexBuffer?.FlushASAP();
	}

	void CreateDynamicIndexBuffer() {
		DynamicIndexBuffer?.Dispose();
		DynamicIndexBuffer = new IndexBufferVulkan(ShaderAPI, IMesh.INDEX_BUFFER_SIZE, true);
	}

	public IMesh GetDynamicMesh(IMaterial? material, VertexFormat vertexFormat, int hwSkinBoneCount, bool buffered, IMesh? vertexOverride, IMesh? indexOverride) {
		Assert(material == null || ((IMaterialInternal)material).IsRealTimeVersion());

		if (BufferedMode != buffered && BufferedMode)
			BufferedMesh.SetMesh(null);

		BufferedMode = buffered;

		IMaterialInternal matInternal = (IMaterialInternal)material!;
		MeshVulkan mesh = DynamicMesh;

		if (BufferedMode) {
			Assert(!BufferedMesh.WasNotRendered());
			BufferedMesh.SetMesh(mesh);
			mesh = BufferedMesh;
		}

		if (vertexOverride == null) {
			VertexFormat fmt = matInternal.GetVertexFormat();
			mesh.SetVertexFormat(fmt);
		}
		else {
			MeshVulkan vertexMesh = (MeshVulkan)vertexOverride;
			mesh.SetVertexFormat(vertexMesh.GetVertexFormat());
		}

		mesh.SetMaterial(matInternal);
		if (mesh == DynamicMesh) {
			DynamicMesh.OverrideVertexBuffer(((MeshVulkan?)vertexOverride)?.GetVertexBuffer());
			DynamicMesh.OverrideIndexBuffer(((MeshVulkan?)indexOverride)?.GetIndexBuffer());
		}

		return mesh;
	}

	internal VertexBufferVulkan FindOrCreateVertexBuffer(int dynamicBufferID, VertexFormat vertexFormat) {
		int vertexSize = VertexFormatSize(vertexFormat);

		while (DynamicVertexBuffers.Count <= dynamicBufferID) {
			int bufferMemory = ShaderAPI.GetCurrentDynamicVBSize();
			VertexBufferVulkan vertexBuffer = new VertexBufferVulkan(ShaderAPI, true);
			vertexBuffer.VertexSize = 0;
			int initVertexSize = bufferMemory / VERTEX_BUFFER_SIZE, initVertexCount = VERTEX_BUFFER_SIZE;
			vertexBuffer.BufferSize = initVertexSize * initVertexCount;
			DynamicVertexBuffers.Add(vertexBuffer);
		}

		VertexBufferVulkan buffer = DynamicVertexBuffers[dynamicBufferID];

		if (buffer.VertexSize != vertexSize) {
			int bufferMemory = ShaderAPI.GetCurrentDynamicVBSize();
			buffer.VertexSize = vertexSize;
			buffer.ChangeConfiguration(vertexFormat, vertexSize, bufferMemory);
		}

		return DynamicVertexBuffers[dynamicBufferID];
	}

	internal unsafe int VertexFormatSize(VertexFormat vertexFormat) {
		MeshDesc desc = new();
		VertexBufferVulkan.ComputeVertexDescription(null, vertexFormat, ref desc.Vertex);
		return desc.Vertex.ActualVertexSize;
	}

	internal IndexBufferVulkan GetDynamicIndexBuffer() => DynamicIndexBuffer!;

	internal IMesh CreateStaticMesh(VertexFormat format, ReadOnlySpan<char> textureGroup, IMaterial? material) {
		MeshVulkan mesh = InitMesh<MeshVulkan>();
		mesh.SetVertexFormat(format);
		if (material != null)
			mesh.SetMaterial((IMaterialInternal)material);
		return mesh;
	}

	internal void DestroyStaticMesh(IMesh mesh) {
		if (mesh is MeshVulkan meshVulkan) {
			meshVulkan.GetVertexBuffer()?.Dispose();
			meshVulkan.GetIndexBuffer()?.Dispose();
		}
	}

	internal int GetMaxIndicesToRender() => IMesh.INDEX_BUFFER_SIZE;

	internal int GetMaxVerticesToRender(IMaterial material) {
		VertexFormat fmt = material.GetVertexFormat();
		int vertexSize = VertexFormatSize(fmt);
		if (vertexSize == 0) {
			Warning($"bad vertex size for material {material.GetName()}\n");
			return 0;
		}

		int nMaxVerts = ShaderAPI.GetCurrentDynamicVBSize() / vertexSize;
		return Math.Min(nMaxVerts, 32767);
	}
}
