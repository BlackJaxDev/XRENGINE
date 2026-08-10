namespace XREngine.Rendering.Vulkan;

internal sealed record SubmissionMarkerOp(
    int PassIndex,
    VulkanTimelineGpuFence Fence,
    string Label,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.SubmissionMarker;

    internal override int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        commandRuntime.RegisterSubmissionMarker(recordingState.CommandBuffer, Fence);
        return recordingInfo.OperationIndex;
    }
}
