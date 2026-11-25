using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI;

/// <summary>Focusable tab with its selection exposed to accessibility services.</summary>
public sealed class UITabButtonComponent : UIButtonComponent
{
    private readonly DelButtonState _closeKey;
    private readonly DelButtonState _moveLeftKey;
    private readonly DelButtonState _moveRightKey;

    public UITabButtonComponent()
    {
        _closeKey = OnCloseKey;
        _moveLeftKey = OnMoveLeftKey;
        _moveRightKey = OnMoveRightKey;
    }

    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.Tab;
    public override string? AccessibilityValue => IsSelected ? "Selected" : "Not selected";
    public event Action<UITabButtonComponent>? KeyboardCloseRequested;
    public event Action<UITabButtonComponent, int>? KeyboardMoveRequested;

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.W, _closeKey);
        input.RegisterKeyStateChange(EKey.PageUp, _moveLeftKey);
        input.RegisterKeyStateChange(EKey.PageDown, _moveRightKey);
    }

    private void OnCloseKey(bool pressed)
    {
        if (pressed && IsFocused && CanInteract &&
            UserInterfaceCanvas?.GetInputComponent()?.IsCtrlHeld == true)
            KeyboardCloseRequested?.Invoke(this);
    }

    private void OnMoveLeftKey(bool pressed) => TryMove(pressed, -1);
    private void OnMoveRightKey(bool pressed) => TryMove(pressed, 1);

    private void TryMove(bool pressed, int direction)
    {
        IUICanvasInputSource? input = UserInterfaceCanvas?.GetInputComponent();
        if (pressed && IsFocused && CanInteract && input?.IsCtrlHeld == true && input.IsShiftHeld)
            KeyboardMoveRequested?.Invoke(this, direction);
    }

    protected override void OnDestroying()
    {
        KeyboardCloseRequested = null;
        KeyboardMoveRequested = null;
        base.OnDestroying();
    }
}
