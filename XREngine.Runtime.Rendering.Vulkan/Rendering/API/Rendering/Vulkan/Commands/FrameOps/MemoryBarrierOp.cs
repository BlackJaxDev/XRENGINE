namespace XREngine.Rendering.Vulkan;

internal sealed record MemoryBarrierOp(
    int PassIndex,
    EMemoryBarrierMask Mask,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public EMemoryBarrierMask Mask { get; private set; } = Mask;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.MemoryBarrier;

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
