namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameDataArena
{
    /// <summary>
    /// Captures allocator ownership only on a rejected publication. Successful
    /// frame recording does not format diagnostic strings or allocate snapshots.
    /// </summary>
    internal string DescribeReservedLaneCursorState(int frameSlot, EVulkanFrameDataLane lane)
    {
        bool validSlot = IsValidFrameSlot(frameSlot);
        bool validLane = IsValidLane(lane);
        int group = validSlot && validLane
            ? _reservedLaneActiveGroups[(int)lane][frameSlot]
            : -1;
        int groupCount = validLane ? _chunkGroupCounts[(int)lane] : 0;
        VulkanFrameDataChunk? chunk = validSlot && validLane && group >= 0 && group < groupCount
            ? _chunks[(int)lane][group][frameSlot]
            : null;
        return $"Arena={Identity} Generation={Generation} Active={IsActive} " +
            $"FrameSlot={frameSlot}/{_frameSlotCount} ResetEpoch={(validSlot ? _frameSlotResetEpochs[frameSlot] : 0u)} " +
            $"Lane={lane} Group={group}/{groupCount} ChunkPresent={chunk is not null} " +
            $"State={chunk?.GetState(Generation) ?? VulkanFrameDataArenaSlotState.Invalid} " +
            $"HostAccess={Volatile.Read(ref _hostAccess)} HostThread={Volatile.Read(ref _hostThreadId)}";
    }
}
