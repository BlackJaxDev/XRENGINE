using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    private const int MaximumQueuedRaycasts = 10;
    private readonly object _raycastQueueSync = new();
    private readonly Queue<AsyncRaycastRequest> _queuedRaycasts = new(MaximumQueuedRaycasts);
    private readonly object _raycastContextSync = new();
    private readonly Stack<RaycastQueryContext> _raycastContexts = new(MaximumQueuedRaycasts);

    private readonly record struct AsyncRaycastRequest(
        Segment Segment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> Hits,
        Func<RenderInfo3D, Segment, (float? distance, object? data)> DirectTest,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> Finished);

    private sealed class RaycastQueryContext
    {
        private Func<RenderInfo3D, Segment, (float? distance, object? data)>? _directTest;
        public readonly List<RenderInfo3D> Providers = [];
        public readonly HashSet<RenderInfo3D> TestedProviders = new(ReferenceEqualityComparer.Instance);
        public readonly Func<RenderInfo3D, Segment, (float? distance, object? data)> Test;

        public RaycastQueryContext() => Test = TestItem;

        public void Reset(Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest)
            => _directTest = directTest;

        public void Clear()
        {
            Providers.Clear();
            TestedProviders.Clear();
            _directTest = null;
        }

        private (float? distance, object? data) TestItem(RenderInfo3D item, Segment segment)
        {
            if (item.HasCommittedWorldBoundsProvider)
                TestedProviders.Add(item);
            return _directTest!(item, segment);
        }
    }

    private RaycastQueryContext RentRaycastContext(
        Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest)
    {
        RaycastQueryContext context;
        lock (_raycastContextSync)
            context = _raycastContexts.Count > 0 ? _raycastContexts.Pop() : new RaycastQueryContext();
        context.Reset(directTest);
        return context;
    }

    private void ReturnRaycastContext(RaycastQueryContext context)
    {
        context.Clear();
        lock (_raycastContextSync)
            _raycastContexts.Push(context);
    }

    private void QueueRaycast(
        Segment segment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> hits,
        Func<RenderInfo3D, Segment, (float? distance, object? data)> directTest,
        Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> finished)
    {
        lock (_raycastQueueSync)
        {
            if (_queuedRaycasts.Count >= MaximumQueuedRaycasts)
                return;
            _queuedRaycasts.Enqueue(new(segment, hits, directTest, finished));
        }
    }

    private void ProcessQueuedRaycasts()
    {
        for (int index = 0; index < MaximumQueuedRaycasts; ++index)
        {
            AsyncRaycastRequest request;
            lock (_raycastQueueSync)
            {
                if (_queuedRaycasts.Count == 0)
                    return;
                request = _queuedRaycasts.Dequeue();
            }
            Raycast(request.Segment, request.Hits, request.DirectTest);
            request.Finished(request.Hits);
        }
    }

    /// <summary>Tests committed bounds even when a spatial-tree move is still queued.</summary>
    private void RaycastCommittedBoundsProviders(
        Segment segment,
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> hits,
        RaycastQueryContext context)
    {
        lock (_renderablesSync)
        {
            for (int index = 0; index < _renderables.Count; ++index)
            {
                RenderInfo3D info = _renderables[index];
                if (info.HasCommittedWorldBoundsProvider)
                    context.Providers.Add(info);
            }
        }

        for (int index = 0; index < context.Providers.Count; ++index)
        {
            RenderInfo3D info = context.Providers[index];
            if (context.TestedProviders.Contains(info) ||
                !info.TryGetCommittedWorldBounds(out AABB bounds, out _) ||
                !bounds.ToBox(Matrix4x4.Identity).IntersectsSegment(segment) ||
                ContainsRaycastHit(hits, info))
                continue;

            (float? distance, object? data) = context.Test(info, segment);
            if (distance is not { } hitDistance)
                continue;
            if (!hits.TryGetValue(hitDistance, out List<(RenderInfo3D item, object? data)>? atDistance))
            {
                atDistance = [];
                hits.Add(hitDistance, atDistance);
            }
            atDistance.Add((info, data));
        }
    }

    private static bool ContainsRaycastHit(
        SortedDictionary<float, List<(RenderInfo3D item, object? data)>> hits,
        RenderInfo3D candidate)
    {
        foreach (KeyValuePair<float, List<(RenderInfo3D item, object? data)>> entry in hits)
            for (int index = 0; index < entry.Value.Count; ++index)
                if (ReferenceEquals(entry.Value[index].item, candidate))
                    return true;
        return false;
    }
}
