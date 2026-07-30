namespace XREngine.Rendering.Vulkan;

internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) : FrameOp(PassIndex, Target, Context)
{
    private PendingMeshDraw _draw = Draw;

    public PendingMeshDraw Draw
    {
        get => _draw;
        private set => _draw = value;
    }

    internal ref readonly PendingMeshDraw DrawRef => ref _draw;

    /// <summary>
    /// True when this draw was enqueued inside an occlusion QueryOp Begin/End bracket
    /// (CPU occlusion proxy AABB draws). Such draws must keep their enqueue position
    /// relative to the surrounding QueryOps: canonical opaque-draw reordering would
    /// make the frame-op sort comparer intransitive and scramble Begin/End pairing
    /// (observed as VUID-vkCmdBeginQuery-queryPool-01922 and
    /// VUID-vkEndCommandBuffer-commandBuffer-00061).
    /// </summary>
    internal bool PreserveSubmissionOrder { get; set; }

    internal static MeshDrawOp Rent(
        int passIndex,
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        bool preserveSubmissionOrder)
    {
        bool frameOwned = TryRentForCurrentFrame(out MeshDrawOp? reusable);
        if (reusable is null)
        {
            MeshDrawOp created = new(passIndex, target, draw, context)
            {
                PreserveSubmissionOrder = preserveSubmissionOrder,
            };
            return frameOwned ? RetainForCurrentFrame(created) : created;
        }

        reusable.Reset(
            passIndex,
            target,
            draw,
            context,
            preserveSubmissionOrder);
        return reusable;
    }

    internal void Reset(
        int passIndex,
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        bool preserveSubmissionOrder)
    {
        PassIndex = passIndex;
        Target = target;
        Draw = draw;
        Context = context;
        PreserveSubmissionOrder = preserveSubmissionOrder;
    }
}
