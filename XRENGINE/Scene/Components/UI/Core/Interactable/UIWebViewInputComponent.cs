using System;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Forwards UI pointer input to a sibling <see cref="UIWebViewComponent"/>.
    /// </summary>
    [RequireComponents(typeof(UIWebViewComponent))]
    public sealed class UIWebViewInputComponent : UIInteractableComponent
    {
        private UIWebViewComponent? _webView;
        private int _lastX;
        private int _lastY;
        private bool _loggedFirstMove;
        private bool _loggedFirstClick;
        private bool _loggedRegister;

        public UIWebViewInputComponent()
        {
            NeedsMouseMove = true;
            RegisterInputsOnFocus = true;
            InteractOnButtonDown = false;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _webView = GetSiblingComponent<UIWebViewComponent>(true);
        }

        protected internal override void OnComponentDeactivated()
        {
            _webView = null;
            base.OnComponentDeactivated();
        }

        public override void RegisterInput(InputInterface input)
        {
            base.RegisterInput(input);
            if (!_loggedRegister)
            {
                _loggedRegister = true;
                Debug.Out($"[WebViewInput] RegisterInput called (Unregister={input.Unregister})");
            }
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, LeftDown);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Released, LeftUp);
            input.RegisterMouseButtonEvent(EMouseButton.RightClick, EButtonInputType.Pressed, RightDown);
            input.RegisterMouseButtonEvent(EMouseButton.RightClick, EButtonInputType.Released, RightUp);
            input.RegisterMouseButtonEvent(EMouseButton.MiddleClick, EButtonInputType.Pressed, MiddleDown);
            input.RegisterMouseButtonEvent(EMouseButton.MiddleClick, EButtonInputType.Released, MiddleUp);
            input.RegisterMouseScroll(OnMouseScroll);
        }

        public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
        {
            base.MouseMoved(lastPosLocal, posLocal);
            if (!TryConvertLocalToWeb(posLocal, out int x, out int y))
                return;

            _lastX = x;
            _lastY = y;
            if (!_loggedFirstMove)
            {
                _loggedFirstMove = true;
                Debug.Out($"[WebViewInput] First MouseMoved: web({x},{y}) local({posLocal.X:F1},{posLocal.Y:F1})");
            }
            _webView?.Backend.SendMouseMove(x, y);
        }

        private void LeftDown()
        {
            if (!_loggedFirstClick)
            {
                _loggedFirstClick = true;
                Debug.Out($"[WebViewInput] First LeftDown at ({_lastX},{_lastY})");
            }
            _webView?.Backend.SendMouseButton(true, WebMouseButton.Left, _lastX, _lastY);
        }
        private void LeftUp() => _webView?.Backend.SendMouseButton(false, WebMouseButton.Left, _lastX, _lastY);
        private void RightDown() => _webView?.Backend.SendMouseButton(true, WebMouseButton.Right, _lastX, _lastY);
        private void RightUp() => _webView?.Backend.SendMouseButton(false, WebMouseButton.Right, _lastX, _lastY);
        private void MiddleDown() => _webView?.Backend.SendMouseButton(true, WebMouseButton.Middle, _lastX, _lastY);
        private void MiddleUp() => _webView?.Backend.SendMouseButton(false, WebMouseButton.Middle, _lastX, _lastY);

        private void OnMouseScroll(float diff)
        {
            int deltaY = (int)MathF.Round(diff * 80.0f);
            if (deltaY == 0)
                return;

            _webView?.Backend.SendScroll(0, -deltaY);
        }

        private bool TryConvertLocalToWeb(Vector2 localPosition, out int x, out int y)
        {
            x = 0;
            y = 0;

            var size = BoundableTransform.ActualSize;
            if (size.X <= float.Epsilon || size.Y <= float.Epsilon)
                return false;

            float clampedX = Math.Clamp(localPosition.X, 0.0f, size.X - 1.0f);
            float clampedY = Math.Clamp(localPosition.Y, 0.0f, size.Y - 1.0f);

            x = (int)MathF.Round(clampedX);
            y = (int)MathF.Round(size.Y - 1.0f - clampedY);
            return true;
        }
    }
}
