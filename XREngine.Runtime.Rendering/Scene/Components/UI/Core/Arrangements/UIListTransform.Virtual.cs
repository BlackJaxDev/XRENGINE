using System;
using System.Collections.Generic;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI
{
    public partial class UIListTransform
    {
        private sealed class VirtualSlot(UIBoundableTransform row)
        {
            public UIBoundableTransform Row { get; } = row;
            public int Index = -1;
            public UIVirtualRowLease Lease;
            public ulong Revision;
        }

        private IVirtualListProvider? _virtualProvider;
        private readonly List<VirtualSlot> _virtualSlots = [];
        private readonly Dictionary<ulong, int> _visibleIndicesById = [];
        private readonly Dictionary<int, VirtualSlot> _slotsByIndex = [];
        private readonly Dictionary<UIBoundableTransform, VirtualSlot> _slotsByRow = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<VirtualSlot> _freeSlots = [];
        private int _virtualItemCount;
        private int _virtualFirstIndex;
        private int _virtualEndIndex;
        private int _virtualOverscan = 2;
        private ulong _nextRowIncarnation;
        private float _virtualViewportExtent;
        private float _virtualScrollOffset;

        /// <summary>
        /// Provider-backed rows use one subtree per visible item and do not use the legacy Virtual flag.
        /// Set the provider on the canvas owner thread.
        /// </summary>
        [YamlIgnore]
        public IVirtualListProvider? VirtualProvider
        {
            get => _virtualProvider;
            set
            {
                if (ReferenceEquals(_virtualProvider, value))
                    return;
                GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
                ReleaseVirtualRows();
                SetField(ref _virtualProvider, value);
                _virtualItemCount = 0;
                InvalidateMeasure();
            }
        }

        public int VirtualOverscan
        {
            get => _virtualOverscan;
            set
            {
                int next = Math.Clamp(value, 0, 256);
                if (_virtualOverscan == next)
                    return;
                GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
                SetField(ref _virtualOverscan, next);
                InvalidateArrange();
            }
        }

        /// <summary>
        /// Runs before a row is unbound or assigned to another item. Consumers must cancel row input and editing here.
        /// </summary>
        public event Action<UIBoundableTransform, UIVirtualRowLease>? VirtualRowRecycling;

        public int VirtualFirstIndex => _virtualFirstIndex;
        public int VirtualEndIndex => _virtualEndIndex;
        public int VirtualItemCount => _virtualItemCount;
        public float VirtualScrollOffset => _virtualScrollOffset;

        /// <summary>
        /// Gets extent from cached count and fixed item size. No source items are visited.
        /// </summary>
        public float VirtualContentExtent
        {
            get
            {
                if (_virtualProvider is null || _virtualItemCount == 0 || ItemSize is not float size || !float.IsFinite(size) || size <= 0)
                    return 0;
                double spacing = float.IsFinite(ItemSpacing) ? Math.Max(0, ItemSpacing) : 0;
                double extent = (double)_virtualItemCount * size + (double)(_virtualItemCount - 1) * spacing;
                return (float)Math.Min(extent, float.MaxValue);
            }
        }

        /// <summary>
        /// Synchronizes a bounded row pool on the owner thread before layout snapshot capture.
        /// A worker must use only the resulting row transforms and cached item count.
        /// </summary>
        public void SynchronizeVirtualRows(float viewportExtent)
        {
            IVirtualListProvider? provider = _virtualProvider;
            if (provider is null)
                return;
            GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
            if (ItemAlignment != EListAlignment.TopOrLeft || ItemSize is not float size || !float.IsFinite(size) || size <= 0)
                throw new InvalidOperationException("Provider-backed lists require a positive fixed ItemSize and TopOrLeft alignment.");

            int count = provider.Count;
            if (count < 0)
                throw new InvalidOperationException("Virtual list item count cannot be negative.");
            float spacing = float.IsFinite(ItemSpacing) ? Math.Max(0, ItemSpacing) : 0;
            double stride = (double)size + spacing;
            double viewport = float.IsFinite(viewportExtent) ? Math.Max(0, viewportExtent) : 0;
            double scroll = float.IsFinite(ContentScrollOffset) ? Math.Max(0, ContentScrollOffset) : 0;
            double maxScroll = Math.Max(0, (double)count * stride - spacing - viewport);
            scroll = Math.Min(scroll, maxScroll);
            int first = (int)Math.Min(count, Math.Max(0, Math.Floor(scroll / stride) - _virtualOverscan));
            int end = (int)Math.Min(count, Math.Max(first, Math.Ceiling((scroll + viewport) / stride) + _virtualOverscan));
            if (end - first > 4096)
                throw new InvalidOperationException("Virtual list viewport exceeds the 4096-row realization limit.");

            bool countChanged = count != _virtualItemCount;
            bool rangeChanged = first != _virtualFirstIndex || end != _virtualEndIndex;
            bool positionChanged = _virtualViewportExtent != (float)viewport || _virtualScrollOffset != (float)scroll;
            _virtualItemCount = count;
            _virtualViewportExtent = (float)viewport;
            _virtualScrollOffset = (float)scroll;
            _virtualFirstIndex = first;
            _virtualEndIndex = end;
            _visibleIndicesById.Clear();
            for (int index = first; index < end; index++)
            {
                ulong itemId = provider.GetItemId(index);
                if (itemId == 0 || !_visibleIndicesById.TryAdd(itemId, index))
                    throw new InvalidOperationException("Virtual list visible item IDs must be unique and nonzero.");
            }

            // A stable item keeps its lease when an insertion changes its visible index.
            for (int i = 0; i < _virtualSlots.Count; i++)
            {
                VirtualSlot slot = _virtualSlots[i];
                if (slot.Index < 0)
                    continue;
                if (_visibleIndicesById.TryGetValue(slot.Lease.ItemId, out int newIndex))
                    slot.Index = newIndex;
                else
                    ReleaseSlot(provider, slot);
            }

            _slotsByIndex.Clear();
            _freeSlots.Clear();
            for (int i = 0; i < _virtualSlots.Count; i++)
            {
                VirtualSlot slot = _virtualSlots[i];
                if (slot.Index >= 0)
                    _slotsByIndex.Add(slot.Index, slot);
                else
                    _freeSlots.Push(slot);
            }

            for (int index = first; index < end; index++)
            {
                ulong itemId = provider.GetItemId(index);
                if (!_slotsByIndex.TryGetValue(index, out VirtualSlot? slot))
                {
                    slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : CreateSlot(provider);
                    slot.Index = index;
                    slot.Lease = new UIVirtualRowLease(itemId, ++_nextRowIncarnation);
                    slot.Revision = provider.Revision;
                    provider.BindRow(slot.Row, index, itemId, slot.Lease.Incarnation);
                    slot.Row.Visibility = EVisibility.Visible;
                    _slotsByIndex.Add(index, slot);
                }
                else if (slot.Revision != provider.Revision)
                {
                    slot.Revision = provider.Revision;
                    provider.BindRow(slot.Row, index, itemId, slot.Lease.Incarnation);
                }
            }

            if (countChanged)
                InvalidateMeasure();
            else if (rangeChanged || positionChanged)
                InvalidateArrange();
        }

        public bool TryGetVirtualRowLease(UIBoundableTransform row, out UIVirtualRowLease lease)
        {
            if (_slotsByRow.TryGetValue(row, out VirtualSlot? slot) && slot.Index >= 0)
            {
                lease = slot.Lease;
                return true;
            }
            lease = default;
            return false;
        }

        public bool TryGetVirtualRowIndex(UIBoundableTransform row, out int index)
        {
            if (_slotsByRow.TryGetValue(row, out VirtualSlot? slot) && slot.Index >= 0)
            {
                index = slot.Index;
                return true;
            }
            index = -1;
            return false;
        }

        private VirtualSlot CreateSlot(IVirtualListProvider provider)
        {
            UIBoundableTransform row = provider.CreateRow() ?? throw new InvalidOperationException("The virtual list provider returned a null row.");
            row.Parent = this;
            VirtualSlot slot = new(row);
            _virtualSlots.Add(slot);
            _slotsByRow.Add(row, slot);
            return slot;
        }

        private void ReleaseSlot(IVirtualListProvider provider, VirtualSlot slot)
        {
            if (slot.Index < 0)
                return;
            VirtualRowRecycling?.Invoke(slot.Row, slot.Lease);
            CancelRowInteractions(slot.Row);
            provider.UnbindRow(slot.Row, slot.Lease.ItemId, slot.Lease.Incarnation);
            slot.Row.Visibility = EVisibility.Hidden;
            slot.Index = -1;
            slot.Lease = default;
            slot.Revision = 0;
        }

        private void ReleaseVirtualRows()
        {
            if (_virtualProvider is not null)
                for (int i = 0; i < _virtualSlots.Count; i++)
                    ReleaseSlot(_virtualProvider, _virtualSlots[i]);
            for (int i = 0; i < _virtualSlots.Count; i++)
                _virtualSlots[i].Row.Parent = null;
            _virtualSlots.Clear();
            _slotsByIndex.Clear();
            _slotsByRow.Clear();
            _freeSlots.Clear();
            _visibleIndicesById.Clear();
        }

        private void ArrangeVirtualRows(BoundingRectangleF region)
        {
            float size = ItemSize ?? 0;
            float stride = size + (float.IsFinite(ItemSpacing) ? Math.Max(0, ItemSpacing) : 0);
            float offset = _virtualScrollOffset;
            for (int i = 0; i < _virtualSlots.Count; i++)
            {
                VirtualSlot slot = _virtualSlots[i];
                if (slot.Index < 0)
                    continue;
                float position = slot.Index * stride - offset;
                float x = _horizontal ? position : 0;
                float y = _horizontal ? 0 : region.Height - position - size;
                if (slot.Row.PlacementInfo is UIListChildPlacementInfo placement)
                    placement.BottomOrLeftOffset = _horizontal ? x : y;
                slot.Row.Arrange(new BoundingRectangleF(x, y, _horizontal ? size : region.Width, _horizontal ? region.Height : size));
            }
        }

        private static void CancelRowInteractions(TransformBase transform)
        {
            if (transform.SceneNode is { } node)
                for (int i = 0; node.GetComponentAtIndex(i) is { } component; i++)
                    if (component is UIInteractableComponent interactable)
                        interactable.CancelForRecycle();
            for (int i = 0; i < transform.Children.Count; i++)
                CancelRowInteractions(transform.Children[i]);
        }
    }
}
