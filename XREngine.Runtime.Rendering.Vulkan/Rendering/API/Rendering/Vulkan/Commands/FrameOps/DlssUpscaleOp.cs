using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a DLSS upscale operation in the Vulkan rendering pipeline.
/// </summary>
/// <param name="PassIndex">The index of the rendering pass.</param>
/// <param name="Session">The native Vulkan DLSS session.</param>
/// <param name="SourceColor">The source color image.</param>
/// <param name="Depth">The depth image.</param>
/// <param name="Motion">The motion image.</param>
/// <param name="OutputColor">The output color image.</param>
/// <param name="Exposure">The optional exposure image.</param>
/// <param name="Parameters">The DLSS upscale parameters.</param>
/// <param name="Context">The context of the frame operation.</param>
internal sealed record DlssUpscaleOp(
    int PassIndex,
    NvidiaDlssManager.Native.NativeVulkanSession Session,
    VulkanStreamlineImage SourceColor,
    VulkanStreamlineImage Depth,
    VulkanStreamlineImage Motion,
    VulkanStreamlineImage OutputColor,
    VulkanStreamlineImage? Exposure,
    VulkanUpscaleBridgeDispatchParameters Parameters,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.DlssUpscale;
}
