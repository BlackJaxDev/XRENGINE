using System.Numerics;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Editor;

internal sealed class InspectorFocusTestInput : XRComponent, IUICanvasInputSource
{
    private UIInteractableComponent? _focused;

    public InspectorFocusTestInput() { }

    public bool IsCtrlHeld => false;
    public bool IsShiftHeld => false;
    public bool IsAltHeld => false;
    public Vector2 CursorPositionWorld2D => Vector2.Zero;
    public ulong FocusIntentRevision { get; private set; } = 1;
    public bool HasPendingFocusRequest { get; set; }
    public UIInteractableComponent? FocusedComponent
    {
        get => _focused;
        set
        {
            FocusIntentRevision++;
            _focused = value;
        }
    }

    public bool TryFocusPresented(UIInteractableComponent target)
        => TryFocusPresented(target, FocusIntentRevision, static () => true);

    public bool TryFocusPresented(UIInteractableComponent target, ulong expectedFocusIntentRevision,
        Func<bool> isCurrent)
    {
        if (FocusIntentRevision != expectedFocusIntentRevision || HasPendingFocusRequest ||
            _focused is not null || !isCurrent())
            return false;
        FocusIntentRevision++;
        if (!isCurrent())
            return false;
        _focused = target;
        return true;
    }

    public bool TryReleaseFocusPreservingDraft(UIInteractableComponent expectedTarget, ulong expectedIntent,
        Func<bool> isCurrent, out ulong clearedIntent)
    {
        clearedIntent = 0;
        if (FocusIntentRevision != expectedIntent || !ReferenceEquals(_focused, expectedTarget) ||
            HasPendingFocusRequest || !isCurrent())
            return false;
        FocusIntentRevision++;
        if (!isCurrent())
            return false;
        _focused = null;
        clearedIntent = FocusIntentRevision;
        return true;
    }

    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false)
        => false;

    public event Action<UIInteractableComponent?>? LeftClickDown { add { } remove { } }
    public event Action<UIInteractableComponent>? RightClick { add { } remove { } }
    public event Action? EscapePressed { add { } remove { } }
    public event Action<UIInteractableComponent?>? BackPressed { add { } remove { } }
}
