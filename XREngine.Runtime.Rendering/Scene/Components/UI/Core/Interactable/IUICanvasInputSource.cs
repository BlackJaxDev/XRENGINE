using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>
/// Exposes presentation-facing canvas input state without coupling rendering UI to
/// the device and controller routing implementation.
/// </summary>
public interface IUICanvasInputSource
{
    bool IsCtrlHeld { get; }
    bool IsShiftHeld { get; }
    bool IsAltHeld { get; }
    Vector2 CursorPositionWorld2D { get; }
    UIInteractableComponent? FocusedComponent { get; set; }

    /// <summary>Focuses a control only when its current row and scope are in the presented input packet.</summary>
    bool TryFocusPresented(UIInteractableComponent target);

    /// <summary>Requests focus for a stable virtual item after its matching row is presented.</summary>
    bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false);

    /// <summary>Reports the exact accepted virtual focus dispatch in progress.</summary>
    bool IsDispatchingVirtualItemFocus(UIListTransform list, ulong itemId, UIInteractableComponent target)
        => false;

    /// <summary>Samples a current displayed target in canvas coordinates.</summary>
    bool TryGetPresentedPointerPosition(UIInteractableComponent target, out Vector2 canvasPosition)
    {
        canvasPosition = default;
        return false;
    }

    event Action<UIInteractableComponent?>? LeftClickDown;
    event Action<UIInteractableComponent>? RightClick;
    event Action? EscapePressed;
    event Action<UIInteractableComponent?>? BackPressed;
}
