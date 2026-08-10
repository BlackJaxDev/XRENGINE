namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a frame operation that publishes a framebuffer for sampling in the Vulkan rendering pipeline.
/// </summary>
/// <param name="PassIndex">The index of the rendering pass.</param>
/// <param name="FrameBuffer">The framebuffer to be published for sampling.</param>
/// <param name="Context">The context of the frame operation.</param>
internal sealed record PublishFramebufferForSamplingOp(
    int PassIndex,
    XRFrameBuffer FrameBuffer,
    FrameOpContext Context) 
    : FrameOp(PassIndex, FrameBuffer, Context)
{
    /// <summary>
    /// Gets the framebuffer that is being published for sampling.
    /// </summary>
    private XRFrameBuffer _frameBuffer = FrameBuffer;
    public XRFrameBuffer FrameBuffer
    {
        get => _frameBuffer;
        internal set
        {
            ThrowIfSealedForFramePlan();
            _frameBuffer = value;
        }
    }
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling;

    internal override int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        commandRuntime.CmdBeginLabel(
            recordingState.CommandBuffer,
            "PublishFramebufferForSampling");
        commandRuntime.RecordPublishFramebufferForSamplingOp(
            recordingState.CommandBuffer,
            this);
        commandRuntime.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
