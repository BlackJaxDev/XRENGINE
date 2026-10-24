using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Reuses a bounded set of horizontal cell rows under UIListTransform virtualization.</summary>
public sealed class UIVirtualTableProvider<T> : IVirtualListProvider, IUIVirtualAccessibilityProvider, IVirtualListItemIndex
{
    private sealed class RowState
    {
        public required UIBoundableTransform[] Cells;
        public required UIVirtualTableRowComponent Interaction;
        public ulong BoundId;
        public ulong BoundIncarnation;
        public ulong ProviderIncarnation;
        public ulong BindingEpoch;
        public UIListTransform? BoundList;
        public required bool[] CellBound;
        public int TransitionDepth;
        public bool DrainingTransitions;
        public bool HasPendingBind;
        public int PendingIndex;
        public ulong PendingId;
        public ulong PendingIncarnation;
        public bool HasPendingUnbind;
        public ulong PendingUnbindId;
        public ulong PendingUnbindIncarnation;
    }

    private readonly IUIVirtualTableSource<T> _source;
    private readonly UIVirtualTableColumn<T>[] _columns;
    private readonly ConditionalWeakTable<UIBoundableTransform, RowState> _rows = new();
    private readonly List<WeakReference<RowState>> _liveRows = [];
    private ulong? _selectedId;

    public UIVirtualTableProvider(IUIVirtualTableSource<T> source,
        params UIVirtualTableColumn<T>[] columns)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (columns is null || columns.Length == 0)
            throw new ArgumentException("A table needs at least one column.", nameof(columns));
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (UIVirtualTableColumn<T> column in columns)
            if (column is null || !keys.Add(column.Key))
                throw new ArgumentException("Table column keys must be unique.", nameof(columns));
        _columns = [.. columns];
    }

    public int Count => _source.Count;
    public ulong Revision => _source.Revision;
    public int AccessibilityItemCount => Count;
    public Func<T, string>? AccessibleRowName { get; set; }
    public bool TryGetAccessibilityItem(int index, out UIVirtualAccessibilityItem item)
    {
        item = default;
        if ((uint)index >= (uint)Count)
            return false;
        ulong id = _source.GetItemId(index);
        item = new(id, AccessibleRowName?.Invoke(_source.GetItem(index)) ??
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EUIAccessibilityRole.TableRow, IsSelected: _selectedId == id);
        return id != 0;
    }
    public IReadOnlyList<UIVirtualTableColumn<T>> Columns => _columns;
    public ulong? SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value)
                return;
            _selectedId = value;
            for (int i = _liveRows.Count - 1; i >= 0; i--)
            {
                if (_liveRows[i].TryGetTarget(out RowState? state))
                    state.Interaction.IsSelected = state.BoundId != 0 && state.BoundId == value;
                else
                    _liveRows.RemoveAt(i);
            }
            SelectionChanged?.Invoke(value);
        }
    }

    public event Action<ulong>? Activated;
    public event Action<ulong?>? SelectionChanged;
    public event Action<ulong, UIVirtualTableRowComponent, Vector2>? RowPointerMoved;
    public event Action<UIVirtualTableRowComponent>? RowPointerLeft;
    public ulong GetItemId(int index) => _source.GetItemId(index);

    public bool TryGetIndexOfItemId(ulong itemId, out int index)
    {
        index = -1;
        return itemId != 0 && _source is IVirtualListItemIndex lookup &&
            lookup.TryGetIndexOfItemId(itemId, out index) &&
            (uint)index < (uint)_source.Count && _source.GetItemId(index) == itemId;
    }

    public UIBoundableTransform CreateRow()
    {
        SceneNode row = new("VirtualTableRow");
        UIListTransform layout = row.SetTransform<UIListTransform>();
        layout.DisplayHorizontal = true;
        layout.ItemAlignment = EListAlignment.TopOrLeft;
        layout.MaxAnchor = Vector2.One;
        UIVirtualTableRowComponent interaction = row.AddComponent<UIVirtualTableRowComponent>()!;
        UIBoundableTransform[] cells = new UIBoundableTransform[_columns.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            UIVirtualTableColumn<T> column = _columns[i];
            UIBoundableTransform cell = column.CreateCell(row);
            if (cell.SceneNode?.Parent != row)
                throw new InvalidOperationException($"Column {column.Key} must create a child of the row.");
            cell.Width = column.Width;
            cell.MinAnchor = Vector2.Zero;
            cell.MaxAnchor = new Vector2(0, 1);
            cells[i] = cell;
        }
        RowState state = new() { Cells = cells, Interaction = interaction, CellBound = new bool[cells.Length] };
        interaction.NeedsMouseMove = true;
        interaction.MouseMove += (x, y, _) =>
        {
            if (state.BoundId != 0)
                RowPointerMoved?.Invoke(state.BoundId, interaction, new Vector2(x, y));
        };
        interaction.MouseDirectOverlapLeave += _ => RowPointerLeft?.Invoke(interaction);
        interaction.InteractAction += _ =>
        {
            if (state.BoundId == 0)
                return;
            SelectedId = state.BoundId;
            Activated?.Invoke(state.BoundId);
        };
        interaction.GotFocus += _ =>
        {
            UIListTransform? list = state.BoundList;
            ulong id = state.BoundId;
            if (list is null || id == 0 || !interaction.CanInteract ||
                !UIVirtualRowNavigationLease.IsCurrent(interaction, this, list,
                    id, state.BoundIncarnation, state.ProviderIncarnation) ||
                interaction.UserInterfaceCanvas?.GetInputComponent() is not { } input ||
                !input.IsDispatchingVirtualItemFocus(list, id, interaction))
                return;
            SelectedId = id;
        };
        _rows.Add(layout, state);
        if (_liveRows.Count >= 128)
            for (int i = _liveRows.Count - 1; i >= 0; i--)
                if (!_liveRows[i].TryGetTarget(out _))
                    _liveRows.RemoveAt(i);
        _liveRows.Add(new WeakReference<RowState>(state));
        return layout;
    }

    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
    {
        if (itemId == 0 || incarnation == 0 || !_rows.TryGetValue(row, out RowState? state))
            throw new InvalidOperationException("The virtual table row lease does not match its source.");
        ulong admissionEpoch = state.BindingEpoch;
        int count = _source.Count;
        if (state.BindingEpoch != admissionEpoch)
            return;
        if ((uint)index >= (uint)count)
            throw new InvalidOperationException("The virtual table row index is outside its source.");
        ulong sourceId = _source.GetItemId(index);
        if (state.BindingEpoch != admissionEpoch)
            return;
        if (sourceId != itemId)
            throw new InvalidOperationException("The virtual table row ID does not match its source.");
        UIListTransform? list = row.Parent as UIListTransform;
        if (row.Parent is not null && list is null ||
            list is not null && (!ReferenceEquals(list.VirtualProvider, this) ||
                !list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) ||
                lease.ItemId != itemId || lease.Incarnation != incarnation))
            throw new InvalidOperationException("The virtual table row has no matching list lease.");
        if (state.TransitionDepth != 0)
        {
            state.HasPendingBind = true;
            state.PendingIndex = index;
            state.PendingId = itemId;
            state.PendingIncarnation = incarnation;
            return;
        }
        ulong providerIncarnation = list?.VirtualProviderIncarnation ?? 0;
        ulong sourceRevision = _source.Revision;
        if (state.BindingEpoch != admissionEpoch)
            return;
        if (state.BoundId == itemId && state.BoundIncarnation == incarnation &&
            ReferenceEquals(state.BoundList, list) &&
            state.ProviderIncarnation == providerIncarnation &&
            IsCurrentLease(row, list, itemId, incarnation, providerIncarnation))
        {
            RefreshRow(row, state, index, itemId, incarnation, sourceRevision);
            return;
        }
        if (state.BoundId != 0)
        {
            ulong priorEpoch = state.BindingEpoch;
            UnbindRow(row, state.BoundId, state.BoundIncarnation);
            if (state.BoundId != 0 || state.BindingEpoch != priorEpoch + 1)
                return;
        }
        ulong beforeGetEpoch = state.BindingEpoch;
        T item = _source.GetItem(index);
        if (state.BindingEpoch != beforeGetEpoch || state.BoundId != 0)
            return;
        ulong observedRevision = _source.Revision;
        if (state.BindingEpoch != beforeGetEpoch || observedRevision != sourceRevision ||
            !IsCurrentLease(row, list, itemId, incarnation, providerIncarnation))
            return;
        state.BoundId = itemId;
        state.BoundIncarnation = incarnation;
        state.BoundList = list;
        state.ProviderIncarnation = providerIncarnation;
        ulong epoch = ++state.BindingEpoch;
        int bound = 0;
        try
        {
            for (; bound < _columns.Length; bound++)
            {
                state.CellBound[bound] = true;
                InvokeCellUpdate(row, state, _columns[bound].BindCell,
                    state.Cells[bound], item, itemId);
                if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                        providerIncarnation, sourceRevision, epoch))
                {
                    CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                    return;
                }
            }
            string? label = AccessibleRowName?.Invoke(item);
            if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                    providerIncarnation, sourceRevision, epoch))
            {
                CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                return;
            }
            state.Interaction.AccessibilityLabel = label;
            if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                    providerIncarnation, sourceRevision, epoch))
            {
                CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                return;
            }
            state.Interaction.IsSelected = itemId == _selectedId;
            if (list is not null && IsCurrentBinding(row, state, itemId, incarnation, list,
                    providerIncarnation, sourceRevision, epoch))
                state.Interaction.BindNavigation(this, list, itemId, incarnation);
        }
        catch
        {
            CleanupFailedBinding(row, state, itemId, incarnation, epoch, preserveOriginalFailure: true);
            throw;
        }
    }

    private void RefreshRow(UIBoundableTransform row, RowState state, int index,
        ulong itemId, ulong incarnation, ulong sourceRevision)
    {
        ulong beforeGetEpoch = state.BindingEpoch;
        T item = _source.GetItem(index);
        if (state.BindingEpoch != beforeGetEpoch)
            return;
        UIListTransform? list = state.BoundList;
        ulong providerIncarnation = state.ProviderIncarnation;
        ulong observedRevision = _source.Revision;
        if (state.BindingEpoch != beforeGetEpoch)
            return;
        if (observedRevision != sourceRevision ||
            !IsCurrentLease(row, list, itemId, incarnation, providerIncarnation))
        {
            CleanupFailedBinding(row, state, itemId, incarnation, state.BindingEpoch);
            return;
        }
        ulong epoch = ++state.BindingEpoch;
        try
        {
            for (int column = 0; column < _columns.Length; column++)
            {
                UIVirtualTableColumn<T> definition = _columns[column];
                if (definition.RefreshCell is { } refresh)
                    InvokeCellUpdate(row, state, refresh, state.Cells[column], item, itemId);
                else
                {
                    state.CellBound[column] = false;
                    state.TransitionDepth++;
                    bool retired = false;
                    try
                    {
                        definition.UnbindCell(state.Cells[column]);
                        retired = true;
                    }
                    finally
                    {
                        state.TransitionDepth--;
                        if (!retired)
                            state.HasPendingBind = false;
                    }
                    DrainPendingTransitions(row, state);
                    if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                            providerIncarnation, sourceRevision, epoch))
                    {
                        CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                        return;
                    }
                    state.CellBound[column] = true;
                    InvokeCellUpdate(row, state, definition.BindCell,
                        state.Cells[column], item, itemId);
                }
                if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                        providerIncarnation, sourceRevision, epoch))
                {
                    CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                    return;
                }
            }
            string? label = AccessibleRowName?.Invoke(item);
            if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                    providerIncarnation, sourceRevision, epoch))
            {
                CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                return;
            }
            state.Interaction.AccessibilityLabel = label;
            if (!IsCurrentBinding(row, state, itemId, incarnation, list,
                    providerIncarnation, sourceRevision, epoch))
            {
                CleanupFailedBinding(row, state, itemId, incarnation, epoch);
                return;
            }
            state.Interaction.IsSelected = itemId == _selectedId;
        }
        catch
        {
            CleanupFailedBinding(row, state, itemId, incarnation, epoch, preserveOriginalFailure: true);
            throw;
        }
    }

    private bool IsCurrentBinding(UIBoundableTransform row, RowState state, ulong itemId,
        ulong incarnation, UIListTransform? list, ulong providerIncarnation,
        ulong sourceRevision, ulong epoch)
        => !state.HasPendingBind && !state.HasPendingUnbind &&
            state.BindingEpoch == epoch && state.BoundId == itemId &&
            state.BoundIncarnation == incarnation && ReferenceEquals(state.BoundList, list) &&
            state.ProviderIncarnation == providerIncarnation && _source.Revision == sourceRevision &&
            state.BindingEpoch == epoch && state.BoundId == itemId &&
            IsCurrentLease(row, list, itemId, incarnation, providerIncarnation);

    private void InvokeCellUpdate(UIBoundableTransform row, RowState state,
        Action<UIBoundableTransform, T, ulong> callback, UIBoundableTransform cell, T item, ulong itemId)
    {
        state.TransitionDepth++;
        bool completed = false;
        try
        {
            callback(cell, item, itemId);
            completed = true;
        }
        finally
        {
            state.TransitionDepth--;
            if (completed)
                DrainPendingTransitions(row, state);
            else
            {
                state.HasPendingBind = false;
                state.HasPendingUnbind = false;
            }
        }
    }

    private bool IsCurrentLease(UIBoundableTransform row, UIListTransform? list,
        ulong itemId, ulong incarnation, ulong providerIncarnation)
        => row.SceneNode is { IsDestroyed: false, IsDestroyQueued: false } &&
            (list is null ? row.Parent is null :
                ReferenceEquals(row.Parent, list) && ReferenceEquals(list.VirtualProvider, this) &&
                list.VirtualProviderIncarnation == providerIncarnation &&
                list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) &&
                lease.ItemId == itemId && lease.Incarnation == incarnation);

    private void CleanupFailedBinding(UIBoundableTransform row, RowState state,
        ulong itemId, ulong incarnation, ulong epoch, bool preserveOriginalFailure = false)
    {
        if (state.BindingEpoch != epoch || state.BoundId != itemId ||
            state.BoundIncarnation != incarnation)
            return;
        try { UnbindRow(row, itemId, incarnation); }
        catch (Exception error) when (preserveOriginalFailure)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        if (!_rows.TryGetValue(row, out RowState? state) || state.BoundId != itemId ||
            state.BoundIncarnation != incarnation)
            return;
        if (state.TransitionDepth != 0)
        {
            state.HasPendingUnbind = true;
            state.PendingUnbindId = itemId;
            state.PendingUnbindIncarnation = incarnation;
            return;
        }
        state.BoundId = 0;
        state.BoundIncarnation = 0;
        state.BoundList = null;
        state.ProviderIncarnation = 0;
        state.Interaction.UnbindNavigation();
        state.BindingEpoch++;
        state.TransitionDepth++;
        List<Exception>? failures = null;
        try
        {
            for (int i = _columns.Length - 1; i >= 0; i--)
            {
                if (!state.CellBound[i])
                    continue;
                state.CellBound[i] = false;
                try { _columns[i].UnbindCell(state.Cells[i]); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            try { state.Interaction.AccessibilityLabel = null; }
            catch (Exception error) { (failures ??= []).Add(error); }
            try { state.Interaction.IsSelected = false; }
            catch (Exception error) { (failures ??= []).Add(error); }
            try { RowPointerLeft?.Invoke(state.Interaction); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        finally { state.TransitionDepth--; }
        if (failures is null)
        {
            try { DrainPendingTransitions(row, state); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        else
        {
            state.HasPendingBind = false;
            state.HasPendingUnbind = false;
        }
        if (failures is not null)
            throw new AggregateException("One or more virtual table cells failed to unbind.", failures);
    }

    private void DrainPendingTransitions(UIBoundableTransform row, RowState state)
    {
        if (state.TransitionDepth != 0 || state.DrainingTransitions)
            return;
        state.DrainingTransitions = true;
        try
        {
            for (int transition = 0; state.HasPendingUnbind || state.HasPendingBind; transition++)
            {
                if (transition >= 32)
                {
                    state.HasPendingUnbind = false;
                    state.HasPendingBind = false;
                    throw new InvalidOperationException("Virtual table row callbacks requested too many transitions.");
                }
                if (state.HasPendingUnbind)
                {
                    ulong oldId = state.PendingUnbindId;
                    ulong oldIncarnation = state.PendingUnbindIncarnation;
                    state.HasPendingUnbind = false;
                    UnbindRow(row, oldId, oldIncarnation);
                    continue;
                }
                int index = state.PendingIndex;
                ulong itemId = state.PendingId;
                ulong incarnation = state.PendingIncarnation;
                state.HasPendingBind = false;
                BindRow(row, index, itemId, incarnation);
            }
        }
        finally { state.DrainingTransitions = false; }
    }
}
