namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record QueryOp(
        int PassIndex,
        XRFrameBuffer? Target,
        VkRenderQuery Query,
        EQueryTarget QueryTarget,
        EVulkanQueryFrameOpKind Operation,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);
}