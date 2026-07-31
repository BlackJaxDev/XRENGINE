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
        if (contract.ProfileModes.Count == 0 ||
            contract.Presets.Count == 0 ||
            contract.Cohorts.Count == 0)
            throw new InvalidDataException(
                "The Vulkan performance contract must define profile modes, presets, and cohorts.");

        string[] requiredProfileModes =
        [
            "Diagnostics",
            "DevelopmentProfile",
            "CleanProfile",
            "ReleaseBenchmark",
        ];
        for (int i = 0; i < requiredProfileModes.Length; i++)
        {
            if (!contract.ProfileModes.ContainsKey(requiredProfileModes[i]))
            {
                throw new InvalidDataException(
                    $"The Vulkan performance contract is missing required profile mode '{requiredProfileModes[i]}'.");
            }
        }

        foreach ((string name, VulkanPerformanceProfileDefinition profile) in
                 contract.ProfileModes)
        {
            if (profile.MaximumLogVerbosity is not
                ("None" or "Minimal" or "Normal" or "Verbose"))
            {
                throw new InvalidDataException(
                    $"Profile mode '{name}' has unsupported maximum log verbosity '{profile.MaximumLogVerbosity}'.");
            }
        }
        foreach ((string name, VulkanPerformancePresetDefinition preset) in
                 contract.Presets)
        {
            if (!contract.ProfileModes.ContainsKey(preset.ProfileMode))
            {
                throw new InvalidDataException(
                    $"Preset '{name}' references unknown profile mode '{preset.ProfileMode}'.");
            }
        }

        return contract;
    }
}
