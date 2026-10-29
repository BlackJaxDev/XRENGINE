using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Describes the written cache in <c>report.json</c>.</summary>
public sealed class BakeReportCache
{
    /// <summary>Gets or sets the SHA-256 hash of the header bytes and the data of all chunks, as lowercase hexadecimal.</summary>
    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the header bytes plus the data bytes of chunk 0.</summary>
    [JsonPropertyName("chunk0Bytes")]
    public int Chunk0Bytes { get; set; }

    /// <summary>Gets or sets the header bytes plus the data bytes of all chunks.</summary>
    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the chunk table of the cache header, in chunk order.</summary>
    [JsonPropertyName("chunks")]
    public List<BakeReportChunk> Chunks { get; set; } = [];
}
