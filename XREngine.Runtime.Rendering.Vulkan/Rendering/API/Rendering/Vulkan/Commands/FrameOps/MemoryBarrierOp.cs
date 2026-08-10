namespace XREngine.Rendering.Vulkan;

internal sealed record MemoryBarrierOp(
    int PassIndex,
    EMemoryBarrierMask Mask,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public EMemoryBarrierMask Mask { get; private set; } = Mask;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.MemoryBarrier;

    internal override int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                commandRuntime,
                ref recordingState,
                in recordingInfo,
                "MemoryBarrier",
                out int lastOperationIndex))
            return lastOperationIndex;

        commandRuntime.CmdBeginLabel(recordingState.CommandBuffer, "MemoryBarrier");
        commandRuntime.EmitMemoryBarrierMask(recordingState.CommandBuffer, Mask);
        commandRuntime.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }

    internal static MemoryBarrierOp Rent(
        int passIndex,
        EMemoryBarrierMask mask,
        in FrameOpContext context)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out MemoryBarrierOp? reusable);
        if (reusable is null)
        {
            MemoryBarrierOp created = new(passIndex, mask, context);
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
        }

        reusable.PassIndex = passIndex;
        reusable.Target = null;
        reusable.Mask = mask;
        reusable.Context = context;
        return reusable;
    }
}
