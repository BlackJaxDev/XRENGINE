using System.Numerics;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UITestCanvasInputProbe : XRComponent, IUICanvasInputSource
{
    private Action<UIInteractableComponent?>? _leftClickDown;
    private Action<UIInteractableComponent>? _rightClick;
    private Action? _escapePressed;
    private Action<UIInteractableComponent?>? _backPressed;

    public UITestCanvasInputProbe() { }

    public bool IsCtrlHeld { get; set; }
    public bool IsShiftHeld { get; set; }
    public bool IsAltHeld => false;
    public Vector2 CursorPositionWorld2D => Vector2.Zero;
    public UIInteractableComponent? FocusedComponent { get; set; }
    public bool TryFocusPresented(UIInteractableComponent target) => false;
    public bool IsDispatchingVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent target) => false;
    public bool VirtualFocusAccepted { get; set; }
    public Action? OnVirtualFocusRequested { get; set; }
    public UIListTransform? RequestedList { get; private set; }
    public ulong RequestedItemId { get; private set; }
    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false)
    {
        RequestedList = list;
        RequestedItemId = itemId;
        OnVirtualFocusRequested?.Invoke();
        return VirtualFocusAccepted;
    }

    public int SubscriberCount => (_leftClickDown?.GetInvocationList().Length ?? 0) +
        (_rightClick?.GetInvocationList().Length ?? 0) +
        (_escapePressed?.GetInvocationList().Length ?? 0) +
        (_backPressed?.GetInvocationList().Length ?? 0);

    public event Action<UIInteractableComponent?>? LeftClickDown
    {
        add => _leftClickDown += value;
        remove => _leftClickDown -= value;
    }

    public event Action<UIInteractableComponent>? RightClick
    {
        add => _rightClick += value;
        remove => _rightClick -= value;
    }

    public event Action? EscapePressed
    {
        add => _escapePressed += value;
        remove => _escapePressed -= value;
    }

    public event Action<UIInteractableComponent?>? BackPressed
    {
        add => _backPressed += value;
        remove => _backPressed -= value;
    }

    public void RaiseEscape() => _escapePressed?.Invoke();
}
