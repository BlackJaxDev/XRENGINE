using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record DlssUpscaleOp(
        int PassIndex,
        NvidiaDlssManager.Native.NativeVulkanSession Session,
        VulkanStreamlineImage SourceColor,
        VulkanStreamlineImage Depth,
        VulkanStreamlineImage Motion,
        VulkanStreamlineImage OutputColor,
        VulkanStreamlineImage? Exposure,
        VulkanUpscaleBridgeDispatchParameters Parameters,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}