using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI.State;
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
            public float Start;
            public float Extent;
        }

        private IVirtualListProvider? _virtualProvider;
        private readonly List<VirtualSlot> _virtualSlots = [];
        private readonly Dictionary<ulong, int> _visibleIndicesById = [];
        private readonly Dictionary<int, VirtualSlot> _slotsByIndex = [];
        private readonly Dictionary<UIBoundableTransform, VirtualSlot> _slotsByRow = new(ReferenceEqualityComparer.Instance);
        // Terminal teardown may be reentered through a different list. This weak-key
        // registry prevents either list from claiming a row until every old lease has
        // finished unbinding; ordinary row admission only performs a lookup.
        private static readonly ConditionalWeakTable<UIBoundableTransform, HashSet<object>> RetiringRows = new();
        private static readonly object RetiringRowsGate = new();
        private readonly Stack<VirtualSlot> _freeSlots = [];
        private int _virtualItemCount;
        private int _virtualFirstIndex;
        private int _virtualEndIndex;
        private int _virtualOverscan = 2;
        private ulong _nextRowIncarnation;
        private ulong _virtualProviderIncarnation;
        private float _virtualViewportExtent;
        private float _virtualScrollOffset;
        // Measured extents use prefix starts: item i covers [starts[i], starts[i + 1] - spacing).
        // The array grows only when the item count grows and is rebuilt only on a revision change.
        private double[] _virtualStarts = [];
        private int _virtualStartsCount = -1;
        private ulong _virtualStartsRevision;
        private ulong _virtualStartsExtentRevision;
        private ulong _virtualStartsIncarnation;
        private double _virtualStartsSpacing;
        private bool _virtualMeasured;
        private float _virtualMeasuredExtent;

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
                _virtualProviderIncarnation++;
                SetField(ref _virtualProvider, value);
                _virtualItemCount = 0;
                ResetVirtualStarts();
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

        /// <summary>
        /// Ends a provider owner's lifetime while retaining the physical row subtree. Unlike
        /// VirtualProvider replacement, this never reparents rows during scene transfer.
        /// Callback failures are reported after every old lease has been canceled and unbound.
        /// </summary>
        public void DetachVirtualProviderForOwnerTeardown()
        {
            GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
            IVirtualListProvider? provider = _virtualProvider;
            VirtualSlot[] oldSlots = [.. _virtualSlots];
            Action<UIBoundableTransform, UIVirtualRowLease>? recycling = VirtualRowRecycling;
            object reservation = new();
            int reserved = 0;
            try
            {
                for (; reserved < oldSlots.Length; reserved++)
                    ReserveRetiringRow(oldSlots[reserved].Row, reservation);
            }
            catch
            {
                for (int index = 0; index < reserved; index++)
                    ReleaseRetiringRow(oldSlots[index].Row, reservation);
                throw;
            }

            // Retire the old owner before invoking user callbacks. A callback may install a
            // replacement provider, which must not be cleared by this operation afterward.
            SetField(ref _virtualProvider, null, publishNotifications: false);
            _virtualProviderIncarnation++;
            VirtualRowRecycling = null;
            _virtualSlots.Clear();
            _slotsByIndex.Clear();
            _slotsByRow.Clear();
            _freeSlots.Clear();
            _visibleIndicesById.Clear();
            _virtualItemCount = 0;
            _virtualFirstIndex = 0;
            _virtualEndIndex = 0;
            _virtualViewportExtent = 0;
            _virtualScrollOffset = 0;
            ResetVirtualStarts();
            List<Exception>? failures = null;
            try { InvalidateMeasure(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            foreach (VirtualSlot slot in oldSlots)
            {
                try
                {
                    UIVirtualRowLease lease = slot.Lease;
                    bool bound = slot.Index >= 0 && lease.ItemId != 0 && provider is not null;
                    slot.Index = -1;
                    slot.Lease = default;
                    slot.Revision = 0;
                    try { slot.Row.Visibility = EVisibility.Hidden; }
                    catch (Exception error) { (failures ??= []).Add(error); }
                    if (!bound)
                        continue;
                    if (recycling is not null)
                        foreach (Delegate callback in recycling.GetInvocationList())
                            try { ((Action<UIBoundableTransform, UIVirtualRowLease>)callback)(slot.Row, lease); }
                            catch (Exception error) { (failures ??= []).Add(error); }
                    try { CancelRowInteractionsForTerminal(slot.Row, ref failures); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                    try { provider!.UnbindRow(slot.Row, lease.ItemId, lease.Incarnation); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                    if (!_slotsByRow.ContainsKey(slot.Row))
                        try { slot.Row.Visibility = EVisibility.Hidden; }
                        catch (Exception error) { (failures ??= []).Add(error); }
                }
                finally { ReleaseRetiringRow(slot.Row, reservation); }
            }
            if (failures is not null)
                throw new AggregateException("Virtual row owner teardown completed with callback failures.", failures);
        }

        private static void CancelRowInteractionsForTerminal(TransformBase transform, ref List<Exception>? failures)
        {
            if (transform.SceneNode is { } node)
                foreach (var component in node.Components.ToArray())
                    if (component is UIInteractableComponent interactable)
                        try { interactable.CancelForRecycle(); }
                        catch (Exception error) { (failures ??= []).Add(error); }
            foreach (TransformBase child in transform.Children.ToArray())
                CancelRowInteractionsForTerminal(child, ref failures);
        }

        private static void ReserveRetiringRow(UIBoundableTransform row, object reservation)
        {
            lock (RetiringRowsGate)
            {
                HashSet<object> reservations = RetiringRows.GetValue(row,
                    static _ => new HashSet<object>(ReferenceEqualityComparer.Instance));
                reservations.Add(reservation);
            }
        }

        private static void ReleaseRetiringRow(UIBoundableTransform row, object reservation)
        {
            lock (RetiringRowsGate)
                if (RetiringRows.TryGetValue(row, out HashSet<object>? reservations) &&
                    reservations.Remove(reservation) && reservations.Count == 0)
                    RetiringRows.Remove(row);
        }

        private static bool IsRetiringRow(UIBoundableTransform row)
        {
            lock (RetiringRowsGate)
                return RetiringRows.TryGetValue(row, out HashSet<object>? reservations) && reservations.Count != 0;
        }

        public int VirtualFirstIndex => _virtualFirstIndex;
        public ulong VirtualProviderIncarnation => _virtualProviderIncarnation;
        public int VirtualEndIndex => _virtualEndIndex;
        public int VirtualItemCount => _virtualItemCount;
        public float VirtualScrollOffset => _virtualScrollOffset;

        /// <summary>Requests an indexed item in the viewport without realizing other rows.</summary>
        public bool TryScrollToVirtualItem(ulong itemId, bool center = false)
        {
            if (itemId == 0 || _virtualProvider is not IVirtualListItemIndex lookup ||
                !lookup.TryGetIndexOfItemId(itemId, out int index) ||
                (uint)index >= (uint)_virtualProvider.Count || _virtualProvider.GetItemId(index) != itemId)
                return false;
            IVirtualListProvider provider = _virtualProvider;
            double spacing = float.IsFinite(ItemSpacing) ? Math.Max(0, ItemSpacing) : 0;
            double start, size, total;
            int count = provider.Count;
            if (provider is IVirtualListItemExtents { HasItemExtents: true } extents)
            {
                GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
                EnsureVirtualStarts(provider, extents, count, spacing);
                start = _virtualStarts[index];
                size = _virtualStarts[index + 1] - start - spacing;
                total = count == 0 ? 0 : _virtualStarts[count] - spacing;
            }
            else
            {
                if (ItemSize is not float fixedSize || !float.IsFinite(fixedSize) || fixedSize <= 0)
                    return false;
                GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
                double stride = fixedSize + spacing;
                start = index * stride;
                size = fixedSize;
                total = (double)count * stride - spacing;
            }
            double end = start + size;
            double viewport = _virtualViewportExtent;
            double scroll = float.IsFinite(ContentScrollOffset) ? Math.Max(0, ContentScrollOffset) : 0;
            if (center && viewport > 0)
                scroll = start - (viewport - size) * 0.5;
            else if (start < scroll)
                scroll = start;
            else if (viewport <= 0 || end > scroll + viewport)
                scroll = viewport > 0 ? end - viewport : start;
            double maxScroll = Math.Max(0, total - viewport);
            ContentScrollOffset = (float)Math.Clamp(scroll, 0, Math.Min(maxScroll, float.MaxValue));
            return true;
        }

        /// <summary>Gets the current lease only after owner-thread synchronization realizes this ID.</summary>
        public bool TryGetRealizedVirtualRow(ulong itemId, out UIBoundableTransform? row,
            out UIVirtualRowLease lease)
        {
            GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
            if (itemId != 0 && _virtualProvider is not null &&
                _visibleIndicesById.TryGetValue(itemId, out int index) &&
                _slotsByIndex.TryGetValue(index, out VirtualSlot? slot) &&
                slot.Lease.ItemId == itemId && slot.Index == index &&
                slot.Revision == _virtualProvider.Revision &&
                (uint)index < (uint)_virtualProvider.Count && _virtualProvider.GetItemId(index) == itemId)
            {
                row = slot.Row;
                lease = slot.Lease;
                return true;
            }
            row = null;
            lease = default;
            return false;
        }

        /// <summary>
        /// Gets extent from the cached count and fixed item size, or from the measured extents of
        /// the last synchronization. No source items are visited.
        /// </summary>
        public float VirtualContentExtent
        {
            get
            {
                if (_virtualProvider is null || _virtualItemCount == 0)
                    return 0;
                if (_virtualMeasured)
                    return _virtualMeasuredExtent;
                if (ItemSize is not float size || !float.IsFinite(size) || size <= 0)
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
            IVirtualListItemExtents? extents = provider is IVirtualListItemExtents { HasItemExtents: true } measured ? measured : null;
            float size = 0;
            if (ItemAlignment != EListAlignment.TopOrLeft ||
                (extents is null && (ItemSize is not float fixedSize || !float.IsFinite(size = fixedSize) || size <= 0)))
                throw new InvalidOperationException(
                    "Provider-backed lists require TopOrLeft alignment and either measured item extents or a positive fixed ItemSize.");

            int count = provider.Count;
            if (count < 0)
                throw new InvalidOperationException("Virtual list item count cannot be negative.");
            float spacing = float.IsFinite(ItemSpacing) ? Math.Max(0, ItemSpacing) : 0;
            double viewport = float.IsFinite(viewportExtent) ? Math.Max(0, viewportExtent) : 0;
            double scroll = float.IsFinite(ContentScrollOffset) ? Math.Max(0, ContentScrollOffset) : 0;
            int first, end;
            float measuredExtent = 0;
            if (extents is not null)
            {
                EnsureVirtualStarts(provider, extents, count, spacing);
                double total = count == 0 ? 0 : _virtualStarts[count] - spacing;
                measuredExtent = (float)Math.Min(total, float.MaxValue);
                scroll = Math.Min(scroll, Math.Max(0, total - viewport));
                first = Math.Max(0, FindVirtualItemAt(scroll, count) - _virtualOverscan);
                end = (int)Math.Min(count, Math.Max(first, (long)FindVirtualItemStartingAtOrAfter(scroll + viewport, count) + _virtualOverscan));
            }
            else
            {
                double stride = (double)size + spacing;
                double maxScroll = Math.Max(0, (double)count * stride - spacing - viewport);
                scroll = Math.Min(scroll, maxScroll);
                first = (int)Math.Min(count, Math.Max(0, Math.Floor(scroll / stride) - _virtualOverscan));
                end = (int)Math.Min(count, Math.Max(first, Math.Ceiling((scroll + viewport) / stride) + _virtualOverscan));
            }
            if (end - first > 4096)
                throw new InvalidOperationException("Virtual list viewport exceeds the 4096-row realization limit.");

            bool countChanged = count != _virtualItemCount || (extents is not null) != _virtualMeasured ||
                (extents is not null && measuredExtent != _virtualMeasuredExtent);
            _virtualMeasured = extents is not null;
            _virtualMeasuredExtent = measuredExtent;
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
                    // Published hit records carry the lease; a sparse capture must revisit this row.
                    slot.Row.RecordUIChange(UIChangeDomain.HitTest);
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

            // Placement is read from the slot by owner arrangement and by worker edge capture.
            bool placementChanged = false;
            double fixedStride = (double)size + spacing;
            for (int i = 0; i < _virtualSlots.Count; i++)
            {
                VirtualSlot slot = _virtualSlots[i];
                if (slot.Index < 0)
                    continue;
                float start, extent;
                if (extents is not null)
                {
                    start = (float)_virtualStarts[slot.Index];
                    extent = (float)Math.Max(0, _virtualStarts[slot.Index + 1] - _virtualStarts[slot.Index] - spacing);
                }
                else
                {
                    start = (float)(slot.Index * fixedStride);
                    extent = size;
                }
                if (slot.Start == start && slot.Extent == extent)
                    continue;
                slot.Start = start;
                slot.Extent = extent;
                placementChanged = true;
            }

            if (countChanged)
                InvalidateMeasure();
            else if (rangeChanged || positionChanged || placementChanged)
                InvalidateArrange();
        }

        /// <summary>
        /// Gets the logical main-axis start and extent of a realized row. The start does not include
        /// the scroll offset. Layout capture uses these values so worker and owner placement agree.
        /// </summary>
        public bool TryGetVirtualRowPlacement(UIBoundableTransform row, out int index, out float start, out float extent)
        {
            if (_slotsByRow.TryGetValue(row, out VirtualSlot? slot) && slot.Index >= 0)
            {
                index = slot.Index;
                start = slot.Start;
                extent = slot.Extent;
                return true;
            }
            index = -1;
            start = 0;
            extent = 0;
            return false;
        }

        private void ResetVirtualStarts()
        {
            _virtualStartsCount = -1;
            _virtualMeasured = false;
            _virtualMeasuredExtent = 0;
        }

        private void EnsureVirtualStarts(IVirtualListProvider provider, IVirtualListItemExtents extents, int count, double spacing)
        {
            ulong extentRevision = extents.ExtentRevision;
            if (_virtualStartsCount == count && _virtualStartsRevision == provider.Revision &&
                _virtualStartsExtentRevision == extentRevision && _virtualStartsIncarnation == _virtualProviderIncarnation &&
                _virtualStartsSpacing == spacing)
                return;
            if (count >= Array.MaxLength)
                throw new InvalidOperationException("Virtual list item count exceeds the measured extent limit.");
            if (_virtualStarts.Length < count + 1)
                _virtualStarts = new double[Math.Max(count + 1L, Math.Min(Array.MaxLength, _virtualStarts.Length * 2L))];
            _virtualStartsCount = -1;
            double cursor = 0;
            for (int index = 0; index < count; index++)
            {
                float extent = extents.GetItemExtent(index);
                if (!float.IsFinite(extent) || extent < 0)
                    throw new InvalidOperationException("Virtual list item extents must be finite and not negative.");
                _virtualStarts[index] = cursor;
                cursor += extent + spacing;
            }
            _virtualStarts[count] = cursor;
            _virtualStartsCount = count;
            _virtualStartsRevision = provider.Revision;
            _virtualStartsExtentRevision = extentRevision;
            _virtualStartsIncarnation = _virtualProviderIncarnation;
            _virtualStartsSpacing = spacing;
        }

        /// <summary>Gets the first item whose slot, including spacing, ends after the position.</summary>
        private int FindVirtualItemAt(double position, int count)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_virtualStarts[middle + 1] > position)
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
        }

        /// <summary>Gets the first item that starts at or after the position, or the count.</summary>
        private int FindVirtualItemStartingAtOrAfter(double position, int count)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_virtualStarts[middle] >= position)
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
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
            if (IsRetiringRow(row))
                throw new InvalidOperationException("A virtual row cannot be reused until its previous lease finishes unbinding.");
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
            slot.Start = 0;
            slot.Extent = 0;
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
            float offset = _virtualScrollOffset;
            for (int i = 0; i < _virtualSlots.Count; i++)
            {
                VirtualSlot slot = _virtualSlots[i];
                if (slot.Index < 0)
                    continue;
                float size = slot.Extent;
                float position = slot.Start - offset;
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
