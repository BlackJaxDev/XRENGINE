using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        internal ConcurrentQueue<(T item, ETreeCommand)> SwapCommands { get; } = new ConcurrentQueue<(T item, ETreeCommand command)>();
        internal ConcurrentQueue<(Segment segment, SortedDictionary<float, List<(T item, object? data)>> items, Func<T, Segment, (float? distance, object? data)> directTest, Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)> RaycastCommands { get; } = new ConcurrentQueue<(Segment segment, SortedDictionary<float, List<(T item, object? data)>> items, Func<T, Segment, (float? distance, object? data)> directTest, Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)>();

        private readonly List<(T item, ETreeCommand command)> _swapCommandBuffer = new(256);
        private readonly Dictionary<T, int> _swapCommandIndices = new();

        /// <summary>
        /// Updates all moved, added and removed items in the octree.
        /// </summary>
        public void Swap()
        {
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

        private void ConsumeRaycastCommandsInternal()
        {
            while (RaycastCommands.TryDequeue(out (
                Segment segment,
                SortedDictionary<float, List<(T item, object? data)>> items,
                Func<T, Segment, (float? distance, object? data)> directTest,
                Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback
            ) command))
            {
                _head.Raycast(command.segment, command.items, command.directTest);
                command.finishedCallback(command.items);
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

            DrainSwapCommands();
            ExecuteSwapCommands();
        }

        private void DrainSwapCommands()
        {
            _swapCommandBuffer.Clear();
            _swapCommandIndices.Clear();

            while (SwapCommands.TryDequeue(out (T item, ETreeCommand command) command))
            {
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
        }

        private void ExecuteSwapCommands()
        {
            if (_swapCommandBuffer.Count == 0)
                return;

            var head = _head;
            for (int i = 0; i < _swapCommandBuffer.Count; i++)
            {
                var (item, command) = _swapCommandBuffer[i];
                if (item is null || command == ETreeCommand.None)
                    continue;

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

            _swapCommandBuffer.Clear();
            _swapCommandIndices.Clear();
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

        public void RaycastAsync(
            Segment segment,
            SortedDictionary<float, List<(T item, object? data)>> items,
            Func<T, Segment, (float? distance, object? data)> directTest,
            Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)
            => RaycastCommands.Enqueue((segment, items, directTest, finishedCallback));

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
