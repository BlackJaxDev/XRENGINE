using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Bounded diagnostic span selection and external sampling correlation.</summary>
public sealed record RenderProfileCpuConfiguration
{
    [JsonPropertyName("stages")]
    public string[] Stages { get; init; } = [];

    [JsonPropertyName("capacity_per_thread")]
    public int CapacityPerThread { get; init; } = 65536;

    [JsonPropertyName("emit_markers")]
    public bool EmitMarkers { get; init; }

    /// <summary>Identity of an externally attached sampler, recorded as diagnostic evidence.</summary>
    [JsonPropertyName("sampler_identity")]
    public string? SamplerIdentity { get; init; }

    public void Validate()
    {
        if (Stages is null || Stages.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("CPU stages must be non-empty stage names.");
        if (CapacityPerThread is < 1 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(CapacityPerThread), "CPU span capacity must be in [1, 1048576] per thread.");
        if (SamplerIdentity is not null && string.IsNullOrWhiteSpace(SamplerIdentity))
            throw new ArgumentException("An external sampler identity must be non-empty.");
    }
}
