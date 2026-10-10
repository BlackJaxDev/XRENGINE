using System.Numerics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;

namespace RollingBall;

/// <summary>
/// Routes keyboard, gamepad, OpenXR, and OpenVR actions into the camera-relative course controls.
/// </summary>
public sealed class RollingBallPawnComponent : PawnComponent
{
    private IRollingBallGameInputTarget? _game;
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

    public void Bind(IRollingBallGameInputTarget game)
        => _game = game;

    public override void RegisterInput(object inputInterface)
    {
        if (inputInterface is not InputInterface input)
            return;

        string inputType = input.GetType().FullName ?? input.GetType().Name;
        RollingBallRuntimeValidation.RecordInputRegistration(inputType, input.Unregister);
        RollingBallRuntimeDiagnostics.RecordInputRegistration(
            inputType, input.Unregister);

        input.RegisterVRVector2Action(RollingBallActionSet.Global, RollingBallAction.Tilt, OnVrTilt);
        input.RegisterVRBoolAction(RollingBallActionSet.Global, RollingBallAction.Reset, OnVrReset);
        input.RegisterVRBoolAction(RollingBallActionSet.Global, RollingBallAction.Pause, OnVrPause);

        input.RegisterKeyStateChange(EKey.A, SetKeyboardLeft);
        input.RegisterKeyStateChange(EKey.D, SetKeyboardRight);
        input.RegisterKeyStateChange(EKey.W, SetKeyboardForward);
        input.RegisterKeyStateChange(EKey.S, SetKeyboardBackward);
        input.RegisterKeyStateChange(EKey.Left, SetArrowLeft);
        input.RegisterKeyStateChange(EKey.Right, SetArrowRight);
        input.RegisterKeyStateChange(EKey.Up, SetArrowForward);
        input.RegisterKeyStateChange(EKey.Down, SetArrowBackward);
        input.RegisterKeyEvent(EKey.R, EButtonInputType.Pressed, ResetFromKeyboard);
        input.RegisterKeyEvent(EKey.Escape, EButtonInputType.Pressed, TogglePauseFromKeyboard);

        input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, OnGamepadX, false);
        input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, OnGamepadY, false);
        input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Pressed, ResetFromGamepad);
        input.RegisterButtonEvent(EGamePadButton.SpecialRight, EButtonInputType.Pressed, TogglePauseFromGamepad);

        RollingBallRuntimeValidation.RecordInputBindings(input.Unregister);
        RollingBallRuntimeDiagnostics.RecordInputBindingSet(input.Unregister);
        if (input is LocalInputInterface localInput)
        {
            RollingBallRuntimeValidation.RecordInputDevices(
                localInput.Keyboard is not null,
                localInput.Gamepad is not null,
                input.Unregister);
            RollingBallRuntimeDiagnostics.RecordEvent(
                "input-devices",
                $"unregister={input.Unregister} keyboardBound={localInput.Keyboard is not null} " +
                $"gamepadBound={localInput.Gamepad is not null} " +
                $"gamepadConnected={localInput.Gamepad?.IsConnected ?? false} " +
                $"vrRuntime={RuntimeVrInputServices.ActiveRuntime} " +
                $"vrService={RuntimeVrInputServices.ActiveServiceName} vrActions={localInput.HasVrActions}");
        }
    }

    private void OnVrTilt(Vector2 oldValue, Vector2 newValue)
    {
        RollingBallRuntimeDiagnostics.RecordAnalogInput("VR", newValue);
        _vrTilt = newValue;
        PublishTilt();
    }

    private void OnVrReset(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("VRReset", pressed);
        if (pressed)
            Reset();
    }

    private void OnVrPause(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("VRPause", pressed);
        if (pressed)
            TogglePause();
    }

    private void OnGamepadX(float value)
    {
        _gamepadTilt.X = value;
        RollingBallRuntimeDiagnostics.RecordAnalogInput("Gamepad", _gamepadTilt);
        PublishTilt();
    }

    private void OnGamepadY(float value)
    {
        _gamepadTilt.Y = value;
        RollingBallRuntimeDiagnostics.RecordAnalogInput("Gamepad", _gamepadTilt);
        PublishTilt();
    }

    private void SetKeyboardLeft(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("A", pressed);
        _keyboardLeft = pressed;
        PublishTilt();
    }

    private void SetKeyboardRight(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("D", pressed);
        _keyboardRight = pressed;
        PublishTilt();
    }

    private void SetKeyboardForward(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("W", pressed);
        _keyboardForward = pressed;
        PublishTilt();
    }

    private void SetKeyboardBackward(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("S", pressed);
        _keyboardBackward = pressed;
        PublishTilt();
    }

    private void SetArrowLeft(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("Left", pressed);
        _arrowLeft = pressed;
        PublishTilt();
    }

    private void SetArrowRight(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("Right", pressed);
        _arrowRight = pressed;
        PublishTilt();
    }

    private void SetArrowForward(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("Up", pressed);
        _arrowForward = pressed;
        PublishTilt();
    }

    private void SetArrowBackward(bool pressed)
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("Down", pressed);
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

    private void ResetFromKeyboard()
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("R", true);
        Reset();
    }

    private void TogglePauseFromKeyboard()
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("Escape", true);
        TogglePause();
    }

    private void ResetFromGamepad()
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("GamepadReset", true);
        Reset();
    }

    private void TogglePauseFromGamepad()
    {
        RollingBallRuntimeDiagnostics.RecordInputCallback("GamepadPause", true);
        TogglePause();
    }

    private void Reset()
        => _game?.ResetRound();

    private void TogglePause()
        => _game?.TogglePause();
}
