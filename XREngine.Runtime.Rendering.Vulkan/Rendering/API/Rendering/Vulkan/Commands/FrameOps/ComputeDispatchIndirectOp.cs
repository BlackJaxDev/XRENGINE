using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed record ComputeDispatchIndirectOp(
    int PassIndex,
    VkRenderProgram Program,
    ComputeDispatchSnapshot Snapshot,
    VkDataBuffer ArgumentOwner,
    Buffer ArgumentBuffer,
    ulong ArgumentOffset,
    string Label,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect;

    internal override FrameOp CreateSealedPlanSnapshot()
    {
        ThrowIfSealedForFramePlan();
        return SealPlanSnapshot(this with { Snapshot = Snapshot.CreateSealedCopy() });
    }

    internal override int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                commandRuntime,
                ref recordingState,
                in recordingInfo,
                "ComputeDispatchIndirect",
                out int lastOperationIndex))
            return lastOperationIndex;

        commandRuntime.CmdBeginLabel(recordingState.CommandBuffer, Label);
        commandRuntime.RecordComputeDispatchIndirectOp(
            recordingState.CommandBuffer,
            recordingState.FrameDataImageIndex,
            this);
        commandRuntime.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
