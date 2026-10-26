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

    /// <summary>Changes when a focus request can replace a pending control focus.</summary>
    ulong FocusIntentRevision => 0;

    /// <summary>True when another control is waiting for accepted virtual focus.</summary>
    bool HasPendingFocusRequest => false;

    /// <summary>Focuses a control only when its current row and scope are in the presented input packet.</summary>
    bool TryFocusPresented(UIInteractableComponent target);

    /// <summary>Requests focus for a stable virtual item after its matching row is presented.</summary>
    bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false);

    /// <summary>Requests virtual focus only while the caller's pure ownership proof remains current.</summary>
    bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, Func<bool> isCurrent,
        bool center = false) => false;

    /// <summary>Restricts an owned virtual focus request to one exact presented control.</summary>
    bool RequestVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent expectedTarget, Func<bool> isCurrent, bool center = false) => false;

    /// <summary>Rejects an exact-target request if another focus intent won before admission.</summary>
    bool RequestVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent expectedTarget, ulong expectedFocusIntentRevision,
        Func<bool> isCurrent, bool center = false) => false;

    /// <summary>Clears only the exact focused target and intent retained by a caller.</summary>
    bool TryClearFocus(UIInteractableComponent expectedTarget, ulong expectedIntent,
        out ulong clearedIntent)
    {
        clearedIntent = 0;
        return false;
    }

    /// <summary>
    /// Releases focus without committing, restoring, or formatting draft, caret, or display values.
    /// Unsupported input sources reject the request before callbacks.
    /// </summary>
    bool TryReleaseFocusPreservingDraft(UIInteractableComponent expectedTarget, ulong expectedIntent,
        Func<bool> isCurrent, out ulong clearedIntent)
    {
        clearedIntent = 0;
        return false;
    }

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
