namespace XREngine.Rendering.Vulkan;

internal enum ERejectedDesktopFrameDisposition
{
    SkipPresent = 0,
    PresentLastCompletedContent = 1,
    PresentInitializationClear = 2,
    /// <summary>Do not present stale content; surface an explicit foreground failure.</summary>
    FailPresentNow = 3,
}
