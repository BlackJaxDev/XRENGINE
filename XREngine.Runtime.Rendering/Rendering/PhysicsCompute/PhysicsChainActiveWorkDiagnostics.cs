namespace XREngine.Rendering.Compute;

/// <summary>
/// CPU-visible structural diagnostics for the GPU active-work pipeline. Dynamic
/// active and overflow counts remain GPU resident to preserve zero-readback frames.
/// </summary>
public readonly record struct PhysicsChainActiveWorkDiagnostics(
    PhysicsChainActiveWorkScanMode ScanMode,
    int CandidateCount,
    int ActiveListCapacityPerBucket,
    int ActiveListGrowthCount,
    int DispatchPassCount,
    int StorageBarrierCount,
    int CommandBarrierCount,
    int ResourceGeneration,
    bool UsesGpuAuthoredIndirectArguments);
