using System.Collections.Generic;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Double-buffers GPU BVH overlay requests without allocating in the per-frame render path.
/// </summary>
internal sealed class GpuBvhDebugOverlayQueue
{
    private readonly object _sync = new();
    private List<GpuBvhDebugRenderRequest> _pending = [];
    private List<GpuBvhDebugRenderRequest> _rendering = [];

    public void Enqueue(in GpuBvhDebugRenderRequest request)
    {
        lock (_sync)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (!ReferenceEquals(_pending[i].Renderer, request.Renderer))
                    continue;

                _pending[i] = request;
                return;
            }

            _pending.Add(request);
        }
    }

    public List<GpuBvhDebugRenderRequest> TakeBatch()
    {
        lock (_sync)
        {
            _rendering.Clear();
            (_pending, _rendering) = (_rendering, _pending);
            return _rendering;
        }
    }

    public static void CompleteBatch(List<GpuBvhDebugRenderRequest> batch)
        => batch.Clear();
}
