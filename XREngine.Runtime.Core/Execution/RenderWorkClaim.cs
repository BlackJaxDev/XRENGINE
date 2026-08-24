namespace XREngine.Execution;

/// <summary>
/// Value queued between persistent render lanes.
/// </summary>
internal readonly record struct RenderWorkClaim(
    RenderWorkBatch Batch,
    long Generation,
    int ItemIndex,
    bool IsAffine,
    long EnqueuedTimestamp);
