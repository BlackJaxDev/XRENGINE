using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Publishes browser device snapshots to the engine's existing local-player input path.</summary>
internal sealed class BrowserEngineInputViewport : IRuntimeLocalPlayerViewport, IRuntimeLocalPlayerInputSource
{
    private readonly WindowInputSnapshotAccumulator _snapshots = new();
    private readonly bool[] _keys = new bool[(int)EKey.LastKey + 1];
    private readonly bool[] _buttons = new bool[3];
    private WindowGamepadSnapshot _gamepad;
    private bool _focused;
    private bool _mouseCaptured;
    private bool _captureDesired;

    public bool CaptureDesired => _captureDesired;

    public WindowInputSnapshot ConsumeInputSnapshot() => _snapshots.ConsumeLatest();
    public void RequestMouseCapture(bool captured) => _captureDesired = captured;
    public void RefreshControlledPawnCamera(XRComponent? controlledPawnComponent)
    {
        // Camera ownership belongs to the render viewport. Input remains valid before a GPU output exists.
    }

    public void Key(int keyValue, bool down)
    {
        if ((uint)keyValue >= (uint)_keys.Length || keyValue == (int)EKey.Unknown)
            return;
        if (_keys[keyValue] == down)
            return;
        _keys[keyValue] = down;
        if (down)
            _snapshots.RecordKeyDown((EKey)keyValue);
        else
            _snapshots.RecordKeyUp((EKey)keyValue);
    }

    public void Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(value), "One browser text event may contain at most 64 characters.");
        foreach (char character in value)
            if (!char.IsControl(character))
                _snapshots.RecordTextInput(character);
    }

    public void Pointer(float x, float y)
    {
        if (float.IsFinite(x) && float.IsFinite(y))
            _snapshots.RecordPointerPosition(Math.Clamp(x, 0, 65535), Math.Clamp(y, 0, 65535));
    }

    public void MouseButton(int buttonValue, bool down)
    {
        if ((uint)buttonValue >= (uint)_buttons.Length)
            return;
        if (_buttons[buttonValue] == down)
            return;
        _buttons[buttonValue] = down;
        if (down)
            _snapshots.RecordMouseDown((EMouseButton)buttonValue);
        else
            _snapshots.RecordMouseUp((EMouseButton)buttonValue);
    }

    public void Scroll(float x, float y)
    {
        if (float.IsFinite(x) && float.IsFinite(y))
            _snapshots.RecordScroll(Math.Clamp(x, -240, 240), Math.Clamp(y, -240, 240));
    }

    public void Focus(bool focused)
    {
        if (_focused == focused)
            return;
        _focused = focused;
        if (!focused)
            ReleasePressed();
    }

    public void Gamepad(bool connected, int buttonMask, float leftTrigger, float rightTrigger,
        float leftX, float leftY, float rightX, float rightY)
    {
        if (!connected)
        {
            _gamepad = default;
            return;
        }
        if (!float.IsFinite(leftTrigger) || !float.IsFinite(rightTrigger) ||
            !float.IsFinite(leftX) || !float.IsFinite(leftY) ||
            !float.IsFinite(rightX) || !float.IsFinite(rightY))
        {
            throw new ArgumentOutOfRangeException(nameof(leftX), "Gamepad axes must be finite.");
        }
        _gamepad = new WindowGamepadSnapshot(true, (ushort)(buttonMask & 0x3fff),
            Math.Clamp(leftTrigger, 0, 1), Math.Clamp(rightTrigger, 0, 1),
            Math.Clamp(leftX, -1, 1), Math.Clamp(leftY, -1, 1),
            Math.Clamp(rightX, -1, 1), Math.Clamp(rightY, -1, 1));
    }

    public void Publish(bool focused, bool mouseCaptured)
    {
        Focus(focused);
        _mouseCaptured = focused && mouseCaptured;
        _snapshots.Publish(1, 1, _gamepad.IsConnected ? 1 : 0,
            _focused, _mouseCaptured, _gamepad);
    }

    public void Reset()
    {
        _focused = false;
        _mouseCaptured = false;
        _captureDesired = false;
        _gamepad = default;
        ReleasePressed();
        Publish(false, false);
    }

    private void ReleasePressed()
    {
        for (int index = 1; index < _keys.Length; index++)
            if (_keys[index])
                Key(index, false);
        for (int index = 0; index < _buttons.Length; index++)
            if (_buttons[index])
                MouseButton(index, false);
    }
}
