namespace XREngine.Rendering;

/// <summary>
/// Completion primitive used before a frame slot can be written again.
/// </summary>
public enum EAdvancedFrameSlotCompletionMode
{
    None = 0,
    OpenGlFence,
    VulkanFence,
    VulkanTimelineSemaphore,
}
