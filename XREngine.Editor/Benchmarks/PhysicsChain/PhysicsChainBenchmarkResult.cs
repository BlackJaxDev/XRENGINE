namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Machine-readable result emitted after a settled physics-chain benchmark.
/// Raw frame samples are retained so summaries can be independently checked.
/// </summary>
public sealed record PhysicsChainBenchmarkResult
{
    public required DateTimeOffset CompletedAt { get; init; }
    public required string ScenarioName { get; init; }
    public required int CopyCount { get; init; }
    public required int DeterministicSeed { get; init; }
    public required bool DebugDisplaysEnabled { get; init; }
    public required int SettleFrameCount { get; init; }
    public required double MeasurementDurationSeconds { get; init; }
    public required double SpawnMilliseconds { get; init; }
    public required double DestroyMilliseconds { get; init; }
    public required PhysicsChainBenchmarkFrameStatistics FrameStatistics { get; init; }
    public required double[] FrameTimesMilliseconds { get; init; }
    /// <summary>Resolved whole-frame GPU timestamp samples when the backend supports them.</summary>
    public double[] GpuFrameTimesMilliseconds { get; init; } = [];

    /// <summary>Whole-frame GPU percentiles calculated from <see cref="GpuFrameTimesMilliseconds"/>.</summary>
    public PhysicsChainBenchmarkFrameStatistics? GpuFrameStatistics { get; init; }
    public required long CpuUploadBytes { get; init; }
    public required long GpuCopyBytes { get; init; }
    public required long CpuReadbackBytes { get; init; }
    public required long DispatchGroupCount { get; init; }
    public required long DispatchIterationCount { get; init; }
    public required long ResidentParticleBytes { get; init; }
}
