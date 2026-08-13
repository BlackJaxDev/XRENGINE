using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Explicit mutation experiment; excluded from the underlying workload identity.</summary>
public sealed record RenderProfileMutationConfiguration
{
    [JsonPropertyName("policy")]
    public RenderProfileMutationPolicy Policy { get; init; } = RenderProfileMutationPolicy.StableReuse;

    [JsonPropertyName("dirty_every_n_frames")]
    public int DirtyEveryNFrames { get; init; } = 1;

    public void Validate()
    {
        if (Policy == RenderProfileMutationPolicy.DirtyEveryNFrames && DirtyEveryNFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(DirtyEveryNFrames));
    }
}
