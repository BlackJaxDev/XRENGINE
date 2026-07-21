namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Canonical numeric axes for physics-chain scale benchmarks. Scenario axes
/// are defined by <see cref="PhysicsChainBenchmarkRequiredMatrix"/>.
/// </summary>
public static class PhysicsChainBenchmarkMatrix
{
    private static readonly int[] ChainCountValues = [100, 500, 1_000, 2_000, 5_000, 10_000];
    private static readonly int[] DynamicSegmentCountValues = [4, 8, 16, 32];
    private static readonly float[] ActiveRatioValues = [1.0f, 0.5f, 0.1f];
    private static readonly int[] FixedSimulationRateValues = [30, 60, 90, 120];

    public static ReadOnlySpan<int> ChainCounts => ChainCountValues;
    public static ReadOnlySpan<int> DynamicSegmentCounts => DynamicSegmentCountValues;
    public static ReadOnlySpan<float> ActiveRatios => ActiveRatioValues;
    public static ReadOnlySpan<int> FixedSimulationRates => FixedSimulationRateValues;
}
