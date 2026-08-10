namespace XREngine.Rendering.Vulkan;

internal sealed record TransformFeedbackOp(
    int PassIndex,
    XRFrameBuffer? Target,
    VkTransformFeedback TransformFeedback,
    EXRTransformFeedbackOperation Operation,
    XRDataBuffer? CounterBuffer,
    ulong FeedbackBufferOffset,
    ulong? FeedbackBufferSize,
    ulong CounterBufferOffset,
    uint CounterOffset,
    uint VertexStride,
    uint InstanceCount,
    uint FirstInstance,
    FrameOpContext Context) 
    : FrameOp(PassIndex, Target, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.TransformFeedback;

    internal override int RecordPrimary(
        VulkanCommandRuntime renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        System.Diagnostics.Debug.Assert(
            recordingInfo.BeginsRendering,
            "Transform-feedback primary-plan nodes must own render-scope entry.");
        if (recordingInfo.BeginsRendering &&
            (!recordingState.RenderScope.IsActive ||
             recordingState.RenderScope.Target != Target))
        {
            renderer.EndActiveRenderPass(ref recordingState);
            renderer.BeginRenderPassForTarget(
                ref recordingState,
                Target,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        bool labelActive = false;
        if (renderer.CanRecordCommandBufferDebugLabels)
        {
            labelActive = renderer.CmdBeginLabel(
                recordingState.CommandBuffer,
                $"TransformFeedback.{Operation}");
        }
        renderer.RecordTransformFeedbackOp(recordingState.CommandBuffer, this);
        if (labelActive)
            renderer.CmdEndLabel(recordingState.CommandBuffer);

        return recordingInfo.OperationIndex;
    }
}
