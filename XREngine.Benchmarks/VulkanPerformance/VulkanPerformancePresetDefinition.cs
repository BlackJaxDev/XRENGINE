namespace XREngine.Benchmarks;

/// <summary>
/// Capture duration and promotion policy for a named Vulkan benchmark preset.
/// </summary>
public sealed class VulkanPerformancePresetDefinition
{
    public int WarmupSeconds { get; init; }
    public int CaptureSeconds { get; init; }
    public int Repetitions { get; init; }
    public bool PromotionEligible { get; init; }
    public string ProfileMode { get; init; } = string.Empty;
}
