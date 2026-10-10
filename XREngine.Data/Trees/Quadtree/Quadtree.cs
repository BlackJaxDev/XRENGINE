using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.Data.Trees
{
    /// <summary>
    /// A 3D space partitioning tree that recursively divides aabbs into 4 smaller axis-aligned rectangles depending on the items they contain.
    /// </summary>
    /// <typeparam name="T">The item type to use. Must be a class deriving from I2DRenderable.</typeparam>
    public class Quadtree<T> : QuadtreeBase, I2DRenderTree<T>, IDisposable where T : class, IQuadtreeItem
    {
        internal QuadtreeNode<T> _head;
        private readonly object _lifetimeGate = new();
        private volatile bool _disposed;
        private bool _storageReleased;
        private bool _releaseInProgress;
        private bool _swapInProgress;
        private bool _releaseAfterSwap;
        private readonly List<QuadtreeNode<T>> _retiredNodes = [];

        public bool IsDisposed => _disposed;

        public BoundingRectangleF Bounds => _head.Bounds;

        public Quadtree(BoundingRectangleF bounds)
            => _head = new QuadtreeNode<T>(bounds, 0, 0, null, this);
        public Quadtree(BoundingRectangleF bounds, List<T> items) : this(bounds)
            => _head.AddHereOrSmaller(items);

        public void Remake()
            => Remake(_head.Bounds);
        public void Remake(BoundingRectangleF newBounds)
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_swapInProgress)
                    throw new InvalidOperationException("A quadtree cannot request a remake during a swap.");
                if (_head.Bounds == newBounds)
                    return;
                _remakeRequested = newBounds;
            }
        }

        /// <summary>
        /// Requests terminal release of owned node/list storage without destroying
        /// scene items. An active swap completes before its storage is released.
        /// </summary>
        public void Dispose()
        {
            bool release;
            lock (_lifetimeGate)
            {
                _disposed = true;
                release = TryClaimTerminalRelease();
            }
            if (release)
                ReleaseStorage();
        }

        private bool TryClaimTerminalRelease()
        {
            if (_storageReleased || _releaseInProgress)
                return false;
            if (_swapInProgress)
            {
                _releaseAfterSwap = true;
                return false;
            }
            _releaseInProgress = true;
            while (AddedItems.TryDequeue(out _)) { }
            while (RemovedItems.TryDequeue(out _)) { }
            while (MovedItems.TryDequeue(out _)) { }
            _remakeRequested = null;
            return true;
        }

        private void ReleaseStorage()
        {
            bool released = false;
            try
            {
                List<Exception>? failures = null;
                try { _head.ReleaseStorage(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
                try { ReleaseRetiredNodes(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
                if (failures is not null)
                    throw new AggregateException("Failed to release quadtree storage.", failures);
                released = true;
            }
            finally
            {
                lock (_lifetimeGate)
                {
                    if (released)
                        _storageReleased = true;
                    _releaseInProgress = false;
                }
            }
        }

        internal ConcurrentQueue<T> AddedItems { get; } = new ConcurrentQueue<T>();
        internal ConcurrentQueue<T> RemovedItems { get; } = new ConcurrentQueue<T>();
        internal ConcurrentQueue<T> MovedItems { get; } = new ConcurrentQueue<T>();

        private BoundingRectangleF? _remakeRequested = null;

        /// <summary>
        /// Updates all moved, added and removed items in the octree.
        /// </summary>
        public void Swap()
        {
            lock (_lifetimeGate)
            {
                if (_swapInProgress)
                    throw new InvalidOperationException("A quadtree cannot run concurrent or recursive swaps.");
                ObjectDisposedException.ThrowIf(_disposed, this);
                _swapInProgress = true;
            }
            try
            {
                if (IRenderTree.ProfilingHook is not null)
                {
                    using IDisposable profile = IRenderTree.ProfilingHook("Quadtree Swap");
                    SwapInternal();
                }
                else
                    SwapInternal();
            }
            finally
            {
                bool releaseAfterSwap;
                lock (_lifetimeGate)
                {
                    _swapInProgress = false;
                    releaseAfterSwap = _releaseAfterSwap && TryClaimTerminalRelease();
                    _releaseAfterSwap = false;
                }
                if (releaseAfterSwap)
                    ReleaseStorage();
            }
        }

        private void SwapInternal()
        {
            if (XRObjectBase.CurrentObjectCachePublicationScope is null)
                ReleaseRetiredNodes();
            ConsumeMoveItemQueue();
            ConsumeRemoveItemQueue();
            ConsumeAddItemQueue();
            RemakeTree();
            if (XRObjectBase.CurrentObjectCachePublicationScope is null)
                ReleaseRetiredNodes();
        }

        private void RemakeTree()
        {
            if (!_remakeRequested.HasValue)
                return;

            if (IRenderTree.ProfilingHook is not null)
            {
                using IDisposable profile = IRenderTree.ProfilingHook("Quadtree Remake");
                RemakeTreeInternal();
            }
            else
                RemakeTreeInternal();
        }

        private void RemakeTreeInternal()
        {
            List<T> renderables = [];
            _head.CollectAll(renderables);

            QuadtreeNode<T> previous = _head;
            _head = new QuadtreeNode<T>(_remakeRequested!.Value, 0, 0, null, this);
            _retiredNodes.Add(previous);

            foreach (T item in renderables)
                if (!_head.AddHereOrSmaller(item))
                    _head.AddHere(item);

            _remakeRequested = null;
        }

        internal void RetireNode(QuadtreeNode<T> node)
            => _retiredNodes.Add(node);

        private void ReleaseRetiredNodes()
        {
            List<Exception>? failures = null;
            for (int index = _retiredNodes.Count - 1; index >= 0; index--)
            {
                try
                {
                    _retiredNodes[index].ReleaseStorage();
                    _retiredNodes.RemoveAt(index);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
            if (failures is not null)
                throw new AggregateException("Failed to retire replaced quadtree storage.", failures);
        }

        private void ConsumeAddItemQueue()
        {
            if (IRenderTree.ProfilingHook is not null)
            {
                using IDisposable profile = IRenderTree.ProfilingHook("Quadtree Add Items");
                ConsumeAddItemQueueInternal();
            }
            else
                ConsumeAddItemQueueInternal();
        }

        private void ConsumeAddItemQueueInternal()
        {
            while (AddedItems.TryDequeue(out T? item))
            {
                if (item is null)
                    continue;
                if (!_head.AddHereOrSmaller(item))
                    _head.AddHere(item);
            }
        }

        private void ConsumeRemoveItemQueue()
        {
            if (IRenderTree.ProfilingHook is not null)
            {
                using IDisposable profile = IRenderTree.ProfilingHook("Quadtree Remove Items");
                ConsumeRemoveItemQueueInternal();
            }
            else
                ConsumeRemoveItemQueueInternal();
        }

        private void ConsumeRemoveItemQueueInternal()
        {
            while (RemovedItems.TryDequeue(out T? item))
            {
                if (item is null)
                    continue;

                _head.RemoveHereOrSmaller(item);
            }
        }

        private void ConsumeMoveItemQueue()
        {
            if (MovedItems?.IsEmpty != false)
                return;

            if (IRenderTree.ProfilingHook is not null)
            {
                using IDisposable profile = IRenderTree.ProfilingHook("Quadtree Move Items");
                ConsumeMoveItemQueueInternal();
            }
            else
                ConsumeMoveItemQueueInternal();
        }

        private void ConsumeMoveItemQueueInternal()
        {
            while (MovedItems.TryDequeue(out T? item))
                item?.QuadtreeNode?.HandleMovedItem(item);
        }

        public void Add(T value)
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                AddedItems.Enqueue(value);
            }
        }
        public void AddRange(IEnumerable<T> value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (T item in value)
                Add(item);
        }
        public void Remove(T value)
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RemovedItems.Enqueue(value);
            }
        }

        internal void QueueMoved(T item)
        {
            lock (_lifetimeGate)
                if (!_disposed)
                    MovedItems.Enqueue(item);
        }

        //public List<T> FindAll(float radius, Vector2 point, EContainment containment)
        //    => FindAll(new Sphere(radius, point), containment);
        //public List<T> FindAll(Shape shape, EContainment containment)
        //{
        //    List<T> list = new List<T>();
        //    _head.FindAll(shape, list, containment);
        //    return list;
        //}

        public void CollectAll(Action<T> action)
            => _head.CollectAll(action);

        public void CollectVisible(BoundingRectangleF? collectionRegion, bool containsOnly, Action<T> action, QuadtreeNode<T>.DelIntersectionTest intersectionTest)
            => _head.CollectVisible(collectionRegion, containsOnly, action, intersectionTest);
        void I2DRenderTree.CollectVisible(BoundingRectangleF? volume, bool onlyContainingItems, Action<IQuadtreeItem> action, QuadtreeNode<IQuadtreeItem>.DelIntersectionTestGeneric intersectionTest)
            => _head.CollectVisible(volume, onlyContainingItems, action, intersectionTest);

        public T? FindDeepest(Vector2 point)
        {
            T? value = null;
            _head.FindDeepest(point, ref value);
            return value;
        }

        public void FindAllIntersecting(Vector2 point, List<T> list, Predicate<T>? predicate = null)
        {
            list.Clear();
            _head.FindAllIntersecting(point, list, predicate);
        }

        /// <summary>Collects point hits without allocating. False means storage overflow; partial hits must not be used.</summary>
        public bool TryFindAllIntersecting(Vector2 point, Span<T?> destination, out int count)
        {
            count = 0;
            return _head.TryFindAllIntersecting(point, destination, ref count);
        }

        public void FindAllIntersectingSorted(Vector2 point, SortedSet<T> sortedSet, Predicate<T>? predicate = null)
            => _head.FindAllIntersecting(point, sortedSet, predicate);

        /// <summary>
        /// Finds all intersecting items while reusing caller-owned collection scratch.
        /// </summary>
        public void FindAllIntersectingSorted(
            Vector2 point,
            SortedSet<T> sortedSet,
            List<T> buffer,
            Predicate<T>? predicate = null)
            => _head.FindAllIntersecting(point, sortedSet, buffer, predicate);

        /// <summary>
        /// Finds all renderables that contain the given point.
        /// </summary>
        /// <param name="point">The point that the returned renderables should contain.</param>
        /// <returns>A list of renderables containing the given point.</returns>
        public List<T> FindAllIntersecting(Vector2 point, Predicate<T>? predicate = null)
        {
            List<T> intersecting = [];
            _head.FindAllIntersecting(point, intersecting, predicate);
            return intersecting;
        }
        /// <summary>
        /// Finds all renderables that contain the given point.
        /// Orders renderables from least deep to deepest.
        /// </summary>
        /// <param name="point">The point that the returned renderables should contain.</param>
        /// <returns>A sorted set of renderables containing the given point.</returns>
        public SortedSet<T> FindAllIntersectingSorted(Vector2 point, Predicate<T>? predicate = null)
        {
            SortedSet<T> intersecting = [];
            _head.FindAllIntersecting(point, intersecting, predicate);
            return intersecting;
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

        public void CollectAll(Action<IQuadtreeItem> action)
            => _head.CollectAll(action);

        public void Raycast<T2>(Vector2 point, SortedDictionary<float, List<(T2 item, object? data)>> items) where T2 : class, IRenderableBase
            => _head.Raycast(point, items);

        public void DebugRender(BoundingRectangleF? volume, bool onlyContainingItems, DelRenderBounds render)
            => _head.DebugRender(true, onlyContainingItems, volume, render);

        public void CollectVisibleNodes(BoundingRectangleF? cullingVolume, bool containsOnly, Action<(QuadtreeNodeBase node, bool intersects)> action)
            => _head.CollectVisibleNodes(cullingVolume, containsOnly, action);

        public SortedDictionary<int, List<T>> Collect(Func<QuadtreeNode<T>, bool> nodeTest, Func<T, bool> itemTest)
        {
            var list = new SortedDictionary<int, List<T>>();
            _head.Collect(nodeTest, itemTest, list, 0);
            return list;
        }
    }
}
