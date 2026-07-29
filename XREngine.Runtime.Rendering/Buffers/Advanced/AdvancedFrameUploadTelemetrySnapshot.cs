namespace XREngine.Rendering;

/// <summary>
/// Allocation-free snapshot of the current advanced frame upload activity.
/// </summary>
public readonly record struct AdvancedFrameUploadTelemetrySnapshot(
    ulong FrameOrdinal,
    uint CurrentSlot,
    ulong BytesWritten,
    int DirtyRangeCount,
    ulong PerSlotCapacityBytes,
    ulong MappedCapacityBytes,
    int CapacityGrowthCount,
    ulong CapacityGrowthBytes,
    int OverflowAllocationCount,
    ulong OverflowBytes,
    int OverflowExhaustionCount,
    int RetiredGenerationCount,
    int GrowthDeferralCount,
    int SlotReuseDeferralCount,
    int PendingOverflowGenerationCount,
    int RetiredMainGenerationCount);
