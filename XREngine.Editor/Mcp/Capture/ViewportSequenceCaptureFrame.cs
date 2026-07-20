using System.Text.Json.Serialization;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Metadata and artifact state for one scheduled viewport readback.
/// </summary>
internal sealed class ViewportSequenceCaptureFrame
{
    [JsonIgnore]
    internal int CompletionClaimed;

    [JsonPropertyName("capture_index")]
    public int CaptureIndex { get; init; }

    [JsonPropertyName("render_frame_id")]
    public ulong RenderFrameId { get; init; }

    [JsonPropertyName("scheduled_at_utc")]
    public DateTimeOffset ScheduledAtUtc { get; init; }

    [JsonPropertyName("capture_elapsed_seconds")]
    public double CaptureElapsedSeconds { get; init; }

    [JsonPropertyName("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [JsonPropertyName("completion_elapsed_seconds")]
    public double? CompletionElapsedSeconds { get; set; }

    [JsonPropertyName("render_delta_seconds")]
    public float RenderDeltaSeconds { get; init; }

    [JsonPropertyName("source_x")]
    public int SourceX { get; init; }

    [JsonPropertyName("source_y")]
    public int SourceY { get; init; }

    [JsonPropertyName("source_width")]
    public int SourceWidth { get; init; }

    [JsonPropertyName("source_height")]
    public int SourceHeight { get; init; }

    [JsonPropertyName("output_width")]
    public int OutputWidth { get; set; }

    [JsonPropertyName("output_height")]
    public int OutputHeight { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("readback_raw_byte_count")]
    public long ReadbackRawByteCount { get; set; }

    [JsonPropertyName("readback_gpu_completion_seconds")]
    public double? ReadbackGpuCompletionSeconds { get; set; }

    [JsonPropertyName("readback_cpu_processing_seconds")]
    public double? ReadbackCpuProcessingSeconds { get; set; }

    [JsonPropertyName("readback_queue_slot")]
    public int? ReadbackQueueSlot { get; set; }

    [JsonPropertyName("readback_source_format")]
    public string? ReadbackSourceFormat { get; set; }

    [JsonPropertyName("used_multisample_resolve")]
    public bool UsedMultisampleResolve { get; set; }

    [JsonPropertyName("camera")]
    public ViewportSequenceCaptureCameraPose? Camera { get; init; }

    [JsonPropertyName("content_sha256")]
    public string? ContentSha256 { get; set; }

    [JsonPropertyName("mean_luminance")]
    public double? MeanLuminance { get; set; }

    [JsonPropertyName("black_pixel_ratio")]
    public double? BlackPixelRatio { get; set; }

    [JsonPropertyName("difference_from_previous")]
    public ViewportSequenceCaptureDifference? DifferenceFromPrevious { get; set; }

    [JsonPropertyName("contact_sheet_row")]
    public int? ContactSheetRow { get; set; }

    [JsonPropertyName("contact_sheet_column")]
    public int? ContactSheetColumn { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("succeeded")]
    public bool Succeeded => Error is null && CompletedAtUtc.HasValue;

    public ViewportSequenceCaptureFrame Clone()
        => new()
        {
            CaptureIndex = CaptureIndex,
            RenderFrameId = RenderFrameId,
            ScheduledAtUtc = ScheduledAtUtc,
            CaptureElapsedSeconds = CaptureElapsedSeconds,
            CompletedAtUtc = CompletedAtUtc,
            CompletionElapsedSeconds = CompletionElapsedSeconds,
            RenderDeltaSeconds = RenderDeltaSeconds,
            SourceX = SourceX,
            SourceY = SourceY,
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            OutputWidth = OutputWidth,
            OutputHeight = OutputHeight,
            Path = Path,
            Backend = Backend,
            ReadbackRawByteCount = ReadbackRawByteCount,
            ReadbackGpuCompletionSeconds = ReadbackGpuCompletionSeconds,
            ReadbackCpuProcessingSeconds = ReadbackCpuProcessingSeconds,
            ReadbackQueueSlot = ReadbackQueueSlot,
            ReadbackSourceFormat = ReadbackSourceFormat,
            UsedMultisampleResolve = UsedMultisampleResolve,
            Camera = Camera,
            ContentSha256 = ContentSha256,
            MeanLuminance = MeanLuminance,
            BlackPixelRatio = BlackPixelRatio,
            DifferenceFromPrevious = DifferenceFromPrevious,
            ContactSheetRow = ContactSheetRow,
            ContactSheetColumn = ContactSheetColumn,
            Error = Error,
        };
}
