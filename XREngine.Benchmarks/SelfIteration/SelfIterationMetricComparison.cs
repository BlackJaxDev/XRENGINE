namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Before/after result for one metric in one scenario.
/// </summary>
public sealed class SelfIterationMetricComparison
{
    public string Scenario { get; init; } = string.Empty;
    public string Metric { get; init; } = string.Empty;
    public double Baseline { get; init; }
    public double Candidate { get; init; }
    public double ImprovementPercent { get; init; }
    public bool MaterialImprovement { get; init; }
    public bool Regression { get; init; }
}
