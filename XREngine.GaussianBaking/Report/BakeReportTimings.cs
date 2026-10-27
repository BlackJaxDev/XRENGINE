using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Gives the time of each bake step, in milliseconds, in <c>report.json</c>.</summary>
public sealed class BakeReportTimings
{
    /// <summary>Gets or sets the time to check the options and the encoders, and to load and check the input.</summary>
    [JsonPropertyName("load")]
    public long Load { get; set; }

    /// <summary>Gets or sets the time to place the splats on the rest surface.</summary>
    [JsonPropertyName("sample")]
    public long Sample { get; set; }

    /// <summary>Gets or sets the time to capture the views and to project their colors onto the splats.</summary>
    [JsonPropertyName("color")]
    public long Color { get; set; }

    /// <summary>Gets or sets the time to choose the motion and to sample the frames.</summary>
    [JsonPropertyName("animate")]
    public long Animate { get; set; }

    /// <summary>Gets or sets the time to build the splat level tree.</summary>
    [JsonPropertyName("tree")]
    public long Tree { get; set; }

    /// <summary>Gets or sets the time to build the driver meshes and to bind the splats.</summary>
    [JsonPropertyName("driver")]
    public long Driver { get; set; }

    /// <summary>Gets or sets the time to encode the cache and to write its files.</summary>
    [JsonPropertyName("encode")]
    public long Encode { get; set; }

    /// <summary>Gets or sets the time to record the loop videos and the posters.</summary>
    [JsonPropertyName("loop")]
    public long Loop { get; set; }
}
