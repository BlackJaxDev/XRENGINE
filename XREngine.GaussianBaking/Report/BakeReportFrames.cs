using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Gives the stored frames of a cache in <c>report.json</c>.</summary>
public sealed class BakeReportFrames
{
    /// <summary>Gets or sets the number of stored frames: 1 for a still cache, otherwise a multiple of 8.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Gets or sets the stored frame rate, in frames per second.</summary>
    [JsonPropertyName("rate")]
    public float Rate { get; set; }
}
