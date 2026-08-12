namespace XREngine.Rendering.Vulkan;

internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) 
    : FrameOp(PassIndex, Target, Context)
{
    private PendingMeshDraw _draw = Draw;
    private DescriptorBindingSnapshot _descriptorBindingSnapshot;
    private bool _hasDescriptorBindingSnapshot;
    private VulkanMeshDrawSortKey _canonicalSortKey;
    private bool _hasCanonicalSortKey;

    public PendingMeshDraw Draw
    {
        get => _draw;
        private set
        {
            _draw = value;
            _hasCanonicalSortKey = false;
        }
    }

    internal ref readonly PendingMeshDraw DrawRef => ref _draw;

    internal ref readonly VulkanMeshDrawSortKey CanonicalSortKey
    {
        get
        {
            if (!_hasCanonicalSortKey)
            {
                _canonicalSortKey = VulkanMeshDrawSortKey.Capture(this);
                _hasCanonicalSortKey = true;
            }

            return ref _canonicalSortKey;
        }
    }

    /// <summary>
    /// Returns the immutable descriptor dependency captured while lowering this
    /// frame operation. Mutable frame-source sampler snapshots deliberately bypass
    /// this cache because their logical binding can acquire a new physical image,
    /// view, or sampler while the retained draw operation remains unchanged.
    /// </summary>
    internal bool TryGetDescriptorBindingSnapshot(
        out DescriptorBindingSnapshot snapshot)
    {
        snapshot = _descriptorBindingSnapshot;
        return _hasDescriptorBindingSnapshot;
    }

    internal void SetDescriptorBindingSnapshot(
        in DescriptorBindingSnapshot snapshot)
    {
        _descriptorBindingSnapshot = snapshot;
        _hasDescriptorBindingSnapshot = true;
    }

    /// <summary>
    /// True when this draw was enqueued inside an occlusion QueryOp Begin/End bracket
    /// (CPU occlusion proxy AABB draws). Such draws must keep their enqueue position
    /// relative to the surrounding QueryOps: canonical opaque-draw reordering would
    /// make the frame-op sort comparer intransitive and scramble Begin/End pairing
    /// (observed as VUID-vkCmdBeginQuery-queryPool-01922 and
    /// VUID-vkEndCommandBuffer-commandBuffer-00061).
    /// </summary>
    private bool _preserveSubmissionOrder;
    internal bool PreserveSubmissionOrder
    {
        get => _preserveSubmissionOrder;
        set
        {
            _preserveSubmissionOrder = value;
            _hasCanonicalSortKey = false;
        }
    }

    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.MeshDraw;

    internal static MeshDrawOp Rent(
        int passIndex,
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        bool preserveSubmissionOrder)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out MeshDrawOp? reusable);
        if (reusable is null)
        {
            MeshDrawOp created = new(passIndex, target, draw, context)
            {
                PreserveSubmissionOrder = preserveSubmissionOrder,
            };
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
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
        _descriptorBindingSnapshot = default;
        _hasDescriptorBindingSnapshot = false;
        _canonicalSortKey = default;
        _hasCanonicalSortKey = false;
    }
}
