using System.Text.Json.Serialization;

namespace XREngine.Browser;

/// <summary>References stable cooked identities independently of physical GPU handles.</summary>
internal sealed class BrowserCookedInstanceDto
{
    public required string Mesh { get; init; }
    public required string Material { get; init; }
    public required float[] ModelMatrix { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Animation { get; init; }
    public bool Occluder { get; init; }
}
