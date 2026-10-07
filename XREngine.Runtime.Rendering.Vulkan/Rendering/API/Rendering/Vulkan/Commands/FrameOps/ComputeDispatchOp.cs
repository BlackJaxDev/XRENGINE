namespace XREngine.Rendering.Vulkan;

internal sealed record ComputeDispatchOp(
    int PassIndex,
    VkRenderProgram Program,
    uint GroupsX,
    uint GroupsY,
    uint GroupsZ,
    ComputeDispatchSnapshot Snapshot,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    private ComputeDispatchSnapshot? _authoringSnapshotStorage;

    public VkRenderProgram Program { get; private set; } = Program;
    public uint GroupsX { get; private set; } = GroupsX;
    public uint GroupsY { get; private set; } = GroupsY;
    public uint GroupsZ { get; private set; } = GroupsZ;
    public ComputeDispatchSnapshot Snapshot { get; private set; } = Snapshot;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.ComputeDispatch;

    /// <summary>
    /// Seals bindings in storage owned by this available operation. The workspace
    /// excludes leased operations until their authoring use is released.
    /// </summary>
    internal void SealAuthoringSnapshot()
    {
        ComputeDispatchSnapshot source = Snapshot;
        ComputeDispatchSnapshot storage = _authoringSnapshotStorage ??= new();
        // Register ownership first so a failed copy releases partial resource retains.
        OwnAuthoringSnapshot(storage);
        storage.CopySealedFrom(source);
        Snapshot = storage;
    }

    protected override void OnSealedAuthoringCopyCreated()
    {
        // MemberwiseClone copied the storage reference. The original operation can
        // return to its pool while this detached copy is still retained.
        _authoringSnapshotStorage = null;
        SealAuthoringSnapshot();
    }

    internal static ComputeDispatchOp Rent(
        int passIndex,
        VkRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        ComputeDispatchSnapshot snapshot,
        in FrameOpContext context)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out ComputeDispatchOp? reusable);
        if (reusable is null)
        {
            ComputeDispatchOp created = new(
                passIndex,
                program,
                groupsX,
                groupsY,
                groupsZ,
                snapshot,
                context);
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
        }

        reusable.Reset(
            passIndex,
            program,
            groupsX,
            groupsY,
            groupsZ,
            snapshot,
            context);
        return reusable;
    }

    private void Reset(
        int passIndex,
        VkRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        ComputeDispatchSnapshot snapshot,
        in FrameOpContext context)
    {
        if (AuthoringResource is not null)
            throw new InvalidOperationException("A retained compute operation cannot be reset.");

        PassIndex = passIndex;
        Target = null;
        Program = program;
        GroupsX = groupsX;
        GroupsY = groupsY;
        GroupsZ = groupsZ;
        Snapshot = snapshot;
        Context = context;
    }
}
