namespace XREngine.Rendering;

/// <summary>
/// Allocation-free process snapshot of exact-foreground arbitration. The
/// counters are monotonic so a capture can prove that background work yielded
/// and later resumed without relying on sampled thread stacks.
/// </summary>
public readonly record struct RenderForegroundWorkSnapshot(
    long ForegroundEpoch,
    int ExactForegroundDepth,
    int ActiveBackgroundSlices,
    long ExactForegroundEntries,
    long ExactForegroundWaitTicks,
    long BackgroundSlicesStarted,
    long BackgroundSlicesCompleted,
    long BackgroundYieldCount,
    long BackgroundResumeCount);
