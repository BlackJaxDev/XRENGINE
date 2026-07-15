namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record PublishFramebufferForSamplingOp(
        int PassIndex,
        XRFrameBuffer FrameBuffer,
        FrameOpContext Context) : FrameOp(PassIndex, FrameBuffer, Context);
}