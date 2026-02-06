using Extensions;
using System.ComponentModel;
using XREngine.Input.Devices;

namespace XREngine.Components
{
    public abstract class FlyingCameraPawnBaseComponent : PawnComponent
    {
        protected float
            _incRight = 0.0f,
            _incForward = 0.0f,
            _incUp = 0.0f,
            _incPitch = 0.0f,
            _incYaw = 0.0f;

        public float Yaw
        {
            get => _yaw;
            set => SetField(ref _yaw, value.RemapToRange(-180.0f, 180.0f));
        }
        public float Pitch
        {
            get => _pitch;
            set => SetField(ref _pitch, value.RemapToRange(-180.0f, 180.0f));
        }

        public void SetYawPitch(float yaw, float pitch)
        {
            _yaw = yaw.RemapToRange(-180.0f, 180.0f);
            _pitch = pitch.RemapToRange(-180.0f, 180.0f);
            YawPitchUpdated();
        }

        public void AddYawPitch(float yawDiff, float pitchDiff)
            => SetYawPitch(Yaw + yawDiff, Pitch + pitchDiff);

        protected abstract void YawPitchUpdated();

        [Browsable(false)]
        public bool ShiftPressed
        {
            get => _shiftPressed;
            private set => SetField(ref _shiftPressed, value);
        }

        [Browsable(false)]
        public bool CtrlPressed
        {
            get => _ctrlPressed;
            private set => SetField(ref _ctrlPressed, value);
        }

        [Browsable(false)]
        public bool RightClickPressed
        {
            get => _rightClickPressed;
            private set => SetField(ref _rightClickPressed, value);
        }

        [Browsable(false)]
        public bool LeftClickPressed
        {
            get => _leftClickPressed;
            private set => SetField(ref _leftClickPressed, value);
        }

        protected bool
            _ctrlPressed = false,
            _shiftPressed = false,
            _rightClickPressed = false,
            _leftClickPressed = false;

        private float _yaw;
        private float _pitch;

        [Browsable(false)]
        public bool Rotating => _rightClickDragging && _ctrlPressed;

        [Browsable(false)]
        public bool Translating => _rightClickDragging && !_ctrlPressed;

        [Browsable(false)]
        public bool Moving => Rotating || Translating;

        [Category("Movement")]
        public float ScrollSpeed { get; set; } = 7.0f;

        [Category("Movement")]
        public float MouseRotateSpeed { get; set; } = 0.75f;

        [Category("Movement")]
        public float MouseTranslateSpeed { get; set; } = 0.01f;

        [Category("Movement")]
        public float GamepadRotateSpeed { get; set; } = 2.0f;

        [Category("Movement")]
        public float GamepadTranslateSpeed { get; set; } = 30.0f;

        [Category("Movement")]
        public float KeyboardTranslateSpeed { get; set; } = 10.0f;

        [Category("Movement")]
        public float KeyboardRotateSpeed { get; set; } = 1.0f;

        public override void RegisterInput(InputInterface input)
        {
            input.RegisterMouseScroll(OnScrolled);
            input.RegisterMouseMove(MouseMove, EMouseMoveType.Relative);

            input.RegisterMouseButtonContinuousState(EMouseButton.LeftClick, OnLeftClick);
            input.RegisterMouseButtonContinuousState(EMouseButton.RightClick, OnRightClick);

            input.RegisterKeyStateChange(EKey.A, MoveLeft);
            input.RegisterKeyStateChange(EKey.W, MoveForward);
            input.RegisterKeyStateChange(EKey.S, MoveBackward);
            input.RegisterKeyStateChange(EKey.D, MoveRight);
            input.RegisterKeyStateChange(EKey.Q, MoveDown);
            input.RegisterKeyStateChange(EKey.E, MoveUp);

            input.RegisterKeyStateChange(EKey.Up, PitchUp);
            input.RegisterKeyStateChange(EKey.Down, PitchDown);
            input.RegisterKeyStateChange(EKey.Left, YawLeft);
            input.RegisterKeyStateChange(EKey.Right, YawRight);

            input.RegisterKeyStateChange(EKey.ControlLeft, OnControl);
            input.RegisterKeyStateChange(EKey.ControlRight, OnControl);
            input.RegisterKeyStateChange(EKey.ShiftLeft, OnShift);
            input.RegisterKeyStateChange(EKey.ShiftRight, OnShift);

            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, OnLeftStickX, false);
            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, OnLeftStickY, false);
            input.RegisterAxisUpdate(EGamePadAxis.RightThumbstickX, OnRightStickX, false);
            input.RegisterAxisUpdate(EGamePadAxis.RightThumbstickY, OnRightStickY, false);

            input.RegisterButtonPressed(EGamePadButton.RightBumper, MoveUp);
            input.RegisterButtonPressed(EGamePadButton.LeftBumper, MoveDown);
        }

        public bool AllowKeyboardInput
            => LocalPlayerController?.FocusedUIComponent is null;

        protected virtual void MoveDown(bool pressed)
        {
            if (AllowKeyboardInput)
                _incUp += KeyboardTranslateSpeed * (pressed ? -1.0f : 1.0f);
        }
        protected virtual void MoveUp(bool pressed)
        {
            if (AllowKeyboardInput)
                _incUp += KeyboardTranslateSpeed * (pressed ? 1.0f : -1.0f);
        }
        protected virtual void MoveLeft(bool pressed)
        {
            if (AllowKeyboardInput)
                _incRight += KeyboardTranslateSpeed * (pressed ? -1.0f : 1.0f);
        }
        protected virtual void MoveRight(bool pressed)
        {
            if (AllowKeyboardInput)
                _incRight += KeyboardTranslateSpeed * (pressed ? 1.0f : -1.0f);
        }
        protected virtual void MoveBackward(bool pressed)
        {
            if (AllowKeyboardInput)
                _incForward += KeyboardTranslateSpeed * (pressed ? -1.0f : 1.0f);
        }
        protected virtual void MoveForward(bool pressed)
        {
            if (AllowKeyboardInput)
                _incForward += KeyboardTranslateSpeed * (pressed ? 1.0f : -1.0f);
        }

        protected virtual void OnLeftStickX(float value)
            => _incRight = value * GamepadTranslateSpeed;
        protected virtual void OnLeftStickY(float value)
            => _incForward = value * GamepadTranslateSpeed;
        protected virtual void OnRightStickX(float value)
            => _incYaw = -value * GamepadRotateSpeed;
        protected virtual void OnRightStickY(float value)
            => _incPitch = value * GamepadRotateSpeed;

        protected virtual void YawRight(bool pressed)
        {
            if (AllowKeyboardInput)
                _incYaw -= KeyboardRotateSpeed * (pressed ? 1.0f : -1.0f);
        }
        protected virtual void YawLeft(bool pressed)
        {
            if (AllowKeyboardInput)
                _incYaw += KeyboardRotateSpeed * (pressed ? 1.0f : -1.0f);
        }
        protected virtual void PitchDown(bool pressed)
        {
            if (AllowKeyboardInput)
                _incPitch -= KeyboardRotateSpeed * (pressed ? 1.0f : -1.0f);
        }
        protected virtual void PitchUp(bool pressed)
        {
            if (AllowKeyboardInput)
                _incPitch += KeyboardRotateSpeed * (pressed ? 1.0f : -1.0f);
        }

        protected void OnShift(bool pressed)
        {
            // Modifier keys are NOT gated by AllowKeyboardInput — they must always
            // update so that Ctrl+right-click rotation works even when a UI element
            // has focus (e.g., after clicking a toolbar button).
            ShiftPressed = pressed;
        }
        private void OnControl(bool pressed)
        {
            CtrlPressed = pressed;
        }

        public bool IsHoveringUI()
            => LinkedUICanvasInputs.Any(x => x.TopMostElement is not null);

        private bool _rightClickDragging = false;

        protected virtual void OnRightClick(bool pressed)
        {
            bool stateChanged = RightClickPressed != pressed;

            RightClickPressed = pressed;

            if (pressed)
            {
                if (!stateChanged)
                    return;

                _rightClickDragging = !IsHoveringUI();

                // When right-clicking in the viewport (not over UI), clear the focused
                // UI component so that keyboard input (WASD, arrows) is restored.
                if (_rightClickDragging)
                {
                    var controller = LocalPlayerController;
                    if (controller is not null)
                        controller.FocusedUIComponent = null;
                }
            }
            else
            {
                _rightClickDragging = false;
            }
        }
        protected virtual void OnLeftClick(bool pressed)
            => LeftClickPressed = pressed;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Normal, ETickOrder.Input, Tick);
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Input, Tick);
        }

        protected abstract void OnScrolled(float diff);
        protected abstract void MouseMove(float x, float y);
        protected abstract void Tick();

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Yaw):
                case nameof(Pitch):
                    YawPitchUpdated();
                    break;
            }
        }

        //Dictionary<ComboModifier, Action<bool>> _combos = new Dictionary<ComboModifier, Action<bool>>();

        //private void ExecuteCombo(EMouseButton button, bool pressed)
        //{
        //    //ComboModifier mod = GetModifier(button, _alt, _ctrl, _shift);
        //    //if (_combos.ContainsKey(mod))
        //    //    _combos[mod](pressed);
        //}

        //private ComboModifier GetModifier(EMouseButton button, bool alt, bool ctrl, bool shift)
        //{
        //    ComboModifier mod = ComboModifier.None;

        //    if (button == EMouseButton.LeftClick)
        //        mod |= ComboModifier.LeftClick;
        //    else if (button == EMouseButton.RightClick)
        //        mod |= ComboModifier.RightClick;
        //    else if (button == EMouseButton.MiddleClick)
        //        mod |= ComboModifier.MiddleClick;

        //    if (_ctrl)
        //        mod |= ComboModifier.Ctrl;
        //    if (_alt)
        //        mod |= ComboModifier.Alt;
        //    if (_shift)
        //        mod |= ComboModifier.Shift;

        //    return mod;
        //}
        //public void SetInputCombo(Action<bool> func, EMouseButton button, bool alt, bool ctrl, bool shift)
        //{
        //    ComboModifier mod = GetModifier(button, alt, ctrl, shift);
        //    if (mod != ComboModifier.None)
        //        if (_combos.ContainsKey(mod))
        //            _combos[mod] = func;
        //        else
        //            _combos.Add(mod, func);
        //}
    }
}
