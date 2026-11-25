namespace XREngine.Rendering.UI;

/// <summary>
/// Supplies measured main-axis extents for the items of a provider-backed list. When
/// <see cref="HasItemExtents"/> is true, the list uses these extents instead of a fixed
/// <c>ItemSize</c>. The provider must know each extent before the list binds the row:
/// the list places the row in the measured slot and does not measure the row content.
/// </summary>
/// <remarks>
/// The list reads every extent again when <see cref="IVirtualListProvider.Count"/>,
/// <see cref="IVirtualListProvider.Revision"/>, or <see cref="ExtentRevision"/> changes.
/// This costs one pass over all items, so increase <see cref="ExtentRevision"/> only when an
/// extent changes. Each extent must be finite and not negative.
/// </remarks>
public interface IVirtualListItemExtents
{
    /// <summary>True when the list must use measured extents for the current items.</summary>
    bool HasItemExtents { get; }

    /// <summary>Increases when any item extent changes without a provider revision change.</summary>
    ulong ExtentRevision { get; }

    /// <summary>Gets the main-axis extent of the item at <paramref name="index"/>.</summary>
    float GetItemExtent(int index);
}
