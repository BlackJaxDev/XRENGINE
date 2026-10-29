using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Describes the baked motion in <c>report.json</c>.</summary>
public sealed class BakeReportClip
{
    /// <summary>Gets or sets the name of the clip. It is null for a world and for content with no clip.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets why the bake plays the clip, for example <c>idle</c> or <c>none</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the length of the baked motion, in seconds. It is 0 for a still cache.</summary>
    [JsonPropertyName("durationSeconds")]
    public float DurationSeconds { get; set; }

    /// <summary>Gets or sets the loop mode of the cache: <c>Loop</c>, <c>PingPong</c>, or <c>Once</c>.</summary>
    [JsonPropertyName("loopMode")]
    public string LoopMode { get; set; } = string.Empty;
}
