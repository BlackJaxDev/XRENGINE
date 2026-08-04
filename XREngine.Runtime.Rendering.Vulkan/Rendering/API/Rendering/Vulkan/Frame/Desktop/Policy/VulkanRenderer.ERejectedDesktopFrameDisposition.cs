namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal enum ERejectedDesktopFrameDisposition
    {
        SkipPresent = 0,
        PresentLastCompletedContent = 1,
        PresentInitializationClear = 2,
    }
}
