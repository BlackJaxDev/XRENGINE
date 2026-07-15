using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record DlssFrameGenerationOp(
        int PassIndex,
        NvidiaDlssManager.Native.NativeFrameGenerationSession Session,
        VulkanStreamlineImage Depth,
        VulkanStreamlineImage Motion,
        VulkanStreamlineImage HudlessColor,
        VulkanUpscaleBridgeDispatchParameters Parameters,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}