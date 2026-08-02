namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One ordered, typed primary-command plan node. The operation reference is
/// retained on operation nodes during migration as the full semantic payload
/// and validation authority. Terminal orchestration nodes have no operation.
/// </summary>
internal readonly record struct VulkanPrimaryPlanNode(
    EVulkanPrimaryPlanNodeKind Kind,
    FrameOp? Operation,
    int SourceIndex,
    EVulkanPrimaryPlanAction Actions,
    bool IsDrawLike);
