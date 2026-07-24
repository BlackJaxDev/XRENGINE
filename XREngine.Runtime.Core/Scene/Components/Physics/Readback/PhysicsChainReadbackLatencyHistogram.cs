namespace XREngine.Components;

/// <summary>
/// Delivered request counts bucketed by submission-to-delivery frame latency.
/// </summary>
public readonly record struct PhysicsChainReadbackLatencyHistogram(
    long OneFrame,
    long TwoFrames,
    long ThreeFrames,
    long FourFrames,
    long FiveToEightFrames,
    long NineOrMoreFrames);
