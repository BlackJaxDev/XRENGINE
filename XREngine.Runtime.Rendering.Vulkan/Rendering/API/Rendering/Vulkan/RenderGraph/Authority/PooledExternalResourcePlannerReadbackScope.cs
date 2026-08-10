using System.Collections.Concurrent;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable readback scope that returns directly to the frame-loop session pool after disposal.
/// </summary>
internal sealed class PooledExternalResourcePlannerReadbackScope : IDisposable
{
    private ConcurrentStack<PooledExternalResourcePlannerReadbackScope>? _pool;
    private ExternalResourcePlannerReadbackScope? _scope;
    private bool _leased;

    public void Lease(
        ExternalResourcePlannerReadbackScope scope,
        ConcurrentStack<PooledExternalResourcePlannerReadbackScope> pool)
    {
        _pool = pool;
        _scope = scope;
        _leased = true;
    }

    public void Dispose()
    {
        if (!_leased)
            return;

        try
        {
            _scope?.Dispose();
        }
        finally
        {
            _leased = false;
            _scope = null;
            ConcurrentStack<PooledExternalResourcePlannerReadbackScope>? pool = _pool;
            _pool = null;
            pool?.Push(this);
        }
    }
}
