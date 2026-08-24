namespace XREngine.Rendering.Vulkan;

internal readonly record struct RejectedDesktopFramePolicyDecision(
    ERejectedDesktopFrameDisposition Disposition,
    ERejectedDesktopFramePolicyReason Reason)
{
    public bool ShouldPresent
        => Disposition != ERejectedDesktopFrameDisposition.SkipPresent;

    public bool ShouldClearBeforePresent
        => Disposition == ERejectedDesktopFrameDisposition.PresentInitializationClear;
}
