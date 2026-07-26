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

    event Action<UIInteractableComponent?>? LeftClickDown;
    event Action<UIInteractableComponent>? RightClick;
    event Action? EscapePressed;
}
