namespace XREngine.Benchmarks;

/// <summary>
/// Capture-window boundaries and promotion rules shared by all cohorts.
/// </summary>
public sealed class VulkanPerformanceWindowPolicy
{
    public string ColdStart { get; init; } = string.Empty;
    public string Warmup { get; init; } = string.Empty;
    public string SteadyState { get; init; } = string.Empty;
    public string StreamingChurn { get; init; } = string.Empty;
}
