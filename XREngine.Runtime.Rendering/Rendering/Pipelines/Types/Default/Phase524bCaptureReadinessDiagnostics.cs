using System.Collections.Generic;

namespace XREngine.Rendering;

/// <summary>
/// Validation-only readiness gate that prevents Phase 5.2.4b image evidence
/// from freezing an imported texture's preview or promotion transition.
/// </summary>
internal static class Phase524bCaptureReadinessDiagnostics
{
    internal const int RequiredStableRenderFrames = 3;

    private static readonly object s_sync = new();
    private static ulong s_lastRenderFrameId = ulong.MaxValue;
    private static int s_stableRenderFrames;
    private static bool s_ready;
    private static string s_reason = "texture streaming has not been observed";

    internal static void Reset()
    {
        lock (s_sync)
        {
            s_lastRenderFrameId = ulong.MaxValue;
            s_stableRenderFrames = 0;
            s_ready = false;
            s_reason = "texture streaming has not been observed";
        }
    }

    internal static bool IsReady(out string reason)
    {
        ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        lock (s_sync)
        {
            if (s_ready)
            {
                reason = s_reason;
                return true;
            }

            if (s_lastRenderFrameId == renderFrameId)
            {
                reason = s_reason;
                return s_ready;
            }

            if (s_lastRenderFrameId != ulong.MaxValue && renderFrameId < s_lastRenderFrameId)
                s_stableRenderFrames = 0;
            s_lastRenderFrameId = renderFrameId;

            ImportedTextureStreamingTelemetry telemetry =
                XRTexture2D.GetImportedTextureStreamingTelemetry();
            IReadOnlyList<ImportedTextureStreamingTextureTelemetry> textures =
                XRTexture2D.GetImportedTextureStreamingTextureTelemetry();
            bool stableThisFrame = IsStable(telemetry, textures, out string currentReason);
            s_stableRenderFrames = stableThisFrame ? s_stableRenderFrames + 1 : 0;
            s_ready = stableThisFrame && s_stableRenderFrames >= RequiredStableRenderFrames;
            s_reason = s_ready
                ? $"stable for {s_stableRenderFrames} render frames"
                : stableThisFrame
                    ? $"settling stable window {s_stableRenderFrames}/{RequiredStableRenderFrames}"
                    : currentReason;

            reason = s_reason;
            return s_ready;
        }
    }

    internal static bool IsStable(
        ImportedTextureStreamingTelemetry telemetry,
        IReadOnlyList<ImportedTextureStreamingTextureTelemetry> textures,
        out string reason)
    {
        if (telemetry.ActiveImportScopes != 0 ||
            telemetry.PendingTransitionCount != 0 ||
            telemetry.ActiveDecodeCount != 0 ||
            telemetry.QueuedDecodeCount != 0 ||
            telemetry.ActiveGpuUploadCount != 0 ||
            telemetry.QueuedTransitionsThisFrame != 0)
        {
            reason =
                $"imports={telemetry.ActiveImportScopes} pending={telemetry.PendingTransitionCount} " +
                $"decodes={telemetry.ActiveDecodeCount}/{telemetry.QueuedDecodeCount} " +
                $"gpuUploads={telemetry.ActiveGpuUploadCount} queued={telemetry.QueuedTransitionsThisFrame}";
            return false;
        }

        for (int i = 0; i < textures.Count; i++)
        {
            ImportedTextureStreamingTextureTelemetry texture = textures[i];
            if (!texture.IsVisible)
                continue;

            if (!texture.PreviewReady ||
                texture.HasPendingTransition ||
                texture.ResidentMaxDimension < texture.DesiredResidentMaxDimension ||
                texture.CurrentPageCoverage + 0.0001f < texture.DesiredPageCoverage ||
                texture.PublishedGeneration < texture.ResidentGeneration ||
                texture.HasValidationFailure)
            {
                reason =
                    $"visible texture '{texture.TextureName ?? "<unnamed>"}' is not stable " +
                    $"(preview={texture.PreviewReady} pending={texture.HasPendingTransition} " +
                    $"resident={texture.ResidentMaxDimension}/{texture.DesiredResidentMaxDimension} " +
                    $"page={texture.CurrentPageCoverage:F3}/{texture.DesiredPageCoverage:F3} " +
                    $"generation={texture.PublishedGeneration}/{texture.ResidentGeneration} " +
                    $"validationFailure={texture.HasValidationFailure})";
                return false;
            }
        }

        reason = "streaming idle and every visible texture generation is published";
        return true;
    }
}
