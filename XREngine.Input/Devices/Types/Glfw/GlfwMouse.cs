using Silk.NET.Input;
using System.Numerics;
using MouseButton = Silk.NET.Input.MouseButton;

namespace XREngine.Input.Devices.Glfw
{
    [Serializable]
    public class GlfwMouse : BaseMouse
    {
        private readonly IMouse _mouse;
        private bool _leftPressed;
        private bool _rightPressed;
        private bool _middlePressed;

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

        private readonly Queue<float> _pendingScrollDeltas = [];
        private readonly List<float> _scrollDispatchBuffer = [];
        private readonly object _scrollLock = new();

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
            if (MathF.Abs(wheel.Y) <= float.Epsilon)
                return;

            lock (_scrollLock)
                _pendingScrollDeltas.Enqueue(wheel.Y);
        }

        public override void ClearScrollBuffer()
        {
            lock (_scrollLock)
                _pendingScrollDeltas.Clear();
        }

        private void MouseDown(IMouse mouse, MouseButton button)
            => SetButtonState(button, true);

        private void MouseUp(IMouse mouse, MouseButton button)
            => SetButtonState(button, false);

        private void MouseMove(IMouse mouse, Vector2 position)
        {

        }

        public override void TickStates(float delta)
        {
            _cursor.Tick(CursorPosition.X, CursorPosition.Y);

            lock (_scrollLock)
            {
                _scrollDispatchBuffer.Clear();
                while (_pendingScrollDeltas.Count > 0)
                    _scrollDispatchBuffer.Add(_pendingScrollDeltas.Dequeue());
            }

            for (int i = 0; i < _scrollDispatchBuffer.Count; i++)
                _wheel.Tick(_scrollDispatchBuffer[i]);

            LeftClick?.Tick(IsButtonPressed(MouseButton.Left), delta);
            RightClick?.Tick(IsButtonPressed(MouseButton.Right), delta);
            MiddleClick?.Tick(IsButtonPressed(MouseButton.Middle), delta);
        }

        private bool IsButtonPressed(MouseButton button)
            => button switch
            {
                MouseButton.Left => _leftPressed,
                MouseButton.Right => _rightPressed,
                MouseButton.Middle => _middlePressed,
                _ => false,
            };

        private void SetButtonState(MouseButton button, bool isPressed)
        {
            switch (button)
            {
                case MouseButton.Left:
                    _leftPressed = isPressed;
                    break;
                case MouseButton.Right:
                    _rightPressed = isPressed;
                    break;
                case MouseButton.Middle:
                    _middlePressed = isPressed;
                    break;
            }
        }
    }
}
