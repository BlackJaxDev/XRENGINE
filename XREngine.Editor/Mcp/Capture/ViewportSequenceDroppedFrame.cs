using System.Text.Json.Serialization;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Records an eligible render frame that an explicit drop overflow policy skipped.
/// </summary>
internal sealed class ViewportSequenceDroppedFrame
{
    [JsonPropertyName("render_frame_id")]
    public ulong RenderFrameId { get; init; }

    [JsonPropertyName("capture_elapsed_seconds")]
    public double CaptureElapsedSeconds { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
