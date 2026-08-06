using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free aggregate for one stable lifecycle stage.</summary>
public struct VulkanFrameStageTiming
{
    public TimeSpan Elapsed;
    public int IntervalCount;
    public EVulkanFrameIntervalClass IntervalClass;
    public EVulkanFrameOutcome Outcome;
    public EVulkanFrameWaitReason WaitReason;

    public void Add(
        TimeSpan elapsed,
        EVulkanFrameIntervalClass intervalClass,
        EVulkanFrameOutcome outcome,
        EVulkanFrameWaitReason waitReason)
    {
        Elapsed += elapsed;
        IntervalCount++;
        IntervalClass = intervalClass;
        Outcome = outcome;
        WaitReason = waitReason;
    }
}
