namespace XREngine.Rendering.Vulkan;

/// <summary>Observed worker time and overlap for a measured frame.</summary>
public readonly record struct VulkanCpuWorkerFrameSummary(
    long FrameId,
    int WorkerId,
    long WorkTicks,
    long WaitTicks,
    long OverlapTicks,
    long ScheduledWindowTicks);
