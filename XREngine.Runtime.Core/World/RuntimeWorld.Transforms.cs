using System.Collections.Concurrent;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine;

public sealed partial class RuntimeWorld
{
    private readonly ConcurrentDictionary<int, ConcurrentHashSet<TransformBase>> _invalidTransforms = [];
    private readonly List<TransformBase> _dirtyTransformBatch = [];
    private readonly HashSet<TransformBase> _dirtyTransformBatchSet = [];
    private readonly List<TransformBase> _dirtyTransformRoots = [];
    private readonly List<TransformBase> _dirtyTransformDepthRoots = [];
    private readonly List<Task> _dirtyTransformTasks = [];
    private int _processingDirtyTransforms;

    /// <summary>Runs the ordinary and late Core tick groups for one update.</summary>
    public void Update()
    {
        ThrowIfDisposed();
        if (PlayState != RuntimeWorldPlayState.Playing)
            return;
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
    }

    /// <summary>
    /// Recalculates dirty transforms after update callbacks. A dirty ancestor
    /// recalculates its entire hierarchy, so descendants in the same batch need
    /// no second traversal. New invalidations remain queued for the next batch.
    /// </summary>
    public void ProcessDirtyTransforms(ELoopType loopType)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _processingDirtyTransforms, 1) != 0)
            return;

        try
        {
            foreach ((_, ConcurrentHashSet<TransformBase> transforms) in _invalidTransforms)
            {
                foreach (TransformBase transform in transforms)
                {
                    if (transforms.TryRemove(transform) && _dirtyTransformBatchSet.Add(transform))
                        _dirtyTransformBatch.Add(transform);
                }
            }

            foreach (TransformBase transform in _dirtyTransformBatch)
            {
                TransformBase? ancestor = transform.Parent;
                while (ancestor is not null && !_dirtyTransformBatchSet.Contains(ancestor))
                    ancestor = ancestor.Parent;

                if (ancestor is null)
                    _dirtyTransformRoots.Add(transform);
            }

            _dirtyTransformRoots.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
            int depth = int.MinValue;
            foreach (TransformBase transform in _dirtyTransformRoots)
            {
                if (transform.Depth != depth && _dirtyTransformDepthRoots.Count > 0)
                {
                    RecalculateTransformDepth(_dirtyTransformDepthRoots, loopType);
                    _dirtyTransformDepthRoots.Clear();
                }

                depth = transform.Depth;
                _dirtyTransformDepthRoots.Add(transform);
            }

            if (_dirtyTransformDepthRoots.Count > 0)
                RecalculateTransformDepth(_dirtyTransformDepthRoots, loopType);
        }
        catch
        {
            // A failed traversal must not lose the other invalidations in this batch.
            foreach (TransformBase transform in _dirtyTransformBatch)
                _invalidTransforms.GetOrAdd(transform.Depth, static _ => []).Add(transform);
            throw;
        }
        finally
        {
            _dirtyTransformTasks.Clear();
            _dirtyTransformDepthRoots.Clear();
            _dirtyTransformRoots.Clear();
            _dirtyTransformBatchSet.Clear();
            _dirtyTransformBatch.Clear();
            Volatile.Write(ref _processingDirtyTransforms, 0);
        }
    }

    private void RecalculateTransformDepth(List<TransformBase> transforms, ELoopType loopType)
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
                bool joinStarted = false;
                try
                {
                    foreach (TransformBase transform in transforms)
                        _dirtyTransformTasks.Add(transform.RecalculateMatrixHierarchy(true, false, ELoopType.Asynchronous));
                    joinStarted = true;
                    Task.WhenAll(_dirtyTransformTasks).GetAwaiter().GetResult();
                }
                finally
                {
                    // If launching a later hierarchy failed, earlier tasks still own this batch.
                    if (!joinStarted)
                    {
                        try
                        {
                            Task.WhenAll(_dirtyTransformTasks).GetAwaiter().GetResult();
                        }
                        catch
                        {
                            // Preserve the launch exception.
                        }
                    }
                    _dirtyTransformTasks.Clear();
                }
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

}
