using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Descriptor-compatible pool and nonblocking resolver for asynchronous
/// hardware occlusion queries.
/// </summary>
public sealed class AsyncOcclusionQueryManager
{
    private readonly object _lock = new();
    private readonly Dictionary<RenderQueryDescriptor, Queue<XRRenderQuery>> _pools = [];
    private readonly List<XRRenderQuery> _discardedPending = [];

    public XRRenderQuery Acquire(RenderQueryDescriptor descriptor)
    {
        DrainDiscardedPending();

        XRRenderQuery query;
        bool isNew;
        lock (_lock)
        {
            if (_pools.TryGetValue(descriptor, out Queue<XRRenderQuery>? pool) && pool.Count != 0)
            {
                query = pool.Dequeue();
                isNew = false;
            }
            else
            {
                query = new XRRenderQuery(descriptor);
                isNew = true;
            }
        }

        if (isNew)
            query.Generate();
        return query;
    }

    public XRRenderQuery AcquireBooleanOcclusion()
        => Acquire(RenderQueryDescriptor.ConservativeOcclusion);

    /// <summary>
    /// Returns a resolved handle to its descriptor-compatible pool. A discarded
    /// pending handle remains quarantined until its exact epoch becomes ready.
    /// </summary>
    public void Release(XRRenderQuery query, bool pendingResult = false)
    {
        if (query is null)
            return;

        lock (_lock)
        {
            if (pendingResult)
            {
                _discardedPending.Add(query);
                return;
            }

            ReturnToPoolNoLock(query);
        }
    }

    public bool TryGetAnySamplesPassed(
        XRRenderQuery query,
        out bool anySamplesPassed,
        in RenderQueryTicket expectedTicket = default)
        => TryGetAnySamplesPassedStatus(query, out anySamplesPassed, expectedTicket) == ERenderQueryReadStatus.Ready;

    public ERenderQueryReadStatus TryGetAnySamplesPassedStatus(
        XRRenderQuery query,
        out bool anySamplesPassed,
        in RenderQueryTicket expectedTicket = default)
    {
        anySamplesPassed = true;
        if (query is null || query.Descriptor.Kind != ERenderQueryKind.Occlusion)
            return ERenderQueryReadStatus.InvalidState;

        OcclusionQueryResult result = default;
        IRuntimeRendererHost? renderer = AbstractRenderer.Current;
        ERenderQueryReadStatus status =
            renderer is not null &&
            renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out var capability) &&
            capability is not null
                ? capability.TryGetAnySamplesPassed(query, out result, expectedTicket)
                : ERenderQueryReadStatus.Unsupported;

        if (status == ERenderQueryReadStatus.Ready)
            anySamplesPassed = result.AnySamplesPassed;
        return status;
    }

    public bool TryGetTicket(XRRenderQuery query, out RenderQueryTicket ticket)
    {
        ticket = default;
        if (query is null)
            return false;

        IRuntimeRendererHost? renderer = AbstractRenderer.Current;
        if (renderer is not null &&
            renderer.TryGetBackendCapability<IOcclusionQueryBackendCapability>(out var capability) &&
            capability is not null)
        {
            ticket = capability.GetTicket(query);
        }
        return ticket.IsValid;
    }

    private void DrainDiscardedPending()
    {
        lock (_lock)
        {
            for (int index = _discardedPending.Count - 1; index >= 0; index--)
            {
                XRRenderQuery query = _discardedPending[index];
                ERenderQueryReadStatus status = TryGetAnySamplesPassedStatus(query, out _);
                if (status == ERenderQueryReadStatus.NotReady)
                    continue;

                _discardedPending.RemoveAt(index);
                if (status == ERenderQueryReadStatus.Ready)
                    ReturnToPoolNoLock(query);
                else
                    query.Destroy();
            }
        }
    }

    private void ReturnToPoolNoLock(XRRenderQuery query)
    {
        if (!_pools.TryGetValue(query.Descriptor, out Queue<XRRenderQuery>? pool))
        {
            pool = new Queue<XRRenderQuery>();
            _pools.Add(query.Descriptor, pool);
        }
        pool.Enqueue(query);
    }
}
