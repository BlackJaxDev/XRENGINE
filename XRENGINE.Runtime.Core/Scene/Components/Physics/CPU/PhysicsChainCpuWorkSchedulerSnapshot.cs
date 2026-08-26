namespace XREngine.Components;

public readonly record struct PhysicsChainCpuWorkSchedulerSnapshot(
    int WorkerCount,
    int HandleCapacity,
    int BatchSize,
    int SubmittedHandleCount,
    int CompletedRangeCount,
    int FailedRangeCount,
    long ExecutionCount,
    long CapacityGrowthCount,
    bool Deterministic);
