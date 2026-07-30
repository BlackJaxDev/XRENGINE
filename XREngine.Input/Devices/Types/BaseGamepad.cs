using XREngine.Input.Devices.DirectX;

namespace XREngine.Input.Devices
{
    public delegate void ConnectedStateChange(bool nowConnected);
    /// <summary>
    /// Input for local
    /// </summary>
    [Serializable]
    public abstract class BaseGamePad(int index) : InputDevice(index)
    {
        public static BaseGamePad NewInstance(int index, EInputType type)
            => type switch
            {
                EInputType.XInput => new DXGamepad(index),
                _ => throw new InvalidOperationException(),
            };

        protected override int GetButtonCount() => 14;
        protected override int GetAxisCount() => 6;
        public override EInputDeviceType DeviceType => EInputDeviceType.Gamepad;

        protected abstract bool ButtonExists(EGamePadButton button);
        protected abstract List<bool> ButtonsExist(IEnumerable<EGamePadButton> buttons);
        protected abstract bool AxisExists(EGamePadAxis axis);
        protected abstract List<bool> AxesExist(IEnumerable<EGamePadAxis> axes);

        private ButtonManager? FindOrCacheButton(EGamePadButton button)
        {
            int index = (int)button;
            if (_buttonStates[index] is null && ButtonExists(button))
                _buttonStates[index] = MakeButtonManager(button, index);
            return _buttonStates[index];
        }

        private AxisManager? FindOrCacheAxis(EGamePadAxis axis)
        {
            int index = (int)axis;
            if (_axisStates[index] is null && AxisExists(axis))
                _axisStates[index] = MakeAxisManager(axis, index);
            return _axisStates[index];
        }

        public void RegisterButtonEvent(EGamePadButton button, EButtonInputType type, Action func, bool unregister)
            => RegisterButtonEvent(unregister ? _buttonStates[(int)button] : FindOrCacheButton(button), type, func, unregister);

        public void RegisterButtonEvent(EGamePadAxis axis, EButtonInputType type, Action func, bool unregister)
            => RegisterButtonEvent(unregister ? _axisStates[(int)axis] : FindOrCacheAxis(axis), type, func, unregister);

        public void RegisterButtonState(EGamePadButton button, DelButtonState func, bool unregister)
        {
            if (unregister)
                _buttonStates[(int)button]?.RegisterPressedState(func, true);
            else
                FindOrCacheButton(button)?.RegisterPressedState(func, false);
        }

        public void RegisterButtonState(EGamePadAxis axis, DelButtonState func, bool unregister)
        {
            if (unregister)
                _axisStates[(int)axis]?.RegisterPressedState(func, true);
            else
                FindOrCacheAxis(axis)?.RegisterPressedState(func, false);
        }

        public void RegisterAxisUpdate(EGamePadAxis axis, DelAxisValue func, bool continuousUpdate, bool unregister)
        {
            if (unregister)
                _axisStates[(int)axis]?.RegisterAxis(func, continuousUpdate, true);
            else
                FindOrCacheAxis(axis)?.RegisterAxis(func, continuousUpdate, false);
        }

        protected void TickButtonState(EGamePadButton button, bool pressed, float delta)
            => _buttonStates[(int)button]?.Tick(pressed, delta);

        protected void TickAxisState(EGamePadAxis axis, float value, float delta)
            => _axisStates[(int)axis]?.Tick(value, delta);

        /// <summary>
        /// Left motor is low freq, right motor is high freq.
        /// They are NOT the same.
        /// </summary>
        /// <param name="left">Low frequency motor speed, 0 - 1.</param>
        /// <param name="right">High frequency motor speed, 0 - 1.</param>
        public abstract void Vibrate(float lowFreq, float highFreq);
        public void ClearVibration() => Vibrate(0.0f, 0.0f);

        public ButtonManager? DPadUp         => _buttonStates[(int)EGamePadButton.DPadUp];
        public ButtonManager? DPadDown       => _buttonStates[(int)EGamePadButton.DPadDown];
        public ButtonManager? DPadLeft       => _buttonStates[(int)EGamePadButton.DPadLeft];
        public ButtonManager? DPadRight      => _buttonStates[(int)EGamePadButton.DPadRight];

        public ButtonManager? FaceUp         => _buttonStates[(int)EGamePadButton.FaceUp];
        public ButtonManager? FaceDown       => _buttonStates[(int)EGamePadButton.FaceDown];
        public ButtonManager? FaceLeft       => _buttonStates[(int)EGamePadButton.FaceLeft];
        public ButtonManager? FaceRight      => _buttonStates[(int)EGamePadButton.FaceRight];

        public ButtonManager? LeftStick      => _buttonStates[(int)EGamePadButton.LeftStick];
        public ButtonManager? RightStick     => _buttonStates[(int)EGamePadButton.RightStick];
        public ButtonManager? LeftBumper     => _buttonStates[(int)EGamePadButton.LeftBumper];
        public ButtonManager? RightBumper    => _buttonStates[(int)EGamePadButton.RightBumper];

        public ButtonManager? SpecialLeft    => _buttonStates[(int)EGamePadButton.SpecialLeft];
        public ButtonManager? SpecialRight   => _buttonStates[(int)EGamePadButton.SpecialRight];

        public AxisManager? LeftTrigger      => _axisStates[(int)EGamePadAxis.LeftTrigger];
        public AxisManager? RightTrigger     => _axisStates[(int)EGamePadAxis.RightTrigger];
        public AxisManager? LeftThumbstickY  => _axisStates[(int)EGamePadAxis.LeftThumbstickY];
        public AxisManager? LeftThumbstickX  => _axisStates[(int)EGamePadAxis.LeftThumbstickX];
        public AxisManager? RightThumbstickY => _axisStates[(int)EGamePadAxis.RightThumbstickY];
        public AxisManager? RightThumbstickX => _axisStates[(int)EGamePadAxis.RightThumbstickX];

        public bool GetButtonState(EGamePadButton button, EButtonInputType type)
            => FindOrCacheButton(button)?.GetState(type) ?? false;
        public bool GetAxisState(EGamePadAxis axis, EButtonInputType type)
            => FindOrCacheAxis(axis)?.GetState(type) ?? false;
        public float GetAxisValue(EGamePadAxis axis)
            => FindOrCacheAxis(axis)?.Value ?? 0.0f;
    }
}
