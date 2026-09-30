using DXNET.XInput;

namespace XREngine.Input.Devices.DirectX
{
    public class DXGamepadConfiguration : BaseGamepadConfiguration
    {
        private const float ByteDiv = 1.0f / byte.MaxValue;
        private const float ShortDiv = 1.0f / short.MaxValue;

        public bool Map(EGamePadButton button, Gamepad state)
            => ButtonMethods[(int)ButtonMap[button]](state);
        public float Map(EGamePadAxis axis, Gamepad state)
            => AxisMethods[(int)AxisMap[axis]](state);
        
        private static readonly Func<Gamepad, bool>[] ButtonMethods =
        [
            state => state.Buttons.HasFlag(GamepadButtonFlags.DPadUp),
            state => state.Buttons.HasFlag(GamepadButtonFlags.DPadDown),
            state => state.Buttons.HasFlag(GamepadButtonFlags.DPadLeft),
            state => state.Buttons.HasFlag(GamepadButtonFlags.DPadRight),

            state => state.Buttons.HasFlag(GamepadButtonFlags.Y),
            state => state.Buttons.HasFlag(GamepadButtonFlags.A),
            state => state.Buttons.HasFlag(GamepadButtonFlags.X),
            state => state.Buttons.HasFlag(GamepadButtonFlags.B),

            state => state.Buttons.HasFlag(GamepadButtonFlags.LeftThumb),
            state => state.Buttons.HasFlag(GamepadButtonFlags.RightThumb),

            state => state.Buttons.HasFlag(GamepadButtonFlags.Back),
            state => state.Buttons.HasFlag(GamepadButtonFlags.Start),

            state => state.Buttons.HasFlag(GamepadButtonFlags.LeftShoulder),
            state => state.Buttons.HasFlag(GamepadButtonFlags.RightShoulder),
        ];
        private static readonly Func<Gamepad, float>[] AxisMethods =
        [
            state => state.LeftTrigger * ByteDiv,
            state => state.RightTrigger * ByteDiv,

            state => state.LeftThumbX * ShortDiv,
            state => state.LeftThumbY * ShortDiv,

            state => state.RightThumbX * ShortDiv,
            state => state.RightThumbY * ShortDiv,
        ];
    }
}
