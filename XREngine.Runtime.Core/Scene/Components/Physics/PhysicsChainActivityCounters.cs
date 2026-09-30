namespace XREngine.Components;

/// <summary>One-frame-delayed aggregate activity counters for a chain world.</summary>
public readonly record struct PhysicsChainActivityCounters(
    long SampledFrame,
    int ActiveCount,
    int SleepingCount,
    int EnteredSleepCount,
    ulong WakeCount);
