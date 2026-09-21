using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.GI.DDGI;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    [XRMcp(
        Name = "arm_ddgi_visibility_interruption",
        Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Intentionally interrupts bounded DDGI update cycles on one selected pipeline to validate GPU receipt lifetime.")]
    [Description("Arm a development-only DDGI receipt-lifetime diagnostic. It skips the selected pipeline's Visibility border copy for skip_count render frames.")]
    public static async Task<McpToolResponse> ArmDdgiVisibilityInterruptionAsync(
        McpToolContext context,
        [McpName("skip_count"), Description("Number of eligible Visibility stages to interrupt (1 through 120). ")] int skipCount,
        [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
        [McpName("vr_eye"), Description("Optional runtime VR viewport: left, right, or stereo.")] string? vrEye = null,
        [McpName("window_index"), Description("Target window index.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Target viewport index within the selected window.")] int viewportIndex = 0,
        CancellationToken token = default)
    {
        if (skipCount is < 1 or > 120)
            return new McpToolResponse("skip_count must be between 1 and 120.", isError: true);
        if (!TryResolveDdgiDiagnosticTarget(context, cameraNodeId, vrEye, windowIndex, viewportIndex, out var target, out string? error))
            return new McpToolResponse(error!, isError: true);

        try
        {
            var result = await RunDdgiDiagnosticOnRenderThreadAsync(target, "MCP: Arm DDGI visibility interruption", instance =>
            {
                if (!DDGIInterruptionDiagnostics.TryArm(instance, skipCount, out DDGIInterruptionDiagnosticSnapshot? snapshot, out string? failure))
                    throw new InvalidOperationException(failure);
                return snapshot!;
            }, token).ConfigureAwait(false);
            return new McpToolResponse("Armed DDGI Visibility interruption diagnostic.", BuildDdgiInterruptionPayload(result));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new McpToolResponse($"Failed to arm the DDGI interruption diagnostic: {ex.Message}", isError: true);
        }
    }

    [XRMcp(Name = "get_ddgi_visibility_interruption", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Read the development-only DDGI receipt-lifetime diagnostic for one exact selected pipeline.")]
    public static async Task<McpToolResponse> GetDdgiVisibilityInterruptionAsync(
        McpToolContext context,
        [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
        [McpName("vr_eye"), Description("Optional runtime VR viewport: left, right, or stereo.")] string? vrEye = null,
        [McpName("window_index"), Description("Target window index.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Target viewport index within the selected window.")] int viewportIndex = 0,
        CancellationToken token = default)
    {
        if (!TryResolveDdgiDiagnosticTarget(context, cameraNodeId, vrEye, windowIndex, viewportIndex, out var target, out string? error))
            return new McpToolResponse(error!, isError: true);

        try
        {
            DDGIInterruptionDiagnosticSnapshot? snapshot = await RunDdgiDiagnosticOnRenderThreadAsync(
                target,
                "MCP: Read DDGI visibility interruption",
                instance => DDGIInterruptionDiagnostics.TryGetSnapshot(instance, out DDGIInterruptionDiagnosticSnapshot? current) ? current : null,
                token).ConfigureAwait(false);
            if (snapshot is null)
                return new McpToolResponse("No active DDGI interruption diagnostic exists for the selected pipeline.", isError: true);
            return new McpToolResponse("Retrieved DDGI Visibility interruption diagnostic.", BuildDdgiInterruptionPayload(snapshot));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new McpToolResponse($"Failed to read the DDGI interruption diagnostic: {ex.Message}", isError: true);
        }
    }

    private static bool TryResolveDdgiDiagnosticTarget(
        McpToolContext context,
        string? cameraNodeId,
        string? vrEye,
        int windowIndex,
        int viewportIndex,
        out (XRViewport viewport, XRWindow window, XRRenderPipelineInstance instance, string? vrEye) target,
        out string? error)
    {
        target = default;
        if (!TryResolveStrictViewport(context, cameraNodeId, vrEye, windowIndex, viewportIndex, out XRViewport? viewport, out error))
            return false;
        XRWindow? window = viewport!.Window ?? RuntimeEngine.Windows.FirstOrDefault(candidate => candidate.Viewports.Contains(viewport));
        if (window is null)
        {
            error = "The selected viewport has no owning window.";
            return false;
        }
        target = (viewport, window, ResolveSelectedPipelineInstance(viewport, vrEye), vrEye);
        return true;
    }

    private static Task<T> RunDdgiDiagnosticOnRenderThreadAsync<T>(
        (XRViewport viewport, XRWindow window, XRRenderPipelineInstance instance, string? vrEye) target,
        string reason,
        Func<XRRenderPipelineInstance, T> action,
        CancellationToken token)
        => RunOnViewportRenderThreadAsync(target.viewport, target.window, reason, renderer =>
        {
            if (!RuntimeEngine.IsRenderThread ||
                !ReferenceEquals(renderer, target.window.Renderer) ||
                !renderer.AcceptsBackendWork ||
                renderer.IsDeviceLost ||
                !ReferenceEquals(target.instance, ResolveSelectedPipelineInstance(target.viewport, target.vrEye)))
            {
                throw new InvalidOperationException("The selected viewport pipeline or renderer changed before DDGI diagnostics could run.");
            }
            using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipeline(target.instance);
            return action(target.instance);
        }, token);

    private static object BuildDdgiInterruptionPayload(DDGIInterruptionDiagnosticSnapshot snapshot)
        => new
        {
            request_token = snapshot.RequestToken,
            pipeline_instance_id = snapshot.PipelineInstanceId,
            resource_generation = snapshot.ResourceGeneration,
            remaining_skips = snapshot.RemainingSkips,
            first_skipped_render_frame = snapshot.FirstSkippedRenderFrame,
            last_skipped_render_frame = snapshot.LastSkippedRenderFrame,
            first_skipped_state_frame_index = snapshot.FirstSkippedStateFrameIndex,
            last_skipped_state_frame_index = snapshot.LastSkippedStateFrameIndex,
            matched_abort_count = snapshot.MatchedAbortCount,
            accepted_nonpublishing_receipt_count = snapshot.AcceptedNonPublishingReceiptCount,
            unexpected_complete_count = snapshot.UnexpectedCompleteCount,
            unexpected_publication_count = snapshot.UnexpectedPublicationCount,
            first_accepted_recovery_render_frame = snapshot.FirstAcceptedRecoveryRenderFrame,
            first_accepted_recovery_state_frame_index = snapshot.FirstAcceptedRecoveryStateFrameIndex,
            failed_receipt_latched = snapshot.FailedReceiptLatched,
        };
}
