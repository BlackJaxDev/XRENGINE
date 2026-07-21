namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Defines the timing policy shared by interactive and automated physics-chain
/// benchmark runners.
/// </summary>
public sealed record PhysicsChainBenchmarkConfiguration
{
    public const int DefaultStableFrameCount = 30;
    public const int DefaultMinimumSampleFrameCount = 1_000;
    public const double DefaultMinimumDurationSeconds = 30.0;
    public const int DefaultDeterministicSeed = 0x58524348;

    public int StableFrameCount { get; init; } = DefaultStableFrameCount;
    public int MinimumSampleFrameCount { get; init; } = DefaultMinimumSampleFrameCount;
    public double MinimumDurationSeconds { get; init; } = DefaultMinimumDurationSeconds;
    public int DeterministicSeed { get; init; } = DefaultDeterministicSeed;

    /// <summary>
    /// Throws when the configuration could produce an unbounded settle phase
    /// or an empty/invalid timed interval.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(StableFrameCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumSampleFrameCount, 1);

        if (!double.IsFinite(MinimumDurationSeconds) || MinimumDurationSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(MinimumDurationSeconds));
    }
}
