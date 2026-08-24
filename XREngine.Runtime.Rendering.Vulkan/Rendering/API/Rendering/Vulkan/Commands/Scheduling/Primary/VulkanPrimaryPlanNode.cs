namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One ordered primary-command plan node. Payload ownership remains with the
/// sealed <see cref="FrameOperationStream"/>; this node deliberately retains
/// only its numeric stream location. Terminal orchestration nodes use -1.
/// </summary>
internal readonly record struct VulkanPrimaryPlanNode(
    EVulkanPrimaryPlanNodeKind Kind,
    int OperationIndex,
    int SourceIndex,
    EVulkanPrimaryPlanAction Actions,
    bool IsDrawLike);
