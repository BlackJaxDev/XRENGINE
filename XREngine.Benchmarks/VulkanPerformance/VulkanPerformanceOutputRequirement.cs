namespace XREngine.Benchmarks;

/// <summary>
/// Per-frame output coverage required for a canonical Vulkan cohort.
/// </summary>
public sealed class VulkanPerformanceOutputRequirement
{
    public string Kind { get; init; } = string.Empty;
    public int MinimumRenderedViews { get; init; } = 1;
}
