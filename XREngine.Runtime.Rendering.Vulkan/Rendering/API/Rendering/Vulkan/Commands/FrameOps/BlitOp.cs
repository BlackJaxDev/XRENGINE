using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record BlitOp(
    int PassIndex,
    XRFrameBuffer? InFbo,
    XRFrameBuffer? OutFbo,
    int InX,
    int InY,
    uint InW,
    uint InH,
    int OutX,
    int OutY,
    uint OutW,
    uint OutH,
    EReadBufferMode ReadBufferMode,
    bool ColorBit,
    bool DepthBit,
    bool StencilBit,
    bool LinearFilter,
    FrameOpContext Context) 
    : FrameOp(PassIndex, OutFbo, Context)
{
    private XRFrameBuffer? _inFbo = InFbo;
    private XRFrameBuffer? _outFbo = OutFbo;
    public XRFrameBuffer? InFbo
    {
        get => _inFbo;
        internal set
        {
            ThrowIfSealedForFramePlan();
            _inFbo = value;
        }
    }
    public XRFrameBuffer? OutFbo
    {
        get => _outFbo;
        internal set
        {
            ThrowIfSealedForFramePlan();
            _outFbo = value;
        }
    }
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.Blit;

    internal override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (ColorBit && (InFbo is null || OutFbo is null))
            renderer.EnsureSwapchainColorAttachmentLayoutForBlit(ref recordingState);

        renderer.CmdBeginLabel(recordingState.CommandBuffer, "Blit");
        bool recorded = renderer.RecordBlitOp(
            recordingState.CommandBuffer,
            recordingState.ImageIndex,
            this,
            in recordingState.SwapchainTarget);
        renderer.CmdEndLabel(recordingState.CommandBuffer);

        if (OutFbo is not null ||
            (!ColorBit && !DepthBit && !StencilBit) ||
            !recorded)
            return recordingInfo.OperationIndex;

        recordingState.SwapchainWrittenOutsideRenderPass = true;
        if (ColorBit)
        {
            recordingState.SwapchainInColorAttachmentLayout = true;
            recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
        }
        recordingState.ActualSwapchainWriteCount++;
        return recordingInfo.OperationIndex;
    }
}
