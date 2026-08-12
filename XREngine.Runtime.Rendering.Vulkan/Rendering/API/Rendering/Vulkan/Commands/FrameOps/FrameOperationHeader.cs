namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Numeric execution header for a lowered frame operation. Planning reads this
/// header without traversing the authoring operation object graph. Diagnostic
/// text deliberately lives outside the hot operation stream.
/// </summary>
internal readonly record struct FrameOperationHeader(
    EVulkanPrimaryPlanNodeKind OpCode,
    int PayloadIndex,
    int PassIndex,
    int TargetIdentity,
    int ContextIndex,
    int ResourceUseIndex,
    int OriginalIndex,
    bool RequiresPrimaryRecordingContext,
    bool PreserveSubmissionOrder);
