namespace XREngine.Rendering.Vulkan;

internal sealed record TextureUploadFrameOp(VulkanImportedTexturePendingUpload Upload, FrameOpContext Context) 
    : FrameOp(int.MinValue, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.TextureUpload;
    internal override bool RequiresPrimaryRecordingContext => false;

    internal override int RecordPrimary(
        VulkanCommandRuntime renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (recordingInfo.EndsRendering)
            renderer.EndActiveRenderPass(ref recordingState);
        if (recordingState.PassIndexLabelActive)
        {
            renderer.CmdEndLabel(recordingState.CommandBuffer);
            recordingState.PassIndexLabelActive = false;
        }

        renderer.CmdBeginLabel(recordingState.CommandBuffer, "TextureUpload");
        renderer.RecordVulkanCommandDiagnosticMarker(
            recordingState.CommandBuffer,
            this,
            recordingInfo.PassIndex,
            recordingInfo.OperationIndex);
        renderer.RecordTextureUploadOp(recordingState.CommandBuffer, Upload);
        renderer.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
