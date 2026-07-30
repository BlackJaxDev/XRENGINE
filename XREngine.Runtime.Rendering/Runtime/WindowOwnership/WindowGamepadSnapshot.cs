using XREngine.Input.Devices;

namespace XREngine.Rendering;

/// <summary>
/// Allocation-free primary-gamepad state copied on the native window thread
/// and consumed by the update thread.
/// </summary>
public readonly record struct WindowGamepadSnapshot(
    bool IsConnected,
    ushort PressedButtonMask,
    float LeftTrigger,
    float RightTrigger,
    float LeftThumbstickX,
    float LeftThumbstickY,
    float RightThumbstickX,
    float RightThumbstickY)
{
    public bool IsButtonPressed(EGamePadButton button)
    {
        int index = (int)button;
        return (uint)index < 16u &&
            (PressedButtonMask & (1u << index)) != 0;
    }

    public float GetAxisValue(EGamePadAxis axis)
        => axis switch
        {
            EGamePadAxis.LeftTrigger => LeftTrigger,
            EGamePadAxis.RightTrigger => RightTrigger,
            EGamePadAxis.LeftThumbstickX => LeftThumbstickX,
            EGamePadAxis.LeftThumbstickY => LeftThumbstickY,
            EGamePadAxis.RightThumbstickX => RightThumbstickX,
            EGamePadAxis.RightThumbstickY => RightThumbstickY,
            _ => 0.0f,
        };
}
