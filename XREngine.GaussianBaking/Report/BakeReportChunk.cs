using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Gives one entry of the chunk table of a cache in <c>report.json</c>.</summary>
public sealed class BakeReportChunk
{
    /// <summary>Gets or sets the position of the chunk. Chunk 0 is the root chunk.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Gets or sets the kind of data in the chunk: <c>Root</c>, <c>DriverLevel</c>, <c>SplatLevel</c>, or <c>DriverTemporal</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the splat level that the chunk adds.</summary>
    [JsonPropertyName("splatLevel")]
    public int SplatLevel { get; set; }

    /// <summary>Gets or sets the driver level that the chunk adds.</summary>
    [JsonPropertyName("driverLevel")]
    public int DriverLevel { get; set; }

    /// <summary>Gets or sets the temporal level that the chunk adds.</summary>
    [JsonPropertyName("temporalLevel")]
    public int TemporalLevel { get; set; }

    /// <summary>Gets or sets the size of the chunk data, in bytes. The cooked asset file is larger, because it also holds an envelope and the asset fields.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; set; }

    /// <summary>Gets or sets the SHA-256 hash of the chunk data, as lowercase hexadecimal.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
