namespace XREngine.Rendering.Compute;

/// <summary>
/// Low-frequency, CPU-side lifecycle and capacity counters for a GPU BVH.
/// GPU timing remains available through <see cref="BvhGpuProfiler"/>.
/// </summary>
public readonly record struct GpuBvhDiagnostics(
    ulong BuildCount,
    ulong RefitCount,
    ulong SkippedCleanFrameCount,
    ulong ClearCount,
    ulong BufferReallocationCount,
    uint LogicalPrimitiveCount,
    uint LogicalNodeCount,
    uint NodeCapacity,
    uint MortonCapacity,
    ulong RetainedBytes,
    ulong InitialBuildCount,
    ulong TopologyChangeRebuildCount,
    ulong NormalizationEscapeRebuildCount,
    ulong PeriodicQualityRebuildCount,
    uint LastDirtyLeafCount,
    ulong LastAabbUploadBytes,
    ulong LastAabbCopyBytes,
    ulong SynchronousReadbackBytes,
    ulong AsynchronousReadbackBytes,
    ulong ZeroReadbackSubmissionCount,
    ulong QualityAnalysisCount,
    uint QualityAnalysisRefitCadence);
