namespace XREngine.Rendering.Vulkan;

internal sealed record TextureUploadFrameOp(VulkanImportedTexturePendingUpload Upload, FrameOpContext Context) 
    : FrameOp(int.MinValue, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.TextureUpload;
    internal override bool RequiresPrimaryRecordingContext => false;

    internal override int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (recordingInfo.EndsRendering)
            commandRuntime.EndActiveRenderPass(ref recordingState);
        if (recordingState.PassIndexLabelActive)
        {
            commandRuntime.CmdEndLabel(recordingState.CommandBuffer);
            recordingState.PassIndexLabelActive = false;
        }

        commandRuntime.CmdBeginLabel(recordingState.CommandBuffer, "TextureUpload");
        commandRuntime.RecordVulkanCommandDiagnosticMarker(
            recordingState.CommandBuffer,
            this,
            recordingInfo.PassIndex,
            recordingInfo.OperationIndex);
        commandRuntime.RecordTextureUploadOp(recordingState.CommandBuffer, Upload);
        commandRuntime.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
