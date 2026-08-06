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
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (TryRecordSecondaryBucket(
                renderer,
                ref recordingState,
                in recordingInfo,
                "MemoryBarrier",
                out int lastOperationIndex))
            return lastOperationIndex;

        renderer.CmdBeginLabel(recordingState.CommandBuffer, "MemoryBarrier");
        renderer.EmitMemoryBarrierMask(recordingState.CommandBuffer, Mask);
        renderer.CmdEndLabel(recordingState.CommandBuffer);
        return recordingInfo.OperationIndex;
    }

    internal static MemoryBarrierOp Rent(
        int passIndex,
        EMemoryBarrierMask mask,
        in FrameOpContext context)
    {
        bool frameOwned = TryRentForCurrentFrame(out MemoryBarrierOp? reusable);
        if (reusable is null)
        {
            MemoryBarrierOp created = new(passIndex, mask, context);
            return frameOwned ? RetainForCurrentFrame(created) : created;
        }

        reusable.PassIndex = passIndex;
        reusable.Target = null;
        reusable.Mask = mask;
        reusable.Context = context;
        return reusable;
    }
}
