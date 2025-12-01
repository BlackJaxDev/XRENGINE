using OpenVR.NET.Input;
using Silk.NET.Input;
using XREngine.Data.Core;
using XREngine.Input.Devices.Glfw;
using XREngine.Input.Devices.Types.OpenVR;
using YamlDotNet.Serialization;

namespace XREngine.Input.Devices
{
    public class LocalInputInterface : InputInterface
    {
        /// <summary>
        /// Global registration methods found here are called to register input for any and all controllers,
        /// regardless of the pawn they control or the type of controller they are.
        /// </summary>
        public static List<DelWantsInputsRegistered> GlobalRegisters { get; } = [];

        [YamlIgnore]
        public BaseGamePad? Gamepad { get; private set; }
        [YamlIgnore]
        public BaseKeyboard? Keyboard { get; private set; }
        [YamlIgnore]
        public BaseMouse? Mouse { get; private set; }
        [YamlIgnore]
        public Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>? OpenVRActions { get; private set; }
        public OpenVR.NET.Input.Action? TryGetOpenVRAction(string category, string name)
            => OpenVRActions is not null &&
            OpenVRActions.TryGetValue(category, out var actions) &&
            actions.TryGetValue(name, out var action) ? action : null;

        private int _localPlayerIndex;
        public int LocalPlayerIndex
        {
            get => _localPlayerIndex;
            set => SetField(ref _localPlayerIndex, value);
        }

        public override bool HideCursor
        {
            get => Mouse?.HideCursor ?? false;
            set
            {
                if (Mouse is not null)
                    Mouse.HideCursor = value;
            }
        }

        public LocalInputInterface(int localPlayerIndex) : base(localPlayerIndex)
        {
            LocalPlayerIndex = localPlayerIndex;
        }
        public LocalInputInterface() : base(0)
        {

        }

        public override void TryRegisterInput()
        {
            if (Gamepad is null && Keyboard is null && Mouse is null && OpenVRActions is null)
                return;
            
            TryUnregisterInput();

            Unregister = false;

            //Interface gets input from pawn, hud, local controller, and global list
            OnInputRegistration();

            foreach (DelWantsInputsRegistered register in GlobalRegisters)
                register(this);
        }

        public override void TryUnregisterInput()
        {
            if (Gamepad is null && Keyboard is null && Mouse is null && OpenVRActions is null)
                return;
            
            //Call for regular old input registration, but in the backend,
            //unregister all calls instead of registering them.
            //This way the user doesn't have to do any extra work
            //other than just registering the inputs.
            Unregister = true;
            OnInputRegistration();
            foreach (DelWantsInputsRegistered register in GlobalRegisters)
                register(this);
            Unregister = false;
        }

        public override void RegisterAxisButtonPressedAction(string actionName, DelButtonState func)
        {
            //Gamepad?.TryRegisterAxisButtonPressedAction(actionName, func);
            //Keyboard?.TryRegisterAxisButtonPressedAction(actionName, func);
            //Mouse?.TryRegisterAxisButtonPressedAction(actionName, func);
        }
        public override void RegisterButtonPressedAction(string actionName, DelButtonState func)
        {

        }
        public override void RegisterAxisButtonEventAction(string actionName, System.Action func)
        {

        }
        public override void RegisterButtonEventAction(string actionName, System.Action func)
        {

        }
        public override void RegisterAxisUpdateAction(string actionName, DelAxisValue func, bool continuousUpdate)
        {

        }

        #region Mouse input registration
        public override void RegisterMouseButtonContinuousState(EMouseButton button, DelButtonState func)
            => Mouse?.RegisterButtonPressed(button, func, Unregister);
        public override void RegisterMouseButtonEvent(EMouseButton button, EButtonInputType type, System.Action func)
            => Mouse?.RegisterButtonEvent(button, type, func, Unregister);
        public override void RegisterMouseScroll(DelMouseScroll func)
            => Mouse?.RegisterScroll(func, Unregister);
        public override void RegisterMouseMove(DelCursorUpdate func, EMouseMoveType type)
            => Mouse?.RegisterMouseMove(func, type, Unregister);
        #endregion

        #region Keyboard input registration
        public override void RegisterKeyStateChange(EKey button, DelButtonState func)
            => Keyboard?.RegisterKeyPressed(button, func, Unregister);
        public override void RegisterKeyEvent(EKey button, EButtonInputType type, System.Action func)
            => Keyboard?.RegisterKeyEvent(button, type, func, Unregister);
        public override void RegisterKeystroke(BaseKeyboard.DelKeystroke func)
            => Keyboard?.RegisterKeystroke(func, Unregister);
        public override void RegisterKeyCharacter(Action<char> func)
            => Keyboard?.RegisterKeyCharacter(func, Unregister);
        #endregion

        #region Gamepad input registration
        public override void RegisterAxisButtonPressed(EGamePadAxis axis, DelButtonState func)
            => Gamepad?.RegisterButtonState(axis, func, Unregister);
        public override void RegisterButtonPressed(EGamePadButton button, DelButtonState func)
            => Gamepad?.RegisterButtonState(button, func, Unregister);
        public override void RegisterButtonEvent(EGamePadButton button, EButtonInputType type, System.Action func)
            => Gamepad?.RegisterButtonEvent(button, type, func, Unregister);
        public override void RegisterAxisButtonEvent(EGamePadAxis button, EButtonInputType type, System.Action func)
            => Gamepad?.RegisterButtonEvent(button, type, func, Unregister);
        public override void RegisterAxisUpdate(EGamePadAxis axis, DelAxisValue func, bool continuousUpdate)
            => Gamepad?.RegisterAxisUpdate(axis, func, continuousUpdate, Unregister);
        #endregion

        /// <summary>
        /// Retrieves the state of the requested mouse button: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="button">The button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns>True if the state is current.</returns>
        public override bool GetMouseButtonState(EMouseButton button, EButtonInputType type)
            => Mouse?.GetButtonState(button, type) ?? false;
        /// <summary>
        /// Retrieves the state of the requested keyboard key: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="key">The button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns></returns>
        public override bool GetKeyState(EKey key, EButtonInputType type)
            => Keyboard?.GetKeyState(key, type) ?? false;
        /// <summary>
        /// Retrieves the state of the requested gamepad button: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="button">The button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns></returns>
        public override bool GetButtonState(EGamePadButton button, EButtonInputType type)
            => Gamepad?.GetButtonState(button, type) ?? false;
        /// <summary>
        /// Retrieves the state of the requested axis button: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="axis">The axis button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns></returns>
        public override bool GetAxisState(EGamePadAxis axis, EButtonInputType type)
            => Gamepad?.GetAxisState(axis, type) ?? false;
        /// <summary>
        /// Retrieves the value of the requested axis in the range 0.0f to 1.0f 
        /// or -1.0f to 1.0f for control sticks.
        /// </summary>
        /// <param name="axis">The axis to read the value of.</param>
        /// <returns>The magnitude of the given axis.</returns>
        public override float GetAxisValue(EGamePadAxis axis)
            => Gamepad?.GetAxisValue(axis) ?? 0.0f;

        public void UpdateDevices(IInputContext? input, Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>? vrActions)
        {
            TryUnregisterInput();
            GetDevices(input, vrActions);
            TryRegisterInput();
        }
        private void GetDevices(IInputContext? context, Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>? vrActions)
        {
            AttachInterfaceToDevices(false);

            if (context is null)
                return;

            context.ConnectionChanged += ConnectionChanged;

            //var gamepads = InputDevice.CurrentDevices[EInputDeviceType.Gamepad];
            //var keyboards = InputDevice.CurrentDevices[EInputDeviceType.Keyboard];
            //var mice = InputDevice.CurrentDevices[EInputDeviceType.Mouse];

            var gamepads = context.Gamepads;
            var keyboards = context.Keyboards;
            var mice = context.Mice;

            if (_localPlayerIndex >= 0 && _localPlayerIndex < gamepads.Count)
                Gamepad = new GlfwGamepad(gamepads[_localPlayerIndex]);

            //Keyboard and mouse are reserved for the first player only
            //TODO: support multiple mice and keyboard? Could get difficult with laptops and trackpads and whatnot. Probably no-go.
            //TODO: support input from ALL keyboards and mice for first player. Not just the first found keyboard and mouse.

            if (keyboards.Count > 0 && _localPlayerIndex == 0)
                Keyboard = new GlfwKeyboard(keyboards[0]);

            if (mice.Count > 0 && _localPlayerIndex == 0)
                Mouse = new GlfwMouse(mice[0]);

            OpenVRActions = vrActions;

            AttachInterfaceToDevices(true);
        }

        private void ConnectionChanged(IInputDevice device, bool connected)
        {

        }

        private void AttachInterfaceToDevices(bool attach)
        {
            if (attach)
            {
                Gamepad?.InputInterfaces.Add(this);
                Keyboard?.InputInterfaces.Add(this);
                Mouse?.InputInterfaces.Add(this);
            }
            else
            {
                Gamepad?.InputInterfaces.Remove(this);
                Keyboard?.InputInterfaces.Remove(this);
                Mouse?.InputInterfaces.Remove(this);
            }
        }

        /// <summary>
        /// Updates the state of all input devices.
        /// </summary>
        /// <param name="delta"></param>
        public void TickStates(float delta)
        {
            Gamepad?.TickStates(delta);
            Keyboard?.TickStates(delta);
            Mouse?.TickStates(delta);
            foreach (var a in _registeredOpenVRActions.Values)
                a.TickStates(delta);
        }

        /// <summary>
        /// Clears any buffered mouse scroll input without dispatching it.
        /// Call this when UI has captured input to prevent scroll accumulation.
        /// </summary>
        public void ClearMouseScrollBuffer()
            => Mouse?.ClearScrollBuffer();

        private readonly Dictionary<string, OpenVRActionSetInputs> _registeredOpenVRActions = [];

        public class OpenVRActionSetInputs : XRBase
        {
            private bool _enabled = true;
            public bool Enabled
            {
                get => _enabled;
                set => SetField(ref _enabled, value);
            }

            private readonly Dictionary<string, (BooleanAction action, List<Action<bool>> callbacks)> _registeredBooleanActions = [];
            private readonly Dictionary<string, (ScalarAction action, List<ScalarAction.ValueChangedHandler> callbacks)> _registeredFloatActions = [];
            private readonly Dictionary<string, (Vector2Action action, List<Vector2Action.ValueChangedHandler> callbacks)> _registeredVector2Actions = [];
            private readonly Dictionary<string, (Vector3Action action, List<Vector3Action.ValueChangedHandler> callbacks)> _registeredVector3Actions = [];
            private readonly Dictionary<string, (HandSkeletonAction action, List<DelVRSkeletonSummary> summaryCallbacks)> _registeredHandSkeletonActions = [];

            public void RegisterBooleanAction(string name, BooleanAction action, Action<bool> callback)
            {
                if (!_registeredBooleanActions.TryGetValue(name, out var data))
                    _registeredBooleanActions.Add(name, data = (action, []));
                
                data.callbacks.Add(callback);
                action.ValueUpdated += callback;
            }
            public void RegisterFloatAction(string name, ScalarAction action, ScalarAction.ValueChangedHandler callback)
            {
                if (!_registeredFloatActions.TryGetValue(name, out var data))
                    _registeredFloatActions.Add(name, data = (action, []));
                
                data.callbacks.Add(callback);
                action.ValueChanged += callback;
            }
            public void RegisterVector2Action(string name, Vector2Action action, Vector2Action.ValueChangedHandler callback)
            {
                if (!_registeredVector2Actions.TryGetValue(name, out var data))
                    _registeredVector2Actions.Add(name, data = (action, []));
                
                data.callbacks.Add(callback);
                action.ValueChanged += callback;
            }
            public void RegisterVector3Action(string name, Vector3Action action, Vector3Action.ValueChangedHandler callback)
            {
                if (!_registeredVector3Actions.TryGetValue(name, out var data))
                    _registeredVector3Actions.Add(name, data = (action, []));
                
                data.callbacks.Add(callback);
                action.ValueChanged += callback;
            }
            public void RegisterHandSkeletonActionQuery(string name, HandSkeletonAction action, DelVRSkeletonSummary? callback)
            {
                if (!_registeredHandSkeletonActions.TryGetValue(name, out var data))
                    _registeredHandSkeletonActions.Add(name, data = (action, []));

                if (callback is not null)
                    data.summaryCallbacks.Add(callback);
            }

            public void UnregisterBooleanAction(string name, Action<bool> callback)
            {
                if (_registeredBooleanActions.TryGetValue(name, out var data))
                {
                    data.action.ValueUpdated -= callback;
                    data.callbacks.Remove(callback);
                }

                if (data.callbacks.Count == 0)
                    _registeredBooleanActions.Remove(name);
            }
            public void UnregisterFloatAction(string name, ScalarAction.ValueChangedHandler callback)
            {
                if (_registeredFloatActions.TryGetValue(name, out var data))
                {
                    data.action.ValueChanged -= callback;
                    data.callbacks.Remove(callback);
                }

                if (data.callbacks.Count == 0)
                    _registeredFloatActions.Remove(name);
            }
            public void UnregisterVector2Action(string name, Vector2Action.ValueChangedHandler callback)
            {
                if (_registeredVector2Actions.TryGetValue(name, out var data))
                {
                    data.action.ValueChanged -= callback;
                    data.callbacks.Remove(callback);
                }

                if (data.callbacks.Count == 0)
                    _registeredVector2Actions.Remove(name);
            }
            public void UnregisterVector3Action(string name, Vector3Action.ValueChangedHandler callback)
            {
                if (_registeredVector3Actions.TryGetValue(name, out var data))
                {
                    data.action.ValueChanged -= callback;
                    data.callbacks.Remove(callback);
                }

                if (data.callbacks.Count == 0)
                    _registeredVector3Actions.Remove(name);
            }
            public void UnregisterHandSkeletonActionQuery(string name, DelVRSkeletonSummary? callback)
            {
                if (_registeredHandSkeletonActions.TryGetValue(name, out var data))
                {
                    if (callback is not null)
                        data.summaryCallbacks.Remove(callback);
                }

                if (data.summaryCallbacks.Count == 0)
                    _registeredHandSkeletonActions.Remove(name);
            }

            public void TickStates(float delta)
            {
                if (!Enabled)
                    return;
                
                //TODO: Is OpenVR thread safe? We could execute all these with async tasks if it is.
                foreach (var a in _registeredBooleanActions.Values)
                    a.action.Update();
                foreach (var a in _registeredFloatActions.Values)
                    a.action.Update();
                foreach (var a in _registeredVector2Actions.Values)
                    a.action.Update();
                foreach (var a in _registeredVector3Actions.Values)
                    a.action.Update();
                foreach (var a in _registeredHandSkeletonActions.Values)
                    a.action.Update();
            }
        }

        public override void RegisterVRBoolAction<TCategory, TName>(TCategory category, TName name, Action<bool> func)
        {
            var c = category.ToString();
            if (!_registeredOpenVRActions.TryGetValue(c, out var actions))
                _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());
            
            BooleanAction? action = TryGetOpenVRAction(c, name.ToString()) as BooleanAction;
            if (action is not null)
                actions.RegisterBooleanAction(name.ToString(), action, func);
        }

        public override void RegisterVRFloatAction<TCategory, TName>(TCategory category, TName name, ScalarAction.ValueChangedHandler func)
        {
            var c = category.ToString();
            if (!_registeredOpenVRActions.TryGetValue(c, out var actions))
                _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());

            ScalarAction? action = TryGetOpenVRAction(c, name.ToString()) as ScalarAction;
            if (action is not null)
                actions.RegisterFloatAction(name.ToString(), action, func);
        }

        public override void RegisterVRVector2Action<TCategory, TName>(TCategory category, TName name, Vector2Action.ValueChangedHandler func)
        {
            var c = category.ToString();
            if (!_registeredOpenVRActions.TryGetValue(c, out var actions))
                _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());

            Vector2Action? action = TryGetOpenVRAction(c, name.ToString()) as Vector2Action;
            if (action is not null)
                actions.RegisterVector2Action(name.ToString(), action, func);
        }

        public override void RegisterVRVector3Action<TCategory, TName>(TCategory category, TName name, Vector3Action.ValueChangedHandler func)
        {
            var c = category.ToString();
            if (!_registeredOpenVRActions.TryGetValue(c, out var actions))
                _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());

            Vector3Action? action = TryGetOpenVRAction(c, name.ToString()) as Vector3Action;
            if (action is not null)
                actions.RegisterVector3Action(name.ToString(), action, func);
        }

        public override bool VibrateVRAction<TCategory, TName>(TCategory category, TName name, double duration, double frequency = 40, double amplitude = 1, double delay = 0) 
            => OpenVRActions is not null &&
            OpenVRActions.TryGetValue(category.ToString(), out var actions) &&
            actions.TryGetValue(name.ToString(), out var action) &&
            action is HapticAction h &&
            h.TriggerVibration(duration, frequency, amplitude, delay);

        public override void RegisterVRHandSkeletonQuery<TCategory, TName>(TCategory category, TName name, bool left, EVRSkeletalTransformSpace transformSpace = EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange motionRange = EVRSkeletalMotionRange.WithController, EVRSkeletalReferencePose? overridePose = null)
        {

        }

        public override void RegisterVRHandSkeletonSummaryAction<TCategory, TName>(TCategory category, TName name, bool left, DelVRSkeletonSummary func, EVRSummaryType type)
        {

        }

        public override void RegisterVRPose<TCategory, TName>(IVRActionPoseTransform<TCategory, TName> poseTransform)
        {

        }
    }
}
