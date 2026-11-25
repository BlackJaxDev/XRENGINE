namespace XREngine.Rendering.UI;

/// <summary>Checks the exact row lease before a virtual row requests focus.</summary>
internal static class UIVirtualRowNavigationLease
{
    public static bool IsCurrent(UIInteractableComponent row, IVirtualListProvider provider,
        UIListTransform list, ulong itemId, ulong leaseIncarnation, ulong providerIncarnation)
        => !row.IsDestroyed && !row.IsDestroyQueued && itemId != 0 && leaseIncarnation != 0 &&
            ReferenceEquals(list.VirtualProvider, provider) &&
            list.VirtualProviderIncarnation == providerIncarnation &&
            ReferenceEquals(row.BoundableTransform.Parent, list) &&
            list.TryGetVirtualRowLease(row.BoundableTransform, out UIVirtualRowLease lease) &&
            lease.ItemId == itemId && lease.Incarnation == leaseIncarnation;

    public static bool RequestFocus(UIInteractableComponent row, UIListTransform list, ulong targetId)
        => targetId != 0 && row.UserInterfaceCanvas?.GetInputComponent() is { } input &&
            input.RequestVirtualItemFocus(list, targetId);
}
