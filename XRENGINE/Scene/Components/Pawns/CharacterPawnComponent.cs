using Extensions;
using MemoryPack;
using System.Numerics;
using XREngine.Components.Movement;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input.Devices;
using XREngine.Networking;
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
        #region Nested Types

        /// <summary>
        /// Serializable snapshot of the pawn's input state for network synchronization.
        /// Captures movement direction, view angles, and action states (jump, crouch, prone).
        /// </summary>
        [MemoryPackable]
        public partial struct NetworkInputState : IPawnInputSnapshot
        {
            /// <summary>
            /// Combined movement input direction (X = strafe, Y = forward/backward).
            /// Values are clamped to [-1, 1] range.
            /// </summary>
            public Vector2 Movement;

            /// <summary>
            /// Current view angles (X = yaw, Y = pitch) in degrees.
            /// </summary>
            public Vector2 ViewAngles;

            /// <summary>
            /// True if jump was pressed this frame (edge-triggered).
            /// </summary>
            public bool JumpPressed;

            /// <summary>
            /// True if jump is currently held down (level-triggered).
            /// </summary>
            public bool JumpHeld;

            /// <summary>
            /// True if crouch toggle was requested this frame.
            /// </summary>
            public bool ToggleCrouch;

            /// <summary>
            /// True if prone toggle was requested this frame.
            /// </summary>
            public bool ToggleProne;
        }

        #endregion

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
        private float _mouseXLookInputMultiplier = 10.0f;
        private float _mouseYLookInputMultiplier = 10.0f;
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

        // Cursor visibility
        private bool _shouldHideCursor = true;

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

        #endregion

        #region Events

        /// <summary>
        /// Raised when the pause toggle key (Escape) is pressed.
        /// Subscribe to this event to show/hide a pause menu.
        /// </summary>
        public event Action? PauseToggled;

        #endregion

        #region Lifecycle Methods

        /// <summary>
        /// Called when the component is activated. Registers the movement input tick.
        /// </summary>
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Normal, ETickOrder.Input, TickMovementInput);
        }

        #endregion

        #region Input Registration

        /// <summary>
        /// Registers all input bindings for character control including movement,
        /// camera look, jumping, crouching, and system controls.
        /// </summary>
        /// <param name="input">The input interface to register bindings with.</param>
        public override void RegisterInput(InputInterface input)
        {
            // Configure cursor visibility based on pawn settings
            if (ShouldHideCursor)
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

            // Gamepad jump (A button / face down)
            input.RegisterButtonPressed(EGamePadButton.FaceDown, Jump);

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
            input.RegisterKeyEvent(EKey.C, EButtonInputType.Pressed, ToggleCrouch);
            input.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, ToggleProne);

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
            var cam = GetCamera();
            var tfm = InputOrientationTransform ?? cam?.Transform ?? Transform;

            // Get XZ-plane directions for ground-based movement
            tfm.GetDirectionsXZ(out Vector3 forward, out Vector3 right);

            AddMovement(forward, right);
            UpdateViewRotation(cam);
        }

        /// <summary>
        /// Converts accumulated input into movement commands sent to the movement component.
        /// </summary>
        /// <param name="forward">The forward direction on the XZ plane.</param>
        /// <param name="right">The right direction on the XZ plane.</param>
        private void AddMovement(Vector3 forward, Vector3 right)
        {
            bool keyboardMovement = _keyboardMovementInput.X != 0.0f || _keyboardMovementInput.Y != 0.0f;
            bool gamepadMovement = _gamepadMovementInput.X != 0.0f || _gamepadMovementInput.Y != 0.0f;

            if (!keyboardMovement && !gamepadMovement)
                return;

            // Select appropriate delta time based on time dilation setting
            float dt = MovementAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;

            if (keyboardMovement)
            {
                // Combine forward/backward and strafe into world-space direction
                Vector3 input = forward * _keyboardMovementInput.Y + right * _keyboardMovementInput.X;
                Movement.AddMovementInput(dt * KeyboardMovementInputMultiplier * input.Normalized());
            }

            if (gamepadMovement)
            {
                Vector3 input = forward * _gamepadMovementInput.Y + right * _gamepadMovementInput.X;
                Movement.AddMovementInput(dt * GamePadMovementInputMultiplier * input.Normalized());
            }
        }

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
            => _keyboardMovementInput.Y += pressed ? 1.0f : -1.0f;

        /// <summary>
        /// Handles keyboard backward movement (S key).
        /// </summary>
        public void MoveBackward(bool pressed)
            => _keyboardMovementInput.Y += pressed ? -1.0f : 1.0f;

        /// <summary>
        /// Handles keyboard left strafe (A key).
        /// </summary>
        public void MoveLeft(bool pressed)
            => _keyboardMovementInput.X += pressed ? -1.0f : 1.0f;

        /// <summary>
        /// Handles keyboard right strafe (D key).
        /// </summary>
        public void MoveRight(bool pressed)
            => _keyboardMovementInput.X += pressed ? 1.0f : -1.0f;

        /// <summary>
        /// Handles gamepad right strafe (left thumbstick X axis).
        /// </summary>
        public void MoveRight(float value)
            => _gamepadMovementInput.X = value;

        /// <summary>
        /// Handles gamepad forward/backward movement (left thumbstick Y axis).
        /// </summary>
        public void MoveForward(float value)
            => _gamepadMovementInput.Y = value;

        #endregion

        #region Look Input Handlers

        /// <summary>
        /// Handles keyboard left look (left arrow key).
        /// </summary>
        public void LookLeft(bool pressed)
            => _keyboardLookInput.X += pressed ? -1.0f : 1.0f;

        /// <summary>
        /// Handles keyboard right look (right arrow key).
        /// </summary>
        public void LookRight(bool pressed)
            => _keyboardLookInput.X += pressed ? 1.0f : -1.0f;

        /// <summary>
        /// Handles keyboard down look (down arrow key).
        /// </summary>
        public void LookDown(bool pressed)
            => _keyboardLookInput.Y += pressed ? -1.0f : 1.0f;

        /// <summary>
        /// Handles keyboard up look (up arrow key).
        /// </summary>
        public void LookUp(bool pressed)
            => _keyboardLookInput.Y += pressed ? 1.0f : -1.0f;

        /// <summary>
        /// Processes mouse look input, applying sensitivity and updating view rotation.
        /// </summary>
        /// <param name="dx">Horizontal mouse delta.</param>
        /// <param name="dy">Vertical mouse delta.</param>
        public void MouseLook(float dx, float dy)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Pitch += dt * dy * MouseYLookInputMultiplier;
            _viewRotation.Yaw -= dt * dx * MouseXLookInputMultiplier;
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
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Pitch += dt * dy * KeyboardLookYInputMultiplier;
            _viewRotation.Yaw -= dt * dx * KeyboardLookXInputMultiplier;
            ClampPitch();
            RemapYaw();
        }

        /// <summary>
        /// Handles gamepad horizontal look (right thumbstick X axis).
        /// </summary>
        public void LookRight(float dx)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Yaw -= dt * dx * GamePadXLookInputMultiplier;
            RemapYaw();
        }

        /// <summary>
        /// Handles gamepad vertical look (right thumbstick Y axis).
        /// </summary>
        public void LookUp(float dy)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
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
            Movement.Jump(pressed);

            // Track edge-triggered state for network sync
            if (pressed)
                _networkJumpPressed = true;
            _networkJumpHeld = pressed;
        }

        /// <summary>
        /// Toggles between crouched and standing states (C key).
        /// </summary>
        public void ToggleCrouch()
        {
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
        /// Toggles mouse cursor capture/visibility (Backspace key).
        /// Useful for accessing UI or debugging while testing.
        /// </summary>
        public void ToggleMouseCapture()
        {
            if (LocalInput is null)
                return;

            LocalInput.HideCursor = !LocalInput.HideCursor;
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
        public NetworkInputState CaptureNetworkInputState(bool resetEdgeStates = true)
        {
            // Combine and clamp keyboard + gamepad movement
            Vector2 movement = Vector2.Clamp(
                _keyboardMovementInput + _gamepadMovementInput,
                new Vector2(-1.0f, -1.0f),
                new Vector2(1.0f, 1.0f));

            NetworkInputState snapshot = new()
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
