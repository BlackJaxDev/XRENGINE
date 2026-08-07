namespace XREngine.Rendering.Vulkan;

internal static class VulkanRejectedDesktopFramePolicy
{
    internal const string InjectedRejectionStage =
        "InjectedPhase524bDesktopRejection";

    internal static RejectedDesktopFramePolicyDecision Resolve(
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
}