namespace XREngine.Rendering.Vulkan;

internal sealed record SubmissionMarkerOp(
    int PassIndex,
    VulkanRenderer.VulkanTimelineGpuFence Fence,
    string Label,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.SubmissionMarker;

    internal override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        renderer.RegisterSubmissionMarker(recordingState.CommandBuffer, Fence);
        return recordingInfo.OperationIndex;
    }
}
