using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Data.Trees;

/// <summary>
/// Flat, revisioned CPU scene BVH. Readers lease immutable published snapshots;
/// mutations are applied to a reusable staging snapshot and atomically published.
/// </summary>
public sealed class CpuBvhRenderTree<T> : I3DRenderTree<T> where T : class, IOctreeItem
{
    private const int MaxRaycastCommands = 10;
    private const long MaxRaycastTicksPerFrame = 3 * TimeSpan.TicksPerMillisecond;
    private const int TraversalStackCapacity = 256;
    private static int _raycastEnqueueDebugBudget = 20;

    private readonly CpuBvhOptions _options;
    private readonly ConcurrentQueue<(T Item, TreeCommand Command)> _swapCommands = new();
    private readonly ConcurrentQueue<RaycastCommand> _raycastCommands = new();
    private readonly List<ItemRecord> _items = [];
    private readonly Dictionary<T, ItemRecord> _itemRecords = new(ReferenceEqualityComparer.Instance);
    private readonly List<BufferedSwapCommand> _swapCommandBuffer = [];
    private readonly Dictionary<T, int> _swapCommandIndices = new(ReferenceEqualityComparer.Instance);
    private readonly List<BoundsMutation> _boundsJournal = [];
    private readonly Snapshot[] _snapshots;
    private readonly object _mutationSyncRoot = new();
    private readonly UnboundedNode _unboundedNode;

    private Snapshot _published;
    private AABB _bounds;
    private long _nextStableId;
    private long _generation;
    private long _topologyRevision = 1;
    private long _boundsRevision;
    private long _payloadRevision;
    private bool _forceTopologyRebuild = true;
    private CpuBvhRebuildReason _pendingRebuildReason = CpuBvhRebuildReason.InitialBuild;

    private long _topologyBuildCount;
    private long _boundsRefitCount;
    private long _partialRebuildCount;
    private long _qualityRebuildCount;
    private long _refittedLeafCount;
    private long _refittedAncestorCount;
    private long _localRotationCount;
    private long _visitedInternalNodeCount;
    private long _visitedLeafCount;
    private long _visitedPrimitiveCount;
    private long _frustumPlaneTestCount;
    private long _frustumPlaneMaskEliminationCount;
    private long _traversalStackOverflowCount;
    private long _mutationLockWaitTicks;
    private long _mutationLockHoldTicks;
    private long _snapshotPublicationTicks;
    private long _droppedMutationCount;
    private int _pendingMutationCount;
    private CpuBvhUpdateKind _lastUpdate = CpuBvhUpdateKind.Clean;
    private CpuBvhRebuildReason _lastRebuildReason = CpuBvhRebuildReason.None;

    public CpuBvhRenderTree(AABB bounds, CpuBvhOptions? options = null)
    {
        _bounds = SanitizeTreeBounds(bounds);
        _options = options ?? new CpuBvhOptions();
        _options.Validate();
        _unboundedNode = new UnboundedNode(this, _bounds);
        _snapshots = new Snapshot[_options.SnapshotCount];
        for (int i = 0; i < _snapshots.Length; i++)
            _snapshots[i] = new Snapshot(this);

        _published = _snapshots[0];
    }

    public CpuBvhRenderTree(AABB bounds, List<T> items, CpuBvhOptions? options = null)
        : this(bounds, options)
    {
        for (int i = 0; i < items.Count; i++)
            AddImmediate(items[i]);

        PublishPending();
    }

    public long PublishedGeneration => Volatile.Read(ref _published).Generation;

    public void Remake()
    {
        EnterMutationLock(out long waitStart);
        try
        {
            RequestTopologyRebuild(CpuBvhRebuildReason.ExplicitRemake);
        }
        finally
        {
            ExitMutationLock(waitStart);
        }
    }

    public void Remake(AABB newBounds)
    {
        EnterMutationLock(out long waitStart);
        try
        {
            _bounds = SanitizeTreeBounds(newBounds);
            _unboundedNode.UpdateBounds(_bounds);
            RequestTopologyRebuild(CpuBvhRebuildReason.ExplicitRemake);
        }
        finally
        {
            ExitMutationLock(waitStart);
        }
    }

    /// <summary>
    /// Records a non-spatial payload change without scheduling bounds or topology work.
    /// </summary>
    public void PayloadChanged(T item)
    {
        if (item is not null && _itemRecords.ContainsKey(item))
            Interlocked.Increment(ref _payloadRevision);
    }

    public void Swap()
    {
        if (IRenderTree.ProfilingHook is not null)
        {
            using var sample = IRenderTree.ProfilingHook("CpuBvhRenderTree.Swap");
            PublishPending();
        }
        else
            PublishPending();

        ConsumeRaycastCommands();
    }

    public void Add(T value)
    {
        if (value is not null)
            TryEnqueueMutation(value, TreeCommand.Add);
    }

    public void Remove(T value)
    {
        if (value is not null)
            TryEnqueueMutation(value, TreeCommand.Remove);
    }

    public void Move(T item)
    {
        if (item is not null)
            TryEnqueueMutation(item, TreeCommand.Move);
    }

    public void AddRange(IEnumerable<T> value)
    {
        foreach (T item in value)
            Add(item);
    }

    public void RemoveRange(IEnumerable<T> value)
    {
        foreach (T item in value)
            Remove(item);
    }

    void IRenderTree.Add(ITreeItem item)
    {
        if (item is T t)
            Add(t);
    }

    void IRenderTree.Remove(ITreeItem item)
    {
        if (item is T t)
            Remove(t);
    }

    void IRenderTree.AddRange(IEnumerable<ITreeItem> renderedObjects)
    {
        foreach (ITreeItem item in renderedObjects)
            if (item is T t)
                Add(t);
    }

    void IRenderTree.RemoveRange(IEnumerable<ITreeItem> renderedObjects)
    {
        foreach (ITreeItem item in renderedObjects)
            if (item is T t)
                Remove(t);
    }

    public void CollectAll(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            for (int i = 0; i < snapshot.UnboundedCount; i++)
            {
                T item = snapshot.UnboundedItems[i];
                if (item.ShouldRender)
                    action(item);
            }

            for (int i = 0; i < snapshot.EntryCount; i++)
            {
                T item = snapshot.Entries[i].Record.Item;
                if (item.ShouldRender)
                    action(item);
            }

            Interlocked.Add(ref _visitedPrimitiveCount, snapshot.UnboundedCount + snapshot.EntryCount);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public void CollectAll<TVisitor>(ref TVisitor visitor) where TVisitor : struct, ICpuBvhVisitor<T>
    {
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            for (int i = 0; i < snapshot.UnboundedCount; i++)
            {
                T item = snapshot.UnboundedItems[i];
                if (item.ShouldRender)
                    visitor.Visit(item);
            }

            for (int i = 0; i < snapshot.EntryCount; i++)
            {
                T item = snapshot.Entries[i].Record.Item;
                if (item.ShouldRender)
                    visitor.Visit(item);
            }

            Interlocked.Add(ref _visitedPrimitiveCount, snapshot.UnboundedCount + snapshot.EntryCount);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    void I3DRenderTree.CollectAll(Action<IOctreeItem> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            for (int i = 0; i < snapshot.UnboundedCount; i++)
            {
                T item = snapshot.UnboundedItems[i];
                if (item.ShouldRender)
                    action(item);
            }

            for (int i = 0; i < snapshot.EntryCount; i++)
            {
                T item = snapshot.Entries[i].Record.Item;
                if (item.ShouldRender)
                    action(item);
            }
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public void CollectVisible(
        IVolume? volume,
        bool onlyContainingItems,
        Action<T> action,
        OctreeNode<T>.DelIntersectionTest intersectionTest)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(intersectionTest);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            CollectUnbounded(snapshot, volume, onlyContainingItems, action, intersectionTest);
            CollectVisible(snapshot, volume, onlyContainingItems, action, intersectionTest);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    /// <summary>
    /// Allocation-free specialized frustum collection for struct visitors.
    /// </summary>
    public void CollectVisible<TVisitor>(in Frustum frustum, ref TVisitor visitor)
        where TVisitor : struct, ICpuBvhVisitor<T>
    {
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            for (int i = 0; i < snapshot.UnboundedCount; i++)
            {
                T item = snapshot.UnboundedItems[i];
                if (item.ShouldRender)
                    visitor.Visit(item);
            }

            Span<TraversalEntry> stack = stackalloc TraversalEntry[TraversalStackCapacity];
            int stackCount = 0;
            if (snapshot.RootIndex >= 0)
                stack[stackCount++] = new TraversalEntry(snapshot.RootIndex, 0x3F);

            long internals = 0;
            long leaves = 0;
            long primitives = snapshot.UnboundedCount;
            long planeTests = 0;
            long maskEliminations = 0;
            while (stackCount > 0)
            {
                TraversalEntry traversal = stack[--stackCount];
                ref readonly FlatNode node = ref snapshot.Nodes[traversal.NodeIndex];
                EContainment containment = ClassifyFrustumNode(
                    frustum,
                    node.Bounds,
                    traversal.PlaneMask,
                    out byte childMask,
                    ref planeTests,
                    ref maskEliminations);
                if (containment == EContainment.Disjoint)
                    continue;

                if (node.IsLeaf)
                {
                    leaves++;
                    int end = node.First + node.Count;
                    for (int i = node.First; i < end; i++)
                    {
                        ref readonly Entry entry = ref snapshot.Entries[i];
                        T item = entry.Record.Item;
                        if (!item.ShouldRender)
                            continue;

                        primitives++;
                        if (containment == EContainment.Contains || ClassifyFrustumNode(
                                frustum,
                                entry.Bounds,
                                childMask,
                                out _,
                                ref planeTests,
                                ref maskEliminations) != EContainment.Disjoint)
                        {
                            visitor.Visit(item);
                        }
                    }

                    continue;
                }

                internals++;
                if (stackCount + 2 > stack.Length)
                {
                    Interlocked.Increment(ref _traversalStackOverflowCount);
                    CollectVisibleRecursive(snapshot, node.Left, frustum, childMask, ref visitor);
                    CollectVisibleRecursive(snapshot, node.Right, frustum, childMask, ref visitor);
                    continue;
                }

                stack[stackCount++] = new TraversalEntry(node.Right, childMask);
                stack[stackCount++] = new TraversalEntry(node.Left, childMask);
            }

            Interlocked.Add(ref _visitedInternalNodeCount, internals);
            Interlocked.Add(ref _visitedLeafCount, leaves);
            Interlocked.Add(ref _visitedPrimitiveCount, primitives);
            Interlocked.Add(ref _frustumPlaneTestCount, planeTests);
            Interlocked.Add(ref _frustumPlaneMaskEliminationCount, maskEliminations);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    void I3DRenderTree.CollectVisible(
        IVolume? volume,
        bool onlyContainingItems,
        Action<IOctreeItem> action,
        OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(intersectionTest);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            for (int i = 0; i < snapshot.UnboundedCount; i++)
            {
                T item = snapshot.UnboundedItems[i];
                if (item.ShouldRender && intersectionTest(item, volume, onlyContainingItems))
                    action(item);
            }

            Span<TraversalEntry> stack = stackalloc TraversalEntry[TraversalStackCapacity];
            int stackCount = 0;
            if (snapshot.RootIndex >= 0)
                stack[stackCount++] = new TraversalEntry(snapshot.RootIndex, 0x3F);

            while (stackCount > 0)
            {
                TraversalEntry traversal = stack[--stackCount];
                ref readonly FlatNode node = ref snapshot.Nodes[traversal.NodeIndex];
                EContainment containment = ClassifyNode(volume, node.Bounds);
                if (containment == EContainment.Disjoint)
                    continue;

                if (node.IsLeaf)
                {
                    int end = node.First + node.Count;
                    for (int i = node.First; i < end; i++)
                    {
                        T item = snapshot.Entries[i].Record.Item;
                        if (item.ShouldRender &&
                            (containment == EContainment.Contains || intersectionTest(item, volume, onlyContainingItems)))
                        {
                            action(item);
                        }
                    }

                    continue;
                }

                stack[stackCount++] = new TraversalEntry(node.Right, traversal.PlaneMask);
                stack[stackCount++] = new TraversalEntry(node.Left, traversal.PlaneMask);
            }
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            if (snapshot.UnboundedCount > 0 || snapshot.RootIndex < 0)
                CollectUnboundedNode(cullingVolume, containsOnly, action);

            Span<int> stack = stackalloc int[TraversalStackCapacity];
            int count = 0;
            if (snapshot.RootIndex >= 0)
                stack[count++] = snapshot.RootIndex;

            while (count > 0)
            {
                int nodeIndex = stack[--count];
                ref readonly FlatNode node = ref snapshot.Nodes[nodeIndex];
                EContainment containment = ClassifyNode(cullingVolume, node.Bounds);
                if (containment == EContainment.Disjoint)
                    continue;

                bool intersects = containment == EContainment.Intersects;
                if (!containsOnly || !intersects)
                    action((snapshot.NodeHandles[nodeIndex], intersects));

                if (!node.IsLeaf)
                {
                    stack[count++] = node.Right;
                    stack[count++] = node.Left;
                }
            }
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public void DebugRender(IVolume? cullingVolume, DelRenderAABB render, bool onlyContainingItems = false)
    {
        ArgumentNullException.ThrowIfNull(render);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            if (snapshot.UnboundedCount > 0 || snapshot.RootIndex < 0)
                DebugRenderBounds(_bounds, cullingVolume, render);

            Span<int> stack = stackalloc int[TraversalStackCapacity];
            int count = 0;
            if (snapshot.RootIndex >= 0)
                stack[count++] = snapshot.RootIndex;

            while (count > 0)
            {
                int nodeIndex = stack[--count];
                ref readonly FlatNode node = ref snapshot.Nodes[nodeIndex];
                EContainment containment = ClassifyNode(cullingVolume, node.Bounds);
                if (containment == EContainment.Disjoint)
                    continue;

                if (!onlyContainingItems || node.IsLeaf)
                {
                    Color color = containment == EContainment.Intersects ? Color.Green : Color.White;
                    render(node.Bounds.HalfExtents, node.Bounds.Center, color);
                }

                if (!node.IsLeaf)
                {
                    stack[count++] = node.Right;
                    stack[count++] = node.Left;
                }
            }
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public SpatialTreeOccupancyStats GetOccupancyStats()
    {
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            return new SpatialTreeOccupancyStats(
                snapshot.NodeCount == 0 ? 1 : snapshot.NodeCount,
                snapshot.EntryCount + snapshot.UnboundedCount,
                snapshot.UnboundedCount,
                Math.Max(snapshot.MaxLeafOccupancy, snapshot.UnboundedCount),
                snapshot.MaxDepth,
                snapshot.UnboundedCount);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public CpuBvhDiagnostics GetDiagnostics()
    {
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            int itemCount = snapshot.EntryCount + snapshot.UnboundedCount;
            return new CpuBvhDiagnostics(
                snapshot.Generation,
                snapshot.TopologyRevision,
                snapshot.BoundsRevision,
                snapshot.PayloadRevision,
                _lastUpdate,
                _lastRebuildReason,
                Interlocked.Read(ref _topologyBuildCount),
                Interlocked.Read(ref _boundsRefitCount),
                Interlocked.Read(ref _partialRebuildCount),
                Interlocked.Read(ref _qualityRebuildCount),
                Interlocked.Read(ref _refittedLeafCount),
                Interlocked.Read(ref _refittedAncestorCount),
                Interlocked.Read(ref _localRotationCount),
                Interlocked.Read(ref _visitedInternalNodeCount),
                Interlocked.Read(ref _visitedLeafCount),
                Interlocked.Read(ref _visitedPrimitiveCount),
                Interlocked.Read(ref _frustumPlaneTestCount),
                Interlocked.Read(ref _frustumPlaneMaskEliminationCount),
                Interlocked.Read(ref _traversalStackOverflowCount),
                snapshot.NodeCount,
                snapshot.LeafCount,
                itemCount,
                snapshot.UnboundedCount,
                snapshot.MaxDepth,
                snapshot.AverageDepth,
                snapshot.AverageLeafOccupancy,
                snapshot.NormalizedSahCost,
                snapshot.BaselineNormalizedSahCost,
                snapshot.AverageSiblingOverlap,
                snapshot.RootVolumeGrowth,
                itemCount == 0 ? 0.0f : snapshot.LastDirtyCount / (float)itemCount,
                snapshot.ConsecutiveRefitCount,
                Volatile.Read(ref _pendingMutationCount) + Math.Max(0, _boundsJournal.Count - snapshot.JournalCursor),
                Interlocked.Read(ref _droppedMutationCount),
                Interlocked.Read(ref _mutationLockWaitTicks),
                Interlocked.Read(ref _mutationLockHoldTicks),
                Interlocked.Read(ref _snapshotPublicationTicks));
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public void RaycastAsync(
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest,
        Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(directTest);
        ArgumentNullException.ThrowIfNull(finishedCallback);

        if (_raycastCommands.Count >= MaxRaycastCommands)
        {
            if (_raycastEnqueueDebugBudget-- > 0)
                Trace.WriteLine($"[CpuBvhRenderTree.RaycastAsync] Queue full ({_raycastCommands.Count}), dropping command");
            return;
        }

        _raycastCommands.Enqueue(new RaycastCommand(segment, items, directTest, finishedCallback));
    }

    public void Raycast(
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(directTest);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            RaycastInternal(snapshot, segment, items, directTest);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public T? FindFirst(Predicate<T> itemTester, Predicate<AABB> bvhNodeTester)
    {
        ArgumentNullException.ThrowIfNull(itemTester);
        ArgumentNullException.ThrowIfNull(bvhNodeTester);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            if (bvhNodeTester(_bounds))
            {
                for (int i = 0; i < snapshot.UnboundedCount; i++)
                {
                    T item = snapshot.UnboundedItems[i];
                    if (item.ShouldRender && itemTester(item))
                        return item;
                }
            }

            Span<int> stack = stackalloc int[TraversalStackCapacity];
            int count = 0;
            if (snapshot.RootIndex >= 0)
                stack[count++] = snapshot.RootIndex;

            while (count > 0)
            {
                ref readonly FlatNode node = ref snapshot.Nodes[stack[--count]];
                if (!bvhNodeTester(node.Bounds))
                    continue;

                if (node.IsLeaf)
                {
                    int end = node.First + node.Count;
                    for (int i = node.First; i < end; i++)
                    {
                        T item = snapshot.Entries[i].Record.Item;
                        if (item.ShouldRender && itemTester(item))
                            return item;
                    }
                }
                else
                {
                    stack[count++] = node.Right;
                    stack[count++] = node.Left;
                }
            }

            return null;
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    public List<T> FindAll(Predicate<T> itemTester, Predicate<AABB> bvhNodeTester)
    {
        ArgumentNullException.ThrowIfNull(itemTester);
        ArgumentNullException.ThrowIfNull(bvhNodeTester);
        List<T> list = [];
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            if (bvhNodeTester(_bounds))
            {
                for (int i = 0; i < snapshot.UnboundedCount; i++)
                {
                    T item = snapshot.UnboundedItems[i];
                    if (item.ShouldRender && itemTester(item))
                        list.Add(item);
                }
            }

            Span<int> stack = stackalloc int[TraversalStackCapacity];
            int count = 0;
            if (snapshot.RootIndex >= 0)
                stack[count++] = snapshot.RootIndex;

            while (count > 0)
            {
                ref readonly FlatNode node = ref snapshot.Nodes[stack[--count]];
                if (!bvhNodeTester(node.Bounds))
                    continue;

                if (node.IsLeaf)
                {
                    int end = node.First + node.Count;
                    for (int i = node.First; i < end; i++)
                    {
                        T item = snapshot.Entries[i].Record.Item;
                        if (item.ShouldRender && itemTester(item))
                            list.Add(item);
                    }
                }
                else
                {
                    stack[count++] = node.Right;
                    stack[count++] = node.Left;
                }
            }

            return list;
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    private void PublishPending()
    {
        EnterMutationLock(out long waitStart);
        try
        {
            SwapExecutionSummary swapSummary = ConsumeSwapCommands();
            Snapshot current = Volatile.Read(ref _published);
            bool topologyChanged = _forceTopologyRebuild || current.TopologyRevision != _topologyRevision;
            bool boundsChanged = current.BoundsRevision != _boundsRevision;
            if (!topologyChanged && !boundsChanged)
            {
                _lastUpdate = CpuBvhUpdateKind.Clean;
                _lastRebuildReason = CpuBvhRebuildReason.None;
                ReportSwapTiming(swapSummary);
                return;
            }

            Snapshot staging = AcquireStagingSnapshot(current);
            long updateStart = Stopwatch.GetTimestamp();
            if (topologyChanged)
            {
                BuildTopology(staging);
                _lastUpdate = _pendingRebuildReason is CpuBvhRebuildReason.SahDegradation or
                    CpuBvhRebuildReason.RootBoundsGrowth or CpuBvhRebuildReason.RefitAge
                        ? CpuBvhUpdateKind.QualityRebuild
                        : CpuBvhUpdateKind.TopologyRebuild;
                _lastRebuildReason = _pendingRebuildReason;
                Interlocked.Increment(ref _topologyBuildCount);
                if (_lastUpdate == CpuBvhUpdateKind.QualityRebuild)
                    Interlocked.Increment(ref _qualityRebuildCount);
            }
            else
            {
                if (staging.TopologyRevision != current.TopologyRevision)
                    staging.CloneFrom(current);

                ApplyBoundsJournal(staging);
                _lastUpdate = staging.LastRotationCount > 0
                    ? CpuBvhUpdateKind.LocalRestructure
                    : CpuBvhUpdateKind.BoundsRefit;
                _lastRebuildReason = CpuBvhRebuildReason.None;
                Interlocked.Increment(ref _boundsRefitCount);

                CpuBvhRebuildReason qualityReason = EvaluateQuality(staging);
                if (qualityReason != CpuBvhRebuildReason.None)
                {
                    _pendingRebuildReason = qualityReason;
                    BuildTopology(staging);
                    _lastUpdate = CpuBvhUpdateKind.QualityRebuild;
                    _lastRebuildReason = qualityReason;
                    Interlocked.Increment(ref _topologyBuildCount);
                    Interlocked.Increment(ref _qualityRebuildCount);
                }
            }

            long updateTicks = Stopwatch.GetTimestamp() - updateStart;
            PublishSnapshot(staging);
            _forceTopologyRebuild = false;
            _pendingRebuildReason = CpuBvhRebuildReason.None;
            swapSummary = swapSummary with
            {
                ExecuteTicks = swapSummary.ExecuteTicks + updateTicks,
                MaxCommandTicks = Math.Max(swapSummary.MaxCommandTicks, updateTicks),
            };
            ReportSwapTiming(swapSummary);
        }
        finally
        {
            ExitMutationLock(waitStart);
        }
    }

    private void TryEnqueueMutation(T item, TreeCommand command)
    {
        int pending = Interlocked.Increment(ref _pendingMutationCount);
        if (pending > _options.MaxPendingMutations)
        {
            Interlocked.Decrement(ref _pendingMutationCount);
            Interlocked.Increment(ref _droppedMutationCount);
            return;
        }
        _swapCommands.Enqueue((item, command));
    }

    private void EnsurePublished()
    {
        Snapshot snapshot = Volatile.Read(ref _published);
        if (_forceTopologyRebuild || snapshot.TopologyRevision != Volatile.Read(ref _topologyRevision))
            PublishPending();
    }

    private SwapExecutionSummary ConsumeSwapCommands()
    {
        if (_swapCommands.IsEmpty)
            return default;

        long drainStart = Stopwatch.GetTimestamp();
        int drained = DrainSwapCommands();
        long drainTicks = Stopwatch.GetTimestamp() - drainStart;
        int adds = 0;
        int moves = 0;
        int removes = 0;
        int executed = 0;
        long maxCommandTicks = 0;
        EOctreeCommandKind maxCommandKind = EOctreeCommandKind.None;
        long executeStart = Stopwatch.GetTimestamp();

        for (int i = 0; i < _swapCommandBuffer.Count; i++)
        {
            BufferedSwapCommand buffered = _swapCommandBuffer[i];
            TreeCommand command = buffered.ToCommand();
            if (command == TreeCommand.None)
                continue;

            long commandStart = Stopwatch.GetTimestamp();
            bool commandExecuted = command switch
            {
                TreeCommand.Add => AddImmediate(buffered.Item),
                TreeCommand.Remove => RemoveImmediate(buffered.Item),
                TreeCommand.Move => MoveImmediate(buffered.Item),
                _ => false,
            };
            if (!commandExecuted)
                continue;

            executed++;
            if (command == TreeCommand.Add)
                adds++;
            else if (command == TreeCommand.Remove)
                removes++;
            else
                moves++;

            long commandTicks = Stopwatch.GetTimestamp() - commandStart;
            if (commandTicks > maxCommandTicks)
            {
                maxCommandTicks = commandTicks;
                maxCommandKind = ToStatsCommandKind(command);
            }
        }

        _swapCommandBuffer.Clear();
        _swapCommandIndices.Clear();
        if (adds > 0 || moves > 0 || removes > 0)
            IRenderTree.OctreeStatsHook?.Invoke(adds, moves, removes, 0);

        return new SwapExecutionSummary(
            drained,
            executed,
            drainTicks,
            Stopwatch.GetTimestamp() - executeStart,
            maxCommandTicks,
            maxCommandKind);
    }

    private int DrainSwapCommands()
    {
        _swapCommandBuffer.Clear();
        _swapCommandIndices.Clear();
        int drained = 0;
        while (_swapCommands.TryDequeue(out (T Item, TreeCommand Command) command))
        {
            Interlocked.Decrement(ref _pendingMutationCount);
            drained++;
            if (!_swapCommandIndices.TryGetValue(command.Item, out int index))
            {
                _swapCommandIndices[command.Item] = _swapCommandBuffer.Count;
                _swapCommandBuffer.Add(new BufferedSwapCommand(
                    command.Item,
                    _itemRecords.ContainsKey(command.Item),
                    command.Command));
                continue;
            }

            BufferedSwapCommand buffered = _swapCommandBuffer[index];
            buffered.Apply(command.Command);
            _swapCommandBuffer[index] = buffered;
        }

        return drained;
    }

    private bool AddImmediate(T item)
    {
        if (_itemRecords.ContainsKey(item))
            return false;

        int stableId = checked((int)_nextStableId++);
        var record = new ItemRecord(this, item, stableId, _items.Count, _bounds);
        _items.Add(record);
        _itemRecords.Add(item, record);
        item.OctreeNode = record.Handle;
        RequestTopologyRebuild(CpuBvhRebuildReason.ItemAddedOrRemoved);
        return true;
    }

    private bool RemoveImmediate(T item)
    {
        if (!_itemRecords.Remove(item, out ItemRecord? record))
            return false;

        int lastIndex = _items.Count - 1;
        if (record.ItemIndex != lastIndex)
        {
            ItemRecord last = _items[lastIndex];
            _items[record.ItemIndex] = last;
            last.ItemIndex = record.ItemIndex;
        }

        _items.RemoveAt(lastIndex);
        item.OctreeNode = null;
        RequestTopologyRebuild(CpuBvhRebuildReason.ItemAddedOrRemoved);
        return true;
    }

    private bool MoveImmediate(T item)
    {
        if (!_itemRecords.TryGetValue(item, out ItemRecord? record))
            return false;

        bool wasBounded = record.IsBounded;
        bool isBounded = TryGetItemBounds(item, out AABB bounds);
        record.IsBounded = isBounded;
        record.Bounds = isBounded ? bounds : _bounds;
        record.Handle.UpdateBounds(record.Bounds);
        if (wasBounded != isBounded)
        {
            RequestTopologyRebuild(CpuBvhRebuildReason.BoundsClassificationChanged);
            return true;
        }

        if (!isBounded)
            return true;

        long revision = ++_boundsRevision;
        record.LastBoundsRevision = revision;
        _boundsJournal.Add(new BoundsMutation(record, revision));
        return true;
    }

    private void RequestTopologyRebuild(CpuBvhRebuildReason reason)
    {
        _topologyRevision++;
        _forceTopologyRebuild = true;
        _pendingRebuildReason = reason;
    }

    private Snapshot AcquireStagingSnapshot(Snapshot current)
    {
        long waitStart = Stopwatch.GetTimestamp();
        SpinWait spin = default;
        while (true)
        {
            Snapshot? oldest = null;
            for (int i = 0; i < _snapshots.Length; i++)
            {
                Snapshot candidate = _snapshots[i];
                if (ReferenceEquals(candidate, current) || Volatile.Read(ref candidate.ReaderCount) != 0)
                    continue;

                if (oldest is null || candidate.Generation < oldest.Generation)
                    oldest = candidate;
            }

            if (oldest is not null)
            {
                Interlocked.Add(ref _mutationLockWaitTicks, Stopwatch.GetTimestamp() - waitStart);
                return oldest;
            }

            spin.SpinOnce();
        }
    }

    private void BuildTopology(Snapshot snapshot)
    {
        snapshot.ResetForBuild(_items.Count, checked((int)_nextStableId));
        int boundedCount = 0;
        int unboundedCount = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            ItemRecord record = _items[i];
            bool bounded = TryGetItemBounds(record.Item, out AABB bounds);
            record.IsBounded = bounded;
            record.Bounds = bounded ? bounds : _bounds;
            record.Handle.UpdateBounds(record.Bounds);
            record.Item.OctreeNode = record.Handle;
            if (bounded)
            {
                snapshot.Entries[boundedCount] = new Entry(record, bounds, bounds.Center);
                snapshot.EntryByStableId[record.StableId] = boundedCount;
                boundedCount++;
            }
            else
            {
                snapshot.UnboundedItems[unboundedCount++] = record.Item;
            }
        }

        snapshot.EntryCount = boundedCount;
        snapshot.UnboundedCount = unboundedCount;
        snapshot.RootIndex = boundedCount == 0 ? -1 : 0;
        if (boundedCount > 0)
            BuildFlatNodes(snapshot);

        snapshot.TopologyRevision = _topologyRevision;
        snapshot.BoundsRevision = _boundsRevision;
        snapshot.PayloadRevision = _payloadRevision;
        snapshot.JournalCursor = _boundsJournal.Count;
        snapshot.ConsecutiveRefitCount = 0;
        snapshot.LastDirtyCount = boundedCount + unboundedCount;
        snapshot.LastRotationCount = 0;
        ComputeFullMetrics(snapshot, establishBaseline: true);
    }

    private void BuildFlatNodes(Snapshot snapshot)
    {
        snapshot.EnsureNodeCapacity(snapshot.EntryCount * 2);
        snapshot.NodeCount = 1;
        snapshot.BuildTasks[0] = new BuildTask(0, snapshot.EntryCount, -1, 0, 0);
        int taskCount = 1;
        while (taskCount > 0)
        {
            BuildTask task = snapshot.BuildTasks[--taskCount];
            ComputeRangeBounds(snapshot.Entries, task.First, task.Count, out AABB bounds, out AABB centroidBounds);
            ref FlatNode node = ref snapshot.Nodes[task.NodeIndex];
            node = new FlatNode(bounds, task.Parent, task.Depth, task.First, task.Count);

            bool mustSplit = task.Count > _options.LeafCapacity;
            bool splitFound = TryFindSahSplit(
                snapshot,
                task.First,
                task.Count,
                bounds,
                centroidBounds,
                out int axis,
                out int splitBin,
                out float splitCost);
            float leafCost = SurfaceArea(bounds) * task.Count;
            if (!mustSplit && (!splitFound || splitCost >= leafCost))
            {
                CompleteLeaf(snapshot, task.NodeIndex);
                continue;
            }

            int leftCount = splitFound
                ? PartitionEntries(snapshot, task.First, task.Count, axis, splitBin, centroidBounds)
                : 0;
            if (leftCount <= 0 || leftCount >= task.Count)
                leftCount = StableMedianSplit(snapshot, task.First, task.Count, SelectLongestAxis(centroidBounds.Size));

            int leftIndex = snapshot.NodeCount++;
            int rightIndex = snapshot.NodeCount++;
            node.Left = leftIndex;
            node.Right = rightIndex;
            node.Count = 0;
            snapshot.BuildTasks[taskCount++] = new BuildTask(
                task.First + leftCount,
                task.Count - leftCount,
                task.NodeIndex,
                task.Depth + 1,
                rightIndex);
            snapshot.BuildTasks[taskCount++] = new BuildTask(
                task.First,
                leftCount,
                task.NodeIndex,
                task.Depth + 1,
                leftIndex);
        }

        for (int i = snapshot.NodeCount - 1; i >= 0; i--)
            RecomputeNodeMetrics(snapshot, i);
        snapshot.ConfigureNodeHandles();
    }

    private bool TryFindSahSplit(
        Snapshot snapshot,
        int first,
        int count,
        AABB bounds,
        AABB centroidBounds,
        out int bestAxis,
        out int bestBin,
        out float bestCost)
    {
        bestAxis = -1;
        bestBin = -1;
        bestCost = float.PositiveInfinity;
        Vector3 extent = centroidBounds.Size;
        for (int axis = 0; axis < 3; axis++)
        {
            if (!(extent[axis] > 1e-8f) || !float.IsFinite(extent[axis]))
                continue;

            Array.Clear(snapshot.Bins, 0, _options.SahBinCount);
            float scale = _options.SahBinCount / extent[axis];
            for (int i = first; i < first + count; i++)
            {
                ref readonly Entry entry = ref snapshot.Entries[i];
                int binIndex = Math.Clamp((int)((entry.Center[axis] - centroidBounds.Min[axis]) * scale), 0, _options.SahBinCount - 1);
                ref SahBin bin = ref snapshot.Bins[binIndex];
                if (bin.Count++ == 0)
                    bin.Bounds = entry.Bounds;
                else
                    bin.Bounds = Union(bin.Bounds, entry.Bounds);
            }

            int leftCount = 0;
            AABB leftBounds = default;
            for (int i = 0; i < _options.SahBinCount - 1; i++)
            {
                ref readonly SahBin bin = ref snapshot.Bins[i];
                if (bin.Count > 0)
                {
                    leftBounds = leftCount == 0 ? bin.Bounds : Union(leftBounds, bin.Bounds);
                    leftCount += bin.Count;
                }

                snapshot.PrefixCounts[i] = leftCount;
                snapshot.PrefixBounds[i] = leftBounds;
            }

            int rightCount = 0;
            AABB rightBounds = default;
            for (int i = _options.SahBinCount - 1; i > 0; i--)
            {
                ref readonly SahBin bin = ref snapshot.Bins[i];
                if (bin.Count > 0)
                {
                    rightBounds = rightCount == 0 ? bin.Bounds : Union(rightBounds, bin.Bounds);
                    rightCount += bin.Count;
                }

                int split = i - 1;
                int candidateLeftCount = snapshot.PrefixCounts[split];
                if (candidateLeftCount == 0 || rightCount == 0)
                    continue;

                float cost = SurfaceArea(snapshot.PrefixBounds[split]) * candidateLeftCount +
                    SurfaceArea(rightBounds) * rightCount;
                if (cost < bestCost ||
                    (cost.Equals(bestCost) && (axis < bestAxis || axis == bestAxis && split < bestBin)))
                {
                    bestCost = cost;
                    bestAxis = axis;
                    bestBin = split;
                }
            }
        }

        return bestAxis >= 0 && bestCost < SurfaceArea(bounds) * count;
    }

    private int PartitionEntries(Snapshot snapshot, int first, int count, int axis, int splitBin, AABB centroidBounds)
    {
        float extent = centroidBounds.Size[axis];
        if (!(extent > 1e-8f))
            return 0;

        float scale = _options.SahBinCount / extent;
        int leftCount = 0;
        for (int i = first; i < first + count; i++)
        {
            Entry entry = snapshot.Entries[i];
            int bin = Math.Clamp((int)((entry.Center[axis] - centroidBounds.Min[axis]) * scale), 0, _options.SahBinCount - 1);
            if (bin <= splitBin)
                snapshot.ScratchEntries[leftCount++] = entry;
        }

        int write = leftCount;
        for (int i = first; i < first + count; i++)
        {
            Entry entry = snapshot.Entries[i];
            int bin = Math.Clamp((int)((entry.Center[axis] - centroidBounds.Min[axis]) * scale), 0, _options.SahBinCount - 1);
            if (bin > splitBin)
                snapshot.ScratchEntries[write++] = entry;
        }

        Array.Copy(snapshot.ScratchEntries, 0, snapshot.Entries, first, count);
        return leftCount;
    }

    private static int StableMedianSplit(Snapshot snapshot, int first, int count, int axis)
    {
        Array.Sort(snapshot.Entries, first, count, EntryComparer.ForAxis(axis));
        return count / 2;
    }

    private static void CompleteLeaf(Snapshot snapshot, int nodeIndex)
    {
        ref readonly FlatNode node = ref snapshot.Nodes[nodeIndex];
        int end = node.First + node.Count;
        for (int i = node.First; i < end; i++)
        {
            ItemRecord record = snapshot.Entries[i].Record;
            snapshot.EntryByStableId[record.StableId] = i;
            snapshot.LeafByStableId[record.StableId] = nodeIndex;
        }
    }

    private void ApplyBoundsJournal(Snapshot snapshot)
    {
        snapshot.BeginRefit();
        for (int i = snapshot.JournalCursor; i < _boundsJournal.Count; i++)
        {
            BoundsMutation mutation = _boundsJournal[i];
            ItemRecord record = mutation.Record;
            if (mutation.Revision <= snapshot.BoundsRevision ||
                record.StableId >= snapshot.EntryByStableId.Length)
            {
                continue;
            }

            int entryIndex = snapshot.EntryByStableId[record.StableId];
            int leafIndex = snapshot.LeafByStableId[record.StableId];
            if (entryIndex < 0 || leafIndex < 0)
                continue;

            ref Entry entry = ref snapshot.Entries[entryIndex];
            entry.Bounds = record.Bounds;
            entry.Center = record.Bounds.Center;
            snapshot.MarkDirtyLeaf(leafIndex);
        }

        int dirtyLeafCount = snapshot.DirtyLeafCount;
        for (int i = 0; i < dirtyLeafCount; i++)
        {
            int leafIndex = snapshot.DirtyLeaves[i];
            RecomputeLeafBounds(snapshot, leafIndex);
            int parent = snapshot.Nodes[leafIndex].Parent;
            while (parent >= 0)
            {
                snapshot.MarkDirtyAncestor(parent);
                parent = snapshot.Nodes[parent].Parent;
            }
        }

        snapshot.OrderDirtyAncestorsByDepth();
        for (int i = 0; i < snapshot.DirtyAncestorCount; i++)
            RecomputeInternalNode(snapshot, snapshot.OrderedDirtyAncestors[i]);

        int rotations = 0;
        if (_options.EnableLocalRestructuring && _options.LocalRotationBudget > 0)
        {
            for (int i = 0; i < snapshot.DirtyAncestorCount && rotations < _options.LocalRotationBudget; i++)
                if (TryRotate(snapshot, snapshot.OrderedDirtyAncestors[i]))
                    rotations++;
        }

        if (rotations > 0)
            ComputeFullMetrics(snapshot, establishBaseline: false);

        snapshot.BoundsRevision = _boundsRevision;
        snapshot.PayloadRevision = _payloadRevision;
        snapshot.JournalCursor = _boundsJournal.Count;
        snapshot.LastDirtyCount = dirtyLeafCount;
        snapshot.LastRotationCount = rotations;
        snapshot.ConsecutiveRefitCount++;
        snapshot.UpdateRootQuality();
        Interlocked.Add(ref _refittedLeafCount, dirtyLeafCount);
        Interlocked.Add(ref _refittedAncestorCount, snapshot.DirtyAncestorCount);
        Interlocked.Add(ref _localRotationCount, rotations);
    }

    private static void RecomputeLeafBounds(Snapshot snapshot, int leafIndex)
    {
        ref FlatNode leaf = ref snapshot.Nodes[leafIndex];
        AABB bounds = snapshot.Entries[leaf.First].Bounds;
        for (int i = leaf.First + 1; i < leaf.First + leaf.Count; i++)
            bounds = Union(bounds, snapshot.Entries[i].Bounds);
        leaf.Bounds = bounds;
        RecomputeNodeMetrics(snapshot, leafIndex);
        snapshot.NodeHandles[leafIndex].UpdateBounds(bounds);
    }

    private static void RecomputeInternalNode(Snapshot snapshot, int nodeIndex)
    {
        ref FlatNode node = ref snapshot.Nodes[nodeIndex];
        node.Bounds = Union(snapshot.Nodes[node.Left].Bounds, snapshot.Nodes[node.Right].Bounds);
        RecomputeNodeMetrics(snapshot, nodeIndex);
        snapshot.NodeHandles[nodeIndex].UpdateBounds(node.Bounds);
    }

    private bool TryRotate(Snapshot snapshot, int nodeIndex)
    {
        ref FlatNode node = ref snapshot.Nodes[nodeIndex];
        if (node.IsLeaf)
            return false;

        int left = node.Left;
        int right = node.Right;
        float currentCost = PairingCost(snapshot, left, right);
        Rotation best = default;
        float bestCost = currentCost;
        EvaluatePromotions(snapshot, nodeIndex, left, right, true, ref best, ref bestCost);
        EvaluatePromotions(snapshot, nodeIndex, right, left, false, ref best, ref bestCost);
        if (best.Kind == RotationKind.None || bestCost >= currentCost * _options.RotationImprovementThreshold)
            return false;

        ApplyRotation(snapshot, nodeIndex, best);
        return true;
    }

    private static void EvaluatePromotions(
        Snapshot snapshot,
        int parent,
        int internalChild,
        int sibling,
        bool internalIsLeft,
        ref Rotation best,
        ref float bestCost)
    {
        ref readonly FlatNode child = ref snapshot.Nodes[internalChild];
        if (child.IsLeaf)
            return;

        float promoteLeft = PairingCost(snapshot, child.Left, internalChild, child.Right, sibling);
        if (promoteLeft < bestCost)
        {
            bestCost = promoteLeft;
            best = new Rotation(
                internalIsLeft ? RotationKind.PromoteLeftFromLeft : RotationKind.PromoteLeftFromRight,
                internalChild,
                child.Left,
                child.Right,
                sibling,
                parent);
        }

        float promoteRight = PairingCost(snapshot, child.Right, internalChild, child.Left, sibling);
        if (promoteRight < bestCost)
        {
            bestCost = promoteRight;
            best = new Rotation(
                internalIsLeft ? RotationKind.PromoteRightFromLeft : RotationKind.PromoteRightFromRight,
                internalChild,
                child.Right,
                child.Left,
                sibling,
                parent);
        }
    }

    private static float PairingCost(Snapshot snapshot, int left, int right)
        => SurfaceArea(snapshot.Nodes[left].Bounds) * snapshot.Nodes[left].PrimitiveCount +
            SurfaceArea(snapshot.Nodes[right].Bounds) * snapshot.Nodes[right].PrimitiveCount;

    private static float PairingCost(Snapshot snapshot, int promoted, int internalNode, int retained, int sibling)
    {
        AABB pairedBounds = Union(snapshot.Nodes[retained].Bounds, snapshot.Nodes[sibling].Bounds);
        int pairedPrimitives = snapshot.Nodes[retained].PrimitiveCount + snapshot.Nodes[sibling].PrimitiveCount;
        return SurfaceArea(snapshot.Nodes[promoted].Bounds) * snapshot.Nodes[promoted].PrimitiveCount +
            SurfaceArea(pairedBounds) * pairedPrimitives + SurfaceArea(pairedBounds);
    }

    private static void ApplyRotation(Snapshot snapshot, int parentIndex, Rotation rotation)
    {
        ref FlatNode parent = ref snapshot.Nodes[parentIndex];
        ref FlatNode internalNode = ref snapshot.Nodes[rotation.InternalNode];
        bool internalWasLeft = rotation.Kind is RotationKind.PromoteLeftFromLeft or RotationKind.PromoteRightFromLeft;
        if (internalWasLeft)
        {
            parent.Left = rotation.Promoted;
            parent.Right = rotation.InternalNode;
        }
        else
        {
            parent.Left = rotation.InternalNode;
            parent.Right = rotation.Promoted;
        }

        internalNode.Left = rotation.Retained;
        internalNode.Right = rotation.Sibling;
        snapshot.Nodes[rotation.Promoted].Parent = parentIndex;
        internalNode.Parent = parentIndex;
        snapshot.Nodes[rotation.Retained].Parent = rotation.InternalNode;
        snapshot.Nodes[rotation.Sibling].Parent = rotation.InternalNode;
        RecomputeInternalNode(snapshot, rotation.InternalNode);
        RecomputeInternalNode(snapshot, parentIndex);
        snapshot.RecomputeDepths(parentIndex);
    }

    private CpuBvhRebuildReason EvaluateQuality(Snapshot snapshot)
    {
        float baseline = Math.Max(snapshot.BaselineNormalizedSahCost, 1e-6f);
        float ratio = snapshot.NormalizedSahCost / baseline;
        if (ratio >= _options.FullRebuildSahRatio)
            return CpuBvhRebuildReason.SahDegradation;
        if (snapshot.RootVolumeGrowth >= _options.FullRebuildRootGrowthRatio)
            return CpuBvhRebuildReason.RootBoundsGrowth;
        if (snapshot.ConsecutiveRefitCount >= _options.FullRebuildRefitCount)
            return CpuBvhRebuildReason.RefitAge;

        // Partial subtree rebuilds were deliberately not selected: bounded local rotations
        // handle moderate degradation; a deterministic full rebuild handles severe damage.
        return CpuBvhRebuildReason.None;
    }

    private void PublishSnapshot(Snapshot snapshot)
    {
        long start = Stopwatch.GetTimestamp();
        snapshot.Generation = ++_generation;
        snapshot.PayloadRevision = _payloadRevision;
        Volatile.Write(ref _published, snapshot);
        Interlocked.Add(ref _snapshotPublicationTicks, Stopwatch.GetTimestamp() - start);
        TrimBoundsJournal();
    }

    private void TrimBoundsJournal()
    {
        if (_boundsJournal.Count < 4096)
            return;

        int minimumCursor = _boundsJournal.Count;
        for (int i = 0; i < _snapshots.Length; i++)
        {
            Snapshot snapshot = _snapshots[i];
            if (snapshot.TopologyRevision == _topologyRevision)
                minimumCursor = Math.Min(minimumCursor, snapshot.JournalCursor);
        }

        if (minimumCursor < 2048)
            return;

        _boundsJournal.RemoveRange(0, minimumCursor);
        for (int i = 0; i < _snapshots.Length; i++)
            _snapshots[i].JournalCursor = Math.Max(0, _snapshots[i].JournalCursor - minimumCursor);
    }

    private Snapshot AcquireSnapshot()
    {
        while (true)
        {
            Snapshot snapshot = Volatile.Read(ref _published);
            Interlocked.Increment(ref snapshot.ReaderCount);
            if (ReferenceEquals(snapshot, Volatile.Read(ref _published)))
                return snapshot;
            Interlocked.Decrement(ref snapshot.ReaderCount);
        }
    }

    private static void ReleaseSnapshot(Snapshot snapshot)
        => Interlocked.Decrement(ref snapshot.ReaderCount);

    private void CollectUnbounded(
        Snapshot snapshot,
        IVolume? volume,
        bool onlyContainingItems,
        Action<T> action,
        OctreeNode<T>.DelIntersectionTest intersectionTest)
    {
        for (int i = 0; i < snapshot.UnboundedCount; i++)
        {
            T item = snapshot.UnboundedItems[i];
            if (item.ShouldRender && intersectionTest(item, volume, onlyContainingItems))
                action(item);
        }
    }

    private void CollectVisible(
        Snapshot snapshot,
        IVolume? volume,
        bool onlyContainingItems,
        Action<T> action,
        OctreeNode<T>.DelIntersectionTest intersectionTest)
    {
        Span<TraversalEntry> stack = stackalloc TraversalEntry[TraversalStackCapacity];
        int stackCount = 0;
        if (snapshot.RootIndex >= 0)
            stack[stackCount++] = new TraversalEntry(snapshot.RootIndex, 0x3F);

        bool isFrustum = volume is Frustum;
        Frustum frustum = isFrustum ? (Frustum)volume! : default;
        long internals = 0;
        long leaves = 0;
        long primitives = snapshot.UnboundedCount;
        long planeTests = 0;
        long maskEliminations = 0;
        while (stackCount > 0)
        {
            TraversalEntry traversal = stack[--stackCount];
            ref readonly FlatNode node = ref snapshot.Nodes[traversal.NodeIndex];
            EContainment containment;
            byte childMask = traversal.PlaneMask;
            if (isFrustum)
            {
                containment = ClassifyFrustumNode(
                    frustum,
                    node.Bounds,
                    traversal.PlaneMask,
                    out childMask,
                    ref planeTests,
                    ref maskEliminations);
            }
            else
                containment = ClassifyNode(volume, node.Bounds);

            if (containment == EContainment.Disjoint)
                continue;

            if (node.IsLeaf)
            {
                leaves++;
                int end = node.First + node.Count;
                for (int i = node.First; i < end; i++)
                {
                    T item = snapshot.Entries[i].Record.Item;
                    if (!item.ShouldRender)
                        continue;
                    primitives++;
                    if (containment == EContainment.Contains || intersectionTest(item, volume, onlyContainingItems))
                        action(item);
                }
                continue;
            }

            internals++;
            if (stackCount + 2 > stack.Length)
            {
                Interlocked.Increment(ref _traversalStackOverflowCount);
                CollectVisibleRecursive(snapshot, node.Left, volume, onlyContainingItems, action, intersectionTest);
                CollectVisibleRecursive(snapshot, node.Right, volume, onlyContainingItems, action, intersectionTest);
                continue;
            }

            stack[stackCount++] = new TraversalEntry(node.Right, childMask);
            stack[stackCount++] = new TraversalEntry(node.Left, childMask);
        }

        Interlocked.Add(ref _visitedInternalNodeCount, internals);
        Interlocked.Add(ref _visitedLeafCount, leaves);
        Interlocked.Add(ref _visitedPrimitiveCount, primitives);
        Interlocked.Add(ref _frustumPlaneTestCount, planeTests);
        Interlocked.Add(ref _frustumPlaneMaskEliminationCount, maskEliminations);
    }

    private static void CollectVisibleRecursive(
        Snapshot snapshot,
        int nodeIndex,
        IVolume? volume,
        bool onlyContainingItems,
        Action<T> action,
        OctreeNode<T>.DelIntersectionTest intersectionTest)
    {
        ref readonly FlatNode node = ref snapshot.Nodes[nodeIndex];
        EContainment containment = ClassifyNode(volume, node.Bounds);
        if (containment == EContainment.Disjoint)
            return;
        if (node.IsLeaf)
        {
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                T item = snapshot.Entries[i].Record.Item;
                if (item.ShouldRender &&
                    (containment == EContainment.Contains || intersectionTest(item, volume, onlyContainingItems)))
                {
                    action(item);
                }
            }
            return;
        }

        CollectVisibleRecursive(snapshot, node.Left, volume, onlyContainingItems, action, intersectionTest);
        CollectVisibleRecursive(snapshot, node.Right, volume, onlyContainingItems, action, intersectionTest);
    }

    private static void CollectVisibleRecursive<TVisitor>(
        Snapshot snapshot,
        int nodeIndex,
        Frustum frustum,
        byte planeMask,
        ref TVisitor visitor)
        where TVisitor : struct, ICpuBvhVisitor<T>
    {
        long tests = 0;
        long eliminations = 0;
        ref readonly FlatNode node = ref snapshot.Nodes[nodeIndex];
        EContainment containment = ClassifyFrustumNode(
            frustum,
            node.Bounds,
            planeMask,
            out byte childMask,
            ref tests,
            ref eliminations);
        if (containment == EContainment.Disjoint)
            return;
        if (node.IsLeaf)
        {
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                ref readonly Entry entry = ref snapshot.Entries[i];
                T item = entry.Record.Item;
                long itemTests = 0;
                long itemEliminations = 0;
                if (item.ShouldRender &&
                    (containment == EContainment.Contains || ClassifyFrustumNode(
                        frustum,
                        entry.Bounds,
                        childMask,
                        out _,
                        ref itemTests,
                        ref itemEliminations) != EContainment.Disjoint))
                {
                    visitor.Visit(item);
                }
            }
            return;
        }

        CollectVisibleRecursive(snapshot, node.Left, frustum, childMask, ref visitor);
        CollectVisibleRecursive(snapshot, node.Right, frustum, childMask, ref visitor);
    }

    private static EContainment ClassifyFrustumNode(
        Frustum frustum,
        AABB bounds,
        byte activeMask,
        out byte childMask,
        ref long planeTests,
        ref long maskEliminations)
    {
        childMask = activeMask;
        IReadOnlyList<Plane> planes = frustum.Planes;
        for (int i = 0; i < 6; i++)
        {
            byte bit = (byte)(1 << i);
            if ((childMask & bit) == 0)
                continue;

            planeTests++;
            Plane plane = planes[i];
            Vector3 positive = new(
                plane.Normal.X >= 0.0f ? bounds.Max.X : bounds.Min.X,
                plane.Normal.Y >= 0.0f ? bounds.Max.Y : bounds.Min.Y,
                plane.Normal.Z >= 0.0f ? bounds.Max.Z : bounds.Min.Z);
            if (Vector3.Dot(plane.Normal, positive) + plane.D < -1e-5f)
                return EContainment.Disjoint;

            Vector3 negative = new(
                plane.Normal.X >= 0.0f ? bounds.Min.X : bounds.Max.X,
                plane.Normal.Y >= 0.0f ? bounds.Min.Y : bounds.Max.Y,
                plane.Normal.Z >= 0.0f ? bounds.Min.Z : bounds.Max.Z);
            if (Vector3.Dot(plane.Normal, negative) + plane.D >= -1e-5f)
            {
                childMask &= (byte)~bit;
                maskEliminations++;
            }
        }

        return childMask == 0 ? EContainment.Contains : EContainment.Intersects;
    }

    private static EContainment ClassifyNode(IVolume? volume, AABB bounds)
    {
        if (volume is null)
            return EContainment.Contains;
        EContainment containment = volume.ContainsAABB(bounds);
        if (containment != EContainment.Disjoint)
            return containment;
        if (volume is AABB aabb && aabb.Intersects(bounds))
            return EContainment.Intersects;
        if (volume is Frustum frustum && frustum.Intersects(bounds))
            return EContainment.Intersects;
        return EContainment.Disjoint;
    }

    private void CollectUnboundedNode(
        IVolume? volume,
        bool containsOnly,
        Action<(OctreeNodeBase node, bool intersects)> action)
    {
        EContainment containment = ClassifyNode(volume, _bounds);
        if (containment == EContainment.Disjoint)
            return;
        bool intersects = containment == EContainment.Intersects;
        if (!containsOnly || !intersects)
            action((_unboundedNode, intersects));
    }

    private static void DebugRenderBounds(AABB bounds, IVolume? volume, DelRenderAABB render)
    {
        EContainment containment = ClassifyNode(volume, bounds);
        if (containment == EContainment.Disjoint)
            return;
        Color color = containment == EContainment.Intersects ? Color.Green : Color.White;
        render(bounds.HalfExtents, bounds.Center, color);
    }

    private void RaycastInternal(
        Snapshot snapshot,
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest)
    {
        for (int i = 0; i < snapshot.UnboundedCount; i++)
            RaycastItem(snapshot.UnboundedItems[i], segment, items, directTest);

        Span<RayTraversalEntry> stack = stackalloc RayTraversalEntry[TraversalStackCapacity];
        int count = 0;
        if (snapshot.RootIndex >= 0 && TryGetSegmentEntry(segment, snapshot.Nodes[snapshot.RootIndex].Bounds, out float rootDistance))
            stack[count++] = new RayTraversalEntry(snapshot.RootIndex, rootDistance);

        while (count > 0)
        {
            RayTraversalEntry traversal = stack[--count];
            ref readonly FlatNode node = ref snapshot.Nodes[traversal.NodeIndex];
            if (node.IsLeaf)
            {
                for (int i = node.First; i < node.First + node.Count; i++)
                    RaycastItem(snapshot.Entries[i].Record.Item, segment, items, directTest);
                continue;
            }

            bool hitLeft = TryGetSegmentEntry(segment, snapshot.Nodes[node.Left].Bounds, out float leftDistance);
            bool hitRight = TryGetSegmentEntry(segment, snapshot.Nodes[node.Right].Bounds, out float rightDistance);
            if (hitLeft && hitRight)
            {
                if (leftDistance <= rightDistance)
                {
                    stack[count++] = new RayTraversalEntry(node.Right, rightDistance);
                    stack[count++] = new RayTraversalEntry(node.Left, leftDistance);
                }
                else
                {
                    stack[count++] = new RayTraversalEntry(node.Left, leftDistance);
                    stack[count++] = new RayTraversalEntry(node.Right, rightDistance);
                }
            }
            else if (hitLeft)
                stack[count++] = new RayTraversalEntry(node.Left, leftDistance);
            else if (hitRight)
                stack[count++] = new RayTraversalEntry(node.Right, rightDistance);
        }
    }

    private static bool TryGetSegmentEntry(Segment segment, AABB bounds, out float entry)
    {
        Vector3 direction = segment.End - segment.Start;
        float minimum = 0.0f;
        float maximum = 1.0f;
        for (int axis = 0; axis < 3; axis++)
        {
            float delta = direction[axis];
            if (MathF.Abs(delta) <= 1e-12f)
            {
                if (segment.Start[axis] < bounds.Min[axis] || segment.Start[axis] > bounds.Max[axis])
                {
                    entry = 0.0f;
                    return false;
                }
                continue;
            }

            float inverse = 1.0f / delta;
            float first = (bounds.Min[axis] - segment.Start[axis]) * inverse;
            float second = (bounds.Max[axis] - segment.Start[axis]) * inverse;
            if (first > second)
                (first, second) = (second, first);
            minimum = MathF.Max(minimum, first);
            maximum = MathF.Min(maximum, second);
            if (minimum > maximum)
            {
                entry = 0.0f;
                return false;
            }
        }

        entry = minimum;
        return true;
    }

    private static void RaycastItem(
        T item,
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest)
    {
        Box? worldCullingVolume = item.WorldCullingVolume;
        if (worldCullingVolume is not null && !worldCullingVolume.Value.IntersectsSegment(segment))
            return;
        (float? distance, object? data) = directTest(item, segment);
        if (distance is null)
            return;
        if (!items.TryGetValue(distance.Value, out List<(T item, object? data)>? list))
        {
            list = [];
            items.Add(distance.Value, list);
        }
        list.Add((item, data));
    }

    private void ConsumeRaycastCommands()
    {
        if (_raycastCommands.IsEmpty)
            return;
        long startTicks = DateTime.UtcNow.Ticks;
        int processed = 0;
        int dropped = 0;
        long traversalTicks = 0;
        long callbackTicks = 0;
        long maxTraversalTicks = 0;
        long maxCallbackTicks = 0;
        long maxCommandTicks = 0;
        while (_raycastCommands.TryDequeue(out RaycastCommand command))
        {
            long commandStart = Stopwatch.GetTimestamp();
            Raycast(command.Segment, command.Items, command.DirectTest);
            long traversal = Stopwatch.GetTimestamp() - commandStart;
            long callbackStart = Stopwatch.GetTimestamp();
            command.FinishedCallback(command.Items);
            long callback = Stopwatch.GetTimestamp() - callbackStart;
            processed++;
            traversalTicks += traversal;
            callbackTicks += callback;
            maxTraversalTicks = Math.Max(maxTraversalTicks, traversal);
            maxCallbackTicks = Math.Max(maxCallbackTicks, callback);
            maxCommandTicks = Math.Max(maxCommandTicks, Stopwatch.GetTimestamp() - commandStart);
            if (DateTime.UtcNow.Ticks - startTicks <= MaxRaycastTicksPerFrame)
                continue;
            while (_raycastCommands.TryDequeue(out _))
                dropped++;
            break;
        }

        IRenderTree.OctreeRaycastTimingHook?.Invoke(new OctreeRaycastTimingStats(
            processed,
            dropped,
            traversalTicks,
            callbackTicks,
            maxTraversalTicks,
            maxCallbackTicks,
            maxCommandTicks));
    }

    private void ReportSwapTiming(SwapExecutionSummary summary)
    {
        if (summary.DrainedCommandCount == 0 && _lastUpdate == CpuBvhUpdateKind.Clean)
            return;
        IRenderTree.OctreeSwapTimingHook?.Invoke(new OctreeSwapTimingStats(
            summary.DrainedCommandCount,
            summary.ExecutedCommandCount,
            summary.ExecutedCommandCount,
            summary.DrainTicks,
            summary.ExecuteTicks,
            summary.MaxCommandTicks,
            summary.MaxCommandKind));
    }

    private static bool TryGetItemBounds(T item, out AABB bounds)
    {
        Box? box = item.WorldCullingVolume;
        if (box is null)
        {
            bounds = default;
            return false;
        }
        bounds = box.Value.GetAABB(true);
        return bounds.IsValid;
    }

    private static AABB SanitizeTreeBounds(AABB bounds)
        => bounds.IsValid ? bounds : AABB.FromCenterSize(Vector3.Zero, Vector3.One);

    private static void ComputeRangeBounds(Entry[] entries, int first, int count, out AABB bounds, out AABB centroidBounds)
    {
        bounds = entries[first].Bounds;
        Vector3 centroid = entries[first].Center;
        centroidBounds = new AABB(centroid, centroid);
        for (int i = first + 1; i < first + count; i++)
        {
            bounds = Union(bounds, entries[i].Bounds);
            centroid = entries[i].Center;
            centroidBounds = new AABB(Vector3.Min(centroidBounds.Min, centroid), Vector3.Max(centroidBounds.Max, centroid));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AABB Union(AABB left, AABB right)
        => new(Vector3.Min(left.Min, right.Min), Vector3.Max(left.Max, right.Max));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SurfaceArea(AABB bounds)
    {
        Vector3 size = Vector3.Max(bounds.Size, Vector3.Zero);
        return 2.0f * (size.X * size.Y + size.X * size.Z + size.Y * size.Z);
    }

    private static float OverlapRatio(AABB left, AABB right, AABB parent)
    {
        Vector3 minimum = Vector3.Max(left.Min, right.Min);
        Vector3 maximum = Vector3.Min(left.Max, right.Max);
        if (minimum.X >= maximum.X || minimum.Y >= maximum.Y || minimum.Z >= maximum.Z)
            return 0.0f;
        float parentArea = SurfaceArea(parent);
        return parentArea <= 1e-12f ? 0.0f : SurfaceArea(new AABB(minimum, maximum)) / parentArea;
    }

    private static int SelectLongestAxis(Vector3 extent)
        => extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;

    private static void RecomputeNodeMetrics(Snapshot snapshot, int nodeIndex)
    {
        ref FlatNode node = ref snapshot.Nodes[nodeIndex];
        if (node.IsLeaf)
        {
            node.PrimitiveCount = node.Count;
            node.Cost = SurfaceArea(node.Bounds) * node.Count;
            node.Overlap = 0.0f;
            return;
        }
        ref readonly FlatNode left = ref snapshot.Nodes[node.Left];
        ref readonly FlatNode right = ref snapshot.Nodes[node.Right];
        node.PrimitiveCount = left.PrimitiveCount + right.PrimitiveCount;
        node.Cost = SurfaceArea(node.Bounds) + left.Cost + right.Cost;
        node.Overlap = OverlapRatio(left.Bounds, right.Bounds, node.Bounds);
    }

    private static void ComputeFullMetrics(Snapshot snapshot, bool establishBaseline)
    {
        int leaves = 0;
        int maxDepth = 0;
        int maxOccupancy = 0;
        long depthSum = 0;
        long occupancySum = 0;
        float overlapSum = 0.0f;
        int internalCount = 0;
        for (int i = snapshot.NodeCount - 1; i >= 0; i--)
        {
            RecomputeNodeMetrics(snapshot, i);
            ref readonly FlatNode node = ref snapshot.Nodes[i];
            maxDepth = Math.Max(maxDepth, node.Depth);
            if (node.IsLeaf)
            {
                leaves++;
                depthSum += node.Depth;
                occupancySum += node.Count;
                maxOccupancy = Math.Max(maxOccupancy, node.Count);
            }
            else
            {
                internalCount++;
                overlapSum += node.Overlap;
            }
        }

        snapshot.LeafCount = leaves;
        snapshot.MaxDepth = maxDepth;
        snapshot.MaxLeafOccupancy = maxOccupancy;
        snapshot.AverageDepth = leaves == 0 ? 0.0f : depthSum / (float)leaves;
        snapshot.AverageLeafOccupancy = leaves == 0 ? 0.0f : occupancySum / (float)leaves;
        snapshot.AverageSiblingOverlap = internalCount == 0 ? 0.0f : overlapSum / internalCount;
        snapshot.UpdateRootQuality();
        if (establishBaseline)
        {
            snapshot.BaselineNormalizedSahCost = snapshot.NormalizedSahCost;
            snapshot.BaselineRootVolume = snapshot.RootIndex < 0 ? 0.0f : snapshot.Nodes[snapshot.RootIndex].Bounds.Volume;
            snapshot.RootVolumeGrowth = 1.0f;
        }
    }

    private void EnterMutationLock(out long holdStart)
    {
        long waitStart = Stopwatch.GetTimestamp();
        Monitor.Enter(_mutationSyncRoot);
        Interlocked.Add(ref _mutationLockWaitTicks, Stopwatch.GetTimestamp() - waitStart);
        holdStart = Stopwatch.GetTimestamp();
    }

    private void ExitMutationLock(long holdStart)
    {
        Interlocked.Add(ref _mutationLockHoldTicks, Stopwatch.GetTimestamp() - holdStart);
        Monitor.Exit(_mutationSyncRoot);
    }

    private sealed class Snapshot(CpuBvhRenderTree<T> owner)
    {
        private int _markGeneration;
        private int[] _dirtyMarks = [];
        private int[] _ancestorMarks = [];
        private int[] _depthCounts = [];
        private int[] _depthOffsets = [];

        public int ReaderCount;
        public long Generation;
        public long TopologyRevision;
        public long BoundsRevision;
        public long PayloadRevision;
        public int JournalCursor;
        public FlatNode[] Nodes = [];
        public Entry[] Entries = [];
        public Entry[] ScratchEntries = [];
        public T[] UnboundedItems = [];
        public int[] EntryByStableId = [];
        public int[] LeafByStableId = [];
        public BuildTask[] BuildTasks = [];
        public SahBin[] Bins = new SahBin[owner._options.SahBinCount];
        public int[] PrefixCounts = new int[owner._options.SahBinCount];
        public AABB[] PrefixBounds = new AABB[owner._options.SahBinCount];
        public SnapshotNode[] NodeHandles = [];
        public int[] DirtyLeaves = [];
        public int[] DirtyAncestors = [];
        public int[] OrderedDirtyAncestors = [];
        public int RootIndex = -1;
        public int NodeCount;
        public int EntryCount;
        public int UnboundedCount;
        public int DirtyLeafCount;
        public int DirtyAncestorCount;
        public int LeafCount;
        public int MaxDepth;
        public int MaxLeafOccupancy;
        public int ConsecutiveRefitCount;
        public int LastDirtyCount;
        public int LastRotationCount;
        public float AverageDepth;
        public float AverageLeafOccupancy;
        public float NormalizedSahCost;
        public float BaselineNormalizedSahCost;
        public float AverageSiblingOverlap;
        public float BaselineRootVolume;
        public float RootVolumeGrowth = 1.0f;

        public void ResetForBuild(int itemCapacity, int stableIdCapacity)
        {
            EnsureEntryCapacity(itemCapacity);
            EnsureMapCapacity(stableIdCapacity);
            Array.Fill(EntryByStableId, -1);
            Array.Fill(LeafByStableId, -1);
            NodeCount = 0;
            EntryCount = 0;
            UnboundedCount = 0;
            RootIndex = -1;
        }

        public void EnsureNodeCapacity(int requested)
        {
            int capacity = GrowCapacity(Nodes.Length, Math.Max(1, requested));
            if (capacity != Nodes.Length)
            {
                Array.Resize(ref Nodes, capacity);
                Array.Resize(ref BuildTasks, capacity);
                Array.Resize(ref DirtyLeaves, capacity);
                Array.Resize(ref DirtyAncestors, capacity);
                Array.Resize(ref OrderedDirtyAncestors, capacity);
                Array.Resize(ref _dirtyMarks, capacity);
                Array.Resize(ref _ancestorMarks, capacity);
            }

            if (NodeHandles.Length < capacity)
            {
                int oldLength = NodeHandles.Length;
                Array.Resize(ref NodeHandles, capacity);
                for (int i = oldLength; i < capacity; i++)
                    NodeHandles[i] = new SnapshotNode(this, i);
            }
        }

        private void EnsureEntryCapacity(int requested)
        {
            int capacity = GrowCapacity(Entries.Length, Math.Max(1, requested));
            if (capacity == Entries.Length)
                return;
            Array.Resize(ref Entries, capacity);
            Array.Resize(ref ScratchEntries, capacity);
            Array.Resize(ref UnboundedItems, capacity);
        }

        private void EnsureMapCapacity(int requested)
        {
            int capacity = GrowCapacity(EntryByStableId.Length, Math.Max(1, requested));
            if (capacity == EntryByStableId.Length)
                return;
            Array.Resize(ref EntryByStableId, capacity);
            Array.Resize(ref LeafByStableId, capacity);
        }

        public void ConfigureNodeHandles()
        {
            for (int i = 0; i < NodeCount; i++)
                NodeHandles[i].Configure(i, Nodes[i].Bounds, Nodes[i].Depth);
        }

        public void CloneFrom(Snapshot source)
        {
            EnsureEntryCapacity(source.EntryCount + source.UnboundedCount);
            EnsureMapCapacity(source.EntryByStableId.Length);
            EnsureNodeCapacity(source.NodeCount);
            Array.Copy(source.Nodes, Nodes, source.NodeCount);
            Array.Copy(source.Entries, Entries, source.EntryCount);
            Array.Copy(source.UnboundedItems, UnboundedItems, source.UnboundedCount);
            Array.Copy(source.EntryByStableId, EntryByStableId, source.EntryByStableId.Length);
            Array.Copy(source.LeafByStableId, LeafByStableId, source.LeafByStableId.Length);
            RootIndex = source.RootIndex;
            NodeCount = source.NodeCount;
            EntryCount = source.EntryCount;
            UnboundedCount = source.UnboundedCount;
            TopologyRevision = source.TopologyRevision;
            BoundsRevision = source.BoundsRevision;
            PayloadRevision = source.PayloadRevision;
            JournalCursor = source.JournalCursor;
            LeafCount = source.LeafCount;
            MaxDepth = source.MaxDepth;
            MaxLeafOccupancy = source.MaxLeafOccupancy;
            ConsecutiveRefitCount = source.ConsecutiveRefitCount;
            AverageDepth = source.AverageDepth;
            AverageLeafOccupancy = source.AverageLeafOccupancy;
            NormalizedSahCost = source.NormalizedSahCost;
            BaselineNormalizedSahCost = source.BaselineNormalizedSahCost;
            AverageSiblingOverlap = source.AverageSiblingOverlap;
            BaselineRootVolume = source.BaselineRootVolume;
            RootVolumeGrowth = source.RootVolumeGrowth;
            ConfigureNodeHandles();
        }

        public void BeginRefit()
        {
            if (++_markGeneration == int.MaxValue)
            {
                Array.Clear(_dirtyMarks);
                Array.Clear(_ancestorMarks);
                _markGeneration = 1;
            }
            DirtyLeafCount = 0;
            DirtyAncestorCount = 0;
            LastRotationCount = 0;
        }

        public void MarkDirtyLeaf(int nodeIndex)
        {
            if (_dirtyMarks[nodeIndex] == _markGeneration)
                return;
            _dirtyMarks[nodeIndex] = _markGeneration;
            DirtyLeaves[DirtyLeafCount++] = nodeIndex;
        }

        public void MarkDirtyAncestor(int nodeIndex)
        {
            if (_ancestorMarks[nodeIndex] == _markGeneration)
                return;
            _ancestorMarks[nodeIndex] = _markGeneration;
            DirtyAncestors[DirtyAncestorCount++] = nodeIndex;
        }

        public void OrderDirtyAncestorsByDepth()
        {
            int depthCapacity = Math.Max(MaxDepth + 2, 2);
            if (_depthCounts.Length < depthCapacity)
            {
                Array.Resize(ref _depthCounts, depthCapacity);
                Array.Resize(ref _depthOffsets, depthCapacity);
            }
            Array.Clear(_depthCounts, 0, depthCapacity);
            for (int i = 0; i < DirtyAncestorCount; i++)
                _depthCounts[Nodes[DirtyAncestors[i]].Depth]++;
            int offset = 0;
            for (int depth = MaxDepth; depth >= 0; depth--)
            {
                _depthOffsets[depth] = offset;
                offset += _depthCounts[depth];
            }
            for (int i = 0; i < DirtyAncestorCount; i++)
            {
                int node = DirtyAncestors[i];
                int depth = Nodes[node].Depth;
                OrderedDirtyAncestors[_depthOffsets[depth]++] = node;
            }
        }

        public void RecomputeDepths(int root)
        {
            int count = 0;
            DirtyLeaves[count++] = root;
            int rootDepth = Nodes[root].Parent < 0 ? 0 : Nodes[Nodes[root].Parent].Depth + 1;
            Nodes[root].Depth = rootDepth;
            while (count > 0)
            {
                int nodeIndex = DirtyLeaves[--count];
                ref readonly FlatNode node = ref Nodes[nodeIndex];
                NodeHandles[nodeIndex].Configure(nodeIndex, node.Bounds, node.Depth);
                if (node.IsLeaf)
                    continue;
                Nodes[node.Left].Depth = node.Depth + 1;
                Nodes[node.Right].Depth = node.Depth + 1;
                DirtyLeaves[count++] = node.Left;
                DirtyLeaves[count++] = node.Right;
            }
        }

        public void UpdateRootQuality()
        {
            if (RootIndex < 0)
            {
                NormalizedSahCost = 0.0f;
                RootVolumeGrowth = 1.0f;
                return;
            }
            ref readonly FlatNode root = ref Nodes[RootIndex];
            NormalizedSahCost = root.Cost / Math.Max(SurfaceArea(root.Bounds), 1e-6f);
            float volume = root.Bounds.Volume;
            RootVolumeGrowth = BaselineRootVolume <= 1e-12f
                ? volume <= 1e-12f ? 1.0f : float.PositiveInfinity
                : volume / BaselineRootVolume;
        }

        private static int GrowCapacity(int current, int requested)
        {
            int capacity = Math.Max(current, 4);
            while (capacity < requested)
                capacity = checked(capacity * 2);
            return capacity;
        }
    }

    private sealed class ItemRecord(
        CpuBvhRenderTree<T> owner,
        T item,
        int stableId,
        int itemIndex,
        AABB initialBounds)
    {
        public T Item { get; } = item;
        public int StableId { get; } = stableId;
        public ItemNode Handle { get; } = new(owner, item, initialBounds);
        public int ItemIndex { get; set; } = itemIndex;
        public long LastBoundsRevision { get; set; }
        public bool IsBounded { get; set; }
        public AABB Bounds { get; set; } = initialBounds;
    }

    private sealed class ItemNode(CpuBvhRenderTree<T> owner, T item, AABB bounds)
        : OctreeNodeBase(bounds, 0, 0)
    {
        public override OctreeNodeBase? GenericParent => null;
        public void UpdateBounds(AABB bounds) => _bounds = bounds;
        protected override OctreeNodeBase? GetNodeInternal(int index) => null;
        public override void QueueItemMoved(IOctreeItem movedItem)
        {
            if (ReferenceEquals(movedItem, item))
                owner.Move(item);
        }
        public override void HandleMovedItem(IOctreeItem movedItem) => QueueItemMoved(movedItem);
        public override bool Remove(IOctreeItem removedItem, out bool destroyNode)
        {
            destroyNode = false;
            if (!ReferenceEquals(removedItem, item))
                return false;
            owner.Remove(item);
            return true;
        }
        protected override void RemoveNodeAt(int subDivIndex) { }
    }

    private sealed class UnboundedNode(CpuBvhRenderTree<T> owner, AABB bounds)
        : OctreeNodeBase(bounds, 0, 0)
    {
        public override OctreeNodeBase? GenericParent => null;
        public void UpdateBounds(AABB bounds) => _bounds = bounds;
        protected override OctreeNodeBase? GetNodeInternal(int index) => null;
        public override void QueueItemMoved(IOctreeItem item)
        {
            if (item is T typed)
                owner.Move(typed);
        }
        public override void HandleMovedItem(IOctreeItem item) => QueueItemMoved(item);
        public override bool Remove(IOctreeItem item, out bool destroyNode)
        {
            destroyNode = false;
            if (item is not T typed)
                return false;
            owner.Remove(typed);
            return true;
        }
        protected override void RemoveNodeAt(int subDivIndex) { }
    }

    private sealed class SnapshotNode(Snapshot snapshot, int nodeIndex)
        : OctreeNodeBase(default, 0, 0)
    {
        private int _nodeIndex = nodeIndex;
        public override OctreeNodeBase? GenericParent
        {
            get
            {
                int parent = snapshot.Nodes[_nodeIndex].Parent;
                return parent < 0 ? null : snapshot.NodeHandles[parent];
            }
        }
        public void Configure(int index, AABB bounds, int depth)
        {
            _nodeIndex = index;
            _bounds = bounds;
            _subDivLevel = depth;
        }
        public void UpdateBounds(AABB bounds) => _bounds = bounds;
        protected override OctreeNodeBase? GetNodeInternal(int index)
        {
            ref readonly FlatNode node = ref snapshot.Nodes[_nodeIndex];
            if (node.IsLeaf)
                return null;
            return index switch
            {
                0 => snapshot.NodeHandles[node.Left],
                1 => snapshot.NodeHandles[node.Right],
                _ => null,
            };
        }
        public override void HandleMovedItem(IOctreeItem item) { }
        public override bool Remove(IOctreeItem item, out bool destroyNode)
        {
            destroyNode = false;
            return false;
        }
        protected override void RemoveNodeAt(int subDivIndex) { }
    }

    private struct FlatNode(AABB bounds, int parent, int depth, int first, int count)
    {
        public AABB Bounds = bounds;
        public int Parent = parent;
        public int Left = -1;
        public int Right = -1;
        public int First = first;
        public int Count = count;
        public int Depth = depth;
        public int PrimitiveCount = count;
        public float Cost;
        public float Overlap;
        public readonly bool IsLeaf => Count > 0;
    }

    private struct Entry(ItemRecord record, AABB bounds, Vector3 center)
    {
        public ItemRecord Record = record;
        public AABB Bounds = bounds;
        public Vector3 Center = center;
    }

    private struct SahBin
    {
        public AABB Bounds;
        public int Count;
    }

    private readonly record struct BoundsMutation(ItemRecord Record, long Revision);
    private readonly record struct BuildTask(int First, int Count, int Parent, int Depth, int NodeIndex);
    private readonly record struct TraversalEntry(int NodeIndex, byte PlaneMask);
    private readonly record struct RayTraversalEntry(int NodeIndex, float Distance);
    private readonly record struct RaycastCommand(
        Segment Segment,
        SortedDictionary<float, List<(T item, object? data)>> Items,
        Func<T, Segment, (float? distance, object? data)> DirectTest,
        Action<SortedDictionary<float, List<(T item, object? data)>>> FinishedCallback);

    private readonly record struct Rotation(
        RotationKind Kind,
        int InternalNode,
        int Promoted,
        int Retained,
        int Sibling,
        int Parent);

    private enum RotationKind
    {
        None,
        PromoteLeftFromLeft,
        PromoteRightFromLeft,
        PromoteLeftFromRight,
        PromoteRightFromRight,
    }

    private struct BufferedSwapCommand
    {
        public BufferedSwapCommand(T item, bool initiallyPresent, TreeCommand command)
        {
            Item = item;
            InitiallyPresent = initiallyPresent;
            Present = initiallyPresent;
            Moved = false;
            StructuralChange = false;
            Apply(command);
        }
        public readonly T Item;
        public readonly bool InitiallyPresent;
        public bool Present;
        public bool Moved;
        public bool StructuralChange;
        public void Apply(TreeCommand command)
        {
            switch (command)
            {
                case TreeCommand.Add:
                    if (!Present)
                    {
                        Present = true;
                        StructuralChange = true;
                    }
                    break;
                case TreeCommand.Move:
                    if (Present)
                        Moved = true;
                    break;
                case TreeCommand.Remove:
                    if (Present)
                    {
                        Present = false;
                        Moved = false;
                        StructuralChange = true;
                    }
                    break;
            }
        }
        public readonly TreeCommand ToCommand()
        {
            if (InitiallyPresent)
            {
                if (!Present)
                    return TreeCommand.Remove;
                return Moved || StructuralChange ? TreeCommand.Move : TreeCommand.None;
            }
            return Present ? TreeCommand.Add : TreeCommand.None;
        }
    }

    private record struct SwapExecutionSummary(
        int DrainedCommandCount,
        int ExecutedCommandCount,
        long DrainTicks,
        long ExecuteTicks,
        long MaxCommandTicks,
        EOctreeCommandKind MaxCommandKind);

    private enum TreeCommand
    {
        None,
        Add,
        Move,
        Remove,
    }

    private static EOctreeCommandKind ToStatsCommandKind(TreeCommand command)
        => command switch
        {
            TreeCommand.Add => EOctreeCommandKind.Add,
            TreeCommand.Move => EOctreeCommandKind.Move,
            TreeCommand.Remove => EOctreeCommandKind.Remove,
            _ => EOctreeCommandKind.None,
        };

    private sealed class EntryComparer(int axis) : IComparer<Entry>
    {
        private static readonly EntryComparer[] Comparers = [new(0), new(1), new(2)];
        public static EntryComparer ForAxis(int axis) => Comparers[axis];
        public int Compare(Entry left, Entry right)
        {
            int result = left.Center[axis].CompareTo(right.Center[axis]);
            return result != 0 ? result : left.Record.StableId.CompareTo(right.Record.StableId);
        }
    }
}
