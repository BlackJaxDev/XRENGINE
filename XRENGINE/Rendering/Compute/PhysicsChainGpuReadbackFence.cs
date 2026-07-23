using XREngine.Components;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Adapts the renderer's nonblocking fence to the selective-readback contract.
/// </summary>
internal sealed class PhysicsChainGpuReadbackFence : IPhysicsChainReadbackFence, IDisposable
{
    private XRGpuFence? _fence;

    public void Reset(XRGpuFence fence)
        => _fence = fence;

    public PhysicsChainReadbackFenceStatus Poll()
        => _fence?.Poll() switch
        {
            EGpuFenceStatus.Pending => PhysicsChainReadbackFenceStatus.Pending,
            EGpuFenceStatus.Signaled or null => PhysicsChainReadbackFenceStatus.Signaled,
            _ => PhysicsChainReadbackFenceStatus.Failed,
        };

    public void Dispose()
    {
        _fence?.Dispose();
        _fence = null;
    }
}
