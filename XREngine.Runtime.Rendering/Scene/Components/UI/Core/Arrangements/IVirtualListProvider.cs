namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Supplies logical items to a fixed-size list. IDs must be unique, nonzero, and stable across revisions.
    /// BindRow must replace all state from the row's prior item. It can run again for the same item and lease
    /// after a revision change. UnbindRow must release item subscriptions.
    /// </summary>
    public interface IVirtualListProvider
    {
        int Count { get; }
        ulong Revision { get; }
        ulong GetItemId(int index);
        UIBoundableTransform CreateRow();
        void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation);
        void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation);
    }
}
