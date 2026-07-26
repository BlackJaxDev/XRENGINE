using System.Numerics;

namespace XREngine.Input.Devices
{
    [Serializable]
    public abstract class BaseMouse(int index) : InputDevice(index)
    {
        protected CursorManager _cursor = new();
        protected ScrollWheelManager _wheel = new();

        public abstract Vector2 CursorPosition { get; set; }

        protected override int GetAxisCount() => 0;
        protected override int GetButtonCount() => 3;
        public override EInputDeviceType DeviceType => EInputDeviceType.Mouse;

        private ButtonManager? FindOrCacheButton(EMouseButton button)
        {
            int index = (int)button;
            return _buttonStates[index] ??= MakeMouseButtonManager(button, index);
        }

        public void RegisterButtonPressed(EMouseButton button, DelButtonState func, bool unregister)
        {
            if (unregister)
                _buttonStates[(int)button]?.RegisterPressedState(func, true);
            else
                FindOrCacheButton(button)?.RegisterPressedState(func, false);
        }

        public void RegisterButtonEvent(EMouseButton button, EButtonInputType type, Action func, bool unregister)
            => RegisterButtonEvent(unregister ? _buttonStates[(int)button] : FindOrCacheButton(button), type, func, unregister);
        public void RegisterScroll(DelMouseScroll func, bool unregister)
            => _wheel.Register(func, unregister);
        public void RegisterMouseMove(DelCursorUpdate func, EMouseMoveType type, bool unregister)
            => _cursor.Register(func, type, unregister);

        /// <summary>Clears buffered scroll input without dispatching it.</summary>
        public virtual void ClearScrollBuffer() { }

        public ButtonManager? LeftClick => _buttonStates[(int)EMouseButton.LeftClick];
        public ButtonManager? RightClick => _buttonStates[(int)EMouseButton.RightClick];
        public ButtonManager? MiddleClick => _buttonStates[(int)EMouseButton.MiddleClick];

        public abstract bool HideCursor { get; set; }

        public bool GetButtonState(EMouseButton button, EButtonInputType type)
            => FindOrCacheButton(button)?.GetState(type) ?? false;
        protected void TickCursorState(float x, float y)
            => _cursor.Tick(x, y);
        protected void TickScrollState(float delta)
            => _wheel.Tick(delta);
        protected void TickMouseButtonState(EMouseButton button, bool isPressed, float delta)
            => _buttonStates[(int)button]?.Tick(isPressed, delta);
    }
}
