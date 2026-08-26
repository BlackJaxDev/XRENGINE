namespace XREngine.Rendering.Vulkan;

internal readonly record struct RejectedDesktopFramePolicyDecision(
    ERejectedDesktopFrameDisposition Disposition,
    ERejectedDesktopFramePolicyReason Reason)
{
    public bool ShouldPresent
        => Disposition is
            ERejectedDesktopFrameDisposition.PresentLastCompletedContent or
            ERejectedDesktopFrameDisposition.PresentInitializationClear;

    public bool ShouldClearBeforePresent
        => Disposition == ERejectedDesktopFrameDisposition.PresentInitializationClear;

    public bool IsExplicitFailure
        => Disposition == ERejectedDesktopFrameDisposition.FailPresentNow;
}
