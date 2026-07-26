namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record SubmissionMarkerOp(
        int PassIndex,
        VulkanTimelineGpuFence Fence,
        string Label,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}
