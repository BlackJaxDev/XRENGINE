using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Mutable owner-thread source for recycled native table rows.</summary>
internal sealed class NativeTableRows<T> : IUIVirtualTableSource<T>, IVirtualListItemIndex
{
    private readonly List<(ulong Id, T Item)> _items = [];
    private readonly Dictionary<ulong, int> _indices = [];

    public int Count => _items.Count;
    public ulong Revision { get; private set; } = 1;
    public ulong GetItemId(int index) => _items[index].Id;
    public T GetItem(int index) => _items[index].Item;
    public bool TryGetIndexOfItemId(ulong id, out int index) => _indices.TryGetValue(id, out index);

    public void Replace(IEnumerable<(ulong Id, T Item)> items)
    {
        _items.Clear();
        _indices.Clear();
        _items.AddRange(items);
        for (int i = 0; i < _items.Count; i++)
            _indices[_items[i].Id] = i;
        Revision++;
    }
}
