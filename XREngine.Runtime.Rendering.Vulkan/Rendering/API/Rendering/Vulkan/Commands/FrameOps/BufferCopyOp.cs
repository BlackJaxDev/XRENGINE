using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed record BufferCopyOp(
    int PassIndex,
    VkDataBuffer SourceOwner,
    Buffer SourceBuffer,
    ulong SourceOffset,
    VkDataBuffer DestinationOwner,
    Buffer DestinationBuffer,
    ulong DestinationOffset,
    ulong ByteCount,
    string Label,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.BufferCopy;

    internal override int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                commandRuntime,
                ref recordingState,
                in recordingInfo,
                Label,
                out int lastOperationIndex))
            return lastOperationIndex;

        commandRuntime.CmdBeginLabel(recordingState.CommandBuffer, Label);
        commandRuntime.RecordBufferCopyOp(recordingState.CommandBuffer, this);
        commandRuntime.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
