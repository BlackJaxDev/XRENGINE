using XREngine.Rendering.Compute;

namespace XREngine;

/// <summary>
/// Thread-safe snapshot of the latest GPU BVH metrics.
/// </summary>
public sealed class RuntimeBvhStats
{
    private readonly object _lock = new();
    private BvhGpuProfiler.Metrics _latest = BvhGpuProfiler.Metrics.Empty;

    public BvhGpuProfiler.Metrics Latest
    {
        get
        {
            lock (_lock)
                return _latest;
        }
    }

    internal void Publish(BvhGpuProfiler.Metrics metrics)
    {
        lock (_lock)
            _latest = metrics;
    }
}
