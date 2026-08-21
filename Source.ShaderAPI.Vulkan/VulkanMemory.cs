using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Chunked VkDeviceMemory allocator (Phase 4 of VULKAN_TODO.md): 64MB chunks per memory type,
/// first-fit free list with merge-on-free, whole-chunk persistent mapping for host-visible types.
/// Exists so buffers/images never call vkAllocateMemory individually (maxMemoryAllocationCount).
/// </summary>
public unsafe class VulkanMemoryAllocator(VulkanCore core) : IDisposable
{
	const ulong DefaultChunkSize = 64UL * 1024 * 1024;

	class Chunk
	{
		public DeviceMemory Memory;
		public ulong Size;
		public uint MemoryTypeIndex;
		public void* Mapped;
		// Sorted by offset, non-overlapping, adjacent ranges merged.
		public readonly List<(ulong Offset, ulong Size)> FreeRanges = [];
	}

	readonly List<Chunk> chunks = [];
	PhysicalDeviceMemoryProperties memoryProperties;
	bool queriedMemoryProperties;

	public struct Allocation
	{
		public DeviceMemory Memory;
		public ulong Offset;
		public ulong Size;
		/// <summary>Pointer to this allocation's bytes; null for non-host-visible memory.</summary>
		public void* Mapped;
		internal int ChunkIndex;
	}

	uint FindMemoryType(uint typeBits, MemoryPropertyFlags props) {
		if (!queriedMemoryProperties) {
			core.Vk.GetPhysicalDeviceMemoryProperties(core.PhysicalDevice, out memoryProperties);
			queriedMemoryProperties = true;
		}
		for (int i = 0; i < memoryProperties.MemoryTypeCount; i++) {
			if ((typeBits & (1u << i)) != 0 && (memoryProperties.MemoryTypes[i].PropertyFlags & props) == props)
				return (uint)i;
		}
		throw new InvalidOperationException($"Vulkan: no memory type matches bits 0x{typeBits:X} with {props}");
	}

	public Allocation Allocate(in MemoryRequirements reqs, MemoryPropertyFlags props) {
		uint typeIndex = FindMemoryType(reqs.MemoryTypeBits, props);
		bool hostVisible = (props & MemoryPropertyFlags.HostVisibleBit) != 0;
		ulong align = Math.Max(reqs.Alignment, 1);

		for (int c = 0; c < chunks.Count; c++) {
			Chunk chunk = chunks[c];
			if (chunk.MemoryTypeIndex != typeIndex)
				continue;
			if (TryCarve(chunk, c, reqs.Size, align, out Allocation alloc))
				return alloc;
		}

		Chunk newChunk = CreateChunk(typeIndex, Math.Max(DefaultChunkSize, reqs.Size), hostVisible);
		chunks.Add(newChunk);
		if (!TryCarve(newChunk, chunks.Count - 1, reqs.Size, align, out Allocation fresh))
			throw new InvalidOperationException("Vulkan: allocation failed from a fresh chunk (alignment?)");
		return fresh;
	}

	Chunk CreateChunk(uint typeIndex, ulong size, bool hostVisible) {
		MemoryAllocateInfo allocInfo = new() {
			SType = StructureType.MemoryAllocateInfo,
			AllocationSize = size,
			MemoryTypeIndex = typeIndex
		};
		if (core.Vk.AllocateMemory(core.Device, &allocInfo, null, out DeviceMemory memory) != Result.Success)
			throw new InvalidOperationException($"Vulkan: vkAllocateMemory failed ({size} bytes, type {typeIndex})");

		void* mapped = null;
		if (hostVisible && core.Vk.MapMemory(core.Device, memory, 0, size, 0, &mapped) != Result.Success)
			throw new InvalidOperationException("Vulkan: vkMapMemory failed");

		Chunk chunk = new() { Memory = memory, Size = size, MemoryTypeIndex = typeIndex, Mapped = mapped };
		chunk.FreeRanges.Add((0, size));
		return chunk;
	}

	static bool TryCarve(Chunk chunk, int chunkIndex, ulong size, ulong align, out Allocation alloc) {
		for (int r = 0; r < chunk.FreeRanges.Count; r++) {
			(ulong off, ulong rangeSize) = chunk.FreeRanges[r];
			ulong aligned = (off + align - 1) / align * align;
			ulong pad = aligned - off;
			if (rangeSize < pad + size)
				continue;

			chunk.FreeRanges.RemoveAt(r);
			int insertAt = r;
			if (pad > 0)
				chunk.FreeRanges.Insert(insertAt++, (off, pad));
			ulong tail = rangeSize - pad - size;
			if (tail > 0)
				chunk.FreeRanges.Insert(insertAt, (aligned + size, tail));

			alloc = new Allocation {
				Memory = chunk.Memory,
				Offset = aligned,
				Size = size,
				Mapped = chunk.Mapped == null ? null : (byte*)chunk.Mapped + aligned,
				ChunkIndex = chunkIndex
			};
			return true;
		}
		alloc = default;
		return false;
	}

	public void Free(in Allocation alloc) {
		if (alloc.Memory.Handle == 0)
			return;
		Chunk chunk = chunks[alloc.ChunkIndex];

		// Insert sorted, then merge with neighbours.
		int i = 0;
		while (i < chunk.FreeRanges.Count && chunk.FreeRanges[i].Offset < alloc.Offset)
			i++;
		chunk.FreeRanges.Insert(i, (alloc.Offset, alloc.Size));

		if (i + 1 < chunk.FreeRanges.Count) {
			(ulong nextOff, ulong nextSize) = chunk.FreeRanges[i + 1];
			if (alloc.Offset + alloc.Size == nextOff) {
				chunk.FreeRanges[i] = (alloc.Offset, alloc.Size + nextSize);
				chunk.FreeRanges.RemoveAt(i + 1);
			}
		}
		if (i > 0) {
			(ulong prevOff, ulong prevSize) = chunk.FreeRanges[i - 1];
			(ulong curOff, ulong curSize) = chunk.FreeRanges[i];
			if (prevOff + prevSize == curOff) {
				chunk.FreeRanges[i - 1] = (prevOff, prevSize + curSize);
				chunk.FreeRanges.RemoveAt(i);
			}
		}
	}

	public void Dispose() {
		foreach (Chunk chunk in chunks) {
			if (chunk.Mapped != null)
				core.Vk.UnmapMemory(core.Device, chunk.Memory);
			core.Vk.FreeMemory(core.Device, chunk.Memory, null);
		}
		chunks.Clear();
	}
}

/// <summary>
/// A VkBuffer bound to suballocated memory. Bring-up policy: everything is host-visible+coherent
/// (Unlock is a plain memcpy through <see cref="Mapped"/>); device-local + staging comes later.
/// </summary>
public unsafe class VulkanBufferResource : IDisposable
{
	readonly VulkanCore core;
	readonly VulkanMemoryAllocator allocator;
	VulkanMemoryAllocator.Allocation allocation;

	public VkBuffer Handle { get; private set; }
	public ulong Size { get; private set; }
	public void* Mapped { get; private set; }

	VulkanBufferResource(VulkanCore core, VulkanMemoryAllocator allocator) {
		this.core = core;
		this.allocator = allocator;
	}

	public static VulkanBufferResource Create(VulkanCore core, VulkanMemoryAllocator allocator, ulong size, BufferUsageFlags usage) {
		BufferCreateInfo bufferInfo = new() {
			SType = StructureType.BufferCreateInfo,
			Size = size,
			Usage = usage,
			SharingMode = SharingMode.Exclusive
		};
		if (core.Vk.CreateBuffer(core.Device, &bufferInfo, null, out VkBuffer buffer) != Result.Success)
			throw new InvalidOperationException($"Vulkan: vkCreateBuffer failed ({size} bytes)");

		core.Vk.GetBufferMemoryRequirements(core.Device, buffer, out MemoryRequirements reqs);
		VulkanMemoryAllocator.Allocation alloc = allocator.Allocate(in reqs,
			MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
		core.Vk.BindBufferMemory(core.Device, buffer, alloc.Memory, alloc.Offset);

		return new VulkanBufferResource(core, allocator) {
			Handle = buffer,
			Size = size,
			Mapped = alloc.Mapped,
			allocation = alloc
		};
	}

	public void Dispose() {
		if (Handle.Handle != 0) {
			core.Vk.DestroyBuffer(core.Device, Handle, null);
			Handle = default;
		}
		allocator.Free(in allocation);
		allocation = default;
		Mapped = null;
	}
}
