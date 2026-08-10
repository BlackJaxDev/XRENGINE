namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    /// <summary>
    /// Arms and injects the controlled 5.2.4b desktop rejection only after two
    /// completed samples from the desktop-owned exposure resource are observed.
    /// </summary>
    private bool TryPreparePhase524bInjectedDesktopRejection(
        in FrameOpContext context,
        uint imageIndex)
    {
        bool enabled = IsTrueEnvironmentValue(
            Environment.GetEnvironmentVariable(
                XREngineEnvironmentVariables.VulkanPhase524bInjectDesktopRejection));
        bool imageHasCompletedContent =
            OutputRuntime.Desktop.ImageHasValidPresentedContent is not null &&
            imageIndex < OutputRuntime.Desktop.ImageHasValidPresentedContent.Length &&
            OutputRuntime.Desktop.ImageHasValidPresentedContent[imageIndex] &&
            OutputRuntime.Desktop.ImageEverPresented is not null &&
            imageIndex < OutputRuntime.Desktop.ImageEverPresented.Length &&
            OutputRuntime.Desktop.ImageEverPresented[imageIndex];
        bool desktopOwned =
            context.ContextKind == EVulkanFrameOpContextKind.MainViewport &&
            context.PipelineIdentity != 0 &&
            context.ResourceRegistry is not null;
        bool sampleSucceeded = false;
        double exposure = 0.0;
        string diagnostic = string.Empty;

        if (enabled && imageHasCompletedContent && desktopOwned)
        {
            sampleSucceeded = TryReadDesktopAutoExposure(
                in context,
                out exposure,
                out diagnostic);
        }

        Phase524bDesktopRejectionDecision decision =
            OutputRuntime._phase524bDesktopRejectionInjection.Observe(
                enabled,
                imageHasCompletedContent && desktopOwned,
                sampleSucceeded,
                exposure,
                diagnostic);

        if (enabled && decision.Action == EPhase524bDesktopRejectionAction.Wait &&
            !string.IsNullOrWhiteSpace(diagnostic))
        {
            lock (VulkanOutputRuntime.Phase524bDesktopRejectionEvidenceLock)
                VulkanOutputRuntime.Phase524bDesktopRejectionEvidence.Diagnostic = diagnostic;
        }

        if (decision.Action != EPhase524bDesktopRejectionAction.Reject)
            return false;

        OutputRuntime._phase524bPendingDesktopRejection = decision;
        return true;
    }

    private static bool IsTrueEnvironmentValue(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
}
