namespace XREngine.Benchmarks;

/// <summary>
/// Aggregated evidence and gate result for one canonical cohort.
/// </summary>
public sealed class VulkanPerformanceCohortReport
{
    public string Id { get; init; } = string.Empty;
    public string Lane { get; init; } = string.Empty;
    public int Repetitions { get; init; }
    public string BudgetMetric { get; init; } = string.Empty;
    public double BudgetMilliseconds { get; init; }
    public double BudgetMetricP95Median { get; init; }
    public double BudgetMetricRunVariancePercent { get; init; }
    public int MissedBudgetFrameCount { get; init; }
    public int FrameSampleCount { get; init; }
    public bool WithinAbsoluteBudget { get; init; }
    public bool WithinVarianceThreshold { get; init; }
    public bool BaselineCompatible { get; init; } = true;
    public double? BaselineDeltaPercent { get; init; }
    public string FailureClassification { get; init; } = string.Empty;
    public Dictionary<string, string> ComparisonIdentity { get; init; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, VulkanPerformanceMetricStatistics> Metrics { get; init; } =
        new(StringComparer.Ordinal);
}
