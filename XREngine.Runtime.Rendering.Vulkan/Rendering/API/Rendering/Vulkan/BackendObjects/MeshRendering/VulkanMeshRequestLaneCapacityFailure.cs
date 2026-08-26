namespace XREngine.Rendering.Vulkan;

/// <summary>Explicit frame-manifest admission failure for one request lane.</summary>
internal readonly record struct VulkanMeshRequestLaneCapacityFailure(
    EVulkanMeshRequestLane Lane,
    int ConfiguredCapacity,
    int ActualOccupancy,
    int RequiredCapacity,
    int OverflowCount)
{
    public bool HasFailure => OverflowCount > 0;
}
