using System.Text.Json.Serialization;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Serializable point-in-time view of a viewport sequence capture session.
/// </summary>
internal sealed class ViewportSequenceCaptureSnapshot
{
    [JsonPropertyName("capture_id")]
    public string CaptureId { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("started_at_utc")]
    public DateTimeOffset StartedAtUtc { get; init; }

    [JsonPropertyName("finished_at_utc")]
    public DateTimeOffset? FinishedAtUtc { get; init; }

    [JsonPropertyName("last_updated_at_utc")]
    public DateTimeOffset LastUpdatedAtUtc { get; init; }

    [JsonPropertyName("elapsed_seconds")]
    public double ElapsedSeconds { get; init; }

    [JsonPropertyName("progress")]
    public double Progress { get; init; }

    [JsonPropertyName("capture_mode")]
    public string CaptureMode { get; init; } = string.Empty;

    [JsonPropertyName("requested_frame_count")]
    public int? RequestedFrameCount { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("frame_stride")]
    public int FrameStride { get; init; }

    [JsonPropertyName("capture_fps")]
    public double? CaptureFramesPerSecond { get; init; }

    [JsonPropertyName("max_frames")]
    public int MaxFrames { get; init; }

    [JsonPropertyName("output_scale")]
    public double OutputScale { get; init; }

    [JsonPropertyName("max_in_flight_readbacks")]
    public int MaxInFlightReadbacks { get; init; }

    [JsonPropertyName("overflow_policy")]
    public string OverflowPolicy { get; init; } = string.Empty;

    [JsonPropertyName("preserve_alpha")]
    public bool PreserveAlpha { get; init; }

    [JsonPropertyName("create_contact_sheet")]
    public bool CreateContactSheet { get; init; }

    [JsonPropertyName("compute_frame_differences")]
    public bool ComputeFrameDifferences { get; init; }

    [JsonPropertyName("scheduled_frame_count")]
    public int ScheduledFrameCount { get; init; }

    [JsonPropertyName("completed_frame_count")]
    public int CompletedFrameCount { get; init; }

    [JsonPropertyName("failed_frame_count")]
    public int FailedFrameCount { get; init; }

    [JsonPropertyName("dropped_frame_count")]
    public int DroppedFrameCount { get; init; }

    [JsonPropertyName("in_flight_readbacks")]
    public int InFlightReadbacks { get; init; }

    [JsonPropertyName("scheduled_output_pixels")]
    public long ScheduledOutputPixels { get; init; }

    [JsonPropertyName("source_width")]
    public int SourceWidth { get; init; }

    [JsonPropertyName("source_height")]
    public int SourceHeight { get; init; }

    [JsonPropertyName("estimated_output_width")]
    public int EstimatedOutputWidth { get; init; }

    [JsonPropertyName("estimated_output_height")]
    public int EstimatedOutputHeight { get; init; }

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("renderer_readback")]
    public ScreenshotReadbackStatus? RendererReadback { get; init; }

    [JsonPropertyName("window_index")]
    public int WindowIndex { get; init; }

    [JsonPropertyName("window_title")]
    public string WindowTitle { get; init; } = string.Empty;

    [JsonPropertyName("viewport_index")]
    public int ViewportIndex { get; init; }

    [JsonPropertyName("camera_node_id")]
    public string? CameraNodeId { get; init; }

    [JsonPropertyName("camera_node_name")]
    public string? CameraNodeName { get; init; }

    [JsonPropertyName("output_directory")]
    public string OutputDirectory { get; init; } = string.Empty;

    [JsonPropertyName("manifest_path")]
    public string ManifestPath { get; init; } = string.Empty;

    [JsonPropertyName("contact_sheet_path")]
    public string? ContactSheetPath { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("warnings")]
    public string[] Warnings { get; init; } = [];

    [JsonPropertyName("frames_included")]
    public bool FramesIncluded { get; init; }

    [JsonPropertyName("frames")]
    public ViewportSequenceCaptureFrame[]? Frames { get; init; }

    [JsonPropertyName("dropped_frames")]
    public ViewportSequenceDroppedFrame[]? DroppedFrames { get; init; }
}
