using Silk.NET.Input;
using System.Numerics;
using MouseButton = Silk.NET.Input.MouseButton;

namespace XREngine.Input.Devices.Glfw
{
    [Serializable]
    public class GlfwMouse : BaseMouse
    {
        private readonly IMouse _mouse;

        public override Vector2 CursorPosition
        {
            get => _mouse.Position;
            set => _mouse.Position = value;
        }
        public override bool HideCursor
        {
            get => _mouse.Cursor.CursorMode == CursorMode.Disabled;
            set => _mouse.Cursor.CursorMode = value ? CursorMode.Disabled : CursorMode.Normal;
        }

        private float _lastScroll = 0.0f;

        public GlfwMouse(IMouse mouse) : base(mouse.Index)
        {
            _mouse = mouse;
            _mouse.MouseMove += MouseMove;
            _mouse.MouseUp += MouseUp;
            _mouse.MouseDown += MouseDown;
            _mouse.Scroll += Scroll;
            _mouse.Click += Click;
            _mouse.DoubleClick += DoubleClick;
        }
        ~GlfwMouse()
        {
            _mouse.MouseMove -= MouseMove;
            _mouse.MouseUp -= MouseUp;
            _mouse.MouseDown -= MouseDown;
            _mouse.Scroll -= Scroll;
            _mouse.Click -= Click;
            _mouse.DoubleClick -= DoubleClick;
        }

        private void DoubleClick(IMouse mouse, MouseButton button, Vector2 vector)
        {

        }

        private void Click(IMouse mouse, MouseButton button, Vector2 vector)
        {

        }

        private void Scroll(IMouse mouse, ScrollWheel wheel)
        {
            _lastScroll += wheel.Y;
        }

        public override void ClearScrollBuffer()
        {
            _lastScroll = 0.0f;
        }

        private void MouseDown(IMouse mouse, MouseButton button)
        {

        }

        private void MouseUp(IMouse mouse, MouseButton button)
        {

        }

        private void MouseMove(IMouse mouse, Vector2 position)
        {

        }

        public override void TickStates(float delta)
        {
            _cursor.Tick(CursorPosition.X, CursorPosition.Y);
            _wheel.Tick(_lastScroll);
            _lastScroll = 0.0f;
            LeftClick?.Tick(_mouse.IsButtonPressed(MouseButton.Left), delta);
            RightClick?.Tick(_mouse.IsButtonPressed(MouseButton.Right), delta);
            MiddleClick?.Tick(_mouse.IsButtonPressed(MouseButton.Middle), delta);
        }
    }
}
