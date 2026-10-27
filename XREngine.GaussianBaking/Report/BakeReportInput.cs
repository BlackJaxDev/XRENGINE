using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Identifies the input of a bake in <c>report.json</c>.</summary>
public sealed class BakeReportInput
{
    /// <summary>Gets or sets the path of the input <c>.pak</c> archive, as the command line gives it.</summary>
    [JsonPropertyName("pak")]
    public string Pak { get; set; } = string.Empty;

    /// <summary>Gets or sets the path of the asset in the archive.</summary>
    [JsonPropertyName("asset")]
    public string Asset { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 hash of the archive, or null when the bake stopped before the load.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
