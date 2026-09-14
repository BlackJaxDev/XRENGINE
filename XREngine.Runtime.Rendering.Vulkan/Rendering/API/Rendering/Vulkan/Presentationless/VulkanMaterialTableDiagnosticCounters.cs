namespace XREngine.Rendering.Vulkan;

/// <summary>Material bank work counters; querying them neither prepares nor uploads rows.</summary>
public readonly record struct VulkanMaterialTableDiagnosticCounters(
    long NativeAllocations, long PageWrites, long BytesWritten, long Reuses,
    long GrowthPending, long EmergencyWaits, int Banks, int PendingAllocations,
    long StandbyAllocationsQueued, long StandbyAllocationsReady,
    long StandbyClaims,
    long StandbyReplenishmentFailures, int StandbyBanks,
    int StandbyPendingAllocations,
    ulong DescriptorWrites, ulong DescriptorRetirements, long ClosureLeaseAcquires,
    long ClosureLeaseReleases, int LiveDescriptorSlots, int LeasedDescriptorSlots);
