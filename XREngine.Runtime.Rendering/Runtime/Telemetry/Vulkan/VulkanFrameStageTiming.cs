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
    public TimeSpan WorkElapsed;
    public TimeSpan WaitElapsed;
    public TimeSpan DriverElapsed;
    public TimeSpan ExternalElapsed;
    public TimeSpan DiagnosticElapsed;

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
        switch (intervalClass)
        {
            case EVulkanFrameIntervalClass.Work:
                WorkElapsed += elapsed;
                break;
            case EVulkanFrameIntervalClass.Wait:
                WaitElapsed += elapsed;
                break;
            case EVulkanFrameIntervalClass.Driver:
                DriverElapsed += elapsed;
                break;
            case EVulkanFrameIntervalClass.External:
                ExternalElapsed += elapsed;
                break;
            case EVulkanFrameIntervalClass.Diagnostic:
                DiagnosticElapsed += elapsed;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(intervalClass));
        }
    }

    /// <summary>
    /// Reclassifies a measured child interval that was initially included in a
    /// coarse work interval. The stage's inclusive elapsed time is unchanged.
    /// </summary>
    public void ReclassifyWork(
        TimeSpan elapsed,
        EVulkanFrameIntervalClass intervalClass,
        EVulkanFrameWaitReason waitReason)
    {
        if (elapsed <= TimeSpan.Zero || intervalClass == EVulkanFrameIntervalClass.Work)
            return;

        TimeSpan reclassified = elapsed <= WorkElapsed
            ? elapsed
            : WorkElapsed;
        if (reclassified <= TimeSpan.Zero)
            return;

        WorkElapsed -= reclassified;
        switch (intervalClass)
        {
            case EVulkanFrameIntervalClass.Wait:
                WaitElapsed += reclassified;
                break;
            case EVulkanFrameIntervalClass.Driver:
                DriverElapsed += reclassified;
                break;
            case EVulkanFrameIntervalClass.External:
                ExternalElapsed += reclassified;
                break;
            case EVulkanFrameIntervalClass.Diagnostic:
                DiagnosticElapsed += reclassified;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(intervalClass));
        }

        IntervalClass = intervalClass;
        WaitReason = waitReason;
    }
}
