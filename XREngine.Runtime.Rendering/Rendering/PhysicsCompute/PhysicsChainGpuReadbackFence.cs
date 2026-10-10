using XREngine.Components;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Adapts the renderer's nonblocking fence to the selective-readback contract.
/// </summary>
internal sealed class PhysicsChainGpuReadbackFence : IPhysicsChainReadbackFence, IDisposable
{
    private XRGpuFence? _fence;
    private bool _ownsFence;
    private bool _rejected;

    public void Reset(XRGpuFence fence, bool ownsFence = true)
    {
        _fence = fence;
        _ownsFence = ownsFence;
        _rejected = false;
    }

    internal void Reject() => _rejected = true;

    public PhysicsChainReadbackFenceStatus Poll()
    {
        if (_rejected || _fence is null || _fence.IsDisposed || _fence.SubmissionStatus == EGpuFenceSubmissionStatus.Failed)
            return PhysicsChainReadbackFenceStatus.Failed;
        if (_fence.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return PhysicsChainReadbackFenceStatus.Pending;
        return _fence.Poll() switch
        {
            EGpuFenceStatus.Pending => PhysicsChainReadbackFenceStatus.Pending,
            EGpuFenceStatus.Signaled => PhysicsChainReadbackFenceStatus.Signaled,
            _ => PhysicsChainReadbackFenceStatus.Failed,
        };
    }

    public void Dispose()
    {
        if (_ownsFence)
            _fence?.Dispose();
        _fence = null;
        _ownsFence = false;
    }
}
