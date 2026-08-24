using System;
using System.Collections.Generic;
using System.Threading;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Block-based suballocator that allocates large Vulkan memory blocks and carves
/// suballocations from them using a sorted free-list approach.
/// <para>
/// Buffers and images are assigned to separate block pools to satisfy
/// <c>bufferImageGranularity</c> requirements without per-allocation alignment padding.
/// </para>
/// </summary>
internal sealed unsafe class VulkanBlockAllocator : IVulkanMemoryAllocator
{
    private readonly VulkanDeviceContext _deviceContext;
    private readonly ulong _defaultBlockSize;

    /// <summary>
    /// Threshold above which we force a dedicated allocation instead of suballocating.
    /// Set to 1/4 of default block size.
    /// </summary>
    private readonly ulong _dedicatedThreshold;

    /// <summary>
    /// Per-memory-type pool list. Key is (memoryTypeIndex, isImage) to segregate
    /// buffer and image allocations for bufferImageGranularity compliance.
    /// </summary>
    private readonly Dictionary<(uint memTypeIndex, bool isImage), List<MemoryBlock>> _pools = new();
    private readonly Dictionary<ulong, MappedMemoryBlock> _mappedBlocks = new();
    private readonly object _lock = new();

    private int _activeVkAllocationCount;
    private long _totalAllocatedBytes;
    private int _nextBlockId;

    public VulkanBlockAllocator(VulkanDeviceContext deviceContext, ulong defaultBlockSizeMB = 64)
    {
        _deviceContext = deviceContext;
        _defaultBlockSize = defaultBlockSizeMB * 1024UL * 1024UL;
        _dedicatedThreshold = _defaultBlockSize / 4;
    }

    public int ActiveVkAllocationCount => _activeVkAllocationCount;
    public long TotalAllocatedBytes => _totalAllocatedBytes;

    public VulkanMemoryAllocation AllocateForBuffer(
        Vk api, Device device, Buffer buffer, MemoryPropertyFlags requiredProperties)
    {
        if (!TryAllocateForBuffer(api, device, buffer, requiredProperties, out VulkanMemoryAllocation allocation, out Result result))
            throw new VulkanOutOfMemoryException($"Failed to suballocate Vulkan buffer memory ({result}).", requiredProperties);
        return allocation;
    }

    public VulkanMemoryAllocation AllocateForImage(
        Vk api, Device device, Image image, MemoryPropertyFlags requiredProperties)
    {
        if (!TryAllocateForImage(api, device, image, requiredProperties, out VulkanMemoryAllocation allocation, out Result result))
            throw new VulkanOutOfMemoryException($"Failed to suballocate Vulkan image memory ({result}).", requiredProperties);
        return allocation;
    }

    public bool TryAllocateForBuffer(
        Vk api, Device device, Buffer buffer,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation,
        out Result result)
    {
        api.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements memReqs);
        return TrySuballocate(api, device, memReqs, requiredProperties, isImage: false, out allocation, out result);
    }

    public bool TryAllocateForImage(
        Vk api, Device device, Image image,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation,
        out Result result)
    {
        api.GetImageMemoryRequirements(device, image, out MemoryRequirements memReqs);
        return TrySuballocate(api, device, memReqs, requiredProperties, isImage: true, out allocation, out result);
    }

    private bool TrySuballocate(
        Vk api, Device device,
        MemoryRequirements memReqs,
        MemoryPropertyFlags requiredProperties,
        bool isImage,
        out VulkanMemoryAllocation allocation,
        out Result result)
    {
        allocation = VulkanMemoryAllocation.Null;
        result = Result.Success;
        uint memoryTypeIndex = ResolveMemoryType(api, memReqs.MemoryTypeBits, requiredProperties);
        MemoryPropertyFlags actualProperties =
            _deviceContext.GetMemoryTypeProperties(api, memoryTypeIndex);
        bool hostVisibleNonCoherent =
            (actualProperties & MemoryPropertyFlags.HostVisibleBit) != 0 &&
            (actualProperties & MemoryPropertyFlags.HostCoherentBit) == 0;
        ulong atomSize = hostVisibleNonCoherent
            ? Math.Max(_deviceContext.NonCoherentAtomSize, 1UL)
            : 1UL;
        // Non-coherent suballocations reserve whole atom ranges. This permits
        // flush/invalidate expansion without touching the next allocation.
        ulong alignment = Math.Max(Math.Max(memReqs.Alignment, 1UL), atomSize);
        ulong size = AlignAllocationSize(memReqs.Size, atomSize);

        // Very large allocations get dedicated memory.
        if (size >= _dedicatedThreshold)
            return TryDedicatedAllocation(api, device, memReqs, memoryTypeIndex, actualProperties, out allocation, out result);

        var poolKey = (memoryTypeIndex, isImage);

        lock (_lock)
        {
            if (!_pools.TryGetValue(poolKey, out List<MemoryBlock>? blocks))
            {
                blocks = new List<MemoryBlock>(4);
                _pools[poolKey] = blocks;
            }

            // Try existing blocks first (best-fit across all blocks in this pool).
            foreach (MemoryBlock block in blocks)
            {
                if (block.TrySuballocate(size, alignment, out ulong offset))
                {
                    Interlocked.Add(ref _totalAllocatedBytes, (long)size);
                    allocation = new VulkanMemoryAllocation(
                        block.Memory, offset, size, memoryTypeIndex, actualProperties, block.Id);
                    return true;
                }
            }

            // No room — allocate a new block.
            ulong blockSize = Math.Max(_defaultBlockSize, size + alignment);
            if (!TryAllocateBlock(api, device, blockSize, memoryTypeIndex, out MemoryBlock? newBlock, out result))
            {
                // OOM on block allocation — try with exactly the requested size.
                bool outOfMemory = result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory;
                if (outOfMemory && blockSize > size + alignment &&
                    TryAllocateBlock(api, device, size + alignment, memoryTypeIndex, out newBlock, out result))
                {
                    // Smaller block succeeded.
                }
                else
                {
                    return false;
                }
            }

            blocks.Add(newBlock!);

            if (newBlock!.TrySuballocate(size, alignment, out ulong newOffset))
            {
                Interlocked.Add(ref _totalAllocatedBytes, (long)size);
                allocation = new VulkanMemoryAllocation(
                    newBlock.Memory, newOffset, size, memoryTypeIndex, actualProperties, newBlock.Id);
                return true;
            }

            // Should be unreachable — we just allocated an empty block.
            return false;
        }
    }

    private uint ResolveMemoryType(Vk api, uint typeFilter, MemoryPropertyFlags properties)
    {
        if (_deviceContext.TryFindMemoryType(api, typeFilter, properties, out uint exactIndex))
            return exactIndex;

        bool prefersReadbackFallback =
            (properties & (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit)) ==
            (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit);
        if (prefersReadbackFallback &&
            _deviceContext.TryFindMemoryType(
                api,
                typeFilter,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out uint coherentIndex))
        {
            Debug.VulkanWarningEvery(
                "Vulkan.ReadbackMemoryTypeFallback",
                TimeSpan.FromSeconds(10),
                "[Vulkan] Host-cached readback memory unavailable; falling back to host-coherent staging memory.");
            return coherentIndex;
        }

        return _deviceContext.FindMemoryType(api, typeFilter, properties);
    }

    private static ulong AlignAllocationSize(ulong value, ulong alignment)
        => alignment <= 1UL
            ? value
            : checked(((value + alignment - 1UL) / alignment) * alignment);

    private bool TryDedicatedAllocation(
        Vk api, Device device,
        MemoryRequirements memReqs,
        uint memoryTypeIndex,
        MemoryPropertyFlags properties,
        out VulkanMemoryAllocation allocation,
        out Result result)
    {
        allocation = VulkanMemoryAllocation.Null;
        result = Result.Success;

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };

        result = api.AllocateMemory(device, ref allocInfo, null, out DeviceMemory memory);
        if (result == Result.ErrorOutOfDeviceMemory || result == Result.ErrorOutOfHostMemory)
            return false;
        if (result != Result.Success)
            return false;

        int blockId = Interlocked.Increment(ref _nextBlockId);
        Interlocked.Increment(ref _activeVkAllocationCount);
        Interlocked.Add(ref _totalAllocatedBytes, (long)memReqs.Size);

        lock (_lock)
        {
            var poolKey = (memoryTypeIndex, true); // Dedicated allocs tracked in image pool for simplicity.
            if (!_pools.TryGetValue(poolKey, out List<MemoryBlock>? blocks))
            {
                blocks = new List<MemoryBlock>(4);
                _pools[poolKey] = blocks;
            }
            var block = new MemoryBlock(memory, memReqs.Size, blockId, isDedicated: true);
            block.TrySuballocate(memReqs.Size, 1, out _); // Mark the whole block as used.
            blocks.Add(block);
        }

        allocation = new VulkanMemoryAllocation(
            memory, 0, memReqs.Size, memoryTypeIndex, properties, blockId);
        return true;
    }

    private bool TryAllocateBlock(
        Vk api, Device device,
        ulong blockSize, uint memoryTypeIndex,
        out MemoryBlock? block,
        out Result result)
    {
        block = null;

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = blockSize,
            MemoryTypeIndex = memoryTypeIndex,
        };

        result = api.AllocateMemory(device, ref allocInfo, null, out DeviceMemory memory);
        if (result != Result.Success)
            return false;

        int blockId = Interlocked.Increment(ref _nextBlockId);
        Interlocked.Increment(ref _activeVkAllocationCount);
        block = new MemoryBlock(memory, blockSize, blockId, isDedicated: false);
        return true;
    }

    public void Free(Vk api, Device device, VulkanMemoryAllocation allocation)
    {
        if (allocation.IsNull)
            return;

        lock (_lock)
        {
            foreach (var kvp in _pools)
            {
                List<MemoryBlock> blocks = kvp.Value;
                for (int i = 0; i < blocks.Count; i++)
                {
                    MemoryBlock block = blocks[i];
                    if (block.Id != allocation.BlockId)
                        continue;

                    Interlocked.Add(ref _totalAllocatedBytes, -(long)allocation.Size);

                    if (block.IsDedicated)
                    {
                        api.FreeMemory(device, block.Memory, null);
                        blocks.RemoveAt(i);
                        Interlocked.Decrement(ref _activeVkAllocationCount);
                        return;
                    }

                    block.FreeSuballocation(allocation.Offset, allocation.Size);

                    // If the entire block is now free and there are other blocks in this pool,
                    // release it to reduce VRAM pressure.
                    if (block.IsCompletelyFree && blocks.Count > 1)
                    {
                        api.FreeMemory(device, block.Memory, null);
                        blocks.RemoveAt(i);
                        Interlocked.Decrement(ref _activeVkAllocationCount);
                    }

                    return;
                }
            }
        }
    }

    public bool TryMap(
        Vk api,
        Device device,
        VulkanMemoryAllocation allocation,
        ulong offset,
        ulong length,
        out void* mappedPtr,
        out Result result)
    {
        _ = length;
        mappedPtr = null;
        result = Result.Success;
        if (allocation.IsNull)
        {
            result = Result.ErrorMemoryMapFailed;
            return false;
        }

        lock (_lock)
        {
            ulong memoryHandle = allocation.Memory.Handle;
            if (!_mappedBlocks.TryGetValue(memoryHandle, out MappedMemoryBlock? mappedBlock))
            {
                void* basePtr = null;
                result = api.MapMemory(device, allocation.Memory, 0, Vk.WholeSize, 0, &basePtr);
                if (result != Result.Success)
                    return false;

                mappedBlock = new MappedMemoryBlock(basePtr);
                _mappedBlocks[memoryHandle] = mappedBlock;
            }

            mappedBlock.RefCount++;
            mappedPtr = (byte*)mappedBlock.BasePtr + allocation.Offset + offset;
            return true;
        }
    }

    public void Unmap(Vk api, Device device, VulkanMemoryAllocation allocation)
    {
        if (allocation.IsNull)
            return;

        lock (_lock)
        {
            ulong memoryHandle = allocation.Memory.Handle;
            if (!_mappedBlocks.TryGetValue(memoryHandle, out MappedMemoryBlock? mappedBlock))
                return;

            mappedBlock.RefCount--;
            if (mappedBlock.RefCount > 0)
                return;

            _mappedBlocks.Remove(memoryHandle);
            api.UnmapMemory(device, allocation.Memory);
        }
    }

    public void Dispose()
    {
        // Blocks are freed during renderer teardown which calls DestroyAllBlocks.
    }

    /// <summary>
    /// Releases all blocks. Called during renderer shutdown after all resources are destroyed.
    /// </summary>
    public void DestroyAllBlocks(Vk api, Device device)
    {
        lock (_lock)
        {
            foreach (var kvp in _pools)
            {
                foreach (MemoryBlock block in kvp.Value)
                    api.FreeMemory(device, block.Memory, null);
                kvp.Value.Clear();
            }
            _pools.Clear();
            _mappedBlocks.Clear();
            _activeVkAllocationCount = 0;
            _totalAllocatedBytes = 0;
        }
    }

    private sealed unsafe class MappedMemoryBlock(void* basePtr)
    {
        public readonly void* BasePtr = basePtr;
        public int RefCount;
    }

    /// <summary>
    /// A contiguous block of Vulkan device memory with a free-list suballocator.
    /// Uses a sorted list of free regions with best-fit selection.
    /// </summary>
    private sealed class MemoryBlock
    {
        public readonly DeviceMemory Memory;
        public readonly ulong TotalSize;
        public readonly int Id;
        public readonly bool IsDedicated;

        /// <summary>Sorted list of free regions (by offset).</summary>
        private readonly List<FreeRegion> _freeList;

        public MemoryBlock(DeviceMemory memory, ulong totalSize, int id, bool isDedicated)
        {
            Memory = memory;
            TotalSize = totalSize;
            Id = id;
            IsDedicated = isDedicated;
            _freeList = isDedicated
                ? new List<FreeRegion>(1)
                : new List<FreeRegion>(8) { new(0, totalSize) };
        }

        public bool IsCompletelyFree =>
            _freeList.Count == 1 && _freeList[0].Size == TotalSize;

        public bool TrySuballocate(ulong size, ulong alignment, out ulong offset)
        {
            offset = 0;
            int bestIdx = -1;
            ulong bestWaste = ulong.MaxValue;
            ulong bestAlignedOffset = 0;

            for (int i = 0; i < _freeList.Count; i++)
            {
                FreeRegion region = _freeList[i];
                ulong alignedOffset = AlignUp(region.Offset, alignment);
                ulong padding = alignedOffset - region.Offset;

                if (region.Size < padding + size)
                    continue;

                ulong waste = region.Size - padding - size;
                if (waste < bestWaste)
                {
                    bestWaste = waste;
                    bestIdx = i;
                    bestAlignedOffset = alignedOffset;
                    if (waste == 0)
                        break;
                }
            }

            if (bestIdx < 0)
                return false;

            FreeRegion chosen = _freeList[bestIdx];
            _freeList.RemoveAt(bestIdx);

            // Front padding becomes a free region.
            ulong frontPadding = bestAlignedOffset - chosen.Offset;
            if (frontPadding > 0)
                InsertFreeRegion(new FreeRegion(chosen.Offset, frontPadding));

            // Back remainder becomes a free region.
            ulong usedEnd = bestAlignedOffset + size;
            ulong chosenEnd = chosen.Offset + chosen.Size;
            if (chosenEnd > usedEnd)
                InsertFreeRegion(new FreeRegion(usedEnd, chosenEnd - usedEnd));

            offset = bestAlignedOffset;
            return true;
        }

        public void FreeSuballocation(ulong offset, ulong size)
        {
            FreeRegion freed = new(offset, size);

            // Find insertion point to maintain sorted order.
            int insertIdx = 0;
            for (int i = 0; i < _freeList.Count; i++)
            {
                if (_freeList[i].Offset > offset)
                    break;
                insertIdx = i + 1;
            }

            _freeList.Insert(insertIdx, freed);

            // Merge with next neighbor.
            if (insertIdx + 1 < _freeList.Count)
            {
                FreeRegion next = _freeList[insertIdx + 1];
                FreeRegion current = _freeList[insertIdx];
                if (current.Offset + current.Size == next.Offset)
                {
                    _freeList[insertIdx] = new FreeRegion(current.Offset, current.Size + next.Size);
                    _freeList.RemoveAt(insertIdx + 1);
                }
            }

            // Merge with previous neighbor.
            if (insertIdx > 0)
            {
                FreeRegion prev = _freeList[insertIdx - 1];
                FreeRegion current = _freeList[insertIdx];
                if (prev.Offset + prev.Size == current.Offset)
                {
                    _freeList[insertIdx - 1] = new FreeRegion(prev.Offset, prev.Size + current.Size);
                    _freeList.RemoveAt(insertIdx);
                }
            }
        }

        private void InsertFreeRegion(FreeRegion region)
        {
            int idx = 0;
            for (int i = 0; i < _freeList.Count; i++)
            {
                if (_freeList[i].Offset > region.Offset)
                    break;
                idx = i + 1;
            }
            _freeList.Insert(idx, region);
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);

        private readonly record struct FreeRegion(ulong Offset, ulong Size);
    }
}
