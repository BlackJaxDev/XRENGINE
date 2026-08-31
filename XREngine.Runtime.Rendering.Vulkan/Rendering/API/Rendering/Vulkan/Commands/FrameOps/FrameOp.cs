namespace XREngine.Rendering.Vulkan;

internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context)
{
    private int _passIndex = PassIndex;
    private XRFrameBuffer? _target = Target;
    private FrameOpContext _context = Context;
    private FrameOpResourceUseList _resourceUses;
    private ComputeDispatchSnapshot? _ownedAuthoringSnapshot;

    public int PassIndex
    {
        get => _passIndex;
        internal set => _passIndex = value;
    }
    public XRFrameBuffer? Target
    {
        get => _target;
        internal set => _target = value;
    }
    public FrameOpContext Context
    {
        get => _context;
        internal set => _context = value;
    }
    internal ref readonly FrameOpContext ContextReference => ref _context;
    internal ref readonly FrameOpResourceUseList ResourceUsesReference
        => ref _resourceUses;
    public abstract EVulkanPrimaryPlanNodeKind Kind { get; }

    /// <summary>
    /// Gets whether this operation participates in the normal context and pass
    /// preparation performed by the primary command recorder.
    /// </summary>
    internal virtual bool RequiresPrimaryRecordingContext => true;

    /// <summary>
    /// Rents an operation whose lifetime is bounded by the current render frame.
    /// The same slot is not reused again until a later frame, so deferred command
    /// recording can safely retain references for the rest of this frame.
    /// </summary>
    protected static bool TryRentForCurrentFrame<T>(
        in FrameOpContext context,
        out T? reusable)
        where T : FrameOp
    {
        reusable = null;
        if (context.OperationWorkspace is null)
            return false;

        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
            return false;

        return context.OperationWorkspace.TryRent(frameId, out reusable);
    }

    internal ref FrameOpResourceUseList BeginResourceUseUpdate()
    {
        _resourceUses.Clear();
        return ref _resourceUses;
    }

    /// <summary>
    /// Freezes an authoring operation before work that can advance the render
    /// frame and reuse its pooled instance. This preserves both the concrete
    /// operation payload and its resource-use column.
    /// </summary>
    internal FrameOp CreateSealedAuthoringCopy()
    {
        FrameOp copy = (FrameOp)MemberwiseClone();
        copy._ownedAuthoringSnapshot = null;
        copy._resourceUses = _resourceUses.CreateSealedCopy();
        copy.OnSealedAuthoringCopyCreated();
        return copy;
    }

    /// <summary>Owns a detached authoring snapshot until lowering or queue discard.</summary>
    internal void OwnAuthoringSnapshot(ComputeDispatchSnapshot snapshot)
    {
        if (_ownedAuthoringSnapshot is not null)
            throw new InvalidOperationException("An authoring operation already owns a snapshot.");
        _ownedAuthoringSnapshot = snapshot;
    }

    internal void ReleaseAuthoringSnapshot()
    {
        ComputeDispatchSnapshot? snapshot = _ownedAuthoringSnapshot;
        _ownedAuthoringSnapshot = null;
        snapshot?.ReleaseReadOnlyStorageBindings();
    }

    /// <summary>
    /// Retains any external authoring lease copied by
    /// <see cref="CreateSealedAuthoringCopy"/>. Ordinary operations own only
    /// their inline record state and require no action.
    /// </summary>
    protected virtual void OnSealedAuthoringCopyCreated()
    {
    }

    protected static T RetainForCurrentFrame<T>(T created, in FrameOpContext context)
        where T : FrameOp
    {
        return context.OperationWorkspace?.Retain(created) ?? created;
    }
}
