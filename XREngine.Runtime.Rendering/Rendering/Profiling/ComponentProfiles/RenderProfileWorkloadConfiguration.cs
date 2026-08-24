using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Fixture scale and target-specific deterministic input sizes.</summary>
public sealed record RenderProfileWorkloadConfiguration
{
    [JsonPropertyName("chain_count")]
    public int? ChainCount { get; init; }

    [JsonPropertyName("draw_count")]
    public int? DrawCount { get; init; }

    [JsonPropertyName("descriptor_count")]
    public int? DescriptorCount { get; init; }

    [JsonPropertyName("barrier_count")]
    public int? BarrierCount { get; init; }

    [JsonPropertyName("upload_bytes")]
    public int? UploadBytes { get; init; }

    [JsonPropertyName("pass_iterations")]
    public int? PassIterations { get; init; }

    [JsonPropertyName("target_inputs")]
    public IReadOnlyDictionary<string, long> TargetInputs { get; init; } = new Dictionary<string, long>();

    public void Validate()
    {
        if (ChainCount < 0 || DrawCount < 0 || DescriptorCount < 0 || BarrierCount < 0 || UploadBytes < 0 || PassIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChainCount), "Workload counts must be non-negative and pass iterations must be positive.");
        if (TargetInputs is null || TargetInputs.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0))
            throw new ArgumentException("Target inputs require non-empty keys and non-negative values.");
    }
}
