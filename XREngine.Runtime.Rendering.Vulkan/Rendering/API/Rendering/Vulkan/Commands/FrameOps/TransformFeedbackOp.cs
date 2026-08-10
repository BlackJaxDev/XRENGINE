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
        VulkanCommandRuntime commandRuntime,
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
            commandRuntime.EndActiveRenderPass(ref recordingState);
            commandRuntime.BeginRenderPassForTarget(
                ref recordingState,
                Target,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        bool labelActive = false;
        if (commandRuntime.CanRecordCommandBufferDebugLabels)
        {
            labelActive = commandRuntime.CmdBeginLabel(
                recordingState.CommandBuffer,
                $"TransformFeedback.{Operation}");
        }
        commandRuntime.RecordTransformFeedbackOp(recordingState.CommandBuffer, this);
        if (labelActive)
            commandRuntime.CmdEndLabel(recordingState.CommandBuffer);

        return recordingInfo.OperationIndex;
    }
}
