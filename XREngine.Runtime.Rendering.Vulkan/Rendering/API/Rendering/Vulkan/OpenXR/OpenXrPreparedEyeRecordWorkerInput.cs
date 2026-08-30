namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen command-worker input for one prepared OpenXR eye. Output and planner
/// authorities are sampled before this value is published to a worker.
/// </summary>
internal readonly record struct OpenXrPreparedEyeRecordWorkerInput(
    VulkanPreparedPrimaryCommandInput CommandInput,
    ResourcePlannerRuntimeState PlannerState,
    VulkanOpenXrFrameContext FrameContext,
    uint OpenXrViewIndex,
    uint OpenXrImageIndex,
    uint FrameDataSlotIndex,
    ulong LogicalViewId,
    int RequiredOutputIndex,
    RenderOutputRequest OutputContract,
    ulong FrameOpsSignature,
    ulong PlannerRevision,
    ulong FrameOpContextId,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    int RenderLaneId,
    int RenderFrameSlot)
{
    internal bool IsValid =>
        CommandInput.PrimaryCommandBuffer.Handle != 0 &&
        CommandInput.RecordingTarget.IsValid &&
        CommandInput.FramePlan.IsSealed &&
        LogicalViewId != 0UL &&
        RequiredOutputIndex >= 0 &&
        OutputContract.IsDefined &&
        RenderLaneId >= 0 &&
        RenderFrameSlot >= 0 &&
        PlannerState.ResourceAllocator is not null &&
        PlannerState.RenderGraphPlan is not null;
}
