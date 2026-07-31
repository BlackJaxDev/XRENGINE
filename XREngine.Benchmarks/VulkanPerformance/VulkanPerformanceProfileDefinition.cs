namespace XREngine.Benchmarks;

/// <summary>
/// Machine-readable observer-overhead contract for a profiler mode.
/// </summary>
public sealed class VulkanPerformanceProfileDefinition
{
    public bool ValidationAllowed { get; init; }
    public bool CommandLabelsAllowed { get; init; }
    public bool DenseGpuTimestampsAllowed { get; init; }
    public bool P3LoggingAllowed { get; init; }
    public bool ImGuiAllowed { get; init; }
    public bool DynamicTextAllowed { get; init; }
    public bool CleanComparisonSuitable { get; init; }
    public bool PromotionEligible { get; init; }
    public string MaximumLogVerbosity { get; init; } = "Verbose";
    public string ExpectedOverhead { get; init; } = string.Empty;
}
