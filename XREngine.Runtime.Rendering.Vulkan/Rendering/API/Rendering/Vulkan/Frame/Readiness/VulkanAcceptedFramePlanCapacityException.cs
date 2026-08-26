namespace XREngine.Rendering.Vulkan;

/// <summary>Actionable bounded-arena overflow for one accepted-frame lane.</summary>
internal sealed class VulkanAcceptedFramePlanCapacityException : InvalidOperationException
{
    internal VulkanAcceptedFramePlanCapacityException(
        EVulkanAcceptedFrameLane lane,
        int configuredCapacity,
        int actualCount)
        : base(
            $"FramePlanCapacityExceeded lane={lane} actual={actualCount} " +
            $"configured={configuredCapacity} required={actualCount}.")
    {
        Lane = lane;
        ConfiguredCapacity = configuredCapacity;
        ActualCount = actualCount;
    }

    internal EVulkanAcceptedFrameLane Lane { get; }
    internal int ConfiguredCapacity { get; }
    internal int ActualCount { get; }
}
