using System.Numerics;
using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI;

/// <summary>Focusable table row with indexed navigation over stable virtual item IDs.</summary>
public sealed class UIVirtualTableRowComponent : UIButtonComponent
{
    private readonly DelButtonState _upKey;
    private readonly DelButtonState _downKey;
    private readonly DelButtonState _homeKey;
    private readonly DelButtonState _endKey;
    private IVirtualListProvider? _provider;
    private UIListTransform? _list;
    private ulong _itemId;
    private ulong _leaseIncarnation;
    private ulong _providerIncarnation;

    public UIVirtualTableRowComponent()
    {
        _upKey = OnUpKey;
        _downKey = OnDownKey;
        _homeKey = OnHomeKey;
        _endKey = OnEndKey;
    }

    public ulong BoundItemId => _itemId;
    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.TableRow;
    public event Action<ulong, ulong>? NavigationRequested;

    public void BindNavigation(IVirtualListProvider provider, UIListTransform list,
        ulong itemId, ulong incarnation)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(list);
        if (!UIVirtualRowNavigationLease.IsCurrent(this, provider, list,
                itemId, incarnation, list.VirtualProviderIncarnation))
            throw new InvalidOperationException("The table navigation row has no matching provider lease.");
        SetField(ref _provider, provider, publishNotifications: false);
        SetField(ref _list, list, publishNotifications: false);
        SetField(ref _itemId, itemId, publishNotifications: false);
        SetField(ref _leaseIncarnation, incarnation, publishNotifications: false);
        SetField(ref _providerIncarnation, list.VirtualProviderIncarnation, publishNotifications: false);
    }

    public void UnbindNavigation()
    {
        SetField(ref _provider, null, publishNotifications: false);
        SetField(ref _list, null, publishNotifications: false);
        SetField(ref _itemId, 0UL, publishNotifications: false);
        SetField(ref _leaseIncarnation, 0UL, publishNotifications: false);
        SetField(ref _providerIncarnation, 0UL, publishNotifications: false);
    }

    public override void CancelForRecycle()
    {
        UnbindNavigation();
        base.CancelForRecycle();
    }

    protected override void OnDestroying()
    {
        NavigationRequested = null;
        UnbindNavigation();
        base.OnDestroying();
    }

    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            MathF.Abs(direction.Y) < MathF.Abs(direction.X) || direction.Y == 0)
            return false;
        return NavigateAdjacent(direction.Y > 0 ? -1 : 1);
    }

    public bool NavigateUp() => NavigateAdjacent(-1);
    public bool NavigateDown() => NavigateAdjacent(1);
    public bool NavigateHome() => NavigateEdge(first: true);
    public bool NavigateEnd() => NavigateEdge(first: false);

    private bool NavigateAdjacent(int delta)
    {
        if (!IsCurrentNavigationRow() || _provider is not IVirtualListItemIndex lookup)
            return false;
        IVirtualListProvider provider = _provider;
        ulong sourceRevision = provider.Revision;
        if (!lookup.TryGetIndexOfItemId(_itemId, out int index) ||
            (uint)index >= (uint)provider.Count || provider.GetItemId(index) != _itemId ||
            provider.Revision != sourceRevision)
            return false;
        long targetIndex = (long)index + delta;
        if ((ulong)targetIndex >= (ulong)provider.Count)
            return true;
        ulong targetId = provider.GetItemId((int)targetIndex);
        if (provider.Revision != sourceRevision || !IsCurrentNavigationRow())
            return false;
        return targetId == 0 || RequestFocus(targetId);
    }

    private bool NavigateEdge(bool first)
    {
        if (!IsCurrentNavigationRow() || _provider is not IVirtualListItemIndex)
            return false;
        IVirtualListProvider provider = _provider;
        ulong sourceRevision = provider.Revision;
        int count = provider.Count;
        if (count == 0)
            return true;
        ulong targetId = provider.GetItemId(first ? 0 : count - 1);
        if (provider.Revision != sourceRevision || !IsCurrentNavigationRow())
            return false;
        return targetId == 0 || targetId == _itemId || RequestFocus(targetId);
    }

    private bool RequestFocus(ulong targetId)
    {
        IVirtualListProvider provider = _provider!;
        UIListTransform list = _list!;
        ulong fromId = _itemId;
        ulong leaseIncarnation = _leaseIncarnation;
        ulong providerIncarnation = _providerIncarnation;
        if (!UIVirtualRowNavigationLease.RequestFocus(this, list, targetId))
            return false;
        if (IsFocused && CanInteract && IsSameNavigationOwner(provider, list, fromId,
                leaseIncarnation, providerIncarnation))
            NavigationRequested?.Invoke(fromId, targetId);
        return true;
    }

    private bool IsCurrentNavigationRow()
        => IsFocused && CanInteract && _provider is { } provider && _list is { } list &&
            IsSameNavigationOwner(provider, list, _itemId, _leaseIncarnation, _providerIncarnation);

    private bool IsSameNavigationOwner(IVirtualListProvider provider, UIListTransform list,
        ulong itemId, ulong leaseIncarnation, ulong providerIncarnation)
        => ReferenceEquals(_provider, provider) && ReferenceEquals(_list, list) &&
            _itemId == itemId && _leaseIncarnation == leaseIncarnation &&
            _providerIncarnation == providerIncarnation &&
            UIVirtualRowNavigationLease.IsCurrent(this, provider, list,
                itemId, leaseIncarnation, providerIncarnation);

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Up, _upKey);
        input.RegisterKeyStateChange(EKey.Down, _downKey);
        input.RegisterKeyStateChange(EKey.Home, _homeKey);
        input.RegisterKeyStateChange(EKey.End, _endKey);
    }

    private void OnUpKey(bool pressed) { if (pressed) NavigateUp(); }
    private void OnDownKey(bool pressed) { if (pressed) NavigateDown(); }
    private void OnHomeKey(bool pressed) { if (pressed) NavigateHome(); }
    private void OnEndKey(bool pressed) { if (pressed) NavigateEnd(); }
}
