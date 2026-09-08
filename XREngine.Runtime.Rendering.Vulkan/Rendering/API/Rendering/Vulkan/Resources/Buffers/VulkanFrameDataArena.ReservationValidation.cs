namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameDataArena
{
    /// <summary>Checks a previously established reservation without resetting or reallocating live slots.</summary>
    internal bool HasReservedLaneCapacity(EVulkanFrameDataLane lane, ulong generation, ulong minimumCapacity)
    {
        if (!IsActive || !_backend.IsOperational || !IsValidLane(lane) ||
            generation == 0 || generation != Generation || minimumCapacity == 0)
            return false;
        int laneIndex = (int)lane;
        lock (_structureSync)
        {
            for (int slot = 0; slot < _frameSlotCount; slot++)
            {
                int group = _reservedLaneActiveGroups[laneIndex][slot];
                if (group < 0 || group >= _chunkGroupCounts[laneIndex] ||
                    _chunks[laneIndex][group][slot] is not { } chunk ||
                    chunk.Buffer.Handle == 0 || chunk.Capacity < minimumCapacity)
                    return false;
            }
            return generation == Generation;
        }
    }
}
