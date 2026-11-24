using System.Text.Json.Serialization;
using XREngine.Cutscenes;

namespace XREngine.Editor.Mcp;

/// <summary>
/// One clip of a <c>cutscene_add_track</c> call. Times are in seconds. The tool converts the clip to the clip type of the
/// track and reads only the members that the type uses.
/// </summary>
public sealed class McpCutsceneClipInput
{
    /// <summary>The cutscene time at which the clip starts.</summary>
    [JsonPropertyName("start")]
    public double Start { get; set; }

    /// <summary>The length of the clip on the timeline.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>The source time at the start of the clip, for example the time inside an animation clip.</summary>
    [JsonPropertyName("clip_offset")]
    public double ClipOffset { get; set; }

    /// <summary>The playback rate of the source inside the clip, or null for one.</summary>
    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    /// <summary>What the clip shows outside its range, or null for the default.</summary>
    [JsonPropertyName("extrapolation")]
    public ECutsceneClipExtrapolation? Extrapolation { get; set; }

    /// <summary>Camera cut clips: the node of the camera to show. The default is the target node of the track.</summary>
    [JsonPropertyName("camera_node_id")]
    public string? CameraNodeId { get; set; }

    /// <summary>Animation clips: the path of the animation clip asset, absolute or relative to the game assets folder.</summary>
    [JsonPropertyName("animation_clip_path")]
    public string? AnimationClipPath { get; set; }

    /// <summary>Transform clips: the local position curve, 3 values per key.</summary>
    [JsonPropertyName("position")]
    public McpCutsceneCurveInput? Position { get; set; }

    /// <summary>Transform clips: the local rotation curve, 4 values (x, y, z, w) per key.</summary>
    [JsonPropertyName("rotation")]
    public McpCutsceneCurveInput? Rotation { get; set; }

    /// <summary>Transform clips: the local scale curve, 3 values per key.</summary>
    [JsonPropertyName("scale")]
    public McpCutsceneCurveInput? Scale { get; set; }

    /// <summary>Camera property clips: the vertical field of view curve in degrees, 1 value per key.</summary>
    [JsonPropertyName("field_of_view")]
    public McpCutsceneCurveInput? FieldOfView { get; set; }

    /// <summary>Property clips: a float curve, 1 value per key.</summary>
    [JsonPropertyName("float_curve")]
    public McpCutsceneCurveInput? FloatCurve { get; set; }

    /// <summary>Property clips: a vector curve, 3 values per key.</summary>
    [JsonPropertyName("vector3_curve")]
    public McpCutsceneCurveInput? Vector3Curve { get; set; }

    /// <summary>Property clips: a quaternion curve, 4 values (x, y, z, w) per key.</summary>
    [JsonPropertyName("quaternion_curve")]
    public McpCutsceneCurveInput? QuaternionCurve { get; set; }
}
