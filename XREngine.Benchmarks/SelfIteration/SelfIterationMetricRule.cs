namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Describes one lower- or higher-is-better comparison metric.
/// </summary>
public sealed class SelfIterationMetricRule
{
    public string Name { get; set; } = string.Empty;
    public bool LowerIsBetter { get; set; } = true;
    public double Weight { get; set; } = 1.0;
    public double MinimumImprovementPercent { get; set; } = 1.0;
    public double MaximumRegressionPercent { get; set; } = 1.0;
    public bool Required { get; set; } = true;
    public double? MaximumCandidateValue { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("Metric rule Name is required.");
        if (Weight <= 0)
            throw new InvalidDataException($"Metric '{Name}' Weight must be positive.");
        if (MinimumImprovementPercent < 0 || MaximumRegressionPercent < 0)
            throw new InvalidDataException($"Metric '{Name}' percentages cannot be negative.");
    }
}
