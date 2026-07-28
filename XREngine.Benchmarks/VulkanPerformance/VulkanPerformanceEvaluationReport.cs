using System.Text.Json;

namespace XREngine.Benchmarks;

/// <summary>
/// Complete machine-readable outcome of a Vulkan benchmark evaluation.
/// </summary>
public sealed class VulkanPerformanceEvaluationReport
{
    public int SchemaVersion { get; init; } = 1;
    public DateTime GeneratedUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public string PromotionStatus { get; init; } = string.Empty;
    public string Preset { get; init; } = string.Empty;
    public bool PromotionEligible { get; init; }
    public string SourceCommit { get; init; } = string.Empty;
    public bool DirtyWorktree { get; init; }
    public string ExecutableSha256 { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string GpuName { get; init; } = string.Empty;
    public string GpuDriver { get; init; } = string.Empty;
    public string DisplayMode { get; init; } = string.Empty;
    public double VarianceThresholdPercent { get; init; }
    public double RegressionThresholdPercent { get; init; }
    public List<VulkanPerformanceCohortReport> Cohorts { get; init; } = [];
    public List<VulkanPerformanceIssue> Issues { get; init; } = [];

    public static VulkanPerformanceEvaluationReport Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<VulkanPerformanceEvaluationReport>(
            stream,
            VulkanPerformanceJson.Options)
            ?? throw new InvalidDataException(
                $"Vulkan performance evaluation '{path}' was empty.");
    }
}
