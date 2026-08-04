using Silk.NET.Vulkan;
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
    : DlssFrameOp(PassIndex, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.DlssUpscale;
    protected override string CommandLabel => "DLSS.SuperResolution";

    protected override void RecordStreamlineCommand(
        VulkanRenderer renderer,
        CommandBuffer commandBuffer,
        uint imageIndex)
    {
        VulkanStreamlineImage sourceColor =
            TransitionImageToGeneral(renderer, commandBuffer, SourceColor);
        VulkanStreamlineImage depth =
            TransitionImageToGeneral(renderer, commandBuffer, Depth);
        VulkanStreamlineImage motion =
            TransitionImageToGeneral(renderer, commandBuffer, Motion);
        VulkanStreamlineImage outputColor =
            TransitionImageToGeneral(renderer, commandBuffer, OutputColor);
        VulkanStreamlineImage? exposure = Exposure.HasValue
            ? TransitionImageToGeneral(
                renderer,
                commandBuffer,
                Exposure.Value)
            : null;

        VulkanUpscaleBridgeDispatchParameters parameters = Parameters;
        if (!NvidiaDlssManager.Native.TryRecordNativeVulkanUpscale(
                Session,
                commandBuffer,
                sourceColor,
                depth,
                motion,
                outputColor,
                exposure,
                in parameters,
                out string failureReason))
            ThrowRecordingFailure("upscale", failureReason);

        MakeOutputVisibleForSampling(
            renderer,
            commandBuffer,
            outputColor);
    }
}
