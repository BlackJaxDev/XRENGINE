namespace XREngine.Rendering.Vulkan;

internal enum ERejectedDesktopFrameDisposition
{
    SkipPresent = 0,
    PresentLastCompletedContent = 1,
    PresentInitializationClear = 2,
}
