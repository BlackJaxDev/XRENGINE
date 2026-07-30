using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

/// <summary>
/// Update-thread gamepad backed by the value snapshot published by the window
/// owner. Registrations remain valid while a controller is disconnected so
/// hot-plugged devices work without repossessing the pawn.
/// </summary>
internal sealed class WindowSnapshotGamepad(int index) : BaseGamePad(index)
{
    private WindowGamepadSnapshot _snapshot;

    public void ApplySnapshot(WindowInputSnapshot snapshot)
        => _snapshot = snapshot.PrimaryGamepad;

    public override void TickStates(float delta)
    {
        bool connected = UpdateConnected(_snapshot.IsConnected);
        for (int i = 0; i < 14; i++)
        {
            EGamePadButton button = (EGamePadButton)i;
            TickButtonState(
                button,
                connected && _snapshot.IsButtonPressed(button),
                delta);
        }

        for (int i = 0; i < 6; i++)
        {
            EGamePadAxis axis = (EGamePadAxis)i;
            TickAxisState(
                axis,
                connected ? _snapshot.GetAxisValue(axis) : 0.0f,
                delta);
        }
    }

    public override void Vibrate(float lowFreq, float highFreq)
    {
        // Snapshot ownership is one-way. A future window-mailbox command can
        // add thread-affine vibration without exposing the native gamepad.
    }

    protected override bool ButtonExists(EGamePadButton button)
        => (uint)button < 14u;

    protected override List<bool> ButtonsExist(IEnumerable<EGamePadButton> buttons)
    {
        List<bool> result = [];
        foreach (EGamePadButton button in buttons)
            result.Add(ButtonExists(button));
        return result;
    }

    protected override bool AxisExists(EGamePadAxis axis)
        => (uint)axis < 6u;

    protected override List<bool> AxesExist(IEnumerable<EGamePadAxis> axes)
    {
        List<bool> result = [];
        foreach (EGamePadAxis axis in axes)
            result.Add(AxisExists(axis));
        return result;
    }
}
