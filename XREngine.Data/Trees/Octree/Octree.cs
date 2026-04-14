using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Data.Trees
{

    /// <summary>
    /// A 3D space partitioning tree that recursively divides aabbs into 8 smaller aabbs depending on the items they contain.
    /// </summary>
    /// <typeparam name="T">The item type to use. Must be a class deriving from I3DBoundable.</typeparam>
    public class Octree<T> : OctreeBase, I3DRenderTree<T> where T : class, IOctreeItem
    {
        internal OctreeNode<T> _head;

        public Octree(AABB bounds)
            => _head = new OctreeNode<T>(bounds, 0, 0, null, this);

        public Octree(AABB bounds, List<T> items) : this(bounds)
            => _head.AddHereOrSmaller(items);

        //public class RenderEquality : IEqualityComparer<I3DRenderable>
        //{
        //    public bool Equals(I3DRenderable x, I3DRenderable y)
        //        => x.RenderInfo.SceneID == y.RenderInfo.SceneID;
        //    public int GetHashCode(I3DRenderable obj)
        //        => obj.RenderInfo.SceneID;
        //}
        
        public void Remake()
            => Remake(_head.Bounds);

        public void Remake(AABB newBounds)
        {
            List<T> renderables = [];
            _head.CollectAll(renderables);
            _head = new OctreeNode<T>(newBounds, 0, 0, null, this);

            for (int i = 0; i < renderables.Count; i++)
            {
                T item = renderables[i];
                if (!_head.AddHereOrSmaller(item))
                    _head.AddHere(item);
            }
        }
        
        internal enum ETreeCommand
        {
            Move,
            Add,
            Remove,
            None,
        }

        private readonly record struct SwapExecutionSummary(
            int BufferedCommandCount,
            int ExecutedCommandCount,
            long ExecuteTicks,
            long MaxCommandTicks,
            EOctreeCommandKind MaxCommandKind);

        internal ConcurrentQueue<(T item, ETreeCommand)> SwapCommands { get; } = new ConcurrentQueue<(T item, ETreeCommand command)>();
        internal ConcurrentQueue<(Segment segment, SortedDictionary<float, List<(T item, object? data)>> items, Func<T, Segment, (float? distance, object? data)> directTest, Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)> RaycastCommands { get; } = new ConcurrentQueue<(Segment segment, SortedDictionary<float, List<(T item, object? data)>> items, Func<T, Segment, (float? distance, object? data)> directTest, Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)>();

        private readonly List<(T item, ETreeCommand command)> _swapCommandBuffer = new(256);
        private readonly Dictionary<T, int> _swapCommandIndices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Updates all moved, added and removed items in the octree.
        /// </summary>
        public void Swap()
        {
            // Debug: track if Swap is being called and if there are pending raycast commands
            //if (!RaycastCommands.IsEmpty)
            //    Trace.WriteLine($"[Octree.Swap] Swap called, RaycastCommands.Count={RaycastCommands.Count}");
            
            if (IRenderTree.ProfilingHook is not null)
            {
                using var sample = IRenderTree.ProfilingHook("Octree.Swap");
                SwapInternal();
            }
            else
                SwapInternal();
        }

        private void SwapInternal()
        {
            ConsumeSwapCommands();
            ConsumeRaycastCommands();
        }

        private void ConsumeRaycastCommands()
        {
            if (RaycastCommands.IsEmpty)
                return;

            if (IRenderTree.ProfilingHook is not null)
            {
                using var sample = IRenderTree.ProfilingHook("Octree.ConsumeRaycastCommands");
                ConsumeRaycastCommandsInternal();
            }
            else
                ConsumeRaycastCommandsInternal();
        }

        private const long MaxRaycastTicksPerFrame = 3 * TimeSpan.TicksPerMillisecond; // 3ms

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

            while (RaycastCommands.TryDequeue(out (
                Segment segment,
                SortedDictionary<float, List<(T item, object? data)>> items,
                Func<T, Segment, (float? distance, object? data)> directTest,
                Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback
            ) command))
            {
                long commandStart = Stopwatch.GetTimestamp();
                _head.Raycast(command.segment, command.items, command.directTest);
                long traversalElapsedTicks = Stopwatch.GetTimestamp() - commandStart;

                long callbackStart = Stopwatch.GetTimestamp();
                command.finishedCallback(command.items);
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
                    while (RaycastCommands.TryDequeue(out _))
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

        private void ConsumeSwapCommands()
        {
            if (SwapCommands.IsEmpty)
                return;

            if (IRenderTree.ProfilingHook is not null)
            {
                using var sample = IRenderTree.ProfilingHook("Octree.ConsumeSwapCommands");
                ConsumeSwapCommandsInternal();
            }
            else
                ConsumeSwapCommandsInternal();
        }

        private void ConsumeSwapCommandsInternal()
        {
            if (SwapCommands.IsEmpty)
                return;

            if (!SwapCommands.TryDequeue(out (T item, ETreeCommand command) firstCommand))
                return;

            if (SwapCommands.IsEmpty)
            {
                long executeStart = Stopwatch.GetTimestamp();
                ExecuteSwapCommand(firstCommand.item, firstCommand.command);
                long executeTicks = Stopwatch.GetTimestamp() - executeStart;
                ReportSingleCommandStats(firstCommand.command);

                IRenderTree.OctreeSwapTimingHook?.Invoke(new OctreeSwapTimingStats(
                    1,
                    1,
                    firstCommand.item is null || firstCommand.command == ETreeCommand.None ? 0 : 1,
                    0L,
                    executeTicks,
                    executeTicks,
                    ToStatsCommandKind(firstCommand.command)));
                return;
            }

            long drainStart = Stopwatch.GetTimestamp();
            int drainedCommandCount = DrainSwapCommands(firstCommand);
            long drainTicks = Stopwatch.GetTimestamp() - drainStart;
            SwapExecutionSummary executionSummary = ExecuteSwapCommands();

            IRenderTree.OctreeSwapTimingHook?.Invoke(new OctreeSwapTimingStats(
                drainedCommandCount,
                executionSummary.BufferedCommandCount,
                executionSummary.ExecutedCommandCount,
                drainTicks,
                executionSummary.ExecuteTicks,
                executionSummary.MaxCommandTicks,
                executionSummary.MaxCommandKind));
        }

        private void ReportSingleCommandStats(ETreeCommand command)
        {
            if (IRenderTree.OctreeStatsHook is null)
                return;

            int adds = command == ETreeCommand.Add ? 1 : 0;
            int moves = command == ETreeCommand.Move ? 1 : 0;
            int removes = command == ETreeCommand.Remove ? 1 : 0;
            IRenderTree.OctreeStatsHook(adds, moves, removes, 0);
        }

        private int DrainSwapCommands((T item, ETreeCommand command) firstCommand)
        {
            _swapCommandBuffer.Clear();
            _swapCommandIndices.Clear();

            int drainedCommandCount = 1;

            if (firstCommand.item is not null)
            {
                _swapCommandIndices[firstCommand.item] = _swapCommandBuffer.Count;
                _swapCommandBuffer.Add(firstCommand);
            }

            while (SwapCommands.TryDequeue(out (T item, ETreeCommand command) command))
            {
                drainedCommandCount++;
                var item = command.item;
                if (item is null)
                    continue;

                if (!_swapCommandIndices.TryGetValue(item, out int index))
                {
                    _swapCommandIndices[item] = _swapCommandBuffer.Count;
                    _swapCommandBuffer.Add(command);
                    continue;
                }

                var combined = CombineCommands(_swapCommandBuffer[index].command, command.command);
                if (combined == ETreeCommand.None)
                {
                    RemoveBufferedCommand(index);
                    continue;
                }

                _swapCommandBuffer[index] = (item, combined);
            }

            return drainedCommandCount;
        }

        private SwapExecutionSummary ExecuteSwapCommands()
        {
            if (_swapCommandBuffer.Count == 0)
                return new SwapExecutionSummary(0, 0, 0L, 0L, EOctreeCommandKind.None);

            int adds = 0, moves = 0, removes = 0;
            int executedCommandCount = 0;
            long executeStart = Stopwatch.GetTimestamp();
            long maxCommandTicks = 0L;
            EOctreeCommandKind maxCommandKind = EOctreeCommandKind.None;
            var head = _head;
            for (int i = 0; i < _swapCommandBuffer.Count; i++)
            {
                var (item, command) = _swapCommandBuffer[i];
                if (item is null || command == ETreeCommand.None)
                    continue;

                long commandStart = Stopwatch.GetTimestamp();
                ExecuteSwapCommand(item, command, head);
                long commandTicks = Stopwatch.GetTimestamp() - commandStart;
                executedCommandCount++;

                if (commandTicks > maxCommandTicks)
                {
                    maxCommandTicks = commandTicks;
                    maxCommandKind = ToStatsCommandKind(command);
                }
                
                switch (command)
                {
                    case ETreeCommand.Add: adds++; break;
                    case ETreeCommand.Move: moves++; break;
                    case ETreeCommand.Remove: removes++; break;
                }
            }

            _swapCommandBuffer.Clear();
            _swapCommandIndices.Clear();

            IRenderTree.OctreeStatsHook?.Invoke(adds, moves, removes, 0);

            return new SwapExecutionSummary(
                adds + moves + removes,
                executedCommandCount,
                Stopwatch.GetTimestamp() - executeStart,
                maxCommandTicks,
                maxCommandKind);
        }

        private void ExecuteSwapCommand(T item, ETreeCommand command, OctreeNode<T>? headOverride = null)
        {
            if (item is null || command == ETreeCommand.None)
                return;

            var head = headOverride ?? _head;
            switch (command)
            {
                case ETreeCommand.Move:
                    item.OctreeNode?.HandleMovedItem(item);
                    break;
                case ETreeCommand.Add:
                    if (!head.AddHereOrSmaller(item))
                        head.AddHere(item);
                    break;
                case ETreeCommand.Remove:
                    var node = item.OctreeNode;
                    if (node is null)
                        break;
                    node.Remove(item, out bool destroyNode);
                    if (destroyNode)
                        node.Destroy();
                    break;
            }
        }

        private static ETreeCommand CombineCommands(ETreeCommand current, ETreeCommand next)
        {
            if (next == ETreeCommand.None)
                return current;

            if (current == ETreeCommand.None)
                return next;

            return (current, next) switch
            {
                (ETreeCommand.Add, ETreeCommand.Remove) => ETreeCommand.None,
                (ETreeCommand.Add, ETreeCommand.Move) => ETreeCommand.Add,
                (ETreeCommand.Move, ETreeCommand.Move) => ETreeCommand.Move,
                (ETreeCommand.Move, ETreeCommand.Remove) => ETreeCommand.Remove,
                (ETreeCommand.Move, ETreeCommand.Add) => ETreeCommand.Add,
                (ETreeCommand.Remove, ETreeCommand.Add) => ETreeCommand.Add,
                (ETreeCommand.Remove, ETreeCommand.Move) => ETreeCommand.Remove,
                (ETreeCommand.Remove, ETreeCommand.Remove) => ETreeCommand.Remove,
                (ETreeCommand.Add, ETreeCommand.Add) => ETreeCommand.Add,
                _ => next,
            };
        }

        private static EOctreeCommandKind ToStatsCommandKind(ETreeCommand command)
            => command switch
            {
                ETreeCommand.Add => EOctreeCommandKind.Add,
                ETreeCommand.Move => EOctreeCommandKind.Move,
                ETreeCommand.Remove => EOctreeCommandKind.Remove,
                _ => EOctreeCommandKind.None,
            };

        private void RemoveBufferedCommand(int index)
        {
            var item = _swapCommandBuffer[index].item;
            _swapCommandIndices.Remove(item);

            int lastIndex = _swapCommandBuffer.Count - 1;
            if (index != lastIndex)
            {
                _swapCommandBuffer[index] = _swapCommandBuffer[lastIndex];
                _swapCommandIndices[_swapCommandBuffer[index].item] = index;
            }

            _swapCommandBuffer.RemoveAt(lastIndex);
        }

        public void Add(T value)
            => SwapCommands.Enqueue((value, ETreeCommand.Add));
        public void Remove(T value)
            => SwapCommands.Enqueue((value, ETreeCommand.Remove));
        public void Move(T item)
            => SwapCommands.Enqueue((item, ETreeCommand.Move));

        public void AddRange(IEnumerable<T> value)
        {
            foreach (T item in value)
                Add(item);
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

        public void RemoveRange(IEnumerable<T> value)
        {
            foreach (T item in value)
                Remove(item);
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

        //public List<T> FindAll(float radius, Vector3 point, EContainment containment)
        //    => FindAll(new Sphere(point, radius), containment);
        //public List<T> FindAll(IShape shape, EContainment containment)
        //{
        //    List<T> list = [];
        //    _head.FindAll(shape, list, containment);
        //    return list;
        //}

        public void CollectAll(Action<T> action)
            => _head.CollectAll(action);

        /// <summary>
        /// Renders the octree using debug bounding boxes.
        /// </summary>
        /// <param name="volume">The frustum to display intersections with. If null, does not show frustum intersections.</param>
        /// <param name="onlyContainingItems">Only renders subdivisions that contain one or more items.</param>
        /// <param name="lineWidth">The width of the bounding box lines.</param>
        public void DebugRender(IVolume? volume, bool onlyContainingItems, DelRenderAABB render)
            => _head.DebugRender(true, onlyContainingItems, volume, render);

        public void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<T> action, OctreeNode<T>.DelIntersectionTest intersectionTest)
            => _head.CollectVisible(volume, onlyContainingItems, action, intersectionTest);
        void I3DRenderTree.CollectVisible(IVolume? volume, bool onlyContainingItems, Action<IOctreeItem> action, OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
            => _head.CollectVisible(volume, onlyContainingItems, action, intersectionTest);
        public void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
            => _head.CollectVisibleNodes(cullingVolume, containsOnly, action);

        void I3DRenderTree.CollectAll(Action<IOctreeItem> action)
        {
            void Add(T item)
                => action(item);

            CollectAll(Add);
        }

        public T? FindFirst(Predicate<T> itemTester, Predicate<AABB> octreeNodeTester)
            => _head.FindFirst(itemTester, octreeNodeTester);

        public List<T> FindAll(Predicate<T> itemTester, Predicate<AABB> octreeNodeTester)
        {
            List<T> list = [];
            _head.FindAll(itemTester, octreeNodeTester, list);
            return list;
        }

        private const int MaxRaycastCommands = 10;
        private static int _raycastEnqueueDebugBudget = 20;

        public void RaycastAsync(
            Segment segment,
            SortedDictionary<float, List<(T item, object? data)>> items,
            Func<T, Segment, (float? distance, object? data)> directTest,
            Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)
        {
            if (RaycastCommands.Count >= MaxRaycastCommands)
            {
                if (_raycastEnqueueDebugBudget-- > 0)
                    Trace.WriteLine($"[Octree.RaycastAsync] Queue full ({RaycastCommands.Count}), dropping command");
                return;
            }
            RaycastCommands.Enqueue((segment, items, directTest, finishedCallback));
            //if (_raycastEnqueueDebugBudget-- > 0)
            //    Trace.WriteLine($"[Octree.RaycastAsync] Enqueued command. QueueCount={RaycastCommands.Count}");
        }

        public void Raycast(
            Segment segment,
            SortedDictionary<float, List<(T item, object? data)>> items,
            Func<T, Segment, (float? distance, object? data)> directTest)
        {
            ArgumentNullException.ThrowIfNull(items);
            _head.Raycast(segment, items, directTest);
        }

        public void DebugRender(IVolume? cullingVolume, DelRenderAABB render, bool onlyContainingItems = false)
            => _head.DebugRender(true, onlyContainingItems, cullingVolume, render);
    }
}
