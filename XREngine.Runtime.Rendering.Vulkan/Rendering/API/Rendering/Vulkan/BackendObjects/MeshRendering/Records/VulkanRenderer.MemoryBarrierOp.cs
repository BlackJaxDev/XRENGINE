namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record MemoryBarrierOp(
        int PassIndex,
        EMemoryBarrierMask Mask,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}