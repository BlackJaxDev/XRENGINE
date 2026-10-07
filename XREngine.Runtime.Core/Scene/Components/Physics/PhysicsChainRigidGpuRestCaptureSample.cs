namespace XREngine.Components;

/// <summary>Contains sampled Stopwatch time for one cached rest capture attempt.</summary>
internal readonly record struct PhysicsChainRigidGpuRestCaptureSample(
    long RootTicks, int RootCount,
    long ExpandTicks, int ExpandCount,
    long PublishTicks, int PublishCount);
