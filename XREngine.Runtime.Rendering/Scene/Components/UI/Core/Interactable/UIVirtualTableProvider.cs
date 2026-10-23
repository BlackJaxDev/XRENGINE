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
        RowState state = new() { Cells = cells, Interaction = interaction };
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
        if (itemId == 0 || _source.GetItemId(index) != itemId ||
            !_rows.TryGetValue(row, out RowState? state))
            throw new InvalidOperationException("The virtual table row lease does not match its source.");
        if (state.BoundId != 0)
            UnbindRow(row, state.BoundId, incarnation);
        T item = _source.GetItem(index);
        int bound = 0;
        try
        {
            for (; bound < _columns.Length; bound++)
                _columns[bound].BindCell(state.Cells[bound], item, itemId);
            state.Interaction.AccessibilityLabel = AccessibleRowName?.Invoke(item);
            state.Interaction.IsSelected = itemId == _selectedId;
            state.BoundId = itemId;
        }
        catch
        {
            state.BoundId = 0;
            state.Interaction.AccessibilityLabel = null;
            state.Interaction.IsSelected = false;
            List<Exception>? cleanupFailures = null;
            for (int i = Math.Min(bound, _columns.Length - 1); i >= 0; i--)
                try { _columns[i].UnbindCell(state.Cells[i]); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
            if (cleanupFailures is not null)
                System.Diagnostics.Debug.WriteLine(new AggregateException(
                    "Virtual table cell cleanup failed after binding failed.", cleanupFailures));
            throw;
        }
    }

    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        if (!_rows.TryGetValue(row, out RowState? state) || state.BoundId != itemId)
            return;
        state.BoundId = 0;
        state.Interaction.AccessibilityLabel = null;
        state.Interaction.IsSelected = false;
        List<Exception>? failures = null;
        try { RowPointerLeft?.Invoke(state.Interaction); }
        catch (Exception error) { (failures ??= []).Add(error); }
        for (int i = _columns.Length - 1; i >= 0; i--)
            try { _columns[i].UnbindCell(state.Cells[i]); }
            catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null)
            throw new AggregateException("One or more virtual table cells failed to unbind.", failures);
    }
}
