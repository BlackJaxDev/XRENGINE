namespace XREngine.Rendering.Vulkan;

internal sealed record MeshTaskDispatchIndirectCountOp(
    int PassIndex,
    VkDataBuffer IndirectBuffer,
    VkDataBuffer CountBuffer,
    uint MaxDrawCount,
    uint Stride,
    nuint ByteOffset,
    nuint CountByteOffset,
    VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount;

    internal override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        System.Diagnostics.Debug.Assert(
            recordingInfo.BeginsRendering,
            "Mesh-task primary-plan nodes must own render-scope entry.");
        if (recordingInfo.BeginsRendering &&
            !recordingState.RenderScope.MatchesTarget(null))
        {
            renderer.EndActiveRenderPass(ref recordingState);
            renderer.BeginRenderPassForTarget(
                ref recordingState,
                null,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        renderer.CmdBeginLabel(
            recordingState.CommandBuffer,
            "MeshTaskDispatchIndirectCount");
        renderer.RecordMeshTaskDispatchIndirectCountOp(
            recordingState.CommandBuffer,
            this);
        renderer.CmdEndLabel(recordingState.CommandBuffer);
        recordingState.ActualSwapchainWriteCount++;
        return recordingInfo.OperationIndex;
    }
}
