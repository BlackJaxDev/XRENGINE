using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Describes one video or poster of the preview loop in <c>report.json</c>.</summary>
public sealed class BakeReportOutput
{
    /// <summary>Gets or sets the file name in the output folder.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the height of the video or poster, in pixels.</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Gets or sets the codec: <c>vp9</c>, <c>h264</c>, or <c>webp</c>.</summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the size of the file, in bytes.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}
