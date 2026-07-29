using System.Numerics;
using XREngine.Components;
using XREngine.Input.Devices;

namespace MonkeyBallVR;

/// <summary>
/// Routes keyboard, gamepad, OpenXR, and OpenVR actions into the camera-relative course controls.
/// </summary>
public sealed class MonkeyBallPawnComponent : PawnComponent
{
    private MonkeyBallGameComponent? _game;
    private Vector2 _vrTilt;
    private Vector2 _gamepadTilt;
    private bool _keyboardLeft;
    private bool _keyboardRight;
    private bool _keyboardForward;
    private bool _keyboardBackward;
    private bool _arrowLeft;
    private bool _arrowRight;
    private bool _arrowForward;
    private bool _arrowBackward;

    public void Bind(MonkeyBallGameComponent game)
        => _game = game;

    public override void RegisterInput(object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;

        input.RegisterVRVector2Action(MonkeyBallActionSet.Global, MonkeyBallAction.Tilt, OnVrTilt);
        input.RegisterVRBoolAction(MonkeyBallActionSet.Global, MonkeyBallAction.Reset, OnVrReset);
        input.RegisterVRBoolAction(MonkeyBallActionSet.Global, MonkeyBallAction.Pause, OnVrPause);

        input.RegisterKeyStateChange(EKey.A, SetKeyboardLeft);
        input.RegisterKeyStateChange(EKey.D, SetKeyboardRight);
        input.RegisterKeyStateChange(EKey.W, SetKeyboardForward);
        input.RegisterKeyStateChange(EKey.S, SetKeyboardBackward);
        input.RegisterKeyStateChange(EKey.Left, SetArrowLeft);
        input.RegisterKeyStateChange(EKey.Right, SetArrowRight);
        input.RegisterKeyStateChange(EKey.Up, SetArrowForward);
        input.RegisterKeyStateChange(EKey.Down, SetArrowBackward);
        input.RegisterKeyEvent(EKey.R, EButtonInputType.Pressed, Reset);
        input.RegisterKeyEvent(EKey.Escape, EButtonInputType.Pressed, TogglePause);

        input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, OnGamepadX, false);
        input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, OnGamepadY, false);
        input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Pressed, Reset);
        input.RegisterButtonEvent(EGamePadButton.SpecialRight, EButtonInputType.Pressed, TogglePause);
    }

    private void OnVrTilt(Vector2 oldValue, Vector2 newValue)
    {
        _vrTilt = newValue;
        PublishTilt();
    }

    private void OnVrReset(bool pressed)
    {
        if (pressed)
            Reset();
    }

    private void OnVrPause(bool pressed)
    {
        if (pressed)
            TogglePause();
    }

    private void OnGamepadX(float value)
    {
        _gamepadTilt.X = value;
        PublishTilt();
    }

    private void OnGamepadY(float value)
    {
        _gamepadTilt.Y = value;
        PublishTilt();
    }

    private void SetKeyboardLeft(bool pressed)
    {
        _keyboardLeft = pressed;
        PublishTilt();
    }

    private void SetKeyboardRight(bool pressed)
    {
        _keyboardRight = pressed;
        PublishTilt();
    }

    private void SetKeyboardForward(bool pressed)
    {
        _keyboardForward = pressed;
        PublishTilt();
    }

    private void SetKeyboardBackward(bool pressed)
    {
        _keyboardBackward = pressed;
        PublishTilt();
    }

    private void SetArrowLeft(bool pressed)
    {
        _arrowLeft = pressed;
        PublishTilt();
    }

    private void SetArrowRight(bool pressed)
    {
        _arrowRight = pressed;
        PublishTilt();
    }

    private void SetArrowForward(bool pressed)
    {
        _arrowForward = pressed;
        PublishTilt();
    }

    private void SetArrowBackward(bool pressed)
    {
        _arrowBackward = pressed;
        PublishTilt();
    }

    private void PublishTilt()
    {
        bool left = _keyboardLeft || _arrowLeft;
        bool right = _keyboardRight || _arrowRight;
        bool forward = _keyboardForward || _arrowForward;
        bool backward = _keyboardBackward || _arrowBackward;
        Vector2 keyboard = new(
            (right ? 1.0f : 0.0f) - (left ? 1.0f : 0.0f),
            (forward ? 1.0f : 0.0f) - (backward ? 1.0f : 0.0f));
        Vector2 analog = _vrTilt.LengthSquared() >= _gamepadTilt.LengthSquared() ? _vrTilt : _gamepadTilt;
        Vector2 combined = keyboard + analog;
        float lengthSquared = combined.LengthSquared();
        if (lengthSquared > 1.0f)
            combined /= MathF.Sqrt(lengthSquared);
        _game?.SetTilt(combined);
    }

    private void Reset()
        => _game?.ResetRound();

    private void TogglePause()
        => _game?.TogglePause();
}
