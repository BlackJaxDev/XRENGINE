using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Starts a bounded asynchronous capture of subsequent frames from one viewport.
    /// </summary>
    [XRMcp(Name = "start_viewport_sequence_capture", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Start a bounded asynchronous viewport frame-sequence capture with manifest, contact sheet, timing, camera, and image-difference metadata.")]
    public static Task<McpToolResponse> StartViewportSequenceCaptureAsync(
        McpToolContext context,
        [McpName("camera_node_id"), Description("Optional camera scene-node ID to target.")] string? cameraNodeId = null,
        [McpName("window_index"), Description("Window index to target when camera_node_id is omitted.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Viewport index within the target window.")] int viewportIndex = 0,
        [McpName("frame_count"), Description("Number of captured frames. Provide exactly one of frame_count or duration_seconds.")] int? frameCount = null,
        [McpName("duration_seconds"), Description("Wall-clock capture duration. Provide exactly one of duration_seconds or frame_count; maximum 60 seconds.")] double? durationSeconds = null,
        [McpName("frame_stride"), Description("Capture every Nth distinct render frame. Use 1 for subsequent/consecutive frames.")] int frameStride = 1,
        [McpName("capture_fps"), Description("Optional maximum capture cadence in frames per second. Omit to sample every frame allowed by frame_stride.")] double? captureFramesPerSecond = null,
        [McpName("max_frames"), Description("Hard frame cap for duration-based captures; ignored in favor of frame_count for count-based captures.")] int maxFrames = 120,
        [McpName("output_scale"), Description("Output image scale from 0.1 to 1.0. Scaling occurs after the bounded GPU readback.")] double outputScale = 1.0,
        [McpName("max_in_flight_readbacks"), Description("Bounded number of GPU readbacks/encodes allowed in flight, from 1 to 8.")] int maxInFlightReadbacks = 3,
        [McpName("overflow_policy"), Description("Backpressure behavior: fail preserves consecutive-frame guarantees; drop records skipped frames in the manifest.")] string overflowPolicy = "fail",
        [McpName("preserve_alpha"), Description("Preserve viewport alpha instead of forcing opaque output.")] bool preserveAlpha = false,
        [McpName("create_contact_sheet"), Description("Create a row-major PNG contact sheet after capture.")] bool createContactSheet = true,
        [McpName("contact_sheet_columns"), Description("Contact-sheet column count; 0 chooses an automatic near-square layout.")] int contactSheetColumns = 0,
        [McpName("contact_sheet_thumbnail_width"), Description("Contact-sheet cell thumbnail width from 64 to 1024 pixels.")] int contactSheetThumbnailWidth = 320,
        [McpName("compute_frame_differences"), Description("Compute hashes, luminance, black-pixel ratio, and normalized differences between subsequent frames.")] bool computeFrameDifferences = true,
        [McpName("output_dir"), Description("Root output directory. A unique capture subdirectory is always created; defaults under Build/_AgentValidation.")] string? outputDirectory = null)
    {
        if (!ViewportSequenceCaptureOptions.TryCreate(
                frameCount,
                durationSeconds,
                frameStride,
                captureFramesPerSecond,
                maxFrames,
                outputScale,
                maxInFlightReadbacks,
                overflowPolicy,
                preserveAlpha,
                createContactSheet,
                contactSheetColumns,
                contactSheetThumbnailWidth,
                computeFrameDifferences,
                outputDirectory,
                out ViewportSequenceCaptureOptions? options,
                out string? validationError))
            return Task.FromResult(new McpToolResponse(validationError ?? "Invalid viewport sequence capture options.", isError: true));

        XRViewport? viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
        if (viewport is null)
            return Task.FromResult(new McpToolResponse("No viewport found to capture.", isError: true));

        if (!ViewportSequenceCaptureManager.Instance.TryStart(viewport, options!, out ViewportSequenceCaptureSession? session, out string? startError))
            return Task.FromResult(new McpToolResponse(startError ?? "Failed to start viewport sequence capture.", isError: true));

        ViewportSequenceCaptureSnapshot snapshot = session!.CreateSnapshot(includeFrames: false);
        return Task.FromResult(new McpToolResponse(
            $"Started viewport sequence capture '{snapshot.CaptureId}'. Poll get_viewport_sequence_capture for completion.",
            snapshot));
    }

    /// <summary>
    /// Returns progress and artifacts for one sequence capture.
    /// </summary>
    [XRMcp(Name = "get_viewport_sequence_capture", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("Get viewport sequence capture progress, terminal state, artifact paths, and optionally per-frame metadata.")]
    public static Task<McpToolResponse> GetViewportSequenceCaptureAsync(
        McpToolContext context,
        [McpName("capture_id"), Description("Capture ID returned by start_viewport_sequence_capture.")] string captureId,
        [McpName("include_frames"), Description("Include per-frame and dropped-frame metadata in the response. The manifest always includes it.")] bool includeFrames = false)
    {
        if (!ViewportSequenceCaptureManager.Instance.TryGet(captureId, out ViewportSequenceCaptureSession? session))
            return Task.FromResult(new McpToolResponse($"Viewport sequence capture '{captureId}' was not found or has expired from in-memory history.", isError: true));

        ViewportSequenceCaptureSnapshot snapshot = session!.CreateSnapshot(includeFrames);
        return Task.FromResult(new McpToolResponse(
            $"Viewport sequence capture '{snapshot.CaptureId}' is {snapshot.State}.",
            snapshot,
            isError: string.Equals(snapshot.State, "failed", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Lists active and recently completed sequence captures.
    /// </summary>
    [XRMcp(Name = "list_viewport_sequence_captures", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Caller)]
    [Description("List active and recently completed viewport sequence captures without per-frame payloads.")]
    public static Task<McpToolResponse> ListViewportSequenceCapturesAsync(
        McpToolContext context,
        [McpName("active_only"), Description("Return only captures that are still capturing, draining readbacks, or finalizing.")] bool activeOnly = false)
    {
        ViewportSequenceCaptureSnapshot[] captures = ViewportSequenceCaptureManager.Instance.List(activeOnly);
        return Task.FromResult(new McpToolResponse(
            $"Found {captures.Length} viewport sequence capture session(s).",
            new
            {
                count = captures.Length,
                active_only = activeOnly,
                captures,
            }));
    }

    /// <summary>
    /// Stops an active capture and finalizes any frames already in flight.
    /// </summary>
    [XRMcp(Name = "cancel_viewport_sequence_capture", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.Main)]
    [Description("Cancel an active viewport sequence capture, drain in-flight readbacks, and finalize its partial manifest/contact sheet.")]
    public static Task<McpToolResponse> CancelViewportSequenceCaptureAsync(
        McpToolContext context,
        [McpName("capture_id"), Description("Capture ID returned by start_viewport_sequence_capture.")] string captureId)
    {
        if (!ViewportSequenceCaptureManager.Instance.TryGet(captureId, out ViewportSequenceCaptureSession? session))
            return Task.FromResult(new McpToolResponse($"Viewport sequence capture '{captureId}' was not found or has expired from in-memory history.", isError: true));

        bool canceled = session!.Cancel();
        ViewportSequenceCaptureSnapshot snapshot = session.CreateSnapshot(includeFrames: false);
        string message = canceled
            ? $"Cancellation requested for viewport sequence capture '{snapshot.CaptureId}'. In-flight frames are being finalized."
            : $"Viewport sequence capture '{snapshot.CaptureId}' is already {snapshot.State}; no cancellation was needed.";
        return Task.FromResult(new McpToolResponse(message, snapshot));
    }
}
