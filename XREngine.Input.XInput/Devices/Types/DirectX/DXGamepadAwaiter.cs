using DXNET.XInput;

namespace XREngine.Input.Devices.DirectX
{
    [Serializable]
    public class DXGamepadAwaiter : GamepadAwaiter
    {
        public const int MaxControllers = 4;

        private readonly Controller[] _controllers =
        [
            new(UserIndex.One),
            new(UserIndex.Two),
            new(UserIndex.Three),
            new(UserIndex.Four),
        ];

        public override BaseGamePad CreateGamepad(int controllerIndex)
            => new DXGamepad(controllerIndex);
    
        public override void Tick()
        {
            var gamepads = InputDevice.CurrentDevices[EInputDeviceType.Gamepad];
            for (int i = 0; i < MaxControllers; ++i)
                if (gamepads[i] is null && _controllers[i].IsConnected)
                {
                    Capabilities c = _controllers[i].GetCapabilities(DeviceQueryType.Gamepad);
                    if (c.Type == DeviceType.Gamepad)
                        OnFoundGamepad(i);
                }
        }
    }
}
