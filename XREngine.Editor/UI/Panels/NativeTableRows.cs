using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Mutable owner-thread source for recycled native table rows.</summary>
internal sealed class NativeTableRows<T> : IUIVirtualTableSource<T>
{
    private readonly List<(ulong Id, T Item)> _items = [];

    public int Count => _items.Count;
    public ulong Revision { get; private set; } = 1;
    public ulong GetItemId(int index) => _items[index].Id;
    public T GetItem(int index) => _items[index].Item;

    public void Replace(IEnumerable<(ulong Id, T Item)> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Revision++;
    }
}
