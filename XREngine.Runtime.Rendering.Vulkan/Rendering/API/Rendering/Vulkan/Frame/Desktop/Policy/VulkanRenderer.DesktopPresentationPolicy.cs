using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal const string Phase524bInjectedDesktopRejectionStage = "InjectedPhase524bDesktopRejection";

    /// <summary>
    /// Selects the only legal presentation behavior after desktop rendering was rejected.
    /// An acquired image may be re-presented only when it contains a completed final write from
    /// an earlier accepted frame; otherwise the compositor keeps its last completed image.
    /// </summary>
    internal static RejectedDesktopFramePolicyDecision ResolveRejectedDesktopFramePolicy(
        bool acquireAvailable,
        bool deviceLost,
        bool imageWasEverPresented,
        bool imageHasValidCompletedContent)
    {
        if (!acquireAvailable)
        {
            return new RejectedDesktopFramePolicyDecision(
                ERejectedDesktopFrameDisposition.SkipPresent,
                ERejectedDesktopFramePolicyReason.AcquireUnavailable);
        }

        if (deviceLost)
        {
            return new RejectedDesktopFramePolicyDecision(
                ERejectedDesktopFrameDisposition.SkipPresent,
                ERejectedDesktopFramePolicyReason.DeviceLost);
        }

        if (!imageWasEverPresented)
        {
            return new RejectedDesktopFramePolicyDecision(
                ERejectedDesktopFrameDisposition.SkipPresent,
                ERejectedDesktopFramePolicyReason.ImageNeverPresented);
        }

        if (!imageHasValidCompletedContent)
        {
            return new RejectedDesktopFramePolicyDecision(
                ERejectedDesktopFrameDisposition.SkipPresent,
                ERejectedDesktopFramePolicyReason.NoCompletedFinalWrite);
        }

        return new RejectedDesktopFramePolicyDecision(
            ERejectedDesktopFrameDisposition.PresentLastCompletedContent,
            ERejectedDesktopFramePolicyReason.ReuseCompletedContent);
    }

    public static void ResetPhase524bDesktopRejectionEvidence(bool injectionRequested)
    {
        lock (VulkanOutputRuntime.Phase524bDesktopRejectionEvidenceLock)
        {
            VulkanOutputRuntime.Phase524bDesktopRejectionEvidence = new OpenXrSmokeDesktopRejectionEvidence
            {
                Injected = injectionRequested,
                Diagnostic = injectionRequested
                    ? "Waiting for two completed desktop-owned exposure samples."
                    : "Controlled desktop rejection was not requested.",
            };
        }
    }

    public static OpenXrSmokeDesktopRejectionEvidence CapturePhase524bDesktopRejectionEvidence()
    {
        lock (VulkanOutputRuntime.Phase524bDesktopRejectionEvidenceLock)
            return ClonePhase524bDesktopRejectionEvidence(VulkanOutputRuntime.Phase524bDesktopRejectionEvidence);
    }

    private static OpenXrSmokeDesktopRejectionEvidence ClonePhase524bDesktopRejectionEvidence(
        OpenXrSmokeDesktopRejectionEvidence source)
        => new()
        {
            Injected = source.Injected,
            Observed = source.Observed,
            Policy = source.Policy,
            SkippedPresent = source.SkippedPresent,
            PresentedLastCompletedImage = source.PresentedLastCompletedImage,
            PresentAccepted = source.PresentAccepted,
            ClearedTargetPublished = source.ClearedTargetPublished,
            PipelineName = source.PipelineName,
            PipelineInstanceId = source.PipelineInstanceId,
            OutputId = source.OutputId,
            RenderFrameId = source.RenderFrameId,
            ManifestFrameId = source.ManifestFrameId,
            Exposure = source.Exposure,
            ExposureHistory = source.ExposureHistory,
            ExposureFinite = source.ExposureFinite,
            ExposureHistoryFinite = source.ExposureHistoryFinite,
            ExposureNonZeroRequired = source.ExposureNonZeroRequired,
            ExposureHistoryNonZeroRequired = source.ExposureHistoryNonZeroRequired,
            ExposureOwnerMatchesDesktop = source.ExposureOwnerMatchesDesktop,
            Diagnostic = source.Diagnostic,
        };
}
