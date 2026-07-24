namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context);
}