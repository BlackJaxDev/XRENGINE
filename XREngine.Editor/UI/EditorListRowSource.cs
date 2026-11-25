using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Keeps occurrence IDs stable when one list row is inserted or removed.</summary>
internal sealed class EditorListRowSource : IUIVirtualTableSource<int>, IVirtualListItemIndex
{
    private readonly Dictionary<int, ulong> _ids = [];
    private readonly Dictionary<ulong, int> _indices = [];
    private ulong _nextId;
    public int Count { get; private set; }
    public ulong Revision { get; private set; } = 1;
    public int GetItem(int index) => index;
    public ulong GetItemId(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (_ids.TryGetValue(index, out ulong id)) return id;
        id = ++_nextId;
        _ids.Add(index, id);
        _indices.Add(id, index);
        return id;
    }
    public bool TryGetIndexOfItemId(ulong id, out int index)
    {
        return _indices.TryGetValue(id, out index);
    }
    public void Reset(int count)
    {
        _ids.Clear();
        _indices.Clear();
        Count = count;
        Revision++;
    }
    public ulong Insert(int index, ulong id = 0)
    {
        if ((uint)index > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (id == 0) id = ++_nextId;
        Shift(index, 1);
        Count++;
        _ids.Add(index, id);
        _indices[id] = index;
        Revision++;
        return id;
    }
    public ulong Remove(int index)
    {
        ulong id = GetItemId(index);
        _ids.Remove(index);
        _indices.Remove(id);
        Shift(index + 1, -1);
        Count--;
        Revision++;
        return id;
    }
    public void Refresh() => Revision++;

    private void Shift(int firstIndex, int amount)
    {
        if (_ids.Count == 0) return;
        var moved = _ids.Where(pair => pair.Key >= firstIndex).ToArray();
        foreach (var pair in moved) _ids.Remove(pair.Key);
        foreach (var pair in moved)
        {
            int next = pair.Key + amount;
            _ids.Add(next, pair.Value);
            _indices[pair.Value] = next;
        }
    }
}
