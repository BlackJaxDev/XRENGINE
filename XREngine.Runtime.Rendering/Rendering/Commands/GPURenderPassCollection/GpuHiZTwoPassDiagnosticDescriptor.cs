namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable references to the GPU-owned streams produced by one completed
/// current-depth two-pass Hi-Z execution. Consumers must wait for the matching
/// native submission receipt before reading any referenced buffer.
/// </summary>
public readonly record struct GpuHiZTwoPassDiagnosticDescriptor(
    ulong LogicalEngineFrameId,
    EMeshSubmissionStrategy Strategy,
    bool TwoPassExecuted,
    uint CandidateUpperBound,
    XRDataBuffer? PhaseOneDrawIds,
    XRDataBuffer? PhaseOneCount,
    XRDataBuffer LateDrawIds,
    XRDataBuffer LateCount,
    XRDataBuffer? CandidateCount,
    XRDataBuffer CullControlMetadata,
    XRDataBuffer? VisibilityHistory)
{
    /// <summary>Exact host decision used to force the early stream visible for this frame.</summary>
    public bool TemporalInvalidated { get; init; }
    public bool CameraCut { get; init; }
    public bool ProjectionDiscontinuity { get; init; }
    public bool UnsafeSceneRevision { get; init; }
    /// <summary>CPU planning time, not GPU execution time or a visibility readback.</summary>
    public double OcclusionCpuMilliseconds { get; init; }
}
