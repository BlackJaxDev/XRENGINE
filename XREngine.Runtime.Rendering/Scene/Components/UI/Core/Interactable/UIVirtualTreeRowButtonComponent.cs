using System.Numerics;
using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI;

/// <summary>Keyboard and DPad navigation for one recycled, provider-backed tree row.</summary>
public sealed class UIVirtualTreeRowButtonComponent : UIButtonComponent
{
    private readonly DelButtonState _upKey;
    private readonly DelButtonState _downKey;
    private readonly DelButtonState _leftKey;
    private readonly DelButtonState _rightKey;
    private readonly DelButtonState _homeKey;
    private readonly DelButtonState _endKey;
    private UIVirtualTreeProvider? _provider;
    private UIListTransform? _list;
    private ulong _itemId;
    private ulong _leaseIncarnation;
    private ulong _providerIncarnation;

    public UIVirtualTreeRowButtonComponent()
    {
        _upKey = OnUpKey;
        _downKey = OnDownKey;
        _leftKey = OnLeftKey;
        _rightKey = OnRightKey;
        _homeKey = OnHomeKey;
        _endKey = OnEndKey;
    }

    public ulong BoundItemId => _itemId;
    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.TreeItem;
    public event Action<ulong, ulong>? NavigationRequested;
    public event Action<ulong, bool>? ExpansionChanged;

    public void BindNavigation(UIVirtualTreeProvider provider, UIListTransform list,
        ulong itemId, ulong incarnation)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(list);
        if (itemId == 0 || incarnation == 0 || !ReferenceEquals(list.VirtualProvider, provider) ||
            !ReferenceEquals(BoundableTransform.Parent, list) ||
            !list.TryGetVirtualRowLease(BoundableTransform, out UIVirtualRowLease lease) ||
            lease.ItemId != itemId || lease.Incarnation != incarnation)
            throw new InvalidOperationException("The tree navigation row has no matching provider lease.");
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
        ExpansionChanged = null;
        UnbindNavigation();
        base.OnDestroying();
    }

    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            !IsCurrentNavigationRow())
            return false;
        if (MathF.Abs(direction.Y) >= MathF.Abs(direction.X) && direction.Y != 0)
            return direction.Y > 0 ? NavigateUp() : NavigateDown();
        if (direction.X != 0)
            return direction.X > 0 ? NavigateRight() : NavigateLeft();
        return false;
    }

    public bool NavigateUp() => NavigateAdjacent(-1);
    public bool NavigateDown() => NavigateAdjacent(1);
    public bool NavigateHome() => NavigateEdge(first: true);
    public bool NavigateEnd() => NavigateEdge(first: false);

    public bool NavigateLeft()
    {
        if (!IsCurrentNavigationRow() || !_provider!.TryGetNode(_itemId, out UIVirtualTreeNode? node) ||
            node is null)
            return false;
        if (node.IsExpanded && node.Children.Count > 0)
            return ChangeExpansion(expanded: false);
        return node.ParentId == 0 || !IsVisible(node.ParentId) || RequestFocus(node.ParentId);
    }

    public bool NavigateRight()
    {
        if (!IsCurrentNavigationRow() || !_provider!.TryGetNode(_itemId, out UIVirtualTreeNode? node) ||
            node is null)
            return false;
        if (node.Children.Count == 0)
            return true;
        if (!node.IsExpanded)
            return ChangeExpansion(expanded: true);
        ulong firstChild = node.Children[0];
        return !IsVisible(firstChild) || RequestFocus(firstChild);
    }

    private bool NavigateAdjacent(int delta)
    {
        if (!IsCurrentNavigationRow())
            return false;
        return !_provider!.TryGetAdjacentVisible(_itemId, delta, out ulong targetId) || RequestFocus(targetId);
    }

    private bool NavigateEdge(bool first)
    {
        if (!IsCurrentNavigationRow())
            return false;
        return !_provider!.TryGetVisibleEdge(first, out ulong targetId) ||
            targetId == _itemId || RequestFocus(targetId);
    }

    private bool IsVisible(ulong itemId)
        => _provider!.TryGetIndexOfItemId(itemId, out _);

    private bool ChangeExpansion(bool expanded)
    {
        UIVirtualTreeProvider provider = _provider!;
        UIListTransform list = _list!;
        ulong itemId = _itemId;
        ulong leaseIncarnation = _leaseIncarnation;
        ulong providerIncarnation = _providerIncarnation;
        if (!provider.SetExpanded(itemId, expanded))
            return true;
        if (!IsSameNavigationOwner(provider, list, itemId, leaseIncarnation, providerIncarnation))
            return true;
        list.InvalidateMeasure();
        if (IsSameNavigationOwner(provider, list, itemId, leaseIncarnation, providerIncarnation))
            ExpansionChanged?.Invoke(itemId, expanded);
        return true;
    }

    private bool IsSameNavigationOwner()
        => _list is { } list && _provider is { } provider &&
            IsSameNavigationOwner(provider, list, _itemId, _leaseIncarnation, _providerIncarnation);

    private bool IsSameNavigationOwner(UIVirtualTreeProvider provider, UIListTransform list,
        ulong itemId, ulong leaseIncarnation, ulong providerIncarnation)
        => !IsDestroyed && !IsDestroyQueued &&
            ReferenceEquals(_list, list) && ReferenceEquals(_provider, provider) &&
            _itemId == itemId && _leaseIncarnation == leaseIncarnation &&
            _providerIncarnation == providerIncarnation &&
            ReferenceEquals(list.VirtualProvider, provider) &&
            list.VirtualProviderIncarnation == providerIncarnation &&
            ReferenceEquals(BoundableTransform.Parent, list) &&
            list.TryGetVirtualRowLease(BoundableTransform, out UIVirtualRowLease lease) &&
            lease.ItemId == itemId && lease.Incarnation == leaseIncarnation;

    private bool RequestFocus(ulong targetId)
    {
        ulong fromId = _itemId;
        ulong incarnation = _leaseIncarnation;
        ulong providerIncarnation = _providerIncarnation;
        UIVirtualTreeProvider? provider = _provider;
        if (_list is not { } list || provider is null ||
            UserInterfaceCanvas?.GetInputComponent() is not { } input ||
            !input.RequestVirtualItemFocus(list, targetId))
            return false;
        if (IsFocused && CanInteract &&
            IsSameNavigationOwner(provider, list, fromId, incarnation, providerIncarnation))
            NavigationRequested?.Invoke(fromId, targetId);
        return true;
    }

    private bool IsCurrentNavigationRow()
        => IsFocused && CanInteract && _itemId != 0 && _leaseIncarnation != 0 &&
            IsSameNavigationOwner();

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Up, _upKey);
        input.RegisterKeyStateChange(EKey.Down, _downKey);
        input.RegisterKeyStateChange(EKey.Left, _leftKey);
        input.RegisterKeyStateChange(EKey.Right, _rightKey);
        input.RegisterKeyStateChange(EKey.Home, _homeKey);
        input.RegisterKeyStateChange(EKey.End, _endKey);
    }

    private void OnUpKey(bool pressed) { if (pressed) NavigateUp(); }
    private void OnDownKey(bool pressed) { if (pressed) NavigateDown(); }
    private void OnLeftKey(bool pressed) { if (pressed) NavigateLeft(); }
    private void OnRightKey(bool pressed) { if (pressed) NavigateRight(); }
    private void OnHomeKey(bool pressed) { if (pressed) NavigateHome(); }
    private void OnEndKey(bool pressed) { if (pressed) NavigateEnd(); }
}
