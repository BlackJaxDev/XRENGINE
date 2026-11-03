namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Authoring operation for the first advanced visibility lane. It retains no
/// native framebuffer, buffer, or CPU readback result: those are admitted
/// only after the frame plan and its render-graph generation are frozen.
/// </summary>
internal sealed record AdvancedVisibilityOp(
    int PassIndex,
    VulkanAdvancedVisibilityStageRequest Request,
    VulkanAdvancedVisibilityInputLease InputLease,
    FrameOpContext Context)
    : FrameOp(PassIndex, Request.Target, Context)
{
    private int _inputLeaseReleased;

    private AdvancedVisibilityOp(AdvancedVisibilityOp original)
        : base(original)
    {
        Request = original.Request;
        InputLease = original.InputLease;
        InputLease.RetainOrThrow();
    }

    public VulkanAdvancedVisibilityStageRequest Request { get; private set; } = Request;
    internal VulkanAdvancedVisibilityInputLease InputLease { get; private set; } =
        InputLease;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.AdvancedVisibility;

    /// <summary>
    /// A pooled visibility operation is reused only after its input lease is
    /// released, so a recycled operation never drops a lease that is still held.
    /// </summary>
    internal override bool CanReuseFromPool
        => base.CanReuseFromPool && Volatile.Read(ref _inputLeaseReleased) == 1;

    /// <summary>
    /// Rents an operation from <paramref name="workspace"/>, or creates one. The
    /// workspace must keep operations for the frames in flight, because recorded
    /// streams read <see cref="InputLease"/> after the operation is lowered.
    /// </summary>
    internal static AdvancedVisibilityOp Rent(
        VulkanFrameOpWorkspace workspace,
        ulong frameId,
        int passIndex,
        in VulkanAdvancedVisibilityStageRequest request,
        VulkanAdvancedVisibilityInputLease inputLease,
        in FrameOpContext context)
    {
        if (workspace.TryRent(frameId, out AdvancedVisibilityOp? reusable) && reusable is not null)
        {
            reusable.PassIndex = passIndex;
            reusable.Target = request.Target;
            reusable.Context = context;
            reusable.Request = request;
            reusable.InputLease = inputLease;
            Volatile.Write(ref reusable._inputLeaseReleased, 0);
            return reusable;
        }

        return workspace.Retain(new AdvancedVisibilityOp(passIndex, request, inputLease, context));
    }

    internal void ReleaseInputLease()
    {
        if (Interlocked.Exchange(ref _inputLeaseReleased, 1) == 0)
            InputLease.Release();
    }

    protected override void OnSealedAuthoringCopyCreated()
    {
        _inputLeaseReleased = 0;
        InputLease.RetainOrThrow();
    }
}
