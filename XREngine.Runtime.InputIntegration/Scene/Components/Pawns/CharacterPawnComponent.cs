using XREngine.Extensions;
using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input.Devices;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    /// <summary>
    /// Pawn component for controllable player characters with full movement and camera control.
    /// <para>
    /// Converts player inputs (keyboard, mouse, gamepad) into kinematic rigid body movements
    /// and provides first/third-person camera view rotation controls.
    /// </para>
    /// <para>
    /// This component requires a <see cref="CharacterMovement3DComponent"/> sibling to handle
    /// the actual physics-based movement, jumping, crouching, and prone states.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Supports keyboard (WASD), mouse, and gamepad input simultaneously</item>
    /// <item>Provides network state capture for multiplayer synchronization</item>
    /// <item>View rotation is clamped to prevent gimbal lock (±89° pitch, ±180° yaw)</item>
    /// <item>Input sensitivity can be configured per-device type</item>
    /// </list>
    /// </remarks>
    [RequireComponents(typeof(CharacterMovement3DComponent))]
    public partial class CharacterPawnComponent : PawnComponent
    {
        #region Private Fields

        // Component reference (cached via sibling lookup)
        //private readonly GameTimer _respawnTimer = new();

        // View rotation state
        private Rotator _viewRotation = Rotator.GetZero(ERotationOrder.YPR);
        private float _lastYaw = 0.0f;
        private float _lastPitch = 0.0f;

        // Input sensitivity multipliers
        private float _keyboardMovementInputMultiplier = 200.0f;
        private float _keyboardLookXInputMultiplier = 200.0f;
        private float _keyboardLookYInputMultiplier = 200.0f;
        private float _mouseXLookInputMultiplier = 0.12f;
        private float _mouseYLookInputMultiplier = 0.12f;
        private float _gamePadMovementInputMultiplier = 200.0f;
        private float _gamePadXLookInputMultiplier = 100.0f;
        private float _gamePadYLookInputMultiplier = 100.0f;

        // Transform references
        private TransformBase? _viewTransform;
        private Transform? _rotationTransform;

        // Input ignore flags
        private bool _ignoreViewPitchInputs = false;
        private bool _ignoreViewYawInputs = false;

        // Time dilation settings
        private bool _movementAffectedByTimeDilation = true;
        private bool _viewRotationAffectedByTimeDilation = true;

        // Cursor visibility state
        private bool _shouldHideCursor = true;

        // Held keyboard state for deterministic zeroing across pause transitions
        private bool _moveForwardHeld;
        private bool _moveBackwardHeld;
        private bool _moveLeftHeld;
        private bool _moveRightHeld;
        private bool _lookLeftHeld;
        private bool _lookRightHeld;
        private bool _lookUpHeld;
        private bool _lookDownHeld;

        // Root-motion steering state
        private bool _useRootMotionSteering;
        private bool _strafeMode;
        private float _keyboardWalkMagnitude = 0.6f;
        private SteeringSnapshot _lastSteering;

        // Keyboard & gamepad run, stance hold, and optional third-person camera rig reference
        private bool _shiftLeftHeld;
        private bool _shiftRightHeld;
        private bool _gamepadRunHeld;
        private bool _gamepadRunToggled;
        private bool _gamepadStancePressed;
        private bool _gamepadStanceHoldTriggered;
        private ThirdPersonCameraRigComponent? _thirdPersonCameraRig;

        /// <summary>
        /// Raised on each input tick while <see cref="UseRootMotionSteering"/> is enabled so
        /// root-motion bridges or custom scripts can consume camera-relative steering.
        /// Bindable in the inspector without code via <see cref="XREvent{T}"/> persistent calls.
        /// </summary>
        public XREvent<SteeringSnapshot>? RootMotionSteeringUpdated;

        // Network state tracking (edge-triggered flags reset after capture)
        private bool _networkJumpPressed;
        private bool _networkJumpHeld;
        private bool _networkToggleCrouchRequested;
        private bool _networkToggleProneRequested;

        #endregion

        #region Protected Fields

        /// <summary>
        /// Current keyboard movement input accumulator.
        /// X = strafe (left/right), Y = forward/backward.
        /// </summary>
        protected Vector2 _keyboardMovementInput = Vector2.Zero;

        /// <summary>
        /// Current gamepad movement input from left thumbstick.
        /// X = strafe (left/right), Y = forward/backward.
        /// </summary>
        protected Vector2 _gamepadMovementInput = Vector2.Zero;

        /// <summary>
        /// Current keyboard look input accumulator for arrow key camera control.
        /// X = yaw (left/right), Y = pitch (up/down).
        /// </summary>
        protected Vector2 _keyboardLookInput = Vector2.Zero;

        #endregion

        #region Component References

        /// <summary>
        /// Gets the required <see cref="CharacterMovement3DComponent"/> sibling that handles physics movement.
        /// </summary>
        private CharacterMovement3DComponent Movement => GetSiblingComponent<CharacterMovement3DComponent>(true)!;

        #endregion

        #region Public Properties - Input Sensitivity

        /// <summary>
        /// Multiplier applied to keyboard WASD movement input speed.
        /// Higher values result in faster movement response.
        /// </summary>
        public float KeyboardMovementInputMultiplier
        {
            get => _keyboardMovementInputMultiplier;
            set => SetField(ref _keyboardMovementInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to keyboard arrow key horizontal (yaw) look speed.
        /// </summary>
        public float KeyboardLookXInputMultiplier
        {
            get => _keyboardLookXInputMultiplier;
            set => SetField(ref _keyboardLookXInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to keyboard arrow key vertical (pitch) look speed.
        /// </summary>
        public float KeyboardLookYInputMultiplier
        {
            get => _keyboardLookYInputMultiplier;
            set => SetField(ref _keyboardLookYInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to gamepad left thumbstick movement input speed.
        /// </summary>
        public float GamePadMovementInputMultiplier
        {
            get => _gamePadMovementInputMultiplier;
            set => SetField(ref _gamePadMovementInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to mouse horizontal (yaw) look sensitivity.
        /// Lower values provide finer control for precise aiming.
        /// </summary>
        public float MouseXLookInputMultiplier
        {
            get => _mouseXLookInputMultiplier;
            set => SetField(ref _mouseXLookInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to mouse vertical (pitch) look sensitivity.
        /// </summary>
        public float MouseYLookInputMultiplier
        {
            get => _mouseYLookInputMultiplier;
            set => SetField(ref _mouseYLookInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to gamepad right thumbstick horizontal (yaw) look speed.
        /// </summary>
        public float GamePadXLookInputMultiplier
        {
            get => _gamePadXLookInputMultiplier;
            set => SetField(ref _gamePadXLookInputMultiplier, value);
        }

        /// <summary>
        /// Multiplier applied to gamepad right thumbstick vertical (pitch) look speed.
        /// </summary>
        public float GamePadYLookInputMultiplier
        {
            get => _gamePadYLookInputMultiplier;
            set => SetField(ref _gamePadYLookInputMultiplier, value);
        }

        #endregion

        #region Public Properties - Transform References

        /// <summary>
        /// The transform used to determine forward and right directions for movement orientation.
        /// When null, falls back to the camera transform or component transform.
        /// </summary>
        /// <remarks>
        /// Set this to decouple movement orientation from camera view (e.g., for tank controls).
        /// </remarks>
        public TransformBase? InputOrientationTransform
        {
            get => _viewTransform;
            set => SetField(ref _viewTransform, value);
        }

        /// <summary>
        /// The transform that will be rotated by player look inputs.
        /// When null, falls back to the camera's scene node transform.
        /// </summary>
        /// <remarks>
        /// Typically set to a camera boom or head bone transform for proper first/third-person view control.
        /// </remarks>
        public Transform? ViewRotationTransform
        {
            get => _rotationTransform;
            set => SetField(ref _rotationTransform, value);
        }

        #endregion

        #region Public Properties - Input Filtering

        /// <summary>
        /// When true, vertical look inputs (pitch) are ignored and pitch is locked to 0.
        /// Useful for side-scrolling or top-down camera modes.
        /// </summary>
        public bool IgnoreViewTransformPitch
        {
            get => _ignoreViewPitchInputs;
            set => SetField(ref _ignoreViewPitchInputs, value);
        }

        /// <summary>
        /// When true, horizontal look inputs (yaw) are ignored and yaw is locked to 0.
        /// Useful for fixed-camera or rail-based movement scenarios.
        /// </summary>
        public bool IgnoreViewTransformYaw
        {
            get => _ignoreViewYawInputs;
            set => SetField(ref _ignoreViewYawInputs, value);
        }

        #endregion

        #region Public Properties - Time Dilation

        /// <summary>
        /// When true, movement speed is affected by engine time dilation (slow-mo effects).
        /// Set to false for UI-controlled pawns that should ignore time scale.
        /// </summary>
        public bool MovementAffectedByTimeDilation
        {
            get => _movementAffectedByTimeDilation;
            set => SetField(ref _movementAffectedByTimeDilation, value);
        }

        /// <summary>
        /// When true, view rotation speed is affected by engine time dilation.
        /// Set to false to maintain consistent look sensitivity during slow-motion.
        /// </summary>
        public bool ViewRotationAffectedByTimeDilation
        {
            get => _viewRotationAffectedByTimeDilation;
            set => SetField(ref _viewRotationAffectedByTimeDilation, value);
        }

        #endregion

        #region Public Properties - Cursor

        /// <summary>
        /// When true, the mouse cursor is hidden and captured when this pawn is possessed.
        /// Typically enabled for first-person or third-person gameplay.
        /// </summary>
        public bool ShouldHideCursor
        {
            get => _shouldHideCursor;
            set => SetField(ref _shouldHideCursor, value);
        }

        /// <summary>
        /// Returns true when <see cref="PauseToggled"/> already has runtime or persistent listeners
        /// implementing a custom pause menu.
        /// </summary>
        public override bool HasCustomPauseMenu
            => PauseToggled is { } pauseEvent
            && (pauseEvent.HasEffectiveListeners || pauseEvent.HasPersistentCalls);

        /// <inheritdoc />
        protected override void OnGameplayInputPausedChanged(bool paused)
        {
            ClearHeldGameplayInputs();
            ApplyCursorCaptureState();
        }

        private void ApplyCursorCaptureState()
        {
            if (LocalInput is not LocalInputInterface input)
                return;

            input.HideCursor = !IsGameplayInputPaused && ShouldHideCursor;
        }

        private void ClearHeldGameplayInputs()
        {
            _moveForwardHeld = false;
            _moveBackwardHeld = false;
            _moveLeftHeld = false;
            _moveRightHeld = false;
            _lookLeftHeld = false;
            _lookRightHeld = false;
            _lookUpHeld = false;
            _lookDownHeld = false;
            _keyboardMovementInput = Vector2.Zero;
            _gamepadMovementInput = Vector2.Zero;
            _keyboardLookInput = Vector2.Zero;
            _shiftLeftHeld = false;
            _shiftRightHeld = false;
            _gamepadRunHeld = false;
            _gamepadRunToggled = false;
            _gamepadStancePressed = false;
            _gamepadStanceHoldTriggered = false;
            UpdateRunState();
            Movement.Jump(false);
            Movement.SetMovementInput(Vector3.Zero);
            ResolveThirdPersonCameraRig()?.ClearHeldInputs();
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised when the pause toggle key (Escape) is pressed.
        /// Bindable from code or the editor inspector via <see cref="XREvent"/>.
        /// </summary>
        public XREvent? PauseToggled;

        /// <summary>
        /// Raised when the locomotion debug visualization toggle key (F) is pressed.
        /// Bindable from code or the editor inspector via <see cref="XREvent"/>.
        /// </summary>
        public XREvent? LocomotionDebugToggled;

        #endregion

        #region Lifecycle Methods

        /// <summary>
        /// Called when the component is activated. Registers the movement input tick.
        /// </summary>
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Normal, InputConsumptionTickOrder, TickMovementInput);
        }

        /// <summary>
        /// Called when the component is deactivated. Unregisters the movement input tick.
        /// </summary>
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, InputConsumptionTickOrder, TickMovementInput);
        }

        #endregion

        #region Input Registration

        /// <summary>
        /// Registers all input bindings for character control including movement,
        /// camera look, jumping, crouching, and system controls.
        /// </summary>
        /// <param name="input">The input interface to register bindings with.</param>
        public override void RegisterInput(object inputInterface)
        {
            if (inputInterface is not InputInterface input)
                return;
            // Configure cursor visibility based on pawn settings and pause state
            if (ShouldHideCursor && !IsGameplayInputPaused)
                input.HideCursor = !input.Unregister;
            else if (input.HideCursor)
                input.HideCursor = false;

            // Mouse look (relative movement for FPS-style camera)
            input.RegisterMouseMove(MouseLook, EMouseMoveType.Relative);

            // Gamepad movement (left thumbstick) - non-continuous, sets direction directly
            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, MoveRight, false);
            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, MoveForward, false);

            // Gamepad look (right thumbstick) - continuous, adds deltas to view rotation
            input.RegisterAxisUpdate(EGamePadAxis.RightThumbstickX, LookRight, true);
            input.RegisterAxisUpdate(EGamePadAxis.RightThumbstickY, LookUp, true);

            // Gamepad jump (lower face button: A / Cross)
            input.RegisterButtonPressed(EGamePadButton.FaceDown, Jump);
            // Gamepad run hold (left face button: X / Square)
            input.RegisterButtonPressed(EGamePadButton.FaceLeft, OnGamepadRun);
            // Gamepad crouch (tap right face button) and prone (hold right face button: B / Circle)
            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Pressed, OnGamepadStancePressed);
            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Held, OnGamepadStanceHeld);
            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Released, OnGamepadStanceReleased);
            // Gamepad debug visualization toggle (top face button: Y / Triangle)
            input.RegisterButtonEvent(EGamePadButton.FaceUp, EButtonInputType.Pressed, ToggleLocomotionDebug);
            // Gamepad third-person shoulder views (LB / RB) and centered zoom (D-Pad Up / Down)
            input.RegisterButtonEvent(EGamePadButton.LeftBumper, EButtonInputType.Pressed, ToggleLeftShoulderView);
            input.RegisterButtonEvent(EGamePadButton.RightBumper, EButtonInputType.Pressed, ToggleRightShoulderView);
            input.RegisterButtonPressed(EGamePadButton.DPadUp, OnGamepadCameraZoomIn);
            input.RegisterButtonPressed(EGamePadButton.DPadDown, OnGamepadCameraZoomOut);

            if (input.Unregister)
            {
                ClearHeldGameplayInputs();
            }

            // Keyboard movement (WASD)
            input.RegisterKeyStateChange(EKey.W, MoveForward);
            input.RegisterKeyStateChange(EKey.A, MoveLeft);
            input.RegisterKeyStateChange(EKey.S, MoveBackward);
            input.RegisterKeyStateChange(EKey.D, MoveRight);

            // Keyboard look (arrow keys)
            input.RegisterKeyStateChange(EKey.Right, LookRight);
            input.RegisterKeyStateChange(EKey.Left, LookLeft);
            input.RegisterKeyStateChange(EKey.Up, LookUp);
            input.RegisterKeyStateChange(EKey.Down, LookDown);

            // Action keys
            input.RegisterKeyStateChange(EKey.Space, Jump);
            input.RegisterKeyStateChange(EKey.ShiftLeft, OnShiftLeftRun);
            input.RegisterKeyStateChange(EKey.ShiftRight, OnShiftRightRun);
            input.RegisterKeyEvent(EKey.C, EButtonInputType.Pressed, ToggleCrouch);
            input.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, ToggleProne);

            // Third-person camera framing keys (Q/E lowered close shoulder views, R/T centered zoom)
            input.RegisterKeyEvent(EKey.Q, EButtonInputType.Pressed, ToggleLeftShoulderView);
            input.RegisterKeyEvent(EKey.E, EButtonInputType.Pressed, ToggleRightShoulderView);
            input.RegisterKeyStateChange(EKey.R, SetCameraZoomInHeld);
            input.RegisterKeyStateChange(EKey.T, SetCameraZoomOutHeld);
            input.RegisterKeyEvent(EKey.F, EButtonInputType.Pressed, ToggleLocomotionDebug);

            // System keys
            input.RegisterKeyEvent(EKey.Escape, EButtonInputType.Pressed, TogglePause);
            input.RegisterKeyEvent(EKey.Backspace, EButtonInputType.Pressed, ToggleMouseCapture);
        }

        #endregion

        #region Tick Processing

        /// <summary>
        /// Called each frame to process accumulated movement input and update view rotation.
        /// </summary>
        protected virtual void TickMovementInput()
        {
            // Determine which transform provides movement orientation
            var cam = this.GetCamera();
            var tfm = InputOrientationTransform ?? cam?.Transform ?? Transform;

            // Project view-relative axes onto the controller's authored movement plane.
            Vector3 up = Movement.UpDirection;
            up = up.LengthSquared() > 1e-8f ? Vector3.Normalize(up) : Globals.Up;
            Vector3 forward = ProjectOntoPlane(tfm.WorldForward, up);
            Vector3 right = ProjectOntoPlane(tfm.WorldRight, up);
            forward = forward.LengthSquared() > 1e-8f ? Vector3.Normalize(forward) : Vector3.Zero;
            right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.Zero;

            if (IsGameplayInputPaused)
            {
                AddMovement(forward, right);
                return;
            }

            AddMovement(forward, right);
            UpdateViewRotation(cam);
        }

        /// <summary>
        /// Optional third-person camera rig controlled by shoulder view (Q/E, LB/RB) and
        /// centered zoom (R/T, D-Pad Up/Down) inputs. When unset, resolved from this node
        /// or its hierarchy.
        /// </summary>
        public ThirdPersonCameraRigComponent? ThirdPersonCameraRig
        {
            get => _thirdPersonCameraRig;
            set => SetField(ref _thirdPersonCameraRig, value);
        }

        private ThirdPersonCameraRigComponent? ResolveThirdPersonCameraRig()
        {
            if (_thirdPersonCameraRig is not null)
                return _thirdPersonCameraRig;

            if (SceneNode?.GetComponent<ThirdPersonCameraRigComponent>() is { } siblingRig)
            {
                _thirdPersonCameraRig = siblingRig;
                return siblingRig;
            }

            if (SceneNode?.GetComponentInHierarchy(nameof(ThirdPersonCameraRigComponent)) is ThirdPersonCameraRigComponent childRig)
            {
                _thirdPersonCameraRig = childRig;
                return childRig;
            }

            return null;
        }

        /// <summary>
        /// When true, grounded WASD and gamepad left-stick inputs publish camera-relative
        /// steering snapshots for root-motion animation controllers (such as MotionBricks)
        /// instead of driving horizontal velocity directly on <see cref="CharacterMovement3DComponent"/>.
        /// Airborne input still forwards to the movement component for air control.
        /// </summary>
        public bool UseRootMotionSteering
        {
            get => _useRootMotionSteering;
            set => SetField(ref _useRootMotionSteering, value);
        }

        /// <summary>
        /// Enables camera-facing directional strafing when <see cref="UseRootMotionSteering"/> is active.
        /// </summary>
        public bool StrafeMode
        {
            get => _strafeMode;
            set => SetField(ref _strafeMode, value);
        }

        /// <summary>
        /// Normalized speed magnitude (0..1) emitted for keyboard WASD input when not holding Run.
        /// </summary>
        public float KeyboardWalkMagnitude
        {
            get => _keyboardWalkMagnitude;
            set => SetField(ref _keyboardWalkMagnitude, Math.Clamp(value, 0.1f, 1.0f));
        }

        public SteeringSnapshot LastSteering => _lastSteering;

        /// <summary>
        /// Computes camera-relative planar steering (preserving analog thumbstick magnitude)
        /// from the provided view basis vectors.
        /// </summary>
        public SteeringSnapshot ComputeCameraRelativeSteering(Vector3 forward, Vector3 right)
        {
            Vector3 desiredWorld = Vector3.Zero;

            bool keyboardActive = _keyboardMovementInput.X != 0.0f || _keyboardMovementInput.Y != 0.0f;
            if (keyboardActive)
            {
                Vector3 kbDir = forward * _keyboardMovementInput.Y + right * _keyboardMovementInput.X;
                if (kbDir.LengthSquared() > 1.0e-8f)
                {
                    float kbMag = Movement.IsRunning ? 1.0f : _keyboardWalkMagnitude;
                    desiredWorld += Vector3.Normalize(kbDir) * kbMag;
                }
            }

            float stickLenSq = _gamepadMovementInput.LengthSquared();
            if (stickLenSq > 1.0e-8f)
            {
                float stickMag = MathF.Min(1.0f, MathF.Sqrt(stickLenSq));
                Vector2 stickDir = _gamepadMovementInput / MathF.Sqrt(stickLenSq);
                Vector3 gpDir = forward * stickDir.Y + right * stickDir.X;
                if (gpDir.LengthSquared() > 1.0e-8f)
                    desiredWorld += Vector3.Normalize(gpDir) * stickMag;
            }

            var worldXZ = new Vector2(desiredWorld.X, desiredWorld.Z);
            float worldLenSq = worldXZ.LengthSquared();
            if (worldLenSq > 1.0f)
                worldXZ /= MathF.Sqrt(worldLenSq);

            float cameraYaw = forward.LengthSquared() > 1.0e-8f
                ? MathF.Atan2(forward.X, forward.Z)
                : 0.0f;
            bool isGrounded = Movement.CharacterController?.IsGrounded
                ?? (Movement.MovementMode == CharacterMovement3DComponent.EMovementMode.Walking);
            ELocomotionStyle locomotionStyle = Movement.CrouchState switch
            {
                CharacterMovement3DComponent.ECrouchState.Crouched => ELocomotionStyle.Crouched,
                CharacterMovement3DComponent.ECrouchState.Prone => ELocomotionStyle.Prone,
                _ => ELocomotionStyle.Standing,
            };
            Vector3 upDir = Movement.UpDirection;
            upDir = upDir.LengthSquared() > 1.0e-8f ? Vector3.Normalize(upDir) : Globals.Up;
            float verticalVelocity = Movement.CharacterController is { } controller
                ? Vector3.Dot(controller.LinearVelocity, upDir)
                : 0.0f;
            if (!float.IsFinite(verticalVelocity))
                verticalVelocity = 0.0f;

            return new SteeringSnapshot(
                worldXZ,
                cameraYaw,
                _strafeMode,
                Movement.IsRunning,
                isGrounded,
                locomotionStyle,
                verticalVelocity);
        }

        /// <summary>
        /// Converts accumulated input into movement commands sent to the movement component.
        /// </summary>
        /// <param name="forward">The forward direction on the XZ plane.</param>
        /// <param name="right">The right direction on the XZ plane.</param>
        private void AddMovement(Vector3 forward, Vector3 right)
        {
            if (_useRootMotionSteering
                && RootMotionSteeringUpdated is { } steeringEvent
                && (steeringEvent.Count > 0 || steeringEvent.HasPersistentCalls))
            {
                SteeringSnapshot steering = ComputeCameraRelativeSteering(forward, right);
                _lastSteering = steering;
                steeringEvent.Invoke(steering);

                // Grounded translation is owned by animation root motion; keep direct input for airborne control.
                if (steering.IsGrounded)
                {
                    Movement.SetMovementInput(Vector3.Zero);
                    return;
                }

                var airInput = new Vector3(steering.DesiredWorldMoveXZ.X, 0.0f, steering.DesiredWorldMoveXZ.Y);
                Movement.SetMovementInput(airInput * GamePadMovementInputMultiplier);
                return;
            }

            bool keyboardMovement = _keyboardMovementInput.X != 0.0f || _keyboardMovementInput.Y != 0.0f;
            bool gamepadMovement = _gamepadMovementInput.X != 0.0f || _gamepadMovementInput.Y != 0.0f;

            Vector3 movementInput = Vector3.Zero;

            if (keyboardMovement)
            {
                // Combine forward/backward and strafe into world-space direction
                Vector3 input = forward * _keyboardMovementInput.Y + right * _keyboardMovementInput.X;
                movementInput += KeyboardMovementInputMultiplier * input.Normalized();
            }

            if (gamepadMovement)
            {
                // Preserve analog stick magnitude and reject sub-deadzone drift instead of
                // normalizing tiny non-zero values to full locomotion speed.
                const float GamepadMoveDeadzone = 0.10f;
                float stickLength = _gamepadMovementInput.Length();
                if (float.IsFinite(stickLength) && stickLength >= GamepadMoveDeadzone)
                {
                    float scaledMagnitude = Math.Clamp((stickLength - GamepadMoveDeadzone) / (1.0f - GamepadMoveDeadzone), 0.0f, 1.0f);
                    Vector2 stickDir = _gamepadMovementInput / stickLength;
                    Vector3 input = forward * stickDir.Y + right * stickDir.X;
                    if (input.LengthSquared() > 1.0e-8f)
                        movementInput += GamePadMovementInputMultiplier * scaledMagnitude * Vector3.Normalize(input);
                }
            }

            Movement.SetMovementInput(movementInput);
        }

        private static Vector3 ProjectOntoPlane(in Vector3 value, in Vector3 planeNormal)
            => value - planeNormal * Vector3.Dot(value, planeNormal);

        /// <summary>
        /// Applies accumulated view rotation to the target transform, with optional axis locking.
        /// </summary>
        /// <param name="cam">The camera component (used as fallback rotation target).</param>
        private void UpdateViewRotation(CameraComponent? cam)
        {
            var rotTfm = ViewRotationTransform ?? cam?.SceneNode.GetTransformAs<Transform>(false);
            if (rotTfm is null)
                return;

            // Process keyboard arrow key look input
            if (_keyboardLookInput != Vector2.Zero)
                KeyboardLook(_keyboardLookInput.X, _keyboardLookInput.Y);

            // Apply axis locks if configured
            if (_ignoreViewPitchInputs)
                _viewRotation.Pitch = 0.0f;

            if (_ignoreViewYawInputs)
                _viewRotation.Yaw = 0.0f;

            // Skip transform update if rotation hasn't changed (optimization)
            if (XRMath.Approx(_viewRotation.Pitch, _lastPitch) &&
                XRMath.Approx(_viewRotation.Yaw, _lastYaw))
                return;

            _lastPitch = _viewRotation.Pitch;
            _lastYaw = _viewRotation.Yaw;

            rotTfm.Rotator = _viewRotation;
        }

        #endregion

        #region Movement Input Handlers

        /// <summary>
        /// Handles keyboard forward movement (W key).
        /// </summary>
        public void MoveForward(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _moveForwardHeld = pressed;
            UpdateKeyboardMovementInput();
        }

        /// <summary>
        /// Handles keyboard backward movement (S key).
        /// </summary>
        public void MoveBackward(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _moveBackwardHeld = pressed;
            UpdateKeyboardMovementInput();
        }

        /// <summary>
        /// Handles keyboard left strafe (A key).
        /// </summary>
        public void MoveLeft(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _moveLeftHeld = pressed;
            UpdateKeyboardMovementInput();
        }

        /// <summary>
        /// Handles keyboard right strafe (D key).
        /// </summary>
        public void MoveRight(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _moveRightHeld = pressed;
            UpdateKeyboardMovementInput();
        }

        private void UpdateKeyboardMovementInput()
            => _keyboardMovementInput = new Vector2(
                (_moveRightHeld ? 1.0f : 0.0f) - (_moveLeftHeld ? 1.0f : 0.0f),
                (_moveForwardHeld ? 1.0f : 0.0f) - (_moveBackwardHeld ? 1.0f : 0.0f));

        /// <summary>
        /// Handles gamepad right strafe (left thumbstick X axis).
        /// </summary>
        public void MoveRight(float value)
            => _gamepadMovementInput.X = IsGameplayInputPaused ? 0.0f : value;

        /// <summary>
        /// Handles gamepad forward/backward movement (left thumbstick Y axis).
        /// </summary>
        public void MoveForward(float value)
            => _gamepadMovementInput.Y = IsGameplayInputPaused ? 0.0f : value;

        #endregion

        private float ViewRotationDeltaSeconds
        {
            get
            {
                IRuntimeTransformServices? services = RuntimeTransformServices.Current;
                if (services is null)
                    return 0.0f;

                return ViewRotationAffectedByTimeDilation
                    ? services.DilatedUpdateDeltaSeconds
                    : services.UndilatedUpdateDeltaSeconds;
            }
        }

        #region Look Input Handlers

        /// <summary>
        /// Handles keyboard left look (left arrow key).
        /// </summary>
        public void LookLeft(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _lookLeftHeld = pressed;
            UpdateKeyboardLookInput();
        }

        /// <summary>
        /// Handles keyboard right look (right arrow key).
        /// </summary>
        public void LookRight(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _lookRightHeld = pressed;
            UpdateKeyboardLookInput();
        }

        /// <summary>
        /// Handles keyboard down look (down arrow key).
        /// </summary>
        public void LookDown(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _lookDownHeld = pressed;
            UpdateKeyboardLookInput();
        }

        /// <summary>
        /// Handles keyboard up look (up arrow key).
        /// </summary>
        public void LookUp(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _lookUpHeld = pressed;
            UpdateKeyboardLookInput();
        }

        private void UpdateKeyboardLookInput()
            => _keyboardLookInput = new Vector2(
                (_lookRightHeld ? 1.0f : 0.0f) - (_lookLeftHeld ? 1.0f : 0.0f),
                (_lookUpHeld ? 1.0f : 0.0f) - (_lookDownHeld ? 1.0f : 0.0f));

        /// <summary>
        /// Applies a relative mouse displacement directly in degrees per count without frame-time scaling.
        /// </summary>
        /// <param name="dx">Horizontal mouse displacement for the current frame.</param>
        /// <param name="dy">Vertical mouse displacement for the current frame.</param>
        public void MouseLook(float dx, float dy)
        {
            if (IsGameplayInputPaused)
                return;

            // Relative mouse deltas are already integrated displacements, not angular velocities.
            // Multiplying by frame delta time causes erratic sensitivity spikes when frame pacing changes.
            _viewRotation.Pitch += dy * MouseYLookInputMultiplier;
            _viewRotation.Yaw -= dx * MouseXLookInputMultiplier;
            ClampPitch();
            RemapYaw();
        }

        /// <summary>
        /// Processes keyboard arrow key look input.
        /// </summary>
        /// <param name="dx">Horizontal input direction.</param>
        /// <param name="dy">Vertical input direction.</param>
        public void KeyboardLook(float dx, float dy)
        {
            float dt = ViewRotationDeltaSeconds;
            _viewRotation.Pitch += dt * dy * KeyboardLookYInputMultiplier;
            _viewRotation.Yaw -= dt * dx * KeyboardLookXInputMultiplier;
            ClampPitch();
            RemapYaw();
        }

        /// <summary>
        /// Handles gamepad horizontal look (right thumbstick X axis).
        /// </summary>
        /// <summary>Applies a discrete body-yaw turn and resets dependent VR pose histories.</summary>
        public bool SnapTurn(float degrees)
        {
            if (!float.IsFinite(degrees) || MathF.Abs(degrees) > 180 || _ignoreViewYawInputs || ViewRotationTransform is not { } rotation)
                return false;
            _viewRotation.Yaw += degrees;
            RemapYaw();
            _lastYaw = _viewRotation.Yaw;
            rotation.Rotator = _viewRotation;
            rotation.RecalculateMatrices(true);
            XREngine.Input.RuntimeVrDiscontinuityServices.Publish(XREngine.Input.EVrPoseDiscontinuity.SnapTurn);
            return true;
        }

        public void LookRight(float dx)
        {
            if (IsGameplayInputPaused)
                return;

            float dt = ViewRotationDeltaSeconds;
            _viewRotation.Yaw -= dt * dx * GamePadXLookInputMultiplier;
            RemapYaw();
        }

        /// <summary>
        /// Handles gamepad vertical look (right thumbstick Y axis).
        /// </summary>
        public void LookUp(float dy)
        {
            if (IsGameplayInputPaused)
                return;

            float dt = ViewRotationDeltaSeconds;
            _viewRotation.Pitch += dt * dy * GamePadYLookInputMultiplier;
            ClampPitch();
        }

        #endregion

        #region Action Input Handlers

        /// <summary>
        /// Handles jump input (Space key or gamepad A button).
        /// </summary>
        /// <param name="pressed">True when the jump key/button is pressed down.</param>
        public void Jump(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            Movement.Jump(pressed);

            // Track edge-triggered state for network sync
            if (pressed)
                _networkJumpPressed = true;
            _networkJumpHeld = pressed;
        }

        /// <summary>
        /// Starts or stops movement based on the specified input state.
        /// </summary>
        /// <param name="pressed">A value indicating whether the input is currently pressed. Pass <see langword="true"/> to start movement;
        /// <see langword="false"/> to stop movement.</param>
        public void Run(bool pressed)
        {
            Movement.Run(pressed);
        }

        /// <summary>
        /// Updates run hold state for the gamepad left face button (X / Square).
        /// </summary>
        public void OnGamepadRun(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _gamepadRunHeld = pressed;
            UpdateRunState();
        }

        /// <summary>
        /// Toggles latched run state for callers that bind a toggle action.
        /// </summary>
        public void ToggleGamepadRun()
        {
            if (IsGameplayInputPaused)
                return;

            _gamepadRunToggled = !_gamepadRunToggled;
            UpdateRunState();
        }

        private void OnShiftLeftRun(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _shiftLeftHeld = pressed;
            UpdateRunState();
        }

        private void OnShiftRightRun(bool pressed)
        {
            if (IsGameplayInputPaused)
                return;

            _shiftRightHeld = pressed;
            UpdateRunState();
        }

        private void UpdateRunState()
            => Run(_shiftLeftHeld || _shiftRightHeld || _gamepadRunHeld || _gamepadRunToggled);

        /// <summary>
        /// Toggles the lowered, close over-the-left-shoulder camera view (Q key or LB) on the bound <see cref="ThirdPersonCameraRig"/>.
        /// </summary>
        public void ToggleLeftShoulderView()
        {
            if (IsGameplayInputPaused)
                return;

            ResolveThirdPersonCameraRig()?.ToggleLeftShoulderView();
        }

        /// <summary>
        /// Toggles the lowered, close over-the-right-shoulder camera view (E key or RB) on the bound <see cref="ThirdPersonCameraRig"/>.
        /// </summary>
        public void ToggleRightShoulderView()
        {
            if (IsGameplayInputPaused)
                return;

            ResolveThirdPersonCameraRig()?.ToggleRightShoulderView();
        }

        /// <summary>
        /// Updates whether the centered zoom-in key (R) is held on the bound <see cref="ThirdPersonCameraRig"/>.
        /// </summary>
        public void SetCameraZoomInHeld(bool held)
        {
            if (IsGameplayInputPaused)
                return;

            ResolveThirdPersonCameraRig()?.SetZoomInHeld(held);
        }

        /// <summary>
        /// Updates whether the centered zoom-out key (T) is held on the bound <see cref="ThirdPersonCameraRig"/>.
        /// </summary>
        public void SetCameraZoomOutHeld(bool held)
        {
            if (IsGameplayInputPaused)
                return;

            ResolveThirdPersonCameraRig()?.SetZoomOutHeld(held);
        }

        /// <summary>
        /// Updates whether the gamepad zoom-in button (D-Pad Up) is held on the bound <see cref="ThirdPersonCameraRig"/>.
        /// </summary>
        public void OnGamepadCameraZoomIn(bool held)
        {
            if (IsGameplayInputPaused)
                return;

            ResolveThirdPersonCameraRig()?.SetGamepadZoomInHeld(held);
        }

        /// <summary>
        /// Updates whether the gamepad zoom-out button (D-Pad Down) is held on the bound <see cref="ThirdPersonCameraRig"/>.
        /// </summary>
        public void OnGamepadCameraZoomOut(bool held)
        {
            if (IsGameplayInputPaused)
                return;

            ResolveThirdPersonCameraRig()?.SetGamepadZoomOutHeld(held);
        }

        /// <summary>
        /// Starts tracking a right-face-button press so a short tap toggles crouch on release
        /// and a sustained hold toggles prone without firing crouch.
        /// </summary>
        public void OnGamepadStancePressed()
        {
            if (IsGameplayInputPaused)
                return;

            _gamepadStancePressed = true;
            _gamepadStanceHoldTriggered = false;
        }

        /// <summary>
        /// Switches prone state when the right face button is held past the hold threshold.
        /// </summary>
        public void OnGamepadStanceHeld()
        {
            if (IsGameplayInputPaused || !_gamepadStancePressed)
                return;

            _gamepadStanceHoldTriggered = true;
            ToggleProne();
        }

        /// <summary>
        /// Toggles crouch when the right face button is released before the prone hold fires.
        /// </summary>
        public void OnGamepadStanceReleased()
        {
            if (IsGameplayInputPaused || !_gamepadStancePressed)
            {
                _gamepadStancePressed = false;
                _gamepadStanceHoldTriggered = false;
                return;
            }

            if (!_gamepadStanceHoldTriggered)
                ToggleCrouch();
            _gamepadStancePressed = false;
            _gamepadStanceHoldTriggered = false;
        }

        /// <summary>
        /// Toggles between crouched and standing states (C key).
        /// </summary>
        public void ToggleCrouch()
        {
            if (IsGameplayInputPaused)
                return;

            Movement.CrouchState = Movement.CrouchState == CharacterMovement3DComponent.ECrouchState.Crouched
                ? CharacterMovement3DComponent.ECrouchState.Standing
                : CharacterMovement3DComponent.ECrouchState.Crouched;
            _networkToggleCrouchRequested = true;
        }

        /// <summary>
        /// Toggles between prone and standing states (Z key).
        /// </summary>
        public void ToggleProne()
        {
            if (IsGameplayInputPaused)
                return;

            Movement.CrouchState = Movement.CrouchState == CharacterMovement3DComponent.ECrouchState.Prone
                ? CharacterMovement3DComponent.ECrouchState.Standing
                : CharacterMovement3DComponent.ECrouchState.Prone;
            _networkToggleProneRequested = true;
        }

        #endregion

        #region System Input Handlers

        /// <summary>
        /// Toggles the pause state by invoking the <see cref="PauseToggled"/> event.
        /// </summary>
        protected virtual void TogglePause()
            => PauseToggled?.Invoke();

        /// <summary>
        /// Toggles locomotion debug visualization rendering by invoking <see cref="LocomotionDebugToggled"/> (F key).
        /// </summary>
        public void ToggleLocomotionDebug()
        {
            if (IsGameplayInputPaused)
                return;

            LocomotionDebugToggled?.Invoke();
        }

        /// <summary>
        /// Toggles mouse cursor capture/visibility (Backspace key).
        /// Useful for accessing UI or debugging while testing.
        /// </summary>
        public void ToggleMouseCapture()
        {
            if (LocalInput is not LocalInputInterface input)
                return;

            input.HideCursor = !input.HideCursor;
        }

        #endregion

        #region Networking

        /// <summary>
        /// Captures the current input state for network transmission.
        /// Edge-triggered states (jump pressed, toggle requests) are reset after capture.
        /// </summary>
        /// <param name="resetEdgeStates">
        /// When true, resets one-shot input flags after capturing.
        /// Set to false for prediction/replay scenarios.
        /// </param>
        /// <returns>A snapshot of the current input state.</returns>
        public override CharacterPawnInputSnapshot CaptureNetworkInputState()
            => CaptureNetworkInputState(resetEdgeStates: true);

        /// <summary>
        /// Captures the current input state and optionally preserves one-shot input flags.
        /// </summary>
        public CharacterPawnInputSnapshot CaptureNetworkInputState(bool resetEdgeStates)
        {
            // Combine and clamp keyboard + gamepad movement
            Vector2 movement = Vector2.Clamp(
                _keyboardMovementInput + _gamepadMovementInput,
                new Vector2(-1.0f, -1.0f),
                new Vector2(1.0f, 1.0f));

            CharacterPawnInputSnapshot snapshot = new()
            {
                Movement = movement,
                ViewAngles = new Vector2(_viewRotation.Yaw, _viewRotation.Pitch),
                JumpPressed = _networkJumpPressed,
                JumpHeld = _networkJumpHeld,
                ToggleCrouch = _networkToggleCrouchRequested,
                ToggleProne = _networkToggleProneRequested
            };

            // Reset edge-triggered flags to prevent duplicate transmission
            if (resetEdgeStates)
            {
                _networkJumpPressed = false;
                _networkToggleCrouchRequested = false;
                _networkToggleProneRequested = false;
            }

            return snapshot;
        }

        #endregion

        #region View Rotation Utilities

        /// <summary>
        /// Clamps pitch rotation to prevent camera flip-over (gimbal lock prevention).
        /// Limits are ±89 degrees to maintain a small buffer from vertical.
        /// </summary>
        private void ClampPitch()
        {
            if (_viewRotation.Pitch > 89.0f)
                _viewRotation.Pitch = 89.0f;
            else if (_viewRotation.Pitch < -89.0f)
                _viewRotation.Pitch = -89.0f;
        }

        /// <summary>
        /// Wraps yaw rotation to stay within ±180 degrees, preventing value overflow
        /// and ensuring consistent interpolation behavior.
        /// </summary>
        private void RemapYaw()
        {
            if (_viewRotation.Yaw > 180.0f)
                _viewRotation.Yaw -= 360.0f;
            else if (_viewRotation.Yaw < -180.0f)
                _viewRotation.Yaw += 360.0f;
        }

        #endregion

        #region Respawn (Future Implementation)

        //public virtual void Kill(PawnComponent instigator, PawnComponent killer) { }

        //public void QueueRespawn(float respawnTimeInSeconds = 0)
        //    => _respawnTimer.StartSingleFire(WantsRespawn, respawnTimeInSeconds);

        //protected virtual void WantsRespawn()
        //    => _respawnTimer.StartMultiFire(AttemptSpawn, 0.1f);

        //private void AttemptSpawn(float totalElapsed, int fireNumber)
        //{
        //    ICharacterGameMode mode = World?.GameMode as ICharacterGameMode;
        //    if (!mode.FindSpawnPoint(Controller, out Matrix4 transform))
        //        return;
        //
        //    _respawnTimer.Stop();
        //
        //    if (IsSpawned)
        //        Engine.World.DespawnActor(this);
        //
        //    RootComponent.WorldMatrix.Value = transform;
        //    World.SpawnActor(this);
        //}

        #endregion
    }
}
