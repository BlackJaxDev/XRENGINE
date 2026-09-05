namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameDataArena
{
    /// <summary>
    /// Adds output slots at the coordinator's cold preparation boundary. Existing
    /// native buffers, offsets, generations, and in-flight slots remain intact.
    /// </summary>
    internal bool TryEnsureFrameSlotCount(int requiredSlots)
    {
        if (requiredSlots <= FrameSlotCount)
            return requiredSlots > 0;
        lock (_structureSync)
            return TryEnsureFrameSlotCountLocked(requiredSlots);
    }

    private bool TryEnsureFrameSlotCountLocked(int requiredSlots)
    {
        int previousCount = FrameSlotCount;
        if (requiredSlots <= previousCount)
            return requiredSlots > 0;
        if (!IsActive || !_backend.IsOperational || requiredSlots > MaxFrameSlots ||
            Volatile.Read(ref _hostAccess) != 0)
            return false;

        ulong currentBytes = checked((ulong)Math.Max(AllocatedBytes, 0L));
        ulong requestedBytes = 0UL;
        int addedSlots = requiredSlots - previousCount;
        for (int lane = 0; lane < LaneCount; lane++)
            for (int group = 0; group < _chunkGroupCounts[lane]; group++)
            {
                ulong capacity = _chunks[lane][group][0]!.Capacity;
                if (capacity > MaximumMappedBytes / (ulong)addedSlots)
                    return false;
                ulong additionalBytes = capacity * (ulong)addedSlots;
                if (requestedBytes > MaximumMappedBytes - additionalBytes)
                    return false;
                requestedBytes += additionalBytes;
            }
        if (currentBytes > MaximumMappedBytes - requestedBytes)
            return false;

        ulong allocatedBytes = 0UL;
        bool committed = false;
        try
        {
            for (int lane = 0; lane < LaneCount; lane++)
                for (int group = 0; group < _chunkGroupCounts[lane]; group++)
                {
                    VulkanFrameDataChunk?[] chunks = _chunks[lane][group];
                    for (int slot = previousCount; slot < requiredSlots; slot++)
                    {
                        VulkanFrameDataChunk chunk = CreateChunk(chunks[0]!.Capacity, _usages[lane], _labels[lane]);
                        chunks[slot] = chunk;
                        if (chunk.AllocationLength > MaximumMappedBytes ||
                            allocatedBytes > MaximumMappedBytes - chunk.AllocationLength ||
                            currentBytes > MaximumMappedBytes - allocatedBytes - chunk.AllocationLength)
                            return false;
                        allocatedBytes += chunk.AllocationLength;
                        chunk.InitializeGeneration(Generation);
                    }
                }

            for (int lane = 0; lane < LaneCount; lane++)
                if (_reservedLaneActiveGroups[lane][0] >= 0)
                    for (int slot = previousCount; slot < requiredSlots; slot++)
                        _reservedLaneActiveGroups[lane][slot] = _chunkGroupCounts[lane] - 1;

            Interlocked.Add(ref _allocatedBytes, checked((long)allocatedBytes));
            if (allocatedBytes != 0UL)
                Interlocked.Increment(ref _allocationCount);
            UpdateHighWater(ref _allocatedBytesHighWater, AllocatedBytes);
            UpdateHighWater(ref _allocationHighWater, AllocationCount);
            // Readers can address the added slots only after every lane is ready.
            Volatile.Write(ref _frameSlotCount, requiredSlots);
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
                for (int lane = 0; lane < LaneCount; lane++)
                    for (int group = 0; group < _chunkGroupCounts[lane]; group++)
                        for (int slot = previousCount; slot < requiredSlots; slot++)
                        {
                            _chunks[lane][group][slot]?.Destroy(_backend, nativeDestroyAllowed: true);
                            _chunks[lane][group][slot] = null;
                        }
        }
    }
}
