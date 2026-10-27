using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Gives the splat counts of a cache in <c>report.json</c>.</summary>
public sealed class BakeReportSplats
{
    /// <summary>Gets or sets the number of splats at full detail.</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>Gets or sets the number of splats at full detail that do not move.</summary>
    [JsonPropertyName("static")]
    public int Static { get; set; }

    /// <summary>Gets or sets the number of splats in each level, from the coarsest level to full detail.</summary>
    [JsonPropertyName("perLevel")]
    public int[] PerLevel { get; set; } = [];
}
