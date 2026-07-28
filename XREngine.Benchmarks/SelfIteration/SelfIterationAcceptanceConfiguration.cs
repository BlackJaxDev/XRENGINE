namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Defines the statistical and invariant gates for accepting an attempted fix.
/// </summary>
public sealed class SelfIterationAcceptanceConfiguration
{
    public double MinimumAggregateImprovementPercent { get; set; } = 1.0;
    public double MaximumMetricCoefficientOfVariationPercent { get; set; } = 10.0;
    public bool RequireAnyMaterialImprovement { get; set; } = true;
    public bool RequireCpuAndGpuDiagnosticDumps { get; set; } = true;
    public bool RequireStableWorkloadIdentity { get; set; } = true;
    public List<SelfIterationMetricRule> Metrics { get; set; } =
    [
        new()
        {
            Name = "RenderP95Ms",
            Weight = 3.0,
            MinimumImprovementPercent = 1.0,
            MaximumRegressionPercent = 1.0,
            Required = true,
        },
        new()
        {
            Name = "RenderP99Ms",
            Weight = 2.0,
            MinimumImprovementPercent = 1.0,
            MaximumRegressionPercent = 2.0,
            Required = true,
        },
        new()
        {
            Name = "GpuP95Ms",
            Weight = 2.0,
            MinimumImprovementPercent = 1.0,
            MaximumRegressionPercent = 2.0,
            Required = false,
        },
        new()
        {
            Name = "CollectVisibleP95Ms",
            Weight = 1.0,
            MinimumImprovementPercent = 2.0,
            MaximumRegressionPercent = 3.0,
            Required = false,
        },
    ];

    internal void Validate()
    {
        if (MinimumAggregateImprovementPercent < 0)
            throw new InvalidDataException("MinimumAggregateImprovementPercent cannot be negative.");
        if (MaximumMetricCoefficientOfVariationPercent < 0)
        {
            throw new InvalidDataException(
                "MaximumMetricCoefficientOfVariationPercent cannot be negative.");
        }
        if (Metrics.Count == 0)
            throw new InvalidDataException("At least one acceptance metric is required.");
        foreach (SelfIterationMetricRule metric in Metrics)
            metric.Validate();
    }
}
