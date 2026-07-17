using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal enum EPhase524bDesktopRejectionAction
{
    Wait = 0,
    Armed = 1,
    Reject = 2,
}

internal readonly record struct Phase524bDesktopRejectionDecision(
    EPhase524bDesktopRejectionAction Action,
    double Exposure,
    double ExposureHistory,
    string Diagnostic);

/// <summary>
/// One-shot state machine used only by the explicit 5.2.4b validation launch.
/// It samples one accepted frame as history, then rejects the next eligible
/// frame so both numeric samples come from completed desktop-owned GPU state.
/// </summary>
internal sealed class Phase524bDesktopRejectionInjection
{
    private bool _armed;
    private bool _completed;
    private double _history;

    internal Phase524bDesktopRejectionDecision Observe(
        bool enabled,
        bool eligible,
        bool sampleSucceeded,
        double exposure,
        string diagnostic)
    {
        if (!enabled || _completed || !eligible)
            return new(EPhase524bDesktopRejectionAction.Wait, 0.0, _history, diagnostic);

        // The HDR validation scene requires positive exposure. Startup clears
        // are valid readbacks but are not completed exposure history and must
        // not arm the rejection sample.
        if (!sampleSucceeded || !double.IsFinite(exposure) || exposure <= double.Epsilon)
            return new(EPhase524bDesktopRejectionAction.Wait, 0.0, _history, diagnostic);

        if (!_armed)
        {
            _history = exposure;
            _armed = true;
            return new(EPhase524bDesktopRejectionAction.Armed, exposure, exposure, diagnostic);
        }

        _completed = true;
        return new(EPhase524bDesktopRejectionAction.Reject, exposure, _history, diagnostic);
    }
}

public unsafe partial class VulkanRenderer
{
    internal const string Phase524bInjectedDesktopRejectionStage = "InjectedPhase524bDesktopRejection";

    private readonly Phase524bDesktopRejectionInjection _phase524bDesktopRejectionInjection = new();
    private Phase524bDesktopRejectionDecision _phase524bPendingDesktopRejection;
    private static readonly object s_phase524bDesktopRejectionEvidenceLock = new();
    private static OpenXrSmokeDesktopRejectionEvidence s_phase524bDesktopRejectionEvidence = new();

    internal enum ERejectedDesktopFrameDisposition
    {
        SkipPresent = 0,
        PresentLastCompletedContent = 1,
        PresentInitializationClear = 2,
    }

    internal enum ERejectedDesktopFramePolicyReason
    {
        AcquireUnavailable = 0,
        DeviceLost = 1,
        ImageNeverPresented = 2,
        NoCompletedFinalWrite = 3,
        ReuseCompletedContent = 4,
        DeferredInitializationClear = 5,
    }

    internal readonly record struct RejectedDesktopFramePolicyDecision(
        ERejectedDesktopFrameDisposition Disposition,
        ERejectedDesktopFramePolicyReason Reason)
    {
        public bool ShouldPresent
            => Disposition != ERejectedDesktopFrameDisposition.SkipPresent;

        public bool ShouldClearBeforePresent
            => Disposition == ERejectedDesktopFrameDisposition.PresentInitializationClear;
    }

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
        lock (s_phase524bDesktopRejectionEvidenceLock)
        {
            s_phase524bDesktopRejectionEvidence = new OpenXrSmokeDesktopRejectionEvidence
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
        lock (s_phase524bDesktopRejectionEvidenceLock)
            return ClonePhase524bDesktopRejectionEvidence(s_phase524bDesktopRejectionEvidence);
    }

    private bool TryPreparePhase524bInjectedDesktopRejection(
        in FrameOpContext context,
        uint imageIndex)
    {
        bool enabled = IsTrueEnvironmentValue(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPhase524bInjectDesktopRejection));
        bool imageHasCompletedContent =
            _swapchainImageHasValidPresentedContent is not null &&
            imageIndex < _swapchainImageHasValidPresentedContent.Length &&
            _swapchainImageHasValidPresentedContent[imageIndex] &&
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

        Phase524bDesktopRejectionDecision decision = _phase524bDesktopRejectionInjection.Observe(
            enabled,
            imageHasCompletedContent && desktopOwned,
            sampleSucceeded,
            exposure,
            diagnostic);

        if (enabled && decision.Action == EPhase524bDesktopRejectionAction.Wait && !string.IsNullOrWhiteSpace(diagnostic))
        {
            lock (s_phase524bDesktopRejectionEvidenceLock)
                s_phase524bDesktopRejectionEvidence.Diagnostic = diagnostic;
        }

        if (decision.Action != EPhase524bDesktopRejectionAction.Reject)
            return false;

        _phase524bPendingDesktopRejection = decision;
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
        Phase524bDesktopRejectionDecision sample = _phase524bPendingDesktopRejection;
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

        lock (s_phase524bDesktopRejectionEvidenceLock)
            s_phase524bDesktopRejectionEvidence = evidence;
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
