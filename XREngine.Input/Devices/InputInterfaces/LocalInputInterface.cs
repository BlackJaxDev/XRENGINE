using OpenVR.NET.Input;
using Silk.NET.Input;
using XREngine.Data.Core;
using XREngine.Input;
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
            if (Gamepad is null &&
                Keyboard is null &&
                Mouse is null &&
                OpenVRActions is null &&
                RuntimeVrInputServices.ActiveRuntime == RuntimeVrRuntimeKind.None)
            {
                return;
            }
            
            TryUnregisterInput();

            Unregister = false;

            //Interface gets input from pawn, hud, local controller, and global list
            OnInputRegistration();

            foreach (DelWantsInputsRegistered register in GlobalRegisters)
                register(this);
        }

        public override void TryUnregisterInput()
        {
            if (Gamepad is null &&
                Keyboard is null &&
                Mouse is null &&
                OpenVRActions is null &&
                RuntimeVrInputServices.ActiveRuntime == RuntimeVrRuntimeKind.None)
            {
                return;
            }
            
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

        public void UpdateDevices(
            BaseKeyboard? keyboard,
            BaseMouse? mouse,
            BaseGamePad? gamepad,
            Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>? vrActions)
        {
            TryUnregisterInput();
            AttachInterfaceToDevices(false);
            Keyboard = keyboard;
            Mouse = mouse;
            Gamepad = gamepad;
            OpenVRActions = vrActions;
            AttachInterfaceToDevices(true);
            TryRegisterInput();
        }

        private void GetDevices(IInputContext? context, Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>? vrActions)
        {
            AttachInterfaceToDevices(false);
            Gamepad = null;
            Keyboard = null;
            Mouse = null;
            OpenVRActions = vrActions;

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
            RuntimeVrInputServices.Update(delta);
        }

        /// <summary>
        /// Clears any buffered mouse scroll input without dispatching it.
        /// Call this when UI has captured input to prevent scroll accumulation.
        /// </summary>
        public void ClearMouseScrollBuffer()
            => Mouse?.ClearScrollBuffer();

        private readonly Dictionary<(string Category, string Name, Delegate Callback), RuntimeVrScalarChanged> _runtimeFloatCallbacks = [];
        private readonly Dictionary<(string Category, string Name, Delegate Callback), RuntimeVrVector2Changed> _runtimeVector2Callbacks = [];
        private readonly Dictionary<(string Category, string Name, Delegate Callback), RuntimeVrVector3Changed> _runtimeVector3Callbacks = [];
        private readonly Dictionary<object, RuntimeVrPoseChanged> _runtimePoseCallbacks = [];
        private readonly Dictionary<(string Category, string Name, bool LeftHand, Delegate Callback), RuntimeVrSkeletonSummaryChanged> _runtimeSkeletonSummaryCallbacks = [];

        private static string RuntimeActionPart<T>(T value)
            => value?.ToString() ?? string.Empty;

        public override void RegisterVRBoolAction<TCategory, TName>(TCategory category, TName name, Action<bool> func)
        {
            string c = RuntimeActionPart(category);
            string n = RuntimeActionPart(name);
            RuntimeVrInputServices.RegisterBoolAction(c, n, func, Unregister);
        }

        public override void RegisterVRFloatAction<TCategory, TName>(TCategory category, TName name, ScalarAction.ValueChangedHandler func)
        {
            string c = RuntimeActionPart(category);
            string n = RuntimeActionPart(name);
            var key = (c, n, (Delegate)func);
            if (Unregister)
            {
                if (_runtimeFloatCallbacks.TryGetValue(key, out RuntimeVrScalarChanged? callback))
                {
                    RuntimeVrInputServices.RegisterFloatAction(c, n, callback, unregister: true);
                    _runtimeFloatCallbacks.Remove(key);
                }
                return;
            }

            if (!_runtimeFloatCallbacks.TryGetValue(key, out RuntimeVrScalarChanged? runtimeCallback))
                _runtimeFloatCallbacks.Add(key, runtimeCallback = func.Invoke);

            RuntimeVrInputServices.RegisterFloatAction(c, n, runtimeCallback, unregister: false);
        }

        public override void RegisterVRVector2Action<TCategory, TName>(TCategory category, TName name, Vector2Action.ValueChangedHandler func)
        {
            string c = RuntimeActionPart(category);
            string n = RuntimeActionPart(name);
            var key = (c, n, (Delegate)func);
            if (Unregister)
            {
                if (_runtimeVector2Callbacks.TryGetValue(key, out RuntimeVrVector2Changed? callback))
                {
                    RuntimeVrInputServices.RegisterVector2Action(c, n, callback, unregister: true);
                    _runtimeVector2Callbacks.Remove(key);
                }
                return;
            }

            if (!_runtimeVector2Callbacks.TryGetValue(key, out RuntimeVrVector2Changed? runtimeCallback))
                _runtimeVector2Callbacks.Add(key, runtimeCallback = func.Invoke);

            RuntimeVrInputServices.RegisterVector2Action(c, n, runtimeCallback, unregister: false);
        }

        public override void RegisterVRVector3Action<TCategory, TName>(TCategory category, TName name, Vector3Action.ValueChangedHandler func)
        {
            string c = RuntimeActionPart(category);
            string n = RuntimeActionPart(name);
            var key = (c, n, (Delegate)func);
            if (Unregister)
            {
                if (_runtimeVector3Callbacks.TryGetValue(key, out RuntimeVrVector3Changed? callback))
                {
                    RuntimeVrInputServices.RegisterVector3Action(c, n, callback, unregister: true);
                    _runtimeVector3Callbacks.Remove(key);
                }
                return;
            }

            if (!_runtimeVector3Callbacks.TryGetValue(key, out RuntimeVrVector3Changed? runtimeCallback))
                _runtimeVector3Callbacks.Add(key, runtimeCallback = func.Invoke);

            RuntimeVrInputServices.RegisterVector3Action(c, n, runtimeCallback, unregister: false);
        }

        public override bool VibrateVRAction<TCategory, TName>(TCategory category, TName name, double duration, double frequency = 40, double amplitude = 1, double delay = 0) 
            => RuntimeVrInputServices.VibrateAction(RuntimeActionPart(category), RuntimeActionPart(name), duration, frequency, amplitude, delay);

        public override void RegisterVRHandSkeletonQuery<TCategory, TName>(TCategory category, TName name, bool left, EVRSkeletalTransformSpace transformSpace = EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange motionRange = EVRSkeletalMotionRange.WithController, EVRSkeletalReferencePose? overridePose = null)
        {
            RuntimeVrInputServices.RegisterHandSkeletonQuery(RuntimeActionPart(category), RuntimeActionPart(name), left, Unregister);
        }

        public override void RegisterVRHandSkeletonSummaryAction<TCategory, TName>(TCategory category, TName name, bool left, DelVRSkeletonSummary func, EVRSummaryType type)
        {
            string c = RuntimeActionPart(category);
            string n = RuntimeActionPart(name);
            var key = (c, n, left, (Delegate)func);
            if (Unregister)
            {
                if (_runtimeSkeletonSummaryCallbacks.TryGetValue(key, out RuntimeVrSkeletonSummaryChanged? callback))
                {
                    RuntimeVrInputServices.RegisterHandSkeletonSummaryAction(c, n, left, callback, unregister: true);
                    _runtimeSkeletonSummaryCallbacks.Remove(key);
                }
                return;
            }

            if (!_runtimeSkeletonSummaryCallbacks.TryGetValue(key, out RuntimeVrSkeletonSummaryChanged? runtimeCallback))
            {
                runtimeCallback = (in RuntimeVrSkeletonSummary summary) =>
                {
                    var level = summary.HasRealHandJoints
                        ? EVRSkeletalTrackingLevel.VRSkeletalTracking_Full
                        : summary.IsActive
                            ? EVRSkeletalTrackingLevel.VRSkeletalTracking_Estimated
                            : EVRSkeletalTrackingLevel.VRSkeletalTracking_Partial;
                    func(
                        summary.ThumbCurl,
                        summary.IndexCurl,
                        summary.MiddleCurl,
                        summary.RingCurl,
                        summary.LittleCurl,
                        summary.ThumbIndexSplay,
                        summary.IndexMiddleSplay,
                        summary.MiddleRingSplay,
                        summary.RingLittleSplay,
                        level);
                };
                _runtimeSkeletonSummaryCallbacks.Add(key, runtimeCallback);
            }

            RuntimeVrInputServices.RegisterHandSkeletonSummaryAction(c, n, left, runtimeCallback, unregister: false);
        }

        public override void RegisterVRPose<TCategory, TName>(IVRActionPoseTransform<TCategory, TName> poseTransform)
        {
            if (Unregister)
            {
                if (_runtimePoseCallbacks.TryGetValue(poseTransform, out RuntimeVrPoseChanged? callback))
                {
                    RuntimeVrInputServices.RegisterPoseAction(
                        poseTransform.ActionCategory.ToString(),
                        poseTransform.ActionName.ToString(),
                        ResolvePoseKind(poseTransform.ActionName.ToString()),
                        ResolveLeftHand(poseTransform.ActionName.ToString()),
                        callback,
                        unregister: true);
                    _runtimePoseCallbacks.Remove(poseTransform);
                }
                return;
            }

            if (!_runtimePoseCallbacks.TryGetValue(poseTransform, out RuntimeVrPoseChanged? runtimeCallback))
            {
                runtimeCallback = (in RuntimeVrPoseState pose) =>
                {
                    if (!pose.IsValid)
                        return;

                    poseTransform.Position = pose.Position;
                    poseTransform.Rotation = pose.Rotation;
                    poseTransform.Velocity = pose.Velocity;
                    poseTransform.AngularVelocity = pose.AngularVelocity;
                };
                _runtimePoseCallbacks.Add(poseTransform, runtimeCallback);
            }

            RuntimeVrInputServices.RegisterPoseAction(
                poseTransform.ActionCategory.ToString(),
                poseTransform.ActionName.ToString(),
                ResolvePoseKind(poseTransform.ActionName.ToString()),
                ResolveLeftHand(poseTransform.ActionName.ToString()),
                runtimeCallback,
                unregister: false);
        }

        private static RuntimeVrPoseKind ResolvePoseKind(string actionName)
            => actionName.Contains("Aim", StringComparison.OrdinalIgnoreCase) ||
               actionName.Contains("Ray", StringComparison.OrdinalIgnoreCase) ||
               actionName.Contains("Pointer", StringComparison.OrdinalIgnoreCase)
                ? RuntimeVrPoseKind.Aim
                : RuntimeVrPoseKind.Grip;

        private static bool ResolveLeftHand(string actionName)
            => actionName.Contains("Left", StringComparison.OrdinalIgnoreCase);
    }
}
