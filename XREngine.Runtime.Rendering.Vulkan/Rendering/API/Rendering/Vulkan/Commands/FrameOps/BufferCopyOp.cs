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
        VulkanRenderer renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                renderer,
                ref recordingState,
                in recordingInfo,
                Label,
                out int lastOperationIndex))
            return lastOperationIndex;

        renderer.CmdBeginLabel(recordingState.CommandBuffer, Label);
        renderer.RecordBufferCopyOp(recordingState.CommandBuffer, this);
        renderer.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
