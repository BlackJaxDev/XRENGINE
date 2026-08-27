using System.Text.Json;

namespace XREngine.Benchmarks;

/// <summary>
/// Machine-readable identity and result locations for one orchestrated run.
/// </summary>
public sealed class VulkanPerformanceRunManifest
{
    public int SchemaVersion { get; init; }
    public string Preset { get; init; } = string.Empty;
    public string GateScope { get; init; } = "Full";
    public bool PromotionEligible { get; init; }
    public string ProfileMode { get; init; } = string.Empty;
    public string ContractPath { get; init; } = string.Empty;
    public string ContractSha256 { get; init; } = string.Empty;
    public string SourceCommit { get; init; } = string.Empty;
    public bool DirtyWorktree { get; init; }
    public string BuildConfiguration { get; init; } = string.Empty;
    public string ExecutableSha256 { get; init; } = string.Empty;
    public Dictionary<string, string> DependencyManifestSha256 { get; init; } =
        new(StringComparer.Ordinal);
    public string OperatingSystem { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string CpuName { get; init; } = string.Empty;
    public int LogicalProcessorCount { get; init; }
    public ulong PhysicalMemoryBytes { get; init; }
    public string GpuName { get; init; } = string.Empty;
    public string GpuDriver { get; init; } = string.Empty;
    public string DisplayMode { get; init; } = string.Empty;
    public string PowerPlan { get; init; } = string.Empty;
    public string WindowMode { get; init; } = string.Empty;
    public string Resolution { get; init; } = string.Empty;
    public string PresentationProfile { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public List<VulkanPerformanceRunCohort> Cohorts { get; init; } = [];

    public static VulkanPerformanceRunManifest Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        VulkanPerformanceRunManifest manifest =
            JsonSerializer.Deserialize<VulkanPerformanceRunManifest>(
                stream,
                VulkanPerformanceJson.Options)
            ?? throw new InvalidDataException(
                $"Vulkan performance run manifest '{path}' was empty.");

        if (manifest.SchemaVersion != 2)
            throw new InvalidDataException(
                $"Unsupported Vulkan performance run schema {manifest.SchemaVersion}.");
        if (manifest.Cohorts.Count == 0)
            throw new InvalidDataException(
                "The Vulkan performance run manifest contains no cohorts.");

        return manifest;
    }
}
