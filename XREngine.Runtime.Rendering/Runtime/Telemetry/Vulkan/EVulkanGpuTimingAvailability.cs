namespace XREngine.Rendering.Vulkan;

/// <summary>Nonblocking state of the coarse Vulkan command-buffer timestamp query.</summary>
public enum EVulkanGpuTimingAvailability
{
    Disabled,
    Pending,
    Unavailable,
    Completed,
}
