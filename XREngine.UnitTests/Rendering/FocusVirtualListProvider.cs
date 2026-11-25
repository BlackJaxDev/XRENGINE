using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

/// <summary>Counts visible work while creating real focusable row subtrees.</summary>
internal sealed class FocusVirtualListProvider(SceneNode parent, int count) : IVirtualListProvider, IVirtualListItemIndex
{
    public int Count { get; } = count;
    public ulong Revision => 1;
    public int Created { get; private set; }
    public int ItemReads { get; private set; }
    public int IndexReads { get; private set; }

    public ulong GetItemId(int index)
    {
        ItemReads++;
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return (ulong)index + 1;
    }

    public bool TryGetIndexOfItemId(ulong itemId, out int index)
    {
        IndexReads++;
        index = itemId > 0 && itemId <= (ulong)Count ? (int)itemId - 1 : -1;
        return index >= 0;
    }

    public UIBoundableTransform CreateRow()
    {
        Created++;
        UIBoundableTransform row = new();
        new SceneNode(parent, "Row", row).NewChild("Button").AddComponent<UIVirtualButtonComponent>();
        return row;
    }

    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
    {
        if (itemId != (ulong)index + 1 || incarnation == 0)
            throw new InvalidOperationException("Invalid row lease.");
    }

    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        if (itemId == 0 || incarnation == 0)
            throw new InvalidOperationException("Invalid retired row lease.");
    }
}
