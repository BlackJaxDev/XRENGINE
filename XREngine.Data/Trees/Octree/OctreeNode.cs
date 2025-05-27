using System.Drawing;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Data.Trees
{
    /// <summary>
    /// A node represents an AABB octant in the octree.
    /// </summary>
    /// <param name="bounds"></param>
    /// <param name="subDivIndex"></param>
    /// <param name="subDivLevel"></param>
    /// <param name="parent"></param>
    /// <param name="owner"></param>
    public class OctreeNode<T>(AABB bounds, int subDivIndex, int subDivLevel, OctreeNode<T>? parent, Octree<T> owner)
        : OctreeNodeBase(bounds, subDivIndex, subDivLevel) where T : class, IOctreeItem
    {
        protected EventList<T> _items = new() { ThreadSafe = true };
        protected OctreeNode<T>?[] _subNodes = new OctreeNode<T>[OctreeBase.MaxChildNodeCount];
        protected OctreeNode<T>? _parentNode = parent;
        //private readonly ReaderWriterLockSlim _lock;

        protected override OctreeNodeBase? GetNodeInternal(int index)
            => _subNodes[index];

        public Octree<T> Owner { get; set; } = owner;
        public OctreeNode<T>? ParentNode
        {
            get => _parentNode;
            set => _parentNode = value;
        }
        public override OctreeNodeBase? GenericParent => ParentNode;
        public override void Destroy()
        {
            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.Destroy();
            foreach (T item in _items)
                item.OctreeNode = null;
            _items.Clear();
            base.Destroy();
        }
        protected override void RemoveNodeAt(int subDivIndex)
        {
            _subNodes[subDivIndex] = null;
        }
        public EventList<T> Items => _items;

        #region Child movement
        public override void QueueItemMoved(IOctreeItem item)
        {
            if (item is T t)
                QueueItemMoved(t);
        }
        public void QueueItemMoved(T item)
        {
            //TODO: if the item is the only item within its volume, no need to subdivide more!!!
            //However, if the item is inserted into a volume with at least one other item in it, 
            //need to try subdividing for all items at that point.

            if (item?.LocalCullingVolume != null)
                Owner.Move(item);
        }
        public override void HandleMovedItem(IOctreeItem item)
        {
            if (item is not T t)
                return;

            //Still within the same volume?
            var worldCullingVolume = t.WorldCullingVolume;
            if (worldCullingVolume is not null && _bounds.ContainsBox(worldCullingVolume.Value) == EContainment.Contains)
            {
                //Try subdividing
                for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                {
                    AABB? bounds = GetSubdivision(i);
                    if (bounds is null)
                        return;
                    if (bounds.Value.ContainsBox(worldCullingVolume.Value) == EContainment.Contains)
                    {
                        bool removed = RemoveHereOrSmaller(t, out bool destroyChild);
                        if (destroyChild)
                            ClearSubNode(_subDivIndex);
                        CreateSubNode(bounds.Value, i)?.AddHereOrSmaller(t);
                        break;
                    }
                }
            }
            else if (ParentNode != null)
            {
                //Belongs in larger parent volume, remove from this node
                bool removed = RemoveHereOrSmaller(t, out bool destroyChild);
                if (!ParentNode.TryAddUp(t, destroyChild ? _subDivIndex : -1))
                {
                    //Force add to root node
                    Owner._head.AddHere(t);
                }
            }
        }
        /// <summary>
        /// Moves up the heirarchy instead of down to add an item.
        /// Returns true if the item was added to this node.
        /// </summary>
        private bool TryAddUp(T item, int childDestroyIndex)
        {
            ClearSubNode(childDestroyIndex);

            if (AddHereOrSmaller(item, childDestroyIndex))
                return true;
            else
            {
                bool shouldDestroy = _items.Count == 0 && HasNoSubNodesExcept(-1);
                if (ParentNode != null)
                    return ParentNode.TryAddUp(item, shouldDestroy ? _subDivIndex : -1);
            }

            return false;
        }
        #endregion

        #region Debug
        /// <summary>
        /// Renders the octree using debug bounding boxes.
        /// Boxes inside the collection voluem render in white, intersecting in green, and disjoint in red.
        /// </summary>
        /// <param name="renderChildren">If true, child boxes will be rendered recursively. If false, only this box will be rendered.</param>
        /// <param name="onlyContainingItems">If true, only boxes fully contained in the collection volume will render. If false, interesecting boxes will also render.</param>
        /// <param name="collectionVolume">The volume to use to determine what boxes to render. If null, all will render.</param>
        /// <param name="render">The delegate used to render boxes. Pass your own rendering logic method in here.</param>
        public void DebugRender(
            bool renderChildren,
            bool onlyContainingItems,
            IVolume? collectionVolume,
            DelRenderAABB render)
        {
            Color color = Color.Red;
            if (renderChildren)
            {
                EContainment containment = collectionVolume?.ContainsAABB(_bounds) ?? EContainment.Contains;
                color = containment == EContainment.Intersects ? Color.Green : containment == EContainment.Contains ? Color.White : Color.Red;
                if (containment != EContainment.Disjoint)
                    foreach (OctreeNode<T>? n in _subNodes)
                        n?.DebugRender(true, onlyContainingItems, collectionVolume, render);
            }
            if (!onlyContainingItems || _items.Count != 0)
                DebugRender(color, render);
        }
        public void DebugRender(Color color, DelRenderAABB render)
            => render(_bounds.HalfExtents, _bounds.Center, color);
        #endregion

        #region Visible collection 
        public void CollectVisible(IVolume? cullingVolume, bool containsOnly, Action<T> action, DelIntersectionTest intersectionTest)
        {
            switch (cullingVolume?.ContainsAABB(_bounds) ?? EContainment.Contains)
            {
                case EContainment.Contains:
                    //If the culling volume contains this bounds, collect all items in this node and all sub nodes. No need to check individual items.
                    CollectAll(action);
                    break;
                case EContainment.Intersects:
                    //If the culling volume intersects this bounds, each item needs to be checked individually.
                    CollectIntersecting(cullingVolume, containsOnly, action, intersectionTest);
                    break;
            }
        }
        public void CollectVisible(IVolume? cullingVolume, bool containsOnly, Action<IOctreeItem> action, OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
        {
            switch (cullingVolume?.ContainsAABB(_bounds) ?? EContainment.Contains)
            {
                case EContainment.Contains:
                    //If the culling volume contains this bounds, collect all items in this node and all sub nodes. No need to check individual items.
                    CollectAll(action);
                    break;
                case EContainment.Intersects:
                    //If the culling volume intersects this bounds, each item needs to be checked individually.
                    CollectIntersecting(cullingVolume, containsOnly, action, intersectionTest);
                    break;
            }
        }
        public void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
        {
            switch (cullingVolume?.ContainsAABB(_bounds) ?? EContainment.Contains)
            {
                case EContainment.Contains:
                    CollectNode(action);
                    break;
                case EContainment.Intersects:
                    IntersectChildNodes(cullingVolume, containsOnly, action);
                    break;
            }
        }

        public delegate bool DelIntersectionTest(T item, IVolume? cullingVolume, bool containsOnly);
        public delegate bool DelIntersectionTestGeneric(IOctreeItem item, IVolume? cullingVolume, bool containsOnly);

        private void CollectIntersecting(IVolume? cullingVolume, bool containsOnly, Action<T> action, DelIntersectionTest intersectionTest)
        {
            //Collect items in this bounds by comparing to culling volume directly
            //Can't do this in parallel because SortedSet is not thread-safe.
            //Would have to program a custom thread-safe sorted tree class with logn add time.
            for (int i = 0; i < _items.Count; ++i)
            {
                T item = _items[i];
                if (item.ShouldRender && intersectionTest(item, cullingVolume, containsOnly))
                    action(item);
            }

            //Collect items from child bounds
            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.CollectIntersecting(cullingVolume, containsOnly, action, intersectionTest);
        }
        private void CollectIntersecting(IVolume? cullingVolume, bool containsOnly, Action<IOctreeItem> action, OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
        {
            //Collect items in this bounds by comparing to culling volume directly
            //Can't do this in parallel because SortedSet is not thread-safe.
            //Would have to program a custom thread-safe sorted tree class with logn add time.
            for (int i = 0; i < _items.Count; ++i)
            {
                T item = _items[i];
                if (item.ShouldRender && intersectionTest(item, cullingVolume, containsOnly))
                    action(item);
            }

            //Collect items from child bounds
            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.CollectIntersecting(cullingVolume, containsOnly, action, intersectionTest);
        }
        private void IntersectChildNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
        {
            if (!containsOnly)
                action((this, true));

            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.CollectVisibleNodes(cullingVolume, containsOnly, action);
        }

        public void CollectAll(Action<T> action)
        {
            for (int i = 0; i < _items.Count; ++i)
            {
                T item = _items[i];
                if (item.ShouldRender)
                    action(item);
            }

            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.CollectAll(action);
        }
        public void CollectAll(List<T> renderables)
        {
            for (int i = 0; i < _items.Count; ++i)
                renderables.Add(_items[i]);

            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.CollectAll(renderables);
        }
        public void CollectNode(Action<(OctreeNodeBase node, bool intersects)> action)
        {
            action((this, false));
            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                _subNodes[i]?.CollectNode(action);
        }
        #endregion

        #region Add/Remove
        public override bool Remove(IOctreeItem item, out bool destroyNode)
        {
            if (item is T t)
            {
                _items.Remove(t);
                t.OctreeNode = null;
            }
            destroyNode = _items.Count == 0 && HasNoSubNodesExcept(-1);
            return false;
        }
        /// <summary>
        /// Returns true if this node no longer contains anything - no items and no sub nodes.
        /// </summary>
        /// <param name="item">The item to remove.</param>
        public bool RemoveHereOrSmaller(T item, out bool destroyNode)
        {
            bool removed;
            if (removed = _items.Remove(item))
            {
                item.OctreeNode = null;
            }
            else
                for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                {
                    OctreeNode<T>? node = _subNodes[i];
                    if (node is null)
                        continue;

                    removed = node.RemoveHereOrSmaller(item, out bool destroyChild);
                    if (destroyChild)
                        _subNodes[i] = null;

                    if (removed)
                        break;
                }

            destroyNode = _items.Count == 0 && HasNoSubNodesExcept(-1);
            return removed;
        }
        /// <summary>
        /// Adds a list of items to this node. May subdivide.
        /// </summary>
        /// <param name="items">The items to add.</param>
        /// <param name="forceAddToThisNode">If true, will add each item regardless of if its culling volume fits within this node's bounds.</param>
        /// <returns>True if ANY node was added.</returns>
        internal void AddHereOrSmaller(List<T> items)
        {
            foreach (T item in items)
                AddHereOrSmaller(item, -1);
        }
        /// <summary>
        /// Adds an item to this node. May subdivide.
        /// </summary>
        /// <param name="items">The item to add.</param>
        /// <param name="forceAddToThisNode">If true, will add the item regardless of if its culling volume fits within the node's bounds.</param>
        /// <returns>True if the node was added.</returns>
        internal bool AddHereOrSmaller(T item, int ignoreSubNode = -1)
        {
            if (item is null)
                return false;

            var worldCullingVolume = item.WorldCullingVolume;
            if (worldCullingVolume != null && _bounds.ContainsBox(worldCullingVolume.Value) == EContainment.Contains)
                for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                {
                    if (i == ignoreSubNode)
                        continue;

                    AABB? subDiv = GetSubdivision(i);
                    if (subDiv is not null && subDiv.Value.ContainsBox(worldCullingVolume.Value) == EContainment.Contains)
                    {
                        CreateSubNode(subDiv.Value, i)?.AddHereOrSmaller(item);
                        return true;
                    }
                }

            AddHere(item);
            return true;
        }
        #endregion

        internal void AddHere(T item)
        {
            if (item is null || _items.Contains(item))
                return;

            _items.Add(item);
            item.OctreeNode = this;
        }
        //internal void RemoveHere(T item)
        //{
        //    if (item is null)
        //        return;

        //    _items.Remove(item);
        //    item.OctreeNode = null;
        //}

        #region Convenience methods
        public T? FindClosest(Vector3 point, ref float closestDistance)
        {
            if (!_bounds.ContainsPoint(point))
                return null;

            //IsLoopingSubNodes = true;
            foreach (OctreeNode<T>? n in _subNodes)
            {
                T? t = n?.FindClosest(point, ref closestDistance);
                if (t is not null)
                    return t;
            }
            //IsLoopingSubNodes = false;

            if (_items.Count == 0)
                return null;

            T? closest = null;

            //IsLoopingItems = true;
            foreach (T item in _items)
            {
                var cullingVolume = item?.WorldCullingVolume;
                if (cullingVolume is null)
                    continue;

                float dist = Vector3.Distance(cullingVolume.Value.ClosestPoint(point, false), point);
                if (dist < closestDistance)
                {
                    closestDistance = dist;
                    closest = item;
                }
            }
            //IsLoopingItems = false;

            return closest;
        }
        //public void FindAll(IShape shape, List<T> list, EContainment containment)
        //{
        //    EContainment c = Bounds.Contains(shape);
        //    if (c == EContainment.Intersects)
        //    {
        //        //Compare each item separately
        //        //IsLoopingItems = true;
        //        foreach (T item in _items)
        //            if (item.LocalCullingVolume != null)
        //            {
        //                c = shape.Contains(item.LocalCullingVolume);
        //                if (c == containment)
        //                    list.Add(item);
        //            }
        //        //IsLoopingItems = false;
        //    }
        //    else if (c == containment)
        //    {
        //        //All items already have this containment
        //        //IsLoopingItems = true;
        //        list.AddRange(_items);
        //        //IsLoopingItems = false;
        //    }
        //    else //Not what we want
        //        return;

        //    //IsLoopingSubNodes = true;
        //    foreach (OctreeNode<T>? n in _subNodes)
        //        n?.FindAll(shape, list, containment);
        //    //IsLoopingSubNodes = false;
        //}
        /// <summary>
        /// Simply collects all the items contained in this node and all of its sub nodes.
        /// </summary>
        /// <returns></returns>
        public List<T> CollectChildren()
        {
            List<T> list = new(_items);
            foreach (OctreeNode<T>? node in _subNodes)
                if (node is not null)
                    list.AddRange(node.CollectChildren());
            return list;
        }
        #endregion

        #region Private Helper Methods
        private void ClearSubNode(int index)
        {
            if (index >= 0)
                _subNodes[index] = null;
        }
        private bool HasNoSubNodesExcept(int index)
        {
            for (int i = 0; i < OctreeBase.MaxChildNodeCount; ++i)
                if (i != index && _subNodes[i] != null)
                    return false;
            return true;
        }
        private OctreeNode<T> CreateSubNode(AABB bounds, int index)
        {
            try
            {
                //IsLoopingSubNodes = true;
                return _subNodes[index] ??= new OctreeNode<T>(bounds, index, _subDivLevel + 1, this, Owner);
            }
            finally
            {
                //IsLoopingSubNodes = false;
            }
        }

        public T? FindFirst(Predicate<T> itemTester, Predicate<AABB> octreeNodeTester)
        {
            if (!octreeNodeTester(_bounds))
                return null;

            for (int i = 0; i < _items.Count; ++i)
                if (_items[i] is T item && itemTester(item))
                    return item;
            
            for (int i = 0; i < _subNodes.Length; ++i)
            {
                if (_subNodes[i] is null)
                    continue;

                T? item = _subNodes[i]?.FindFirst(itemTester, octreeNodeTester);
                if (item != null)
                    return item;
            }

            return null;
        }

        public void FindAll(Predicate<T> itemTester, Predicate<AABB> octreeNodeTester, List<T> list)
        {
            if (!octreeNodeTester(_bounds))
                return;

            for (int i = 0; i < _items.Count; ++i)
                if (_items[i] is T item && itemTester(item))
                    list.Add(item);

            for (int i = 0; i < _subNodes.Length; ++i)
                _subNodes[i]?.FindAll(itemTester, octreeNodeTester, list);
        }

        /// <summary>
        /// Casts a segment through the octree and returns the items in order of distance from the segment start.
        /// directTest is a function that returns the distance from the segment start to the item, or null if the item is not intersected.
        /// Typically, this function is used to test against the depth buffer.
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="orderedItems"></param>
        /// <param name="directTest"></param>
        public void Raycast(
            Segment segment,
            SortedDictionary<float, List<(T item, object? data)>> items,
            Func<T, Segment, (float? distance, object? data)> directTest)
        {
            if (!_bounds.IntersectsSegment(segment))
                return;

            for (int i = 0; i < _items.Count; ++i)
            {
                T item = _items[i];

                var worldCullingVolume = item.WorldCullingVolume;
                if (worldCullingVolume is null || !worldCullingVolume.Value.IntersectsSegment(segment))
                    continue;

                (float? dist, object? data) = directTest(item, segment);
                if (dist is not null)
                {
                    if (!items.TryGetValue(dist.Value, out List<(T item, object? data)>? list))
                        items.Add(dist.Value, list = []);
                    list.Add((item, data));
                }
            }

            for (int i = 0; i < _subNodes.Length; ++i)
                _subNodes[i]?.Raycast(segment, items, directTest);
        }

        /// <summary>
        /// Casts a segment through the octree and returns the items in order of distance from the segment start.
        /// directTest is a function that returns the distance from the segment start to the item, or null if the item is not intersected.
        /// Typically, this function is used to test against the depth buffer.
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="orderedItems"></param>
        /// <param name="directTest"></param>
        public void Raycast(Segment segment, SortedDictionary<float, List<(ITreeItem item, object? data)>> items, Func<ITreeItem, Segment, (float? distance, object? data)> directTest)
        {
            if (!_bounds.IntersectsSegment(segment))
                return;

            for (int i = 0; i < _items.Count; ++i)
            {
                T item = _items[i];

                var worldCullingVolume = item.WorldCullingVolume;
                if (worldCullingVolume is null || !worldCullingVolume.Value.IntersectsSegment(segment))
                    continue;

                (float? dist, object? data) = directTest(item, segment);
                if (dist is not null && dist > 0.0f)
                {
                    if (!items.TryGetValue(dist.Value, out List<(ITreeItem item, object? data)>? list))
                        items.Add(dist.Value, list = []);
                    list.Add((item, data));
                }
            }

            for (int i = 0; i < _subNodes.Length; ++i)
                _subNodes[i]?.Raycast(segment, items, directTest);
        }

        #endregion
    }
}
