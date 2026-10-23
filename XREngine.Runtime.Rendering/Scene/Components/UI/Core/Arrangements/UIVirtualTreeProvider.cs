using System;
using System.Collections.Generic;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Keeps a visible preorder index. Structural edits change the affected range; scrolling reads it by index.
    /// This type is owned by the UI thread. Large inserts and moves can shift later list elements.
    /// </summary>
    public sealed class UIVirtualTreeProvider : IVirtualListProvider
    {
        private readonly Dictionary<ulong, UIVirtualTreeNode> _nodes = [];
        private readonly List<ulong> _roots = [];
        private readonly List<ulong> _visible = [];
        private readonly List<ulong> _scratch = [];
        private readonly HashSet<ulong> _selection = [];
        private readonly Func<UIBoundableTransform> _createRow;
        private readonly Action<UIBoundableTransform, UIVirtualTreeNode, int, ulong> _bindRow;
        private readonly Action<UIBoundableTransform, ulong, ulong> _unbindRow;
        private ulong _revision = 1;
        private ulong _activeId;
        private bool _bulkLoading;

        public UIVirtualTreeProvider(
            Func<UIBoundableTransform> createRow,
            Action<UIBoundableTransform, UIVirtualTreeNode, int, ulong> bindRow,
            Action<UIBoundableTransform, ulong, ulong> unbindRow)
        {
            _createRow = createRow ?? throw new ArgumentNullException(nameof(createRow));
            _bindRow = bindRow ?? throw new ArgumentNullException(nameof(bindRow));
            _unbindRow = unbindRow ?? throw new ArgumentNullException(nameof(unbindRow));
        }

        public int Count => _visible.Count;
        public ulong Revision => _revision;
        public ulong ActiveId => _activeId;
        public bool IsSelected(ulong id) => _selection.Contains(id);
        public bool TryGetNode(ulong id, out UIVirtualTreeNode? node) => _nodes.TryGetValue(id, out node);
        public ulong GetItemId(int index) => _visible[index];
        public int FindVisibleIndex(ulong id) => _visible.IndexOf(id);
        public UIBoundableTransform CreateRow() => _createRow();
        public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
            => _bindRow(row, _nodes[itemId], index, incarnation);
        public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
            => _unbindRow(row, itemId, incarnation);

        /// <summary>Loads a complete source tree once without maintaining the visible index on each insert.</summary>
        public void BeginBulkLoad()
        {
            if (_bulkLoading)
                throw new InvalidOperationException("Bulk loading is already active.");
            _nodes.Clear();
            _roots.Clear();
            _visible.Clear();
            _scratch.Clear();
            _selection.Clear();
            _activeId = 0;
            _bulkLoading = true;
        }

        public void EndBulkLoad()
        {
            if (!_bulkLoading)
                throw new InvalidOperationException("Bulk loading is not active.");
            _visible.Clear();
            for (int i = 0; i < _roots.Count; i++)
            {
                ulong id = _roots[i];
                _visible.Add(id);
                if (_nodes[id].IsExpanded)
                    AppendVisibleDescendants(_nodes[id], _visible);
            }
            _bulkLoading = false;
            _revision++;
        }

        public void Insert(ulong id, ulong parentId, int siblingIndex, string name, bool isSection = false)
        {
            if (id == 0 || _nodes.ContainsKey(id))
                throw new ArgumentException("Tree IDs must be unique and nonzero.", nameof(id));
            List<ulong> siblings = GetSiblings(parentId);
            siblingIndex = Math.Clamp(siblingIndex, 0, siblings.Count);
            UIVirtualTreeNode node = new(id, name, isSection) { ParentId = parentId };
            int insertAt = _bulkLoading ? -1 : FindInsertionIndex(parentId, siblings, siblingIndex);
            siblings.Insert(siblingIndex, id);
            _nodes.Add(id, node);
            if (insertAt >= 0)
                _visible.Insert(insertAt, id);
            _revision++;
        }

        public bool SetExpanded(ulong id, bool expanded)
        {
            if (!_nodes.TryGetValue(id, out UIVirtualTreeNode? node) || node.IsExpanded == expanded)
                return false;
            int index = _bulkLoading ? -1 : _visible.IndexOf(id);
            if (index >= 0)
            {
                if (expanded)
                {
                    _scratch.Clear();
                    AppendVisibleDescendants(node, _scratch);
                    _visible.InsertRange(index + 1, _scratch);
                }
                else
                {
                    int end = FindSubtreeEnd(index, id);
                    _visible.RemoveRange(index + 1, end - index - 1);
                }
            }
            node.IsExpanded = expanded;
            _revision++;
            return true;
        }

        public bool Rename(ulong id, string name)
        {
            if (!_nodes.TryGetValue(id, out UIVirtualTreeNode? node) || node.Name == name)
                return false;
            node.Name = name;
            _revision++;
            return true;
        }

        public bool SetSelected(ulong id, bool selected)
        {
            if (!_nodes.ContainsKey(id))
                return false;
            bool changed = selected ? _selection.Add(id) : _selection.Remove(id);
            if (changed)
                _revision++;
            return changed;
        }

        public bool Activate(ulong id)
        {
            if (id != 0 && !_nodes.ContainsKey(id))
                return false;
            if (_activeId == id)
                return false;
            _activeId = id;
            _revision++;
            return true;
        }

        public bool Remove(ulong id)
        {
            if (!_nodes.TryGetValue(id, out UIVirtualTreeNode? node))
                return false;
            int index = _visible.IndexOf(id);
            if (index >= 0)
                _visible.RemoveRange(index, FindSubtreeEnd(index, id) - index);
            GetSiblings(node.ParentId).Remove(id);
            _scratch.Clear();
            CollectAllDescendants(id, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                ulong removedId = _scratch[i];
                _nodes.Remove(removedId);
                _selection.Remove(removedId);
                if (_activeId == removedId)
                    _activeId = 0;
            }
            _revision++;
            return true;
        }

        public bool Move(ulong id, ulong newParentId, int siblingIndex)
        {
            if (!_nodes.TryGetValue(id, out UIVirtualTreeNode? node))
                return false;
            if (newParentId != 0 && !_nodes.ContainsKey(newParentId))
                return false;
            for (ulong ancestor = newParentId; ancestor != 0; ancestor = _nodes[ancestor].ParentId)
                if (ancestor == id)
                    return false;

            int oldIndex = _visible.IndexOf(id);
            _scratch.Clear();
            if (oldIndex >= 0)
            {
                int end = FindSubtreeEnd(oldIndex, id);
                for (int i = oldIndex; i < end; i++)
                    _scratch.Add(_visible[i]);
                _visible.RemoveRange(oldIndex, end - oldIndex);
            }
            GetSiblings(node.ParentId).Remove(id);
            List<ulong> siblings = GetSiblings(newParentId);
            siblingIndex = Math.Clamp(siblingIndex, 0, siblings.Count);
            int insertAt = FindInsertionIndex(newParentId, siblings, siblingIndex);
            siblings.Insert(siblingIndex, id);
            node.ParentId = newParentId;
            if (insertAt >= 0)
            {
                if (_scratch.Count == 0)
                {
                    _scratch.Add(id);
                    if (node.IsExpanded)
                        AppendVisibleDescendants(node, _scratch);
                }
                _visible.InsertRange(insertAt, _scratch);
            }
            _revision++;
            return true;
        }

        private List<ulong> GetSiblings(ulong parentId)
            => parentId == 0 ? _roots : _nodes.TryGetValue(parentId, out UIVirtualTreeNode? parent)
                ? parent.Children : throw new ArgumentException("Parent ID does not exist.", nameof(parentId));

        private int FindInsertionIndex(ulong parentId, List<ulong> siblings, int siblingIndex)
        {
            if (parentId == 0)
                return siblingIndex == 0 ? 0 : FindSubtreeEndOf(siblings[siblingIndex - 1]);
            UIVirtualTreeNode parent = _nodes[parentId];
            int parentIndex = _visible.IndexOf(parentId);
            if (parentIndex < 0 || !parent.IsExpanded)
                return -1;
            return siblingIndex == 0 ? parentIndex + 1 : FindSubtreeEndOf(siblings[siblingIndex - 1]);
        }

        private int FindSubtreeEndOf(ulong id)
        {
            int index = _visible.IndexOf(id);
            return index < 0 ? -1 : FindSubtreeEnd(index, id);
        }

        private int FindSubtreeEnd(int index, ulong id)
        {
            int end = index + 1;
            while (end < _visible.Count && IsDescendantOf(_visible[end], id))
                end++;
            return end;
        }

        private bool IsDescendantOf(ulong id, ulong ancestorId)
        {
            for (ulong parent = _nodes[id].ParentId; parent != 0; parent = _nodes[parent].ParentId)
                if (parent == ancestorId)
                    return true;
            return false;
        }

        private void AppendVisibleDescendants(UIVirtualTreeNode node, List<ulong> result)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                ulong id = node.Children[i];
                result.Add(id);
                UIVirtualTreeNode child = _nodes[id];
                if (child.IsExpanded)
                    AppendVisibleDescendants(child, result);
            }
        }

        private void CollectAllDescendants(ulong id, List<ulong> result)
        {
            result.Add(id);
            List<ulong> children = _nodes[id].Children;
            for (int i = 0; i < children.Count; i++)
                CollectAllDescendants(children[i], result);
        }
    }
}
