namespace XREngine.Rendering.Vulkan;

/// <summary>Coherent current and last-completed coarse Vulkan timing observations.</summary>
public readonly record struct VulkanGpuCommandBufferTimingSnapshot(
    VulkanGpuCommandBufferTimingSample Current,
    VulkanGpuCommandBufferTimingSample LastCompleted);
