using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Creates frozen command operations for renderer API translation boundaries.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal static MemoryBarrierOp CreateMemoryBarrierOperation(
        int passIndex,
        EMemoryBarrierMask mask,
        in FrameOpContext context)
        => MemoryBarrierOp.Rent(passIndex, mask, context);

    internal static PublishFramebufferForSamplingOp CreatePublishFramebufferOperation(
        int passIndex,
        XRFrameBuffer frameBuffer,
        in FrameOpContext context)
        => new(passIndex, frameBuffer, context);
}
