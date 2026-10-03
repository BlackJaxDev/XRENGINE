
using Silk.NET.Input;
using System.Diagnostics;

namespace XREngine.Input.Devices.Glfw
{
    public class GlfwGamepad : BaseGamePad
    {
        private readonly IGamepad _controller;

        public GlfwGamepad(IGamepad gamepad) : base(gamepad.Index)
        {
            _controller = gamepad;
            _controller.ButtonDown += ButtonDown;
            _controller.ButtonUp += ButtonUp;
            _controller.ThumbstickMoved += ThumbstickMoved;
            _controller.TriggerMoved += TriggerMoved;
        }

        private void TriggerMoved(IGamepad gamepad, Trigger trigger)
        {

        }

        private void ThumbstickMoved(IGamepad gamepad, Thumbstick thumbstick)
        {

        }

        private void ButtonUp(IGamepad gamepad, Button button)
        {

        }

        private void ButtonDown(IGamepad gamepad, Button button)
        {

        }

        public override void Vibrate(float lowFreq, float highFreq)
        {
            if (_controller.VibrationMotors.Count <= 0)
                return;

            _controller.VibrationMotors[0].Speed = lowFreq;

            if (_controller.VibrationMotors.Count <= 1)
                return;

            _controller.VibrationMotors[1].Speed = highFreq;
        }

        protected override List<bool> AxesExist(IEnumerable<EGamePadAxis> axes)
        {
            List<bool> exists = [];
            foreach (EGamePadAxis axis in axes)
                exists.Add(AxisExists(axis));
            return exists;
        }

        protected override bool AxisExists(EGamePadAxis axis) => axis switch
        {
            EGamePadAxis.LeftTrigger => _controller.Triggers.Count > 0,
            EGamePadAxis.RightTrigger => _controller.Triggers.Count > 1,
            EGamePadAxis.LeftThumbstickX or EGamePadAxis.LeftThumbstickY => _controller.Thumbsticks.Count > 0,
            EGamePadAxis.RightThumbstickX or EGamePadAxis.RightThumbstickY => _controller.Thumbsticks.Count > 1,
            _ => false,
        };

        private readonly Dictionary<EGamePadButton, int> _buttonRemap = [];

        protected override bool ButtonExists(EGamePadButton button)
        {
            int index = button switch
            {
                EGamePadButton.DPadUp => FindIndex(_controller.Buttons, x => x.Name == ButtonName.DPadUp),
                EGamePadButton.DPadDown => FindIndex(_controller.Buttons, x => x.Name == ButtonName.DPadDown),
                EGamePadButton.DPadLeft => FindIndex(_controller.Buttons, x => x.Name == ButtonName.DPadLeft),
                EGamePadButton.DPadRight => FindIndex(_controller.Buttons, x => x.Name == ButtonName.DPadRight),
                EGamePadButton.FaceUp => FindIndex(_controller.Buttons, x => x.Name == ButtonName.Y),
                EGamePadButton.FaceDown => FindIndex(_controller.Buttons, x => x.Name == ButtonName.A),
                EGamePadButton.FaceLeft => FindIndex(_controller.Buttons, x => x.Name == ButtonName.X),
                EGamePadButton.FaceRight => FindIndex(_controller.Buttons, x => x.Name == ButtonName.B),
                EGamePadButton.LeftStick => FindIndex(_controller.Buttons, x => x.Name == ButtonName.LeftStick),
                EGamePadButton.RightStick => FindIndex(_controller.Buttons, x => x.Name == ButtonName.RightStick),
                EGamePadButton.SpecialLeft => FindIndex(_controller.Buttons, x => x.Name == ButtonName.Home),
                EGamePadButton.SpecialRight => FindIndex(_controller.Buttons, x => x.Name == ButtonName.Start),
                EGamePadButton.LeftBumper => FindIndex(_controller.Buttons, x => x.Name == ButtonName.LeftBumper),
                EGamePadButton.RightBumper => FindIndex(_controller.Buttons, x => x.Name == ButtonName.RightBumper),
                _ => -1,
            };
            if (index == -1)
                return false;
            _buttonRemap[button] = index;
            return true;
        }

        private static int FindIndex(IReadOnlyList<Button> buttons, Predicate<Button> match)
        {
            for (int i = 0; i < buttons.Count; ++i)
                if (match(buttons[i]))
                    return i;
            return -1;
        }

        protected override List<bool> ButtonsExist(IEnumerable<EGamePadButton> buttons)
        {
            List<bool> exists = [];
            foreach (EGamePadButton button in buttons)
                exists.Add(ButtonExists(button));
            return exists;
        }

        private class GlfwAxisManager(int index, string name, Func<float> valueFactory) : AxisManager(index, name)
        {
            public float GetGlfwValue() => valueFactory();
        }
        private class GlfwButtonManager(int index, string name, Func<bool> pressedFactory) : ButtonManager(index, name)
        {
            public bool GetGlfwPressed() => pressedFactory();
        }

        //TODO: maybe don't capture 'name' here
        private Func<bool> GetGlfwButtonFactory(EGamePadButton name)
            => () => _controller.Buttons[_buttonRemap[name]].Pressed;

        protected override ButtonManager? MakeButtonManager(EGamePadButton name, int index)
        {
            var man = new GlfwButtonManager(index, name.ToString(), GetGlfwButtonFactory(name));
            man.ActionExecuted += SendButtonAction;
            man.StatePressed += SendButtonPressedState;
            return man;
        }

        protected override AxisManager? MakeAxisManager(EGamePadAxis name, int index)
        {
            GlfwAxisManager man;
            switch (name)
            {
                case EGamePadAxis.LeftTrigger:
                    man = new GlfwAxisManager(index, name.ToString(), () => _controller.Triggers[0].Position);
                    break;
                case EGamePadAxis.RightTrigger:
                    man = new GlfwAxisManager(index, name.ToString(), () => _controller.Triggers[1].Position);
                    break;

                case EGamePadAxis.LeftThumbstickX:
                    man = new GlfwAxisManager(index, name.ToString(), () => _controller.Thumbsticks[0].X);
                    break;
                case EGamePadAxis.LeftThumbstickY:
                    man = new GlfwAxisManager(index, name.ToString(), () => -_controller.Thumbsticks[0].Y);
                    break;

                case EGamePadAxis.RightThumbstickX:
                    man = new GlfwAxisManager(index, name.ToString(), () => _controller.Thumbsticks[1].X);
                    break;
                case EGamePadAxis.RightThumbstickY:
                    man = new GlfwAxisManager(index, name.ToString(), () => -_controller.Thumbsticks[1].Y);
                    break;

                default:
                    return null;
            }
            man.ActionExecuted += SendButtonAction;
            man.StatePressed += SendButtonPressedState;
            man.ListExecuted += SendAxisValue;
            return man;
        }

        public override void TickStates(float delta)
        {
            if (!UpdateConnected(_controller.IsConnected))
                return;

            for (int i = 0; i < 14; ++i)
                if (_buttonStates[i] is GlfwButtonManager glfwState)
                    TickButtonState((EGamePadButton)i, glfwState.GetGlfwPressed(), delta);
            
            for (int i = 0; i < 6; ++i)
                if (_axisStates[i] is GlfwAxisManager glfwState)
                    TickAxisState((EGamePadAxis)i, glfwState.GetGlfwValue(), delta);
        }
    }
}
