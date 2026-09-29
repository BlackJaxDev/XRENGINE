using System.Text.Json.Serialization;

namespace XREngine.Browser;

/// <summary>Explicit surface policy and an already resident texture identity.</summary>
internal sealed class BrowserCookedMaterialDto
{
    public required float[] Tint { get; init; }
    public required string? Texture { get; init; }
    public required string AlphaMode { get; init; }
    public required string Shading { get; init; }
    public required string CullMode { get; init; }
    public required float AlphaCutoff { get; init; }
    public required bool CastShadow { get; init; }
    public required bool ReceiveShadow { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserCookedSamplerDto? Sampler { get; init; }
}
