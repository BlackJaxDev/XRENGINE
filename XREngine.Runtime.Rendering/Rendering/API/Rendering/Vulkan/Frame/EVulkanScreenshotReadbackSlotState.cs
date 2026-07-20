namespace XREngine.Rendering.Vulkan;

internal enum EVulkanScreenshotReadbackSlotState
{
    Idle,
    Preparing,
    Submitted,
    CpuProcessing,
    Abandoned,
    Disposed,
}
