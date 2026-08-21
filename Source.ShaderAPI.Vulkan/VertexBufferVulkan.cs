using Source.Common.MaterialSystem;

using System.Runtime.InteropServices;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Port of VertexBufferGl46: sysmem shadow copy that MeshBuilder writes into, uploaded to a
/// VkBuffer at Unlock (host-visible+coherent, so upload is a memcpy). GL's dynamic-buffer
/// orphaning becomes retire-and-replace: the live VkBuffer goes on the shader API's retire queue
/// (freed once no frame in flight can reference it) and a fresh one takes its place.
/// </summary>
public unsafe class VertexBufferVulkan : IDisposable
{
	readonly ShaderAPIVulkan shaderAPI;

	VertexFormat VertexBufferFormat;
	internal int Position;
	internal int VertexCount;
	internal int VertexSize;
	internal void* SysmemBuffer;
	internal int BufferSize;

	internal bool Dynamic;
	internal bool Locked;
	internal bool Flush;

	VulkanBufferResource? buffer;
	int lastBufferSize = -1;

	internal VulkanBufferResource? Buffer => buffer;
	internal VertexFormat Format => VertexBufferFormat;

	public VertexBufferVulkan(ShaderAPIVulkan shaderAPI, bool dynamic) {
		this.shaderAPI = shaderAPI;
		Dynamic = dynamic;
	}

	public VertexBufferVulkan(ShaderAPIVulkan shaderAPI, VertexFormat format, int vertexSize, int vertexCount, bool dynamic) {
		this.shaderAPI = shaderAPI;
		VertexBufferFormat = format;
		VertexSize = vertexSize;
		VertexCount = vertexCount;
		BufferSize = vertexSize * vertexCount;
		Dynamic = dynamic;
		Locked = false;
		Flush = true;
	}

	public void FlushASAP() => Flush = true;

	public int NextLockOffset() {
		int nextOffset = VertexSize == 0 ? 0 : (Position + VertexSize - 1) / VertexSize;
		nextOffset *= VertexSize;
		return nextOffset;
	}

	internal void ChangeConfiguration(VertexFormat format, int vertexSize, int totalSize) {
		VertexBufferFormat = format;
		VertexSize = vertexSize;
		VertexCount = BufferSize / vertexSize;
		EnsureBuffer();
	}

	/// <summary>GL orphaning equivalent: hand the GPU-visible buffer to the retire queue and start fresh.</summary>
	void Orphan() {
		if (buffer != null) {
			shaderAPI.RetireBuffer(buffer);
			buffer = null;
		}
	}

	void EnsureBuffer() {
		if (BufferSize > lastBufferSize) {
			if (SysmemBuffer != null) {
				NativeMemory.Free(SysmemBuffer);
				SysmemBuffer = null;
			}
			lastBufferSize = BufferSize;
			SysmemBuffer = NativeMemory.AllocZeroed((nuint)BufferSize);
			Orphan();
		}
		buffer ??= VulkanBufferResource.Create(shaderAPI.Core!, shaderAPI.Allocator!, (ulong)BufferSize,
			Silk.NET.Vulkan.BufferUsageFlags.VertexBufferBit);
	}

	public byte* Lock(int numVerts, out int baseVertexIndex) {
		Assert(!Locked);

		if (numVerts > VertexCount) {
			baseVertexIndex = 0;
			return null;
		}

		bool discard = false;
		if (Dynamic) {
			if (Position == 0 || Flush || !HasEnoughRoom(numVerts)) {
				Flush = false;
				Position = 0;
				discard = true;
			}
		}
		else {
			Position = 0;
		}

		int lockOffset = NextLockOffset();
		baseVertexIndex = VertexSize == 0 ? 0 : (lockOffset / VertexSize);
		if (SysmemBuffer == null)
			EnsureBuffer();
		else if (discard) {
			Orphan();
			EnsureBuffer();
		}

		Locked = true;
		Position = lockOffset;
		return (byte*)((nint)SysmemBuffer + lockOffset);
	}

	public void Unlock(int vertexCount) {
		if (!Locked)
			return;

		int lockOffset = NextLockOffset();
		int bufferSize = vertexCount * VertexSize;

		EnsureBuffer();
		System.Buffer.MemoryCopy((byte*)SysmemBuffer + Position, (byte*)buffer!.Mapped + Position, BufferSize - Position, bufferSize);
		Position = lockOffset + bufferSize;
		Locked = false;
	}

	int modifyOffset;

	public byte* ModifyLock(int firstVertex, int numVerts, out int baseVertexIndex) {
		Assert(!Locked);

		if (SysmemBuffer == null)
			EnsureBuffer();

		modifyOffset = firstVertex * VertexSize;
		baseVertexIndex = firstVertex;
		Locked = true;
		return (byte*)((nint)SysmemBuffer + modifyOffset);
	}

	public void ModifyUnlock(int vertexCount) {
		if (!Locked)
			return;

		EnsureBuffer();
		int bytes = vertexCount * VertexSize;
		System.Buffer.MemoryCopy((byte*)SysmemBuffer + modifyOffset, (byte*)buffer!.Mapped + modifyOffset, BufferSize - modifyOffset, bytes);
		Locked = false;
	}

	internal bool HasEnoughRoom(int numVertices) {
		return NextLockOffset() + (numVertices * VertexSize) <= BufferSize;
	}

	unsafe static readonly nint dummyData = (nint)NativeMemory.AlignedAlloc(512, 16);

	// Verbatim port of VertexBufferGl46.ComputeVertexDescription - the CPU-side element order that
	// VulkanVertexLayout.BuildAttributes must always agree with.
	public static unsafe void ComputeVertexDescription(byte* vertexMemory, VertexFormat vertexFormat, ref VertexDesc desc) {
		desc.NumBoneWeights = vertexFormat.GetBoneWeightsSize();
		fixed (VertexDesc* descPtr = &desc) {
			nint offset = 0;
			nint baseptr = (nint)vertexMemory;
			int** vertexSizesToSet = stackalloc int*[64];
			int vertexSizesToSetPtr = 0;

			if ((vertexFormat & VertexFormat.Position) != 0) {
				descPtr->Position = (float*)(baseptr + offset);
				offset += VertexElement.Position.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->PositionSize;
			}
			else {
				descPtr->Position = (float*)dummyData;
				descPtr->PositionSize = 0;
			}

			if ((vertexFormat & VertexFormat.BoneIndex) != 0) {
				if (desc.NumBoneWeights > 0) {
					VertexElement boneWeightElement = VertexElement.BoneWeights1 + (desc.NumBoneWeights - 1);
					descPtr->BoneWeight = (float*)(baseptr + offset);
					offset += boneWeightElement.GetSize();
					vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->BoneWeightSize;
				}
				else {
					descPtr->BoneWeight = (float*)dummyData;
					descPtr->BoneWeightSize = 0;
				}

				descPtr->BoneMatrixIndex = (byte*)(baseptr + offset);
				offset += VertexElement.BoneIndex.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->BoneMatrixIndexSize;
			}
			else {
				descPtr->BoneMatrixIndex = (byte*)dummyData;
				descPtr->BoneMatrixIndexSize = 0;
			}

			if ((vertexFormat & VertexFormat.Normal) != 0) {
				descPtr->Normal = (float*)(baseptr + offset);
				offset += VertexElement.Normal.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->NormalSize;
			}
			else {
				descPtr->Normal = (float*)dummyData;
				descPtr->NormalSize = 0;
			}

			if ((vertexFormat & VertexFormat.Color) != 0) {
				descPtr->Color = (byte*)(baseptr + offset);
				offset += VertexElement.Color.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->ColorSize;
			}
			else {
				descPtr->Color = (byte*)dummyData;
				descPtr->ColorSize = 0;
			}

			if ((vertexFormat & VertexFormat.Specular) != 0) {
				descPtr->Specular = (byte*)(baseptr + offset);
				offset += VertexElement.Specular.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->SpecularSize;
			}
			else {
				descPtr->Specular = (byte*)dummyData;
				descPtr->SpecularSize = 0;
			}

			Span<VertexElement> texCoordElements = [VertexElement.TexCoord1D_0, VertexElement.TexCoord2D_0, VertexElement.TexCoord3D_0, VertexElement.TexCoord4D_0];
			for (int i = 0; i < IMesh.VERTEX_MAX_TEXTURE_COORDINATES; i++) {
				int size = vertexFormat.GetTexCoordDimensionSize(i);
				if (size != 0) {
					desc.SetTexCoord(i, (float*)(baseptr + offset));
					offset += ((VertexElement)((int)texCoordElements[size - 1] + i)).GetSize();
					vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->TexCoordSize[i];
				}
				else {
					desc.SetTexCoord(i, (float*)dummyData);
					desc.TexCoordSize[i] = 0;
				}
			}

			if ((vertexFormat & VertexFormat.TangentS) != 0) {
				descPtr->TangentS = (float*)(baseptr + offset);
				offset += VertexElement.TangentS.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->TangentSSize;
			}
			else {
				descPtr->TangentS = (float*)dummyData;
				descPtr->TangentSSize = 0;
			}

			if ((vertexFormat & VertexFormat.TangentT) != 0) {
				descPtr->TangentT = (float*)(baseptr + offset);
				offset += VertexElement.TangentT.GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->TangentTSize;
			}
			else {
				descPtr->TangentT = (float*)dummyData;
				descPtr->TangentTSize = 0;
			}

			int userDataSize = vertexFormat.GetUserDataSize();
			if (userDataSize > 0) {
				desc.UserData = (float*)(baseptr + offset);
				offset += (VertexElement.UserData1 + (userDataSize - 1)).GetSize();
				vertexSizesToSet[vertexSizesToSetPtr++] = &descPtr->UserDataSize;
			}
			else {
				descPtr->UserData = (float*)dummyData;
				descPtr->UserDataSize = 0;
			}

			desc.ActualVertexSize = (int)offset;
			for (int i = 0; i < vertexSizesToSetPtr; i++) {
				*vertexSizesToSet[i] = (int)offset;
			}
		}
	}

	public void Dispose() {
		Orphan();
		if (SysmemBuffer != null) {
			NativeMemory.Free(SysmemBuffer);
			SysmemBuffer = null;
		}
	}
}
