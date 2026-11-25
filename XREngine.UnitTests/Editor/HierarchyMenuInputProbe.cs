using System.Numerics;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Editor;

/// <summary>Dispatches one accepted pointer target to the hierarchy menu tests.</summary>
internal sealed class HierarchyMenuInputProbe : XRComponent, IUICanvasInputSource
{
    private UIInteractableComponent? _acceptedTarget;

    public HierarchyMenuInputProbe() { }

    public bool IsCtrlHeld => false;
    public bool IsShiftHeld => false;
    public bool IsAltHeld => false;
    public Vector2 CursorPositionWorld2D => new(-1000);
    public Vector2 PresentedPosition { get; set; } = new(170, 190);
    public bool AcceptPointer { get; set; } = true;
    public UIInteractableComponent? FocusedComponent { get; set; }
    public bool TryFocusPresented(UIInteractableComponent target) => false;
    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false) => false;
    public bool TryGetPresentedPointerPosition(UIInteractableComponent target, out Vector2 canvasPosition)
    {
        canvasPosition = PresentedPosition;
        return AcceptPointer && ReferenceEquals(target, _acceptedTarget);
    }

    public event Action<UIInteractableComponent>? RightClick;
    public event Action<UIInteractableComponent?>? LeftClickDown { add { } remove { } }
    public event Action? EscapePressed { add { } remove { } }
    public event Action<UIInteractableComponent?>? BackPressed { add { } remove { } }

    public void RaiseRightClick(UIInteractableComponent target)
    {
        UIInteractableComponent? previous = _acceptedTarget;
        _acceptedTarget = target;
        try { RightClick?.Invoke(target); }
        finally { _acceptedTarget = previous; }
    }
}
