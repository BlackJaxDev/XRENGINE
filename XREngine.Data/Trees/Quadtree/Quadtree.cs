using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Data.Trees
{
    /// <summary>
    /// A 3D space partitioning tree that recursively divides aabbs into 4 smaller axis-aligned rectangles depending on the items they contain.
    /// </summary>
    /// <typeparam name="T">The item type to use. Must be a class deriving from I2DRenderable.</typeparam>
    public class Quadtree<T> : QuadtreeBase, I2DRenderTree<T> where T : class, IQuadtreeItem
    {
        internal QuadtreeNode<T> _head;

        public BoundingRectangleF Bounds => _head.Bounds;

        public Quadtree(BoundingRectangleF bounds)
            => _head = new QuadtreeNode<T>(bounds, 0, 0, null, this);
        public Quadtree(BoundingRectangleF bounds, List<T> items) : this(bounds)
            => _head.AddHereOrSmaller(items);

        public void Remake()
            => Remake(_head.Bounds);
        public void Remake(BoundingRectangleF newBounds)
        {
            if (_head.Bounds == newBounds)
                return;
            _remakeRequested = newBounds;
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
            if (_remakeRequested.HasValue)
            {
                List<T> renderables = [];
                _head.CollectAll(renderables);

                _head = new QuadtreeNode<T>(_remakeRequested.Value, 0, 0, null, this);

                foreach (T item in renderables)
                    if (!_head.AddHereOrSmaller(item))
                        _head.AddHere(item);

                _remakeRequested = null;
            }
            while (MovedItems.TryDequeue(out T? item))
                item?.QuadtreeNode?.HandleMovedItem(item);
            while (RemovedItems.TryDequeue(out T? item))
            {
                if (item is null)
                    continue;

                _head.RemoveHereOrSmaller(item);
            }
            while (AddedItems.TryDequeue(out T? item))
            {
                if (item is null)
                    continue;

                if (!_head.AddHereOrSmaller(item))
                    _head.AddHere(item);
            }
        }

        public void Add(T value)
        {
            AddedItems.Enqueue(value);
        }
        public void AddRange(IEnumerable<T> value)
        {
            foreach (T item in value)
                Add(item);
        }
        public void Remove(T value)
        {
            RemovedItems.Enqueue(value);
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

        public void FindAllIntersectingSorted(Vector2 point, SortedSet<T> sortedSet, Predicate<T>? predicate = null)
        {
            sortedSet.Clear();
            _head.FindAllIntersecting(point, sortedSet, predicate);
        }

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
