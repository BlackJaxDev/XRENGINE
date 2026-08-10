using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a DLSS frame generation operation in the Vulkan rendering pipeline.
/// </summary>
/// <param name="PassIndex">The index of the rendering pass.</param>
/// <param name="Session">The native Vulkan DLSS frame generation session.</param>
/// <param name="Depth">The depth image.</param>
/// <param name="Motion">The motion image.</param>
/// <param name="HudlessColor">The HUD-less color image.</param>
/// <param name="Parameters">The DLSS upscale parameters.</param>
/// <param name="Context">The context of the frame operation.</param>
/// <param name="UiColorAndAlpha">The producer-frozen UI image for the acquired output image.</param>
internal sealed record DlssFrameGenerationOp(
    int PassIndex,
    NvidiaDlssManager.Native.NativeFrameGenerationSession Session,
    VulkanStreamlineImage Depth,
    VulkanStreamlineImage Motion,
    VulkanStreamlineImage HudlessColor,
    VulkanUpscaleBridgeDispatchParameters Parameters,
    FrameOpContext Context,
    VulkanStreamlineImage UiColorAndAlpha = default)
    : DlssFrameOp(PassIndex, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.DlssFrameGeneration;
    protected override string CommandLabel => "DLSS.FrameGenerationInputs";

    protected override void RecordStreamlineCommand(
        VulkanCommandRuntime renderer,
        CommandBuffer commandBuffer,
        uint imageIndex)
    {
        VulkanStreamlineImage depth =
            TransitionImageToGeneral(renderer, commandBuffer, Depth);
        VulkanStreamlineImage motion =
            TransitionImageToGeneral(renderer, commandBuffer, Motion);
        VulkanStreamlineImage hudlessColor =
            TransitionImageToGeneral(renderer, commandBuffer, HudlessColor);
        if (!renderer.TryPrepareStreamlineUiImage(
                commandBuffer,
                UiColorAndAlpha,
                out VulkanStreamlineImage uiColorAndAlpha))
        {
            throw new InvalidOperationException(
                $"DLSS frame generation requires a frozen UI color/alpha image for swapchain image {imageIndex}.");
        }

        VulkanUpscaleBridgeDispatchParameters parameters = Parameters;
        if (!NvidiaDlssManager.Native.TryRecordNativeVulkanFrameGeneration(
                Session,
                commandBuffer,
                depth,
                motion,
                hudlessColor,
                uiColorAndAlpha,
                in parameters,
                out string failureReason))
        {
            ThrowRecordingFailure("frame generation", failureReason);
        }
    }
}
