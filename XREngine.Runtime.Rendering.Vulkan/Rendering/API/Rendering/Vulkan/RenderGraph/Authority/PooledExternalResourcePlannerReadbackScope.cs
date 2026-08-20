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
    private VulkanResourcePlannerSessionService.RuntimeStateScope _runtimeStateScope;
    private bool _hasRuntimeStateScope;
    private bool _leased;

    public void Lease(
        ExternalResourcePlannerReadbackScope scope,
        VulkanResourcePlannerSessionService.RuntimeStateScope runtimeStateScope,
        bool hasRuntimeStateScope,
        ConcurrentStack<PooledExternalResourcePlannerReadbackScope> pool)
    {
        _pool = pool;
        _scope = scope;
        _runtimeStateScope = runtimeStateScope;
        _hasRuntimeStateScope = hasRuntimeStateScope;
        _leased = true;
    }

    public void Dispose()
    {
        if (!_leased)
            return;

        try
        {
            if (_hasRuntimeStateScope)
                _runtimeStateScope.Dispose();
        }
        finally
        {
            try
            {
                _scope?.Dispose();
            }
            finally
            {
                _leased = false;
                _scope = null;
                _runtimeStateScope = default;
                _hasRuntimeStateScope = false;
                ConcurrentStack<PooledExternalResourcePlannerReadbackScope>? pool = _pool;
                _pool = null;
                pool?.Push(this);
            }
        }
    }
}
