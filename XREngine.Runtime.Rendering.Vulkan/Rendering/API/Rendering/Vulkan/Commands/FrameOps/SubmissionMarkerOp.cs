namespace XREngine.Rendering.Vulkan;

internal sealed record SubmissionMarkerOp(
    int PassIndex,
    VulkanTimelineGpuFence Fence,
    string Label,
    FrameOpContext Context,
    int RequiredOperationCount = 0)
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.SubmissionMarker;
}
