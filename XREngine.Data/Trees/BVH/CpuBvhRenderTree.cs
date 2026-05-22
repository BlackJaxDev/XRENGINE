using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Data.Trees;

public sealed class CpuBvhRenderTree<T> : I3DRenderTree<T> where T : class, IOctreeItem
{
    private const int LeafCapacity = 8;
    private const int MaxRaycastCommands = 10;
    private const long MaxRaycastTicksPerFrame = 3 * TimeSpan.TicksPerMillisecond;
    private static int _raycastEnqueueDebugBudget = 20;

    private readonly ConcurrentQueue<(T item, TreeCommand command)> _swapCommands = new();
    private readonly ConcurrentQueue<RaycastCommand> _raycastCommands = new();
    private readonly List<T> _items = [];
    private readonly Dictionary<T, int> _itemIndices = new(ReferenceEqualityComparer.Instance);
    private readonly List<BufferedSwapCommand> _swapCommandBuffer = [];
    private readonly Dictionary<T, int> _swapCommandIndices = new(ReferenceEqualityComparer.Instance);
    private readonly List<BvhEntry> _entries = [];
    private readonly List<T> _unboundedItems = [];
    private AABB _bounds;
    private BvhNode? _root;
    private BvhNode _unboundedNode;
    private bool _dirty = true;

    public CpuBvhRenderTree(AABB bounds)
    {
        _bounds = bounds;
        _unboundedNode = new BvhNode(this, bounds, 0, 0, null);
    }

    public CpuBvhRenderTree(AABB bounds, List<T> items) : this(bounds)
    {
        for (int i = 0; i < items.Count; i++)
            AddImmediate(items[i]);

        Rebuild();
    }

    public void Remake()
    {
        _dirty = true;
    }

    public void Remake(AABB newBounds)
    {
        _bounds = newBounds;
        _unboundedNode = new BvhNode(this, newBounds, 0, 0, null);
        _dirty = true;
    }

    public void Swap()
    {
        if (IRenderTree.ProfilingHook is not null)
        {
            using var sample = IRenderTree.ProfilingHook("CpuBvhRenderTree.Swap");
            SwapInternal();
        }
        else
            SwapInternal();
    }

    private void SwapInternal()
    {
        SwapExecutionSummary swapSummary = ConsumeSwapCommands();
        bool rebuilt = false;
        if (_dirty)
        {
            long rebuildStart = Stopwatch.GetTimestamp();
            Rebuild();
            long rebuildTicks = Stopwatch.GetTimestamp() - rebuildStart;
            rebuilt = true;
            swapSummary = new SwapExecutionSummary(
                swapSummary.DrainedCommandCount,
                swapSummary.ExecutedCommandCount,
                swapSummary.DrainTicks,
                swapSummary.ExecuteTicks + rebuildTicks,
                Math.Max(swapSummary.MaxCommandTicks, rebuildTicks),
                swapSummary.MaxCommandKind);
        }

        if (rebuilt || swapSummary.DrainedCommandCount > 0)
        {
            IRenderTree.OctreeSwapTimingHook?.Invoke(new OctreeSwapTimingStats(
                swapSummary.DrainedCommandCount,
                swapSummary.ExecutedCommandCount,
                swapSummary.ExecutedCommandCount,
                swapSummary.DrainTicks,
                swapSummary.ExecuteTicks,
                swapSummary.MaxCommandTicks,
                swapSummary.MaxCommandKind));
        }

        ConsumeRaycastCommands();
    }

    public void Add(T value)
    {
        if (value is not null)
            _swapCommands.Enqueue((value, TreeCommand.Add));
    }

    public void Remove(T value)
    {
        if (value is not null)
            _swapCommands.Enqueue((value, TreeCommand.Remove));
    }

    public void Move(T item)
    {
        if (item is not null)
            _swapCommands.Enqueue((item, TreeCommand.Move));
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
        EnsureBuilt();

        for (int i = 0; i < _unboundedItems.Count; i++)
        {
            T item = _unboundedItems[i];
            if (item.ShouldRender)
                action(item);
        }

        if (_root is not null)
            CollectAll(_root, action);
    }

    void I3DRenderTree.CollectAll(Action<IOctreeItem> action)
    {
        EnsureBuilt();

        for (int i = 0; i < _unboundedItems.Count; i++)
        {
            T item = _unboundedItems[i];
            if (item.ShouldRender)
                action(item);
        }

        if (_root is not null)
            CollectAll(_root, action);
    }

    public void CollectVisible(
        IVolume? volume,
        bool onlyContainingItems,
        Action<T> action,
        OctreeNode<T>.DelIntersectionTest intersectionTest)
    {
        EnsureBuilt();

        for (int i = 0; i < _unboundedItems.Count; i++)
        {
            T item = _unboundedItems[i];
            if (item.ShouldRender && intersectionTest(item, volume, onlyContainingItems))
                action(item);
        }

        if (_root is not null)
            CollectVisible(_root, volume, onlyContainingItems, action, intersectionTest);
    }

    void I3DRenderTree.CollectVisible(
        IVolume? volume,
        bool onlyContainingItems,
        Action<IOctreeItem> action,
        OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
    {
        EnsureBuilt();

        for (int i = 0; i < _unboundedItems.Count; i++)
        {
            T item = _unboundedItems[i];
            if (item.ShouldRender && intersectionTest(item, volume, onlyContainingItems))
                action(item);
        }

        if (_root is not null)
            CollectVisible(_root, volume, onlyContainingItems, action, intersectionTest);
    }

    public void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
    {
        EnsureBuilt();

        if (_unboundedItems.Count > 0)
            CollectUnboundedNode(cullingVolume, containsOnly, action);

        if (_root is not null)
            CollectVisibleNodes(_root, cullingVolume, containsOnly, action);
        else if (_unboundedItems.Count == 0)
            CollectUnboundedNode(cullingVolume, containsOnly, action);
    }

    public void DebugRender(IVolume? cullingVolume, DelRenderAABB render, bool onlyContainingItems = false)
    {
        EnsureBuilt();

        if (_unboundedItems.Count > 0)
            DebugRenderUnboundedNode(cullingVolume, render);

        if (_root is not null)
            DebugRender(_root, cullingVolume, render, onlyContainingItems);
        else if (_unboundedItems.Count == 0 && !onlyContainingItems)
            DebugRenderUnboundedNode(cullingVolume, render);
    }

    public SpatialTreeOccupancyStats GetOccupancyStats()
    {
        EnsureBuilt();

        int nodeCount = _root is null ? 1 : 0;
        int maxNodeItemCount = _unboundedItems.Count;
        int maxDepth = 0;

        if (_root is not null)
            CollectStats(_root, ref nodeCount, ref maxNodeItemCount, ref maxDepth);

        return new SpatialTreeOccupancyStats(
            nodeCount,
            _items.Count,
            _unboundedItems.Count,
            maxNodeItemCount,
            maxDepth,
            _unboundedItems.Count);
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

        EnsureBuilt();
        RaycastInternal(segment, items, directTest);
    }

    public T? FindFirst(Predicate<T> itemTester, Predicate<AABB> bvhNodeTester)
    {
        ArgumentNullException.ThrowIfNull(itemTester);
        ArgumentNullException.ThrowIfNull(bvhNodeTester);

        EnsureBuilt();
        if (bvhNodeTester(_unboundedNode.Bounds))
        {
            for (int i = 0; i < _unboundedItems.Count; i++)
            {
                T item = _unboundedItems[i];
                if (item.ShouldRender && itemTester(item))
                    return item;
            }
        }

        return _root is null ? null : FindFirst(_root, itemTester, bvhNodeTester);
    }

    public List<T> FindAll(Predicate<T> itemTester, Predicate<AABB> bvhNodeTester)
    {
        ArgumentNullException.ThrowIfNull(itemTester);
        ArgumentNullException.ThrowIfNull(bvhNodeTester);

        List<T> list = [];
        EnsureBuilt();
        if (bvhNodeTester(_unboundedNode.Bounds))
        {
            for (int i = 0; i < _unboundedItems.Count; i++)
            {
                T item = _unboundedItems[i];
                if (item.ShouldRender && itemTester(item))
                    list.Add(item);
            }
        }

        if (_root is not null)
            FindAll(_root, itemTester, bvhNodeTester, list);

        return list;
    }

    private SwapExecutionSummary ConsumeSwapCommands()
    {
        if (_swapCommands.IsEmpty)
            return new SwapExecutionSummary(0, 0, 0L, 0L, 0L, EOctreeCommandKind.None);

        if (IRenderTree.ProfilingHook is not null)
        {
            using var sample = IRenderTree.ProfilingHook("CpuBvhRenderTree.ConsumeSwapCommands");
            return ConsumeSwapCommandsInternal();
        }

        return ConsumeSwapCommandsInternal();
    }

    private SwapExecutionSummary ConsumeSwapCommandsInternal()
    {
        long drainStart = Stopwatch.GetTimestamp();
        int drained = DrainSwapCommands();
        long drainTicks = Stopwatch.GetTimestamp() - drainStart;

        int adds = 0;
        int moves = 0;
        int removes = 0;
        int executed = 0;
        long executeStart = Stopwatch.GetTimestamp();
        long maxCommandTicks = 0L;
        EOctreeCommandKind maxCommandKind = EOctreeCommandKind.None;

        for (int i = 0; i < _swapCommandBuffer.Count; i++)
        {
            BufferedSwapCommand bufferedCommand = _swapCommandBuffer[i];
            TreeCommand command = bufferedCommand.ToCommand();
            if (command == TreeCommand.None)
                continue;

            bool commandExecuted = false;
            long commandStart = Stopwatch.GetTimestamp();

            switch (command)
            {
                case TreeCommand.Add:
                    if (AddImmediate(bufferedCommand.Item))
                    {
                        adds++;
                        commandExecuted = true;
                    }
                    break;
                case TreeCommand.Remove:
                    if (RemoveImmediate(bufferedCommand.Item))
                    {
                        removes++;
                        commandExecuted = true;
                    }
                    break;
                case TreeCommand.Move:
                    if (_itemIndices.ContainsKey(bufferedCommand.Item))
                    {
                        _dirty = true;
                        moves++;
                        commandExecuted = true;
                    }
                    break;
            }

            if (!commandExecuted)
                continue;

            executed++;
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
        while (_swapCommands.TryDequeue(out (T item, TreeCommand command) command))
        {
            drained++;
            T item = command.item;
            if (!_swapCommandIndices.TryGetValue(item, out int index))
            {
                _swapCommandIndices[item] = _swapCommandBuffer.Count;
                _swapCommandBuffer.Add(new BufferedSwapCommand(item, _itemIndices.ContainsKey(item), command.command));
                continue;
            }

            BufferedSwapCommand bufferedCommand = _swapCommandBuffer[index];
            bufferedCommand.Apply(command.command);
            _swapCommandBuffer[index] = bufferedCommand;
        }

        return drained;
    }

    private bool AddImmediate(T item)
    {
        if (_itemIndices.ContainsKey(item))
            return false;

        _itemIndices[item] = _items.Count;
        _items.Add(item);
        item.OctreeNode = _unboundedNode;
        _dirty = true;
        return true;
    }

    private bool RemoveImmediate(T item)
    {
        if (!_itemIndices.TryGetValue(item, out int index))
            return false;

        int lastIndex = _items.Count - 1;
        if (index != lastIndex)
        {
            T lastItem = _items[lastIndex];
            _items[index] = lastItem;
            _itemIndices[lastItem] = index;
        }

        _items.RemoveAt(lastIndex);
        _itemIndices.Remove(item);
        item.OctreeNode = null;
        _dirty = true;
        return true;
    }

    private void Rebuild()
    {
        _entries.Clear();
        _unboundedItems.Clear();

        for (int i = 0; i < _items.Count; i++)
        {
            T item = _items[i];
            if (item.WorldCullingVolume is Box bounds)
            {
                AABB aabb = bounds.GetAABB(true);
                _entries.Add(new BvhEntry(item, aabb, aabb.Center, i));
            }
            else
            {
                _unboundedItems.Add(item);
                item.OctreeNode = _unboundedNode;
            }
        }

        _root = _entries.Count == 0
            ? null
            : BuildNode(0, _entries.Count, 0, null, 0);

        _dirty = false;
    }

    private BvhNode BuildNode(int start, int count, int depth, BvhNode? parent, int childIndex)
    {
        AABB bounds = _entries[start].Bounds;
        for (int i = start + 1; i < start + count; i++)
            bounds.ExpandToInclude(_entries[i].Bounds);

        BvhNode node = new(this, bounds, childIndex, depth, parent)
        {
            Start = start,
            Count = count,
        };

        if (count <= LeafCapacity)
        {
            for (int i = start; i < start + count; i++)
                _entries[i].Item.OctreeNode = node;

            return node;
        }

        Vector3 size = bounds.Size;
        IComparer<BvhEntry> comparer = size.X >= size.Y && size.X >= size.Z
            ? BvhEntryComparer.X
            : size.Y >= size.Z
                ? BvhEntryComparer.Y
                : BvhEntryComparer.Z;

        _entries.Sort(start, count, comparer);

        int leftCount = count / 2;
        node.Left = BuildNode(start, leftCount, depth + 1, node, 0);
        node.Right = BuildNode(start + leftCount, count - leftCount, depth + 1, node, 1);
        return node;
    }

    private void EnsureBuilt()
    {
        if (_dirty)
            Rebuild();
    }

    private static void CollectAll(BvhNode node, Action<T> action)
    {
        if (node.IsLeaf)
        {
            CpuBvhRenderTree<T> owner = node.Owner;
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
            {
                T item = owner._entries[i].Item;
                if (item.ShouldRender)
                    action(item);
            }

            return;
        }

        if (node.Left is not null)
            CollectAll(node.Left, action);
        if (node.Right is not null)
            CollectAll(node.Right, action);
    }

    private static void CollectAll(BvhNode node, Action<IOctreeItem> action)
    {
        if (node.IsLeaf)
        {
            CpuBvhRenderTree<T> owner = node.Owner;
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
            {
                T item = owner._entries[i].Item;
                if (item.ShouldRender)
                    action(item);
            }

            return;
        }

        if (node.Left is not null)
            CollectAll(node.Left, action);
        if (node.Right is not null)
            CollectAll(node.Right, action);
    }

    private static void CollectVisible(
        BvhNode node,
        IVolume? volume,
        bool onlyContainingItems,
        Action<T> action,
        OctreeNode<T>.DelIntersectionTest intersectionTest)
    {
        EContainment containment = ClassifyNode(volume, node.Bounds);
        if (containment == EContainment.Disjoint)
            return;

        if (containment == EContainment.Contains)
        {
            CollectAll(node, action);
            return;
        }

        if (node.IsLeaf)
        {
            CpuBvhRenderTree<T> owner = node.Owner;
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
            {
                T item = owner._entries[i].Item;
                if (item.ShouldRender && intersectionTest(item, volume, onlyContainingItems))
                    action(item);
            }

            return;
        }

        if (node.Left is not null)
            CollectVisible(node.Left, volume, onlyContainingItems, action, intersectionTest);
        if (node.Right is not null)
            CollectVisible(node.Right, volume, onlyContainingItems, action, intersectionTest);
    }

    private static void CollectVisible(
        BvhNode node,
        IVolume? volume,
        bool onlyContainingItems,
        Action<IOctreeItem> action,
        OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
    {
        EContainment containment = ClassifyNode(volume, node.Bounds);
        if (containment == EContainment.Disjoint)
            return;

        if (containment == EContainment.Contains)
        {
            CollectAll(node, action);
            return;
        }

        if (node.IsLeaf)
        {
            CpuBvhRenderTree<T> owner = node.Owner;
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
            {
                T item = owner._entries[i].Item;
                if (item.ShouldRender && intersectionTest(item, volume, onlyContainingItems))
                    action(item);
            }

            return;
        }

        if (node.Left is not null)
            CollectVisible(node.Left, volume, onlyContainingItems, action, intersectionTest);
        if (node.Right is not null)
            CollectVisible(node.Right, volume, onlyContainingItems, action, intersectionTest);
    }

    private static void CollectVisibleNodes(
        BvhNode node,
        IVolume? volume,
        bool containsOnly,
        Action<(OctreeNodeBase node, bool intersects)> action)
    {
        EContainment containment = ClassifyNode(volume, node.Bounds);
        if (containment == EContainment.Disjoint)
            return;

        bool intersects = containment == EContainment.Intersects;
        if (!containsOnly || !intersects)
            action((node, intersects));

        if (node.Left is not null)
            CollectVisibleNodes(node.Left, volume, containsOnly, action);
        if (node.Right is not null)
            CollectVisibleNodes(node.Right, volume, containsOnly, action);
    }

    private void CollectUnboundedNode(
        IVolume? volume,
        bool containsOnly,
        Action<(OctreeNodeBase node, bool intersects)> action)
    {
        EContainment containment = ClassifyNode(volume, _unboundedNode.Bounds);
        if (containment == EContainment.Disjoint)
            return;

        bool intersects = containment == EContainment.Intersects;
        if (!containsOnly || !intersects)
            action((_unboundedNode, intersects));
    }

    private static void DebugRender(BvhNode node, IVolume? volume, DelRenderAABB render, bool onlyContainingItems)
    {
        EContainment containment = ClassifyNode(volume, node.Bounds);
        if (containment == EContainment.Disjoint)
            return;

        if (!onlyContainingItems || node.IsLeaf)
        {
            Color color = containment == EContainment.Intersects ? Color.Green : Color.White;
            render(node.Bounds.HalfExtents, node.Bounds.Center, color);
        }

        if (node.Left is not null)
            DebugRender(node.Left, volume, render, onlyContainingItems);
        if (node.Right is not null)
            DebugRender(node.Right, volume, render, onlyContainingItems);
    }

    private void DebugRenderUnboundedNode(IVolume? volume, DelRenderAABB render)
    {
        EContainment containment = ClassifyNode(volume, _unboundedNode.Bounds);
        if (containment == EContainment.Disjoint)
            return;

        Color color = containment == EContainment.Intersects ? Color.Green : Color.White;
        render(_unboundedNode.Bounds.HalfExtents, _unboundedNode.Bounds.Center, color);
    }

    private static EContainment ClassifyNode(IVolume? volume, AABB bounds)
    {
        if (volume is null)
            return EContainment.Contains;

        EContainment containment = volume.ContainsAABB(bounds);
        if (containment == EContainment.Disjoint)
        {
            if (volume is AABB aabb && aabb.Intersects(bounds))
                return EContainment.Intersects;

            if (volume is Frustum frustum && frustum.Intersects(bounds))
                return EContainment.Intersects;
        }

        return containment;
    }

    private static void CollectStats(BvhNode node, ref int nodeCount, ref int maxNodeItemCount, ref int maxDepth)
    {
        nodeCount++;

        if (node.SubDivLevel > maxDepth)
            maxDepth = node.SubDivLevel;

        if (node.IsLeaf && node.Count > maxNodeItemCount)
            maxNodeItemCount = node.Count;

        if (node.Left is not null)
            CollectStats(node.Left, ref nodeCount, ref maxNodeItemCount, ref maxDepth);
        if (node.Right is not null)
            CollectStats(node.Right, ref nodeCount, ref maxNodeItemCount, ref maxDepth);
    }

    private void ConsumeRaycastCommands()
    {
        if (_raycastCommands.IsEmpty)
            return;

        if (IRenderTree.ProfilingHook is not null)
        {
            using var sample = IRenderTree.ProfilingHook("CpuBvhRenderTree.ConsumeRaycastCommands");
            ConsumeRaycastCommandsInternal();
        }
        else
            ConsumeRaycastCommandsInternal();
    }

    private void ConsumeRaycastCommandsInternal()
    {
        long startTicks = DateTime.UtcNow.Ticks;
        int processedCommandCount = 0;
        int droppedCommandCount = 0;
        long traversalTicks = 0L;
        long callbackTicks = 0L;
        long maxTraversalTicks = 0L;
        long maxCallbackTicks = 0L;
        long maxCommandTicks = 0L;

        while (_raycastCommands.TryDequeue(out RaycastCommand command))
        {
            long commandStart = Stopwatch.GetTimestamp();
            RaycastInternal(command.Segment, command.Items, command.DirectTest);
            long traversalElapsedTicks = Stopwatch.GetTimestamp() - commandStart;

            long callbackStart = Stopwatch.GetTimestamp();
            command.FinishedCallback(command.Items);
            long callbackElapsedTicks = Stopwatch.GetTimestamp() - callbackStart;

            processedCommandCount++;
            traversalTicks += traversalElapsedTicks;
            callbackTicks += callbackElapsedTicks;

            if (traversalElapsedTicks > maxTraversalTicks)
                maxTraversalTicks = traversalElapsedTicks;

            if (callbackElapsedTicks > maxCallbackTicks)
                maxCallbackTicks = callbackElapsedTicks;

            long commandElapsedTicks = Stopwatch.GetTimestamp() - commandStart;
            if (commandElapsedTicks > maxCommandTicks)
                maxCommandTicks = commandElapsedTicks;

            if (DateTime.UtcNow.Ticks - startTicks > MaxRaycastTicksPerFrame)
            {
                while (_raycastCommands.TryDequeue(out _))
                    droppedCommandCount++;
                break;
            }
        }

        if (processedCommandCount > 0 || droppedCommandCount > 0)
        {
            IRenderTree.OctreeRaycastTimingHook?.Invoke(new OctreeRaycastTimingStats(
                processedCommandCount,
                droppedCommandCount,
                traversalTicks,
                callbackTicks,
                maxTraversalTicks,
                maxCallbackTicks,
                maxCommandTicks));
        }
    }

    private void RaycastInternal(
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest)
    {
        for (int i = 0; i < _unboundedItems.Count; i++)
            RaycastItem(_unboundedItems[i], segment, items, directTest);

        if (_root is not null)
            RaycastNode(_root, segment, items, directTest);
    }

    private void RaycastNode(
        BvhNode node,
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest)
    {
        if (!node.Bounds.IntersectsSegment(segment))
            return;

        if (node.IsLeaf)
        {
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
                RaycastItem(_entries[i].Item, segment, items, directTest);

            return;
        }

        if (node.Left is not null)
            RaycastNode(node.Left, segment, items, directTest);
        if (node.Right is not null)
            RaycastNode(node.Right, segment, items, directTest);
    }

    private static T? FindFirst(BvhNode node, Predicate<T> itemTester, Predicate<AABB> bvhNodeTester)
    {
        if (!bvhNodeTester(node.Bounds))
            return null;

        CpuBvhRenderTree<T> owner = node.Owner;
        if (node.IsLeaf)
        {
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
            {
                T item = owner._entries[i].Item;
                if (item.ShouldRender && itemTester(item))
                    return item;
            }

            return null;
        }

        if (node.Left is not null)
        {
            T? item = FindFirst(node.Left, itemTester, bvhNodeTester);
            if (item is not null)
                return item;
        }

        return node.Right is null ? null : FindFirst(node.Right, itemTester, bvhNodeTester);
    }

    private static void FindAll(BvhNode node, Predicate<T> itemTester, Predicate<AABB> bvhNodeTester, List<T> list)
    {
        if (!bvhNodeTester(node.Bounds))
            return;

        CpuBvhRenderTree<T> owner = node.Owner;
        if (node.IsLeaf)
        {
            int end = node.Start + node.Count;
            for (int i = node.Start; i < end; i++)
            {
                T item = owner._entries[i].Item;
                if (item.ShouldRender && itemTester(item))
                    list.Add(item);
            }

            return;
        }

        if (node.Left is not null)
            FindAll(node.Left, itemTester, bvhNodeTester, list);
        if (node.Right is not null)
            FindAll(node.Right, itemTester, bvhNodeTester, list);
    }

    private static void RaycastItem(
        T item,
        Segment segment,
        SortedDictionary<float, List<(T item, object? data)>> items,
        Func<T, Segment, (float? distance, object? data)> directTest)
    {
        Box? worldCullingVolume = item.WorldCullingVolume;
        if (worldCullingVolume is null || !worldCullingVolume.Value.IntersectsSegment(segment))
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

                return Moved || StructuralChange
                    ? TreeCommand.Move
                    : TreeCommand.None;
            }

            return Present
                ? TreeCommand.Add
                : TreeCommand.None;
        }
    }

    private readonly record struct BvhEntry(T Item, AABB Bounds, Vector3 Center, int ItemIndex);
    private readonly record struct SwapExecutionSummary(
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

    private readonly record struct RaycastCommand(
        Segment Segment,
        SortedDictionary<float, List<(T item, object? data)>> Items,
        Func<T, Segment, (float? distance, object? data)> DirectTest,
        Action<SortedDictionary<float, List<(T item, object? data)>>> FinishedCallback);

    private sealed class BvhNode(
        CpuBvhRenderTree<T> owner,
        AABB bounds,
        int subDivIndex,
        int subDivLevel,
        BvhNode? parent)
        : OctreeNodeBase(bounds, subDivIndex, subDivLevel)
    {
        public CpuBvhRenderTree<T> Owner { get; } = owner;
        public BvhNode? Parent { get; } = parent;
        public BvhNode? Left { get; set; }
        public BvhNode? Right { get; set; }
        public int Start { get; init; }
        public int Count { get; init; }
        public bool IsLeaf => Left is null && Right is null;
        public override OctreeNodeBase? GenericParent => Parent;

        protected override OctreeNodeBase? GetNodeInternal(int index)
            => index switch
            {
                0 => Left,
                1 => Right,
                _ => null,
            };

        public override void QueueItemMoved(IOctreeItem item)
        {
            if (item is T t)
                Owner.Move(t);
        }

        public override void HandleMovedItem(IOctreeItem item)
        {
            if (item is T t)
                Owner.Move(t);
        }

        public override bool Remove(IOctreeItem item, out bool destroyNode)
        {
            destroyNode = false;
            if (item is not T t)
                return false;

            Owner.Remove(t);
            return true;
        }

        protected override void RemoveNodeAt(int subDivIndex)
        {
            if (subDivIndex == 0)
                Left = null;
            else if (subDivIndex == 1)
                Right = null;
        }
    }

    private sealed class BvhEntryComparer : IComparer<BvhEntry>
    {
        public static readonly BvhEntryComparer X = new(0);
        public static readonly BvhEntryComparer Y = new(1);
        public static readonly BvhEntryComparer Z = new(2);

        private readonly int _axis;

        private BvhEntryComparer(int axis)
        {
            _axis = axis;
        }

        public int Compare(BvhEntry left, BvhEntry right)
        {
            int result = left.Center[_axis].CompareTo(right.Center[_axis]);
            return result != 0
                ? result
                : left.ItemIndex.CompareTo(right.ItemIndex);
        }
    }
}
