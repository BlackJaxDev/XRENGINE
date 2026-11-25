using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class MeasuredVirtualListProvider(float[] extents) : IVirtualListProvider, IVirtualListItemIndex,
    IVirtualListItemExtents
{
    private readonly float[] _extents = extents;

    public int Count => _extents.Length;
    public ulong Revision { get; private set; } = 1;
    public bool HasItemExtents { get; set; } = true;
    public ulong ExtentRevision { get; private set; } = 1;
    public int ExtentReads { get; private set; }

    public float GetItemExtent(int index)
    {
        ExtentReads++;
        return _extents[index];
    }

    public void SetExtent(int index, float extent)
    {
        _extents[index] = extent;
        ExtentRevision++;
    }

    public ulong GetItemId(int index) => (ulong)index + 1;

    public bool TryGetIndexOfItemId(ulong itemId, out int index)
    {
        index = itemId > 0 && itemId <= (ulong)Count ? (int)itemId - 1 : -1;
        return index >= 0;
    }

    public UIBoundableTransform CreateRow() => new();
    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation) { }
    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation) { }
}
