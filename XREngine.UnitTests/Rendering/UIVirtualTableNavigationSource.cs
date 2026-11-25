using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIVirtualTableNavigationSource(int count)
    : IUIVirtualTableSource<int>, IVirtualListItemIndex
{
    public int Count { get; } = count;
    public ulong Revision { get; private set; } = 1;
    public int IndexReads { get; private set; }
    public int IdReads { get; private set; }
    public ulong GetItemId(int index)
    {
        IdReads++;
        return (uint)index < (uint)Count ? (ulong)index + 1 :
            throw new ArgumentOutOfRangeException(nameof(index));
    }
    public int GetItem(int index)
        => (uint)index < (uint)Count ? index : throw new ArgumentOutOfRangeException(nameof(index));
    public bool TryGetIndexOfItemId(ulong itemId, out int index)
    {
        IndexReads++;
        index = itemId is > 0 and <= int.MaxValue ? (int)itemId - 1 : -1;
        return (uint)index < (uint)Count;
    }
    public void AdvanceRevision() => Revision++;
}
