using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class CountingVirtualListProvider(int count) : IVirtualListProvider
{
    public int Count { get; } = count;
    public ulong Revision { get; private set; } = 1;
    public int Created { get; private set; }
    public int Bound { get; private set; }
    public int Unbound { get; private set; }

    public ulong GetItemId(int index) => (ulong)index + 1;

    public UIBoundableTransform CreateRow()
    {
        Created++;
        return new UIBoundableTransform();
    }

    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
    {
        Bound++;
        if (itemId != (ulong)index + 1 || incarnation == 0)
            throw new InvalidOperationException("The list supplied an invalid row lease.");
    }

    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        Unbound++;
        if (itemId == 0 || incarnation == 0)
            throw new InvalidOperationException("The list released an invalid row lease.");
    }

    public void AdvanceRevision() => Revision++;
}
