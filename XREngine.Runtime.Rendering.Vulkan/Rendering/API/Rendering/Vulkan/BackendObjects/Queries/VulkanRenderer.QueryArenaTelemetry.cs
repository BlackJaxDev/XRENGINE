namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation and pressure counters for renderer-owned query arenas.
/// </summary>
internal readonly record struct QueryArenaTelemetry(
    uint PoolCount,
    uint Capacity,
    uint AllocatedSlots,
    uint HighWaterSlots,
    ulong AllocationCount,
    ulong ReleaseCount,
    ulong GrowthCount,
    ulong ExhaustionCount,
    ulong ResetEpochCount,
    ulong RetiredPoolCount);
