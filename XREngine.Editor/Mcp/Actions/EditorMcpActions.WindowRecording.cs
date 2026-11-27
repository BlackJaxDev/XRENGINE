using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Editor.WindowRecording;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Starts an MP4 video recording of an editor window using either OS Windows Graphics Capture
    /// (<c>wgc</c>), in-process renderer GPU readback piped to ffmpeg (<c>readback</c>), or
    /// automatic selection (<c>auto</c>).
    /// </summary>
    [XRMcp(Name = "start_window_video_recording", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Starts an ffmpeg MP4 recording and writes a video file to disk.")]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Start recording an editor window to an MP4 file at up to 60 fps. Supports 'auto', 'wgc', and 'readback' (works on locked/remote desktops).")]
    public static Task<McpToolResponse> StartWindowVideoRecordingAsync(
        McpToolContext context,
        [McpName("output_path"), Description("Optional destination MP4 path. Defaults to a timestamped file under Videos/XRENGINE.")] string? outputPath = null,
        [McpName("backend"), Description("Capture backend: 'auto' (default), 'wgc', or 'readback'.")] string backend = "auto",
        [McpName("encoder"), Description("H.264 encoder: 'auto' (prefers NVIDIA NVENC when available), 'nvenc', or 'x264'.")] string encoder = "auto",
        [McpName("window_index"), Description("Target editor window index (default: 0).")] int windowIndex = 0,
        [McpName("fps"), Description("Target output frame rate from 1 to 120 (default: 60).")] int fps = EditorWindowRecordingOptions.DefaultFramesPerSecond,
        [McpName("max_width"), Description("Maximum output width in pixels (default: 1920).")] int maxWidth = EditorWindowRecordingOptions.DefaultMaxWidth,
        [McpName("max_height"), Description("Maximum output height in pixels (default: 1080).")] int maxHeight = EditorWindowRecordingOptions.DefaultMaxHeight,
        [McpName("crf"), Description("Quality target (CRF for libx264, CQ for NVENC) from 10 to 40 (default: 26).")] int crf = EditorWindowRecordingOptions.DefaultCrf,
        [McpName("max_bitrate"), Description("Peak H.264 bitrate limit such as '3M' or '8M' (default: '8M').")] string maxBitrate = EditorWindowRecordingOptions.DefaultMaxBitrate,
        [McpName("duration_seconds"), Description("Optional automatic stop duration in seconds. Omit or <= 0 to record until stop_window_video_recording is called.")] double? durationSeconds = null)
    {
        if (!EditorWindowRecordingOptions.TryParseBackend(backend, out EEditorWindowRecordingBackend parsedBackend))
            return Task.FromResult(new McpToolResponse($"Unknown recording backend '{backend}'. Use 'auto', 'wgc', or 'readback'.", isError: true));
        if (!EditorWindowRecordingOptions.TryParseEncoder(encoder, out EEditorWindowVideoEncoder parsedEncoder))
            return Task.FromResult(new McpToolResponse($"Unknown video encoder '{encoder}'. Use 'auto', 'nvenc', or 'x264'.", isError: true));

        if (windowIndex < 0 || windowIndex >= RuntimeEngine.Windows.Count)
            return Task.FromResult(new McpToolResponse($"Window index {windowIndex} is out of range (active windows: {RuntimeEngine.Windows.Count}).", isError: true));

        XRWindow window = RuntimeEngine.Windows[windowIndex];
        EditorWindowRecordingOptions options = new()
        {
            OutputPath = outputPath,
            Backend = parsedBackend,
            Encoder = parsedEncoder,
            FramesPerSecond = fps,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Crf = crf,
            MaxBitrate = maxBitrate,
            MaxDuration = durationSeconds is { } seconds && seconds > 0.0
                ? TimeSpan.FromSeconds(seconds)
                : null,
        };

        if (!EditorWindowVideoRecorder.TryStart(window, options, out EditorWindowRecordingStatus? status, out string? failure))
            return Task.FromResult(new McpToolResponse(failure ?? "Failed to start window video recording.", isError: true));

        return Task.FromResult(new McpToolResponse(
            $"Started {status!.Backend} window recording at {status.FramesPerSecond} fps to '{status.OutputPath}'.",
            status));
    }

    /// <summary>
    /// Stops the active editor window video recording, flushes ffmpeg so the MP4 index is written,
    /// and returns final file and frame statistics.
    /// </summary>
    [XRMcp(Name = "stop_window_video_recording", Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Stops the active ffmpeg MP4 recording and finalizes the video file.")]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Stop the active editor window video recording and finalize its MP4 file.")]
    public static Task<McpToolResponse> StopWindowVideoRecordingAsync(McpToolContext context)
    {
        if (!EditorWindowVideoRecorder.TryStop(out EditorWindowRecordingStatus? status, out string? failure))
            return Task.FromResult(new McpToolResponse(failure ?? "Failed to stop window video recording.", status, isError: true));

        return Task.FromResult(new McpToolResponse(
            $"Stopped window video recording and wrote '{status!.OutputPath}' ({status.FileSizeBytes / (1024.0 * 1024.0):0.00} MB, {status.WrittenVideoFrameCount} frames).",
            status));
    }

    /// <summary>
    /// Reports whether an editor window recording is active along with elapsed time, frame counts,
    /// output path, and last error if any.
    /// </summary>
    [XRMcp(Name = "get_window_video_recording_status", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Get status, elapsed time, backend, frame counts, and output path for editor window video recording.")]
    public static Task<McpToolResponse> GetWindowVideoRecordingStatusAsync(McpToolContext context)
    {
        EditorWindowRecordingStatus status = EditorWindowVideoRecorder.GetStatus();
        string message = status.IsRecording
            ? $"Recording active via {status.Backend} ({status.ElapsedSeconds:0.0}s, captured={status.CapturedFrameCount}, written={status.WrittenVideoFrameCount}) to '{status.OutputPath}'."
            : status.OutputPath is not null
                ? $"Recording idle. Last recording: '{status.OutputPath}' ({status.FileSizeBytes / (1024.0 * 1024.0):0.00} MB)."
                : "No editor window video recording is active.";
        return Task.FromResult(new McpToolResponse(message, status));
    }
}
