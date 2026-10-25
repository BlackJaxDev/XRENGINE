using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIVirtualFocusCallbackProvider : IVirtualListProvider, IVirtualListItemIndex
{
    private readonly UIVirtualTreeProvider _inner;

    internal UIVirtualFocusCallbackProvider(UIVirtualTreeProvider inner) => _inner = inner;

    internal Action? OnGetItemId { get; set; }
    public int Count => _inner.Count;
    public ulong Revision => _inner.Revision;
    public ulong GetItemId(int index)
    {
        ulong itemId = _inner.GetItemId(index);
        OnGetItemId?.Invoke();
        return itemId;
    }
    public bool TryGetIndexOfItemId(ulong itemId, out int index)
        => _inner.TryGetIndexOfItemId(itemId, out index);
    public UIBoundableTransform CreateRow() => _inner.CreateRow();
    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
        => _inner.BindRow(row, index, itemId, incarnation);
    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
        => _inner.UnbindRow(row, itemId, incarnation);
}
