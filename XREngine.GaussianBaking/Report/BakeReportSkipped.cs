using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Describes one component that renders content but that the bake does not capture, in <c>report.json</c>.</summary>
public sealed class BakeReportSkipped
{
    /// <summary>Gets or sets the path of the component node, with node names joined by <c>/</c>.</summary>
    [JsonPropertyName("node")]
    public string Node { get; set; } = string.Empty;

    /// <summary>Gets or sets the type name of the component.</summary>
    [JsonPropertyName("component")]
    public string Component { get; set; } = string.Empty;

    /// <summary>Gets or sets why the bake skips the component, for example <c>not-captured</c>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
