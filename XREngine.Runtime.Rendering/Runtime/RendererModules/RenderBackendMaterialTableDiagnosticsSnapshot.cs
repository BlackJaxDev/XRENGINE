namespace XREngine.Rendering;

/// <summary>Material-table reserve and occupancy counters for a retained backend frame owner.</summary>
public readonly record struct RenderBackendMaterialTableDiagnosticsSnapshot(
    long NativeAllocations,
    long GrowthPending,
    int Banks,
    int PendingAllocations,
    long StandbyAllocationsQueued,
    long StandbyAllocationsReady,
    long StandbyClaims,
    long StandbyReplenishmentFailures,
    int StandbyBanks,
    int StandbyPendingAllocations);
