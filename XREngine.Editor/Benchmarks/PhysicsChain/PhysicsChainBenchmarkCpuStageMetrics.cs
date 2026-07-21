namespace XREngine.Editor.Benchmarks.PhysicsChain;

public sealed record PhysicsChainBenchmarkCpuStageMetrics
{
    public double StructuralCommandsMilliseconds { get; init; }
    public double InputGatherMilliseconds { get; init; }
    public double SchedulingMilliseconds { get; init; }
    public double SolveMilliseconds { get; init; }
    public double CollisionMilliseconds { get; init; }
    public double PaletteMilliseconds { get; init; }
    public double BoundsMilliseconds { get; init; }
    public double PublicationMilliseconds { get; init; }
    public double RendererSubmissionMilliseconds { get; init; }
    public double CompatibilitySynchronizationMilliseconds { get; init; }
    public double WorkerUtilizationPercent { get; init; }
    public double WorkImbalancePercent { get; init; }
    public double QueueLatencyMilliseconds { get; init; }
    public double LockAndFenceWaitMilliseconds { get; init; }
    public long ManagedAllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}
