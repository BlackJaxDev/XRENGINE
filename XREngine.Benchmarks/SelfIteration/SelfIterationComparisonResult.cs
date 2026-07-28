namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Complete deterministic decision for one attempted fix.
/// </summary>
public sealed class SelfIterationComparisonResult
{
    public bool Accepted { get; init; }
    public double AggregateImprovementPercent { get; init; }
    public List<string> Reasons { get; init; } = [];
    public List<SelfIterationMetricComparison> Metrics { get; init; } = [];
}
