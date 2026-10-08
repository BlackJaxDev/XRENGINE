namespace XREngine.Rendering.Compute;

/// <summary>Reports immutable output page availability and reuse.</summary>
/// <param name="UnpreparedBufferReleaseCount">
/// Free-page buffers released because they were never made ready for native reuse.
/// </param>
public readonly record struct PhysicsChainGpuOutputPageDiagnostics(
    long BusyCount,
    long ReuseCount,
    long FailureCount,
    bool HasPublishedPage,
    uint ProducerEpoch,
    long UnpreparedBufferReleaseCount);
