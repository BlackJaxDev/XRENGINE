using System.Text.Json.Serialization;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Immutable camera pose sampled when a frame readback is scheduled.
/// </summary>
internal sealed class ViewportSequenceCaptureCameraPose
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; init; }

    [JsonPropertyName("node_name")]
    public string? NodeName { get; init; }

    [JsonPropertyName("position_x")]
    public float PositionX { get; init; }

    [JsonPropertyName("position_y")]
    public float PositionY { get; init; }

    [JsonPropertyName("position_z")]
    public float PositionZ { get; init; }

    [JsonPropertyName("rotation_x")]
    public float RotationX { get; init; }

    [JsonPropertyName("rotation_y")]
    public float RotationY { get; init; }

    [JsonPropertyName("rotation_z")]
    public float RotationZ { get; init; }

    [JsonPropertyName("rotation_w")]
    public float RotationW { get; init; }

    [JsonPropertyName("forward_x")]
    public float ForwardX { get; init; }

    [JsonPropertyName("forward_y")]
    public float ForwardY { get; init; }

    [JsonPropertyName("forward_z")]
    public float ForwardZ { get; init; }
}
