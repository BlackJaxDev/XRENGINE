namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable primary-plan metadata for one frame operation.
/// </summary>
internal readonly record struct VulkanPrimaryOperationRecordingInfo(
    EVulkanPrimaryPlanAction Actions,
    int OperationIndex,
    int PassIndex)
{
    public bool BeginsRendering
        => HasAction(EVulkanPrimaryPlanAction.BeginRendering);

    public bool ExecutesSecondaryRange
        => HasAction(EVulkanPrimaryPlanAction.ExecuteSecondaryRange);

    public bool EndsRendering
        => HasAction(EVulkanPrimaryPlanAction.EndRendering);

    public bool HasAction(EVulkanPrimaryPlanAction action)
        => (Actions & action) != 0;
}
