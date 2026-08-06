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
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                renderer,
                ref recordingState,
                in recordingInfo,
                "ComputeDispatchIndirect",
                out int lastOperationIndex))
            return lastOperationIndex;

        renderer.CmdBeginLabel(recordingState.CommandBuffer, Label);
        renderer.RecordComputeDispatchIndirectOp(
            recordingState.CommandBuffer,
            recordingState.FrameDataImageIndex,
            this);
        renderer.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }
}
