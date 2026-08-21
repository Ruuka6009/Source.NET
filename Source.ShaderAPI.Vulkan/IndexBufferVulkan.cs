using System.Runtime.InteropServices;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Port of IndexBufferGl46 (16-bit indices). Same sysmem shadow + memcpy-on-unlock scheme and the
/// same retire-based orphaning as <see cref="VertexBufferVulkan"/>.
/// </summary>
public unsafe class IndexBufferVulkan : IDisposable
{
	readonly ShaderAPIVulkan shaderAPI;

	internal int IndexCount;
	internal int Position;
	internal void* SysmemBuffer;
	internal int BufferSize;

	internal bool Dynamic;
	internal bool Locked;
	internal bool Flush;

	VulkanBufferResource? buffer;
	int lastBufferSize = -1;

	internal VulkanBufferResource? Buffer => buffer;

	public IndexBufferVulkan(ShaderAPIVulkan shaderAPI, int count, bool dynamic = false) {
		this.shaderAPI = shaderAPI;
		Position = 0;
		Locked = false;
		Flush = true;
		Dynamic = dynamic;

		count += count % 2;
		IndexCount = count;
		BufferSize = sizeof(ushort) * IndexCount;

		EnsureBuffer();
	}

	public void FlushASAP() => Flush = true;

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
			Silk.NET.Vulkan.BufferUsageFlags.IndexBufferBit);
	}

	public short* Lock(bool readOnly, int indexCount, out int startIndex, int firstIndex) {
		Assert(!Locked);

		bool discard = false;
		if (Dynamic) {
			if (Position == 0 || Flush || !HasEnoughRoom(indexCount)) {
				Flush = false;
				Position = 0;
				discard = true;
			}
		}

		int position = Position;
		if (firstIndex >= 0)
			position = firstIndex;

		startIndex = position;
		if (SysmemBuffer == null)
			EnsureBuffer();
		else if (discard) {
			Orphan();
			EnsureBuffer();
		}
		Locked = true;
		return (short*)SysmemBuffer + position;
	}

	public void Unlock(int indexCount) {
		if (!Locked)
			return;

		EnsureBuffer();
		System.Buffer.MemoryCopy((byte*)SysmemBuffer + Position * 2, (byte*)buffer!.Mapped + Position * 2,
			BufferSize - Position * 2, indexCount * 2);
		Position += indexCount;
		Locked = false;
	}

	public short* ModifyLock(int firstIndex, int indexCount, out int startIndex) {
		Assert(!Locked);

		if (SysmemBuffer == null)
			EnsureBuffer();

		startIndex = firstIndex;
		Locked = true;
		return (short*)SysmemBuffer + firstIndex;
	}

	public void ModifyUnlock(int firstIndex, int indexCount) {
		if (!Locked)
			return;

		EnsureBuffer();
		System.Buffer.MemoryCopy((byte*)SysmemBuffer + firstIndex * 2, (byte*)buffer!.Mapped + firstIndex * 2,
			BufferSize - firstIndex * 2, indexCount * 2);
		Locked = false;
	}

	internal bool HasEnoughRoom(int indices) {
		return indices + Position <= IndexCount;
	}

	public void Dispose() {
		Orphan();
		if (SysmemBuffer != null) {
			NativeMemory.Free(SysmemBuffer);
			SysmemBuffer = null;
		}
	}
}
