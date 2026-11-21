using System.Text.Json.Serialization;
using XREngine.Cutscenes;

namespace XREngine.Editor.Mcp;

/// <summary>
/// The keys of one curve in a <c>cutscene_add_track</c> call. <see cref="Values"/> holds the components of all keys in
/// order, so a vector curve with two keys has six values.
/// </summary>
public sealed class McpCutsceneCurveInput
{
    /// <summary>The key times in seconds of clip time, in increasing order.</summary>
    [JsonPropertyName("times")]
    public double[]? Times { get; set; }

    /// <summary>The key values: 1, 3, or 4 numbers for each key, depending on the curve type.</summary>
    [JsonPropertyName("values")]
    public float[]? Values { get; set; }

    /// <summary>How the curve blends between keys, or null for linear.</summary>
    [JsonPropertyName("interpolation")]
    public ECutsceneCurveInterpolation? Interpolation { get; set; }
}
