namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Complete environment, scenario, stage, and raw-sample evidence for one
/// benchmark run. Unsupported backend points are records, not omitted cases.
/// </summary>
public sealed record PhysicsChainBenchmarkEvidence
{
    public required PhysicsChainBenchmarkResult Result { get; init; }
    public required PhysicsChainBenchmarkCase MatrixCase { get; init; }
    public required PhysicsChainBenchmarkMeasurementKind MeasurementKind { get; init; }
    public required PhysicsChainBenchmarkEnvironment Environment { get; init; }
    public required PhysicsChainBenchmarkCpuStageMetrics CpuStages { get; init; }
    public required PhysicsChainBenchmarkGpuStageMetrics GpuStages { get; init; }
    public required PhysicsChainBenchmarkPopulationMetrics Population { get; init; }
    public required int MatchedRunIndex { get; init; }
    public required double CpuMillisecondsPerActiveChain { get; init; }
    public required double CpuMillisecondsPerActiveParticle { get; init; }
    public string? UnsupportedReason { get; init; }
    public PhysicsChainBenchmarkCpuHardwareMetrics CpuHardware { get; init; } = PhysicsChainBenchmarkCpuHardwareMetrics.Unavailable;
    public PhysicsChainBenchmarkGpuHardwareMetrics GpuHardware { get; init; } = PhysicsChainBenchmarkGpuHardwareMetrics.Unavailable;
    public PhysicsChainBenchmarkArenaMetrics Arenas { get; init; } = new();
}
