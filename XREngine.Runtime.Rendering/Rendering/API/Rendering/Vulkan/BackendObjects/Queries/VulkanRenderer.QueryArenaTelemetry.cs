namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Allocation and pressure counters for renderer-owned query arenas.
    /// </summary>
    public readonly record struct QueryArenaTelemetry(
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
}
