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

    private bool TryPreparePhase524bInjectedDesktopRejection(
        in FrameOpContext context,
        uint imageIndex)
    {
        bool enabled = IsTrueEnvironmentValue(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPhase524bInjectDesktopRejection));
        bool imageHasCompletedContent =
            OutputRuntime.Desktop.ImageHasValidPresentedContent is not null &&
            imageIndex < OutputRuntime.Desktop.ImageHasValidPresentedContent.Length &&
            OutputRuntime.Desktop.ImageHasValidPresentedContent[imageIndex] &&
            IsSwapchainImageEverPresented(imageIndex);
        bool desktopOwned =
            context.ContextKind == EVulkanFrameOpContextKind.MainViewport &&
            context.PipelineIdentity != 0 &&
            context.ResourceRegistry is not null;
        bool sampleSucceeded = false;
        double exposure = 0.0;
        string diagnostic = string.Empty;

        if (enabled && imageHasCompletedContent && desktopOwned)
            sampleSucceeded = TryReadPhase524bDesktopExposure(in context, out exposure, out diagnostic);

        Phase524bDesktopRejectionDecision decision = _outputRuntime._phase524bDesktopRejectionInjection.Observe(
            enabled,
            imageHasCompletedContent && desktopOwned,
            sampleSucceeded,
            exposure,
            diagnostic);

        if (enabled && decision.Action == EPhase524bDesktopRejectionAction.Wait && !string.IsNullOrWhiteSpace(diagnostic))
        {
            lock (VulkanOutputRuntime.Phase524bDesktopRejectionEvidenceLock)
                VulkanOutputRuntime.Phase524bDesktopRejectionEvidence.Diagnostic = diagnostic;
        }

        if (decision.Action != EPhase524bDesktopRejectionAction.Reject)
            return false;

        _outputRuntime._phase524bPendingDesktopRejection = decision;
        return true;
    }

    private bool TryReadPhase524bDesktopExposure(
        in FrameOpContext context,
        out double exposure,
        out string diagnostic)
    {
        exposure = 0.0;
        diagnostic = string.Empty;
        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.TryGetValue(
                DefaultRenderPipeline.AutoExposureTextureName,
                out RenderTextureResource? record) ||
            record.Instance is null)
        {
            diagnostic = "Desktop AutoExposureTex is not registered with a live texture instance.";
            return false;
        }

        using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(in context);
        if (!TryReadTextureMipRgbaFloat(
                record.Instance,
                mipLevel: 0,
                layerIndex: 0,
                out float[]? rgba,
                out int width,
                out int height,
                out string failure) ||
            rgba is null || rgba.Length == 0 || width != 1 || height != 1)
        {
            diagnostic = string.IsNullOrWhiteSpace(failure)
                ? $"Desktop AutoExposureTex readback returned {width}x{height} with no sample."
                : failure;
            return false;
        }

        exposure = rgba[0];
        diagnostic = "Read 1x1 desktop AutoExposureTex from the owning pipeline.";
        return true;
    }

    private void RecordPhase524bInjectedDesktopRejection(
        in FrameOpContext context,
        in RejectedDesktopFramePolicyDecision policy,
        bool presentAccepted,
        ulong renderFrameId)
    {
        Phase524bDesktopRejectionDecision sample = _outputRuntime._phase524bPendingDesktopRejection;
        bool exposureFinite = double.IsFinite(sample.Exposure);
        bool historyFinite = double.IsFinite(sample.ExposureHistory);
        var evidence = new OpenXrSmokeDesktopRejectionEvidence
        {
            Injected = true,
            Observed = true,
            Policy = policy.Disposition.ToString(),
            SkippedPresent = !policy.ShouldPresent,
            PresentedLastCompletedImage = policy.ShouldPresent,
            PresentAccepted = presentAccepted,
            ClearedTargetPublished = false,
            PipelineName = context.PipelineInstance?.DebugName ?? "<unknown>",
            PipelineInstanceId = context.PipelineIdentity,
            OutputId = unchecked((ulong)(uint)context.ViewportIdentity),
            RenderFrameId = renderFrameId,
            ManifestFrameId = 0UL,
            Exposure = sample.Exposure,
            ExposureHistory = sample.ExposureHistory,
            ExposureFinite = exposureFinite,
            ExposureHistoryFinite = historyFinite,
            ExposureNonZeroRequired = true,
            ExposureHistoryNonZeroRequired = true,
            ExposureOwnerMatchesDesktop =
                context.ContextKind == EVulkanFrameOpContextKind.MainViewport &&
                context.PipelineIdentity != 0 &&
                context.ResourceRegistry?.TextureRecords.ContainsKey(DefaultRenderPipeline.AutoExposureTextureName) == true,
            Diagnostic = sample.Diagnostic,
        };

        lock (VulkanOutputRuntime.Phase524bDesktopRejectionEvidenceLock)
            VulkanOutputRuntime.Phase524bDesktopRejectionEvidence = evidence;
    }

    private static bool IsTrueEnvironmentValue(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

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
