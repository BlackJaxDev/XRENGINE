namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Untimed metric snapshot supplied by the active engine scenario.</summary>
public sealed record PhysicsChainBenchmarkScenarioMetrics
{
    public PhysicsChainBenchmarkCpuStageMetrics CpuStages { get; init; } = new();
    public PhysicsChainBenchmarkGpuStageMetrics GpuStages { get; init; } = new();
    public PhysicsChainBenchmarkPopulationMetrics Population { get; init; } = new();
    public PhysicsChainBenchmarkCpuHardwareMetrics CpuHardware { get; init; } = PhysicsChainBenchmarkCpuHardwareMetrics.Unavailable;
    public PhysicsChainBenchmarkGpuHardwareMetrics GpuHardware { get; init; } = PhysicsChainBenchmarkGpuHardwareMetrics.Unavailable;
    public PhysicsChainBenchmarkArenaMetrics Arenas { get; init; } = new();
    public long CpuUploadBytes { get; init; }
    public long GpuCopyBytes { get; init; }
    public long CpuReadbackBytes { get; init; }
    public long DispatchGroupCount { get; init; }
    public long DispatchIterationCount { get; init; }
    public long ResidentParticleBytes { get; init; }

    /// <summary>Resolved whole-frame GPU timestamps for the measured window.</summary>
    public double[] GpuFrameTimesMilliseconds { get; init; } = [];
}
