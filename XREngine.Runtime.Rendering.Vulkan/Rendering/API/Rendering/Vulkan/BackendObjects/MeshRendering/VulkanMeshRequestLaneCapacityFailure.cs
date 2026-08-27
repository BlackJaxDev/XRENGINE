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

    internal EVulkanAcceptedFrameLane AcceptedFrameLane => Lane switch
    {
        EVulkanMeshRequestLane.TerminalComposition =>
            EVulkanAcceptedFrameLane.Terminal,
        EVulkanMeshRequestLane.Ui => EVulkanAcceptedFrameLane.Ui,
        EVulkanMeshRequestLane.MainScene =>
            EVulkanAcceptedFrameLane.MainScene,
        EVulkanMeshRequestLane.Shadow => EVulkanAcceptedFrameLane.Shadow,
        _ => throw new ArgumentOutOfRangeException(nameof(Lane)),
    };

    internal string FormatDiagnostic(int totalRejectedCount)
        => $"FramePlanCapacityExceeded lane={AcceptedFrameLane} " +
           $"meshLane={Lane} actual={RequiredCapacity} " +
           $"configured={ConfiguredCapacity} " +
           $"accepted={ActualOccupancy} rejected={totalRejectedCount}.";
}
