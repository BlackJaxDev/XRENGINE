namespace XREngine.Rendering.UI;

/// <summary>Resolves stable virtual item IDs without scanning the logical collection.</summary>
public interface IVirtualListItemIndex
{
    bool TryGetIndexOfItemId(ulong itemId, out int index);
}
