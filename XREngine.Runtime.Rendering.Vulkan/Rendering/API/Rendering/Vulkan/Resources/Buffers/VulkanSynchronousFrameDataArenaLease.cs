namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exclusive operation-wide ownership of the single synchronous frame-data slot. The lease is
/// intentionally stack-only so callers cannot retain it beyond their submit-and-wait boundary.
/// </summary>
internal ref struct VulkanSynchronousFrameDataArenaLease
{
    private VulkanResourceRuntime? _owner;

    internal VulkanSynchronousFrameDataArenaLease(VulkanResourceRuntime owner, VulkanFrameDataArena arena)
    {
        _owner = owner;
        Arena = arena;
    }

    internal VulkanFrameDataArena Arena { get; }

    internal bool TryPrepare(in VulkanFrameDataSlice slice)
        => Arena.TryPrepareFrameSlotForSubmission(0, slice.Generation);

    internal void MarkSubmitted(in VulkanFrameDataSlice slice)
        => Arena.MarkFrameSlotSubmitted(0, slice.Generation);

    internal bool TryComplete(in VulkanFrameDataSlice slice)
        => Arena.TryResetFrameSlot(0, slice.Generation, submissionCompletionProven: true);

    public void Dispose()
    {
        VulkanResourceRuntime? owner = _owner;
        if (owner is null)
            return;

        _owner = null;
        owner.ReleaseSynchronousFrameDataArenaLease();
    }
}
