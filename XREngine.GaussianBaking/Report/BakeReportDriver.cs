using System.Text.Json.Serialization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Report;

/// <summary>Gives the size of the driver meshes of a cache in <c>report.json</c>. The arrays are empty for a cache with no motion.</summary>
public sealed class BakeReportDriver
{
    /// <summary>Gets or sets the number of driver vertices that each driver level uses.</summary>
    [JsonPropertyName("verticesPerLevel")]
    public int[] VerticesPerLevel { get; set; } = [];

    /// <summary>Gets or sets the number of triangles of each driver level.</summary>
    [JsonPropertyName("trianglesPerLevel")]
    public int[] TrianglesPerLevel { get; set; } = [];
}
