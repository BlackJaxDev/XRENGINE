using System.Text.Json;

namespace XREngine.Benchmarks;

/// <summary>
/// Tracked definition of canonical Vulkan performance presets and cohorts.
/// </summary>
public sealed class VulkanPerformanceContract
{
    public int SchemaVersion { get; init; }
    public double DefaultVarianceThresholdPercent { get; init; }
    public double DefaultRegressionThresholdPercent { get; init; }
    public VulkanPerformanceGateEnvironment PrimaryGateEnvironment { get; init; } =
        new();
    public VulkanPerformanceWindowPolicy WindowPolicy { get; init; } = new();
    public Dictionary<string, VulkanPerformanceProfileDefinition> ProfileModes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VulkanPerformancePresetDefinition> Presets { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<VulkanPerformanceCohort> Cohorts { get; init; } = [];

    public static VulkanPerformanceContract Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        VulkanPerformanceContract contract =
            JsonSerializer.Deserialize<VulkanPerformanceContract>(
                stream,
                VulkanPerformanceJson.Options)
            ?? throw new InvalidDataException(
                $"Vulkan performance contract '{path}' was empty.");

        if (contract.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Unsupported Vulkan performance contract schema {contract.SchemaVersion}.");
        if (contract.Presets.Count == 0 || contract.Cohorts.Count == 0)
            throw new InvalidDataException(
                "The Vulkan performance contract must define presets and cohorts.");

        return contract;
    }
}
