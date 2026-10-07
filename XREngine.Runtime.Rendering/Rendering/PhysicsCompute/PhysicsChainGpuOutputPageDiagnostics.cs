namespace XREngine.Rendering.Compute;

/// <summary>Reports immutable output page availability and reuse.</summary>
public readonly record struct PhysicsChainGpuOutputPageDiagnostics(
    long BusyCount,
    long ReuseCount,
    long FailureCount,
    bool HasPublishedPage,
    uint ProducerEpoch);
