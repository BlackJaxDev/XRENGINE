using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Two-column virtual property grid with retained value editor cells.</summary>
public sealed class UIVirtualPropertyGridProvider<T> : IVirtualListProvider, IUIVirtualAccessibilityProvider, IVirtualListItemIndex
{
    private readonly UIVirtualTableProvider<T> _table;

    public UIVirtualPropertyGridProvider(IUIVirtualTableSource<T> source,
        Func<T, string> getName, float nameWidth, float valueWidth,
        Func<SceneNode, UIBoundableTransform> createValueEditor,
        Action<UIBoundableTransform, T, ulong> bindValueEditor,
        Action<UIBoundableTransform> unbindValueEditor,
        Action<UIBoundableTransform, T, ulong>? refreshValueEditor = null)
    {
        ArgumentNullException.ThrowIfNull(getName);
        _table = new(source,
            UIVirtualTableColumn<T>.Text("name", "Property", nameWidth, getName),
            new UIVirtualTableColumn<T>("value", "Value", valueWidth,
                createValueEditor, bindValueEditor, unbindValueEditor, refreshValueEditor));
        _table.AccessibleRowName = getName;
    }

    public int Count => _table.Count;
    public ulong Revision => _table.Revision;
    public int AccessibilityItemCount => _table.AccessibilityItemCount;
    public bool TryGetAccessibilityItem(int index, out UIVirtualAccessibilityItem item)
        => _table.TryGetAccessibilityItem(index, out item);
    public ulong? SelectedId { get => _table.SelectedId; set => _table.SelectedId = value; }
    public event Action<ulong>? Activated
    {
        add => _table.Activated += value;
        remove => _table.Activated -= value;
    }
    public event Action<ulong?>? SelectionChanged
    {
        add => _table.SelectionChanged += value;
        remove => _table.SelectionChanged -= value;
    }
    public ulong GetItemId(int index) => _table.GetItemId(index);
    public bool TryGetIndexOfItemId(ulong itemId, out int index)
        => _table.TryGetIndexOfItemId(itemId, out index);
    public UIBoundableTransform CreateRow() => _table.CreateRow();
    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
        => _table.BindRow(row, index, itemId, incarnation);
    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
        => _table.UnbindRow(row, itemId, incarnation);
}
