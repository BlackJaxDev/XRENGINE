namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free view of one production frame's retirement accounting. It is
/// valid until the next <c>BeginFrame</c> call on its owning runtime.
/// </summary>
internal readonly record struct VulkanRetirementMeterSnapshot(
    VulkanRetirementMeter Meter,
    long FrameSerial)
{
    internal int GetAdmitted(EVulkanRetirementWorkClass workClass) => Meter.GetAdmitted(workClass);
    internal int GetCompleted(EVulkanRetirementWorkClass workClass) => Meter.GetCompleted(workClass);
    internal int GetOrdinaryCap(EVulkanRetirementWorkClass workClass) => Meter.GetOrdinaryCap(workClass);
    internal int GetHighWaterMark(EVulkanRetirementWorkClass workClass) => Meter.GetHighWaterMark(workClass);
    internal int GetDeferred(EVulkanRetirementWorkClass workClass) => Meter.GetDeferred(workClass);
    internal int GetBacklog(EVulkanRetirementWorkClass workClass) => Meter.GetBacklog(workClass);
    internal bool IsUncapped(EVulkanRetirementWorkClass workClass) => Meter.IsUncapped(workClass);
    internal int GetUncappedActivationCount(EVulkanRetirementWorkClass workClass) => Meter.GetUncappedActivationCount(workClass);
    internal double GetOldestPendingAgeMilliseconds(EVulkanRetirementWorkClass workClass)
        => Meter.GetOldestPendingAgeMilliseconds(workClass);
    internal double GetElapsedMilliseconds() => Meter.GetElapsedMilliseconds();
    internal long GetDrainDurationSampleCount() => Meter.GetDrainDurationSampleCount();
    internal long GetDrainDurationOverflowCount() => Meter.GetDrainDurationOverflowCount();
    internal double GetMaximumPublishedDrainDurationMilliseconds() => Meter.GetMaximumPublishedDrainDurationMilliseconds();
    internal double GetDrainDurationP50Milliseconds() => Meter.GetDrainDurationPercentileMilliseconds(0.50);
    internal double GetDrainDurationP95Milliseconds() => Meter.GetDrainDurationPercentileMilliseconds(0.95);
    internal double GetDrainDurationP99Milliseconds() => Meter.GetDrainDurationPercentileMilliseconds(0.99);
}
