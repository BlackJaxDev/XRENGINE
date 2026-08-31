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
