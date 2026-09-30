using DXNET.XInput;

namespace XREngine.Input.Devices.DirectX
{
    [Serializable]
    public class DXGamepad(int index) : BaseGamePad(index)
    {
        private readonly Controller _controller = new(UserIndex.One + (byte)index);

        public static DXGamepadConfiguration Config { get; set; } = new DXGamepadConfiguration();

        public override void Vibrate(float lowFreq, float highFreq)
        {
            Vibration v = new()
            {
                LeftMotorSpeed = (ushort)(lowFreq * ushort.MaxValue),
                RightMotorSpeed = (ushort)(highFreq * ushort.MaxValue)
            };
            _controller.SetVibration(v);
        }
        private static bool AxistExists(EGamePadAxis axis, Capabilities c)
            => axis switch
            {
                EGamePadAxis.LeftTrigger => c.Gamepad.LeftTrigger != 0,
                EGamePadAxis.RightTrigger => c.Gamepad.RightTrigger != 0,
                EGamePadAxis.LeftThumbstickX => c.Gamepad.LeftThumbX != 0,
                EGamePadAxis.LeftThumbstickY => c.Gamepad.LeftThumbY != 0,
                EGamePadAxis.RightThumbstickX => c.Gamepad.RightThumbX != 0,
                EGamePadAxis.RightThumbstickY => c.Gamepad.RightThumbY != 0,
                _ => false,
            };
        protected override bool AxisExists(EGamePadAxis axis)
            => AxistExists(axis, _controller.GetCapabilities(DeviceQueryType.Gamepad));
        protected override List<bool> AxesExist(IEnumerable<EGamePadAxis> axes)
        {
            Capabilities c = _controller.GetCapabilities(DeviceQueryType.Gamepad);
            return axes.Select(x => AxistExists(x, c)).ToList();
        }
        private static bool ButtonExists(EGamePadButton button, Capabilities c)
            => button switch
            {
                EGamePadButton.FaceDown => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.A),
                EGamePadButton.FaceRight => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.B),
                EGamePadButton.FaceLeft => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.X),
                EGamePadButton.FaceUp => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.Y),
                EGamePadButton.DPadDown => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.DPadDown),
                EGamePadButton.DPadRight => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.DPadRight),
                EGamePadButton.DPadLeft => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.DPadLeft),
                EGamePadButton.DPadUp => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.DPadUp),
                EGamePadButton.LeftBumper => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.LeftShoulder),
                EGamePadButton.RightBumper => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.RightShoulder),
                EGamePadButton.LeftStick => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.LeftThumb),
                EGamePadButton.RightStick => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.RightThumb),
                EGamePadButton.SpecialLeft => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.Back),
                EGamePadButton.SpecialRight => c.Gamepad.Buttons.HasFlag(GamepadButtonFlags.Start),
                _ => false,
            };
        protected override bool ButtonExists(EGamePadButton button)
            => ButtonExists(button, _controller.GetCapabilities(DeviceQueryType.Gamepad));
        protected override List<bool> ButtonsExist(IEnumerable<EGamePadButton> buttons)
        {
            Capabilities c = _controller.GetCapabilities(DeviceQueryType.Gamepad);
            return buttons.Select(x => ButtonExists(x, c)).ToList();
        }
        public override void TickStates(float delta)
        {
            if (!UpdateConnected(_controller.IsConnected))
                return;

            State state = _controller.GetState();
            for (int i = 0; i < 14; ++i)
                TickButtonState((EGamePadButton)i, Config.Map((EGamePadButton)i, state.Gamepad), delta);
            for (int i = 0; i < 6; ++i)
                TickAxisState((EGamePadAxis)i, Config.Map((EGamePadAxis)i, state.Gamepad), delta);
        }
    }
}
