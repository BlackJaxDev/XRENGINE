namespace XREngine;

/// <summary>
/// Resource barrier backends used by the engine.
/// </summary>
public enum ERvcResourceBarrierBackend
{
    /// <summary>
    /// OpenGL memory barrier backend.
    /// </summary>
    OpenGlMemoryBarrier,
    /// <summary>
    /// Vulkan synchronization2 backend.
    /// </summary>
    VulkanSynchronization2,
    /// <summary>
    /// Vulkan dynamic rendering attachment backend.
    /// </summary>
    VulkanDynamicRenderingAttachment,
    /// <summary>
    /// Vulkan timeline semaphore handoff backend.
    /// </summary>
    VulkanTimelineSemaphoreHandoff,
}
