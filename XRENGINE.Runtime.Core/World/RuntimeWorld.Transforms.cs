using System.Collections.Concurrent;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine;

public sealed partial class RuntimeWorld
{
    private readonly ConcurrentDictionary<int, ConcurrentHashSet<TransformBase>> _invalidTransforms = [];
    private int _dirtyMinDepth = int.MaxValue;
    private int _dirtyMaxDepth = int.MinValue;

    /// <summary>Runs the ordinary and late Core tick groups for one update.</summary>
    public void Update()
    {
        ThrowIfDisposed();
        TickGroup(ETickGroup.Normal);
        TickGroup(ETickGroup.Late);
    }

    /// <summary>Queues a transform for parent-before-child recalculation.</summary>
    public void AddDirtyTransform(TransformBase transform)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.ForceManualRecalc)
            return;

        _invalidTransforms.GetOrAdd(transform.Depth, static _ => []).Add(transform);
        UpdateDirtyDepthRange(transform.Depth);
    }

    /// <summary>
    /// Recalculates dirty transforms after update callbacks. Async producers may
    /// enqueue while this method runs; the depth range is rebuilt from any work
    /// that remains so a one-shot invalidation cannot be stranded.
    /// </summary>
    public void ProcessDirtyTransforms(ELoopType loopType)
    {
        ThrowIfDisposed();
        int minDepth = Volatile.Read(ref _dirtyMinDepth);
        int maxDepth = Volatile.Read(ref _dirtyMaxDepth);
        if (minDepth <= maxDepth)
        {
            for (int depth = minDepth; depth <= maxDepth; ++depth)
            {
                if (!_invalidTransforms.TryGetValue(depth, out ConcurrentHashSet<TransformBase>? transforms)
                    || transforms.Count == 0)
                {
                    continue;
                }

                RecalculateTransformDepth(transforms, loopType);
                transforms.Clear();
            }
        }

        Volatile.Write(ref _dirtyMinDepth, int.MaxValue);
        Volatile.Write(ref _dirtyMaxDepth, int.MinValue);
        foreach ((int depth, ConcurrentHashSet<TransformBase> transforms) in _invalidTransforms)
            if (transforms.Count > 0)
                UpdateDirtyDepthRange(depth);
    }

    private static void RecalculateTransformDepth(ConcurrentHashSet<TransformBase> transforms, ELoopType loopType)
    {
        if (transforms.Count <= 1)
        {
            foreach (TransformBase transform in transforms)
                transform.RecalculateMatrixHierarchy(true, false, ELoopType.Sequential).GetAwaiter().GetResult();
            return;
        }

        switch (loopType)
        {
            case ELoopType.Asynchronous:
            {
                List<Task> tasks = new(transforms.Count);
                foreach (TransformBase transform in transforms)
                    tasks.Add(transform.RecalculateMatrixHierarchy(true, false, ELoopType.Asynchronous));
                Task.WhenAll(tasks).GetAwaiter().GetResult();
                break;
            }
            case ELoopType.Parallel:
                Parallel.ForEach(
                    transforms,
                    static transform => transform.RecalculateMatrixHierarchy(true, false, ELoopType.Parallel).GetAwaiter().GetResult());
                break;
            default:
                foreach (TransformBase transform in transforms)
                    transform.RecalculateMatrixHierarchy(true, false, ELoopType.Sequential).GetAwaiter().GetResult();
                break;
        }
    }

    private void UpdateDirtyDepthRange(int depth)
    {
        int currentMin;
        while (depth < (currentMin = Volatile.Read(ref _dirtyMinDepth))
            && Interlocked.CompareExchange(ref _dirtyMinDepth, depth, currentMin) != currentMin)
        {
        }

        int currentMax;
        while (depth > (currentMax = Volatile.Read(ref _dirtyMaxDepth))
            && Interlocked.CompareExchange(ref _dirtyMaxDepth, depth, currentMax) != currentMax)
        {
        }
    }
}
