namespace XREngine.Benchmarks;

/// <summary>
/// Structured validation, compatibility, variance, or budget failure.
/// </summary>
public sealed class VulkanPerformanceIssue
{
    public string Code { get; init; } = string.Empty;
    public string Cohort { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
