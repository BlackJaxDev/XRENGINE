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
    private WindowGamepadSnapshot _virtualSnapshot;
    private ushort _deliveredButtons;
    private ushort _deliveredPhysicalButtons;
    private ushort _deliveredVirtualButtons;
    private readonly float[] _deliveredAxes = new float[6];
    private readonly float[] _deliveredPhysicalAxes = new float[6];
    private readonly float[] _deliveredVirtualAxes = new float[6];
    private bool _isTicking;
    private bool _cancelPending;
    private bool _isCancelling;
    public bool IsVirtualDispatch { get; private set; }

    public void ApplySnapshot(WindowInputSnapshot snapshot)
        => _snapshot = snapshot.PrimaryGamepad;

    public void SetVirtualSnapshot(WindowGamepadSnapshot snapshot) => _virtualSnapshot = snapshot;

    /// <summary>Releases only the virtual contribution, including while ordinary input is captured by UI.</summary>
    public void CancelVirtualInput()
    {
        _virtualSnapshot = default;
        if (_isTicking)
        {
            _cancelPending = true;
            return;
        }
        if (_isCancelling)
            return;
        _isCancelling = true;
        try
        {
            IsVirtualDispatch = true;
            for (int index = 0; index < 14; index++)
            {
                ushort bit = (ushort)(1 << index);
                if ((_deliveredVirtualButtons & bit) == 0)
                    continue;
                bool previous = (_deliveredButtons & bit) != 0;
                bool physical = (_deliveredPhysicalButtons & bit) != 0;
                _deliveredVirtualButtons = SetButton(_deliveredVirtualButtons, bit, false);
                _deliveredButtons = SetButton(_deliveredButtons, bit, physical);
                if (previous != physical)
                    TickButtonState((EGamePadButton)index, physical, 0);
            }
            for (int index = 0; index < 6; index++)
            {
                if (_deliveredVirtualAxes[index] == 0)
                    continue;
                float previous = _deliveredAxes[index];
                float physical = _deliveredPhysicalAxes[index];
                _deliveredVirtualAxes[index] = 0;
                _deliveredAxes[index] = physical;
                if (previous != physical)
                    TickAxisState((EGamePadAxis)index, physical, 0);
            }
        }
        finally
        {
            _isCancelling = false;
            IsVirtualDispatch = false;
        }
    }

    public override void TickStates(float delta)
    {
        WindowGamepadSnapshot combined = Merge(_snapshot, _virtualSnapshot);
        WindowGamepadSnapshot physical = _snapshot.IsConnected ? _snapshot : default;
        WindowGamepadSnapshot virtualInput = _virtualSnapshot.IsConnected ? _virtualSnapshot : default;
        _isTicking = true;
        try
        {
            bool connected = UpdateConnected(combined.IsConnected);
            for (int i = 0; i < 14; i++)
            {
                if (_cancelPending)
                    break;
                EGamePadButton button = (EGamePadButton)i;
                ushort bit = (ushort)(1 << i);
                bool physicalPressed = physical.IsButtonPressed(button);
                bool virtualPressed = virtualInput.IsButtonPressed(button);
                bool pressed = connected && combined.IsButtonPressed(button);
                IsVirtualDispatch = !physicalPressed &&
                    (virtualPressed || ((_deliveredVirtualButtons & bit) != 0 && (_deliveredPhysicalButtons & bit) == 0));
                // Publish this channel before callbacks. Unvisited channels retain their previous
                // delivered state when a callback changes ownership and interrupts the tick.
                _deliveredButtons = SetButton(_deliveredButtons, bit, pressed);
                _deliveredPhysicalButtons = SetButton(_deliveredPhysicalButtons, bit, physicalPressed);
                _deliveredVirtualButtons = SetButton(_deliveredVirtualButtons, bit, virtualPressed);
                TickButtonState(button, pressed, delta);
            }

            for (int i = 0; i < 6; i++)
            {
                if (_cancelPending)
                    break;
                EGamePadAxis axis = (EGamePadAxis)i;
                float physicalValue = physical.GetAxisValue(axis);
                float virtualValue = virtualInput.GetAxisValue(axis);
                float value = connected ? combined.GetAxisValue(axis) : 0;
                IsVirtualDispatch = MathF.Abs(virtualValue) > MathF.Abs(physicalValue);
                _deliveredAxes[i] = value;
                _deliveredPhysicalAxes[i] = physicalValue;
                _deliveredVirtualAxes[i] = virtualValue;
                TickAxisState(axis, value, delta);
            }
        }
        finally
        {
            _isTicking = false;
            IsVirtualDispatch = false;
            if (_cancelPending)
            {
                _cancelPending = false;
                CancelVirtualInput();
            }
        }
    }

    private static ushort SetButton(ushort mask, ushort bit, bool pressed)
        => pressed ? (ushort)(mask | bit) : (ushort)(mask & ~bit);

    private static WindowGamepadSnapshot Merge(WindowGamepadSnapshot physical, WindowGamepadSnapshot virtualInput)
    {
        if (!physical.IsConnected)
            physical = default;
        if (!virtualInput.IsConnected)
            return physical;
        return new WindowGamepadSnapshot(true, (ushort)(physical.PressedButtonMask | virtualInput.PressedButtonMask),
            Stronger(physical.LeftTrigger, virtualInput.LeftTrigger),
            Stronger(physical.RightTrigger, virtualInput.RightTrigger),
            Stronger(physical.LeftThumbstickX, virtualInput.LeftThumbstickX),
            Stronger(physical.LeftThumbstickY, virtualInput.LeftThumbstickY),
            Stronger(physical.RightThumbstickX, virtualInput.RightThumbstickX),
            Stronger(physical.RightThumbstickY, virtualInput.RightThumbstickY));
    }

    private static float Stronger(float physical, float virtualInput)
        => MathF.Abs(virtualInput) > MathF.Abs(physical) ? virtualInput : physical;

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
