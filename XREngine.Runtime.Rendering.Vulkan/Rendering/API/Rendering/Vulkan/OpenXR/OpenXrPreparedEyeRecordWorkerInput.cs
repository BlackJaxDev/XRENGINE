namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen command-worker input for one prepared OpenXR eye. Output and planner
/// authorities are sampled before this value is published to a worker.
/// </summary>
internal readonly record struct OpenXrPreparedEyeRecordWorkerInput(
    VulkanPreparedPrimaryCommandInput CommandInput,
    VulkanOpenXrFrameContext FrameContext,
    uint OpenXrViewIndex,
    uint OpenXrImageIndex,
    uint FrameDataSlotIndex,
    ulong FrameOpsSignature,
    ulong PlannerRevision,
    ulong FrameOpContextId,
    ulong ResourceGeneration,
    ulong DescriptorGeneration)
{
    internal bool IsValid =>
        CommandInput.PrimaryCommandBuffer.Handle != 0 &&
        CommandInput.RecordingTarget.IsValid &&
        CommandInput.FramePlan.IsSealed;
}
