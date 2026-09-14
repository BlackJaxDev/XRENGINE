namespace XREngine.Rendering.Vulkan;

/// <summary>Monotonic publication-lowering diagnostics plus the current bounded map occupancy.</summary>
internal readonly record struct VulkanMaterialTablePreparedMapCounters(
    long NativeAllocations,
    long PageWrites,
    long BytesWritten,
    long Reuses,
    long GrowthPending,
    long EmergencyWaits,
    int Banks,
    int PendingAllocations,
    long StandbyAllocationsQueued,
    long StandbyAllocationsReady,
    long StandbyClaims,
    long StandbyReplenishmentFailures,
    int StandbyBanks,
    int StandbyPendingAllocations);
