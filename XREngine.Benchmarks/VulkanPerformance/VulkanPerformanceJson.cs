using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Benchmarks;

/// <summary>
/// Shared JSON policy for the Vulkan performance contract and evidence files.
/// </summary>
public static class VulkanPerformanceJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
        => new()
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
}
