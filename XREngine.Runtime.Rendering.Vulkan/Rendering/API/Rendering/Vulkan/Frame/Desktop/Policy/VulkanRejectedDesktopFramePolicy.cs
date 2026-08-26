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
        => Resolve(acquireAvailable, deviceLost, imageWasEverPresented,
            imageHasValidCompletedContent, allowStaleReuse: true);

    internal static RejectedDesktopFramePolicyDecision Resolve(
        bool acquireAvailable,
        bool deviceLost,
        bool imageWasEverPresented,
        bool imageHasValidCompletedContent,
        bool allowStaleReuse)
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

        // A present-now transaction is never allowed to turn a rejected fresh
        // frame into an implicit old-content presentation.  Preserve the
        // normal WSI/device recovery decisions above, but make the foreground
        // contract explicit once the acquired image is otherwise usable.
        if (!allowStaleReuse)
            return new RejectedDesktopFramePolicyDecision(
                ERejectedDesktopFrameDisposition.FailPresentNow,
                ERejectedDesktopFramePolicyReason.PresentNowFreshOutputRequired);

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
