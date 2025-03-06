using Extensions;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input.Devices;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{

    /// <summary>
    /// Pawn used for moveable player characters.
    /// Converts inputs into kinematic rigid body movements and provides inputs for a camera view.
    /// Requires CharacterMovement3DComponent to apply movement inputs.
    /// </summary>
    [RequireComponents(typeof(CharacterMovement3DComponent))]
    public class CharacterPawnComponent : PawnComponent
    {
        private CharacterMovement3DComponent Movement => GetSiblingComponent<CharacterMovement3DComponent>(true)!;
        
        //private readonly GameTimer _respawnTimer = new();
        private Rotator _viewRotation = Rotator.GetZero(ERotationOrder.YPR);
        private float _gamePadMovementInputMultiplier = 200.0f;
        private float _keyboardMovementInputMultiplier = 200.0f;
        private float _keyboardLookXInputMultiplier = 200.0f;
        private float _keyboardLookYInputMultiplier = 200.0f;
        private float _mouseXLookInputMultiplier = 10.0f;
        private float _mouseYLookInputMultiplier = 10.0f;
        private float _gamePadXLookInputMultiplier = 100.0f;
        private float _gamePadYLookInputMultiplier = 100.0f;

        protected Vector2 _keyboardMovementInput = Vector2.Zero;
        protected Vector2 _gamepadMovementInput = Vector2.Zero;
        protected Vector2 _keyboardLookInput = Vector2.Zero;

        public float KeyboardMovementInputMultiplier
        {
            get => _keyboardMovementInputMultiplier;
            set => SetField(ref _keyboardMovementInputMultiplier, value);
        }
        public float KeyboardLookXInputMultiplier
        {
            get => _keyboardLookXInputMultiplier;
            set => SetField(ref _keyboardLookXInputMultiplier, value);
        }
        public float KeyboardLookYInputMultiplier
        {
            get => _keyboardLookYInputMultiplier;
            set => SetField(ref _keyboardLookYInputMultiplier, value);
        }
        public float GamePadMovementInputMultiplier
        {
            get => _gamePadMovementInputMultiplier;
            set => SetField(ref _gamePadMovementInputMultiplier, value);
        }
        public float MouseXLookInputMultiplier
        {
            get => _mouseXLookInputMultiplier;
            set => SetField(ref _mouseXLookInputMultiplier, value);
        }
        public float MouseYLookInputMultiplier
        {
            get => _mouseYLookInputMultiplier;
            set => SetField(ref _mouseYLookInputMultiplier, value);
        }
        public float GamePadXLookInputMultiplier
        {
            get => _gamePadXLookInputMultiplier;
            set => SetField(ref _gamePadXLookInputMultiplier, value);
        }
        public float GamePadYLookInputMultiplier
        {
            get => _gamePadYLookInputMultiplier;
            set => SetField(ref _gamePadYLookInputMultiplier, value);
        }

        private TransformBase? _viewTransform;
        /// <summary>
        /// This is the transform that will be used to pull forward and right vectors from for orienting movement.
        /// </summary>
        public TransformBase? InputOrientationTransform
        {
            get => _viewTransform;
            set => SetField(ref _viewTransform, value);
        }

        private Transform? _rotationTransform;
        /// <summary>
        /// This is the transform that will be rotated by player inputs.
        /// </summary>
        public Transform? ViewRotationTransform
        {
            get => _rotationTransform;
            set => SetField(ref _rotationTransform, value);
        }

        private bool _ignoreViewPitchInputs = false;
        public bool IgnoreViewTransformPitch
        {
            get => _ignoreViewPitchInputs;
            set => SetField(ref _ignoreViewPitchInputs, value);
        }

        private bool _ignoreViewYawInputs = false;
        public bool IgnoreViewTransformYaw
        {
            get => _ignoreViewYawInputs;
            set => SetField(ref _ignoreViewYawInputs, value);
        }

        private bool _movementAffectedByTimeDilation = true;
        public bool MovementAffectedByTimeDilation
        {
            get => _movementAffectedByTimeDilation;
            set => SetField(ref _movementAffectedByTimeDilation, value);
        }

        private bool _viewRotationAffectedByTimeDilation = true;
        public bool ViewRotationAffectedByTimeDilation
        {
            get => _viewRotationAffectedByTimeDilation;
            set => SetField(ref _viewRotationAffectedByTimeDilation, value);
        }

        private bool _shouldHideCursor = true;
        public bool ShouldHideCursor
        {
            get => _shouldHideCursor;
            set => SetField(ref _shouldHideCursor, value);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Normal, ETickOrder.Logic, TickMovementInput);
        }

        protected virtual void TickMovementInput()
        {
            var cam = GetCamera();
            GetDirectionalVectorsFromView(
                InputOrientationTransform ?? cam?.Transform ?? Transform,
                out Vector3 forward,
                out Vector3 right);
            AddMovement(forward, right);
            UpdateViewRotation(cam);
        }

        private void AddMovement(Vector3 forward, Vector3 right)
        {
            bool keyboardMovement = _keyboardMovementInput.X != 0.0f || _keyboardMovementInput.Y != 0.0f;
            bool gamepadMovement = _gamepadMovementInput.X != 0.0f || _gamepadMovementInput.Y != 0.0f;
            if (!keyboardMovement && !gamepadMovement)
                return;

            float dt = MovementAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;

            if (keyboardMovement)
            {
                Vector3 input = forward * _keyboardMovementInput.Y + right * _keyboardMovementInput.X;
                Movement.AddMovementInput(dt * KeyboardMovementInputMultiplier * Vector3.Normalize(input));
            }

            if (gamepadMovement)
            {
                Vector3 input = forward * _gamepadMovementInput.Y + right * _gamepadMovementInput.X;
                Movement.AddMovementInput(dt * GamePadMovementInputMultiplier * Vector3.Normalize(input));
            }
        }

        private void UpdateViewRotation(CameraComponent? cam)
        {
            var rotTfm = ViewRotationTransform ?? cam?.SceneNode.GetTransformAs<Transform>(false);
            if (rotTfm is null)
                return;

            if (_keyboardLookInput != Vector2.Zero)
                KeyboardLook(_keyboardLookInput.X, _keyboardLookInput.Y);

            if (_ignoreViewPitchInputs)
                _viewRotation.Pitch = 0.0f;

            if (_ignoreViewYawInputs)
                _viewRotation.Yaw = 0.0f;

            if (XRMath.Approx(_viewRotation.Pitch, _lastPitch) &&
                XRMath.Approx(_viewRotation.Yaw, _lastYaw))
                return;

            _lastPitch = _viewRotation.Pitch;
            _lastYaw = _viewRotation.Yaw;

            rotTfm.Rotator = _viewRotation;
        }

        private float _lastYaw = 0.0f;
        private float _lastPitch = 0.0f;

        private static void GetDirectionalVectorsFromView(TransformBase viewTransform, out Vector3 forward, out Vector3 right)
        {
            forward = viewTransform.WorldForward;
            float dot = forward.Dot(Globals.Up);
            if (Math.Abs(dot) >= 0.5f)
            {
                //if dot is 1, looking straight up. need to use camera down for forward
                //if dot is -1, looking straight down. need to use camera up for forward
                forward = dot > 0.0f
                    ? -viewTransform.WorldUp
                    : viewTransform.WorldUp;
            }
            forward.Y = 0.0f;
            forward = Vector3.Normalize(forward);
            right = viewTransform.WorldRight;
        }

        public override void RegisterInput(InputInterface input)
        {
            if (ShouldHideCursor)
                input.HideCursor = !input.Unregister;
            else if (input.HideCursor)
                input.HideCursor = false;

            input.RegisterMouseMove(MouseLook, EMouseMoveType.Relative);

            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, MoveRight, true);
            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, MoveForward, true);

            input.RegisterAxisUpdate(EGamePadAxis.RightThumbstickX, LookRight, true);
            input.RegisterAxisUpdate(EGamePadAxis.RightThumbstickY, LookUp, true);

            input.RegisterButtonPressed(EGamePadButton.FaceDown, Jump);

            input.RegisterKeyStateChange(EKey.W, MoveForward);
            input.RegisterKeyStateChange(EKey.A, MoveLeft);
            input.RegisterKeyStateChange(EKey.S, MoveBackward);
            input.RegisterKeyStateChange(EKey.D, MoveRight);

            input.RegisterKeyStateChange(EKey.Right, LookRight);
            input.RegisterKeyStateChange(EKey.Left, LookLeft);
            input.RegisterKeyStateChange(EKey.Up, LookUp);
            input.RegisterKeyStateChange(EKey.Down, LookDown);

            input.RegisterKeyStateChange(EKey.Space, Jump);
            input.RegisterKeyEvent(EKey.C, EButtonInputType.Pressed, ToggleCrouch);
            input.RegisterKeyEvent(EKey.Z, EButtonInputType.Pressed, ToggleProne);

            input.RegisterKeyEvent(EKey.Escape, EButtonInputType.Pressed, Quit);
            input.RegisterKeyEvent(EKey.Backspace, EButtonInputType.Pressed, ToggleMouseCapture);
        }

        public void ToggleMouseCapture()
        {
            if (LocalInput is null)
                return;

            LocalInput.HideCursor = !LocalInput.HideCursor;
        }

        protected virtual void Quit()
            => Engine.ShutDown();

        public void Jump(bool pressed)
            => Movement.Jump(pressed);

        public void ToggleCrouch()
            => Movement.CrouchState = Movement.CrouchState == CharacterMovement3DComponent.ECrouchState.Crouched
                ? CharacterMovement3DComponent.ECrouchState.Standing
                : CharacterMovement3DComponent.ECrouchState.Crouched;

        public void ToggleProne()
            => Movement.CrouchState = Movement.CrouchState == CharacterMovement3DComponent.ECrouchState.Prone
                ? CharacterMovement3DComponent.ECrouchState.Standing
                : CharacterMovement3DComponent.ECrouchState.Prone;

        public void LookLeft(bool pressed)
            => _keyboardLookInput.X += pressed ? -1.0f : 1.0f;
        public void LookRight(bool pressed)
            => _keyboardLookInput.X += pressed ? 1.0f : -1.0f;
        public void LookDown(bool pressed)
            => _keyboardLookInput.Y += pressed ? -1.0f : 1.0f;
        public void LookUp(bool pressed)
            => _keyboardLookInput.Y += pressed ? 1.0f : -1.0f;

        public void MoveForward(bool pressed)
            => _keyboardMovementInput.Y += pressed ? 1.0f : -1.0f;
        public void MoveLeft(bool pressed)
            => _keyboardMovementInput.X += pressed ? -1.0f : 1.0f;
        public void MoveRight(bool pressed)
            => _keyboardMovementInput.X += pressed ? 1.0f : -1.0f;
        public void MoveBackward(bool pressed)
            => _keyboardMovementInput.Y += pressed ? -1.0f : 1.0f;

        public void MoveRight(float value)
            => _gamepadMovementInput.X = value;
        public void MoveForward(float value)
            => _gamepadMovementInput.Y = value;

        public void MouseLook(float dx, float dy)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Pitch += dt * dy * MouseYLookInputMultiplier;
            _viewRotation.Yaw -= dt * dx * MouseXLookInputMultiplier;
            ClampPitch();
            RemapYaw();
        }
        public void KeyboardLook(float dx, float dy)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Pitch += dt * dy * KeyboardLookYInputMultiplier;
            _viewRotation.Yaw -= dt * dx * KeyboardLookXInputMultiplier;
            ClampPitch();
            RemapYaw();
        }

        public void LookRight(float dx)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Yaw -= dt * dx * GamePadXLookInputMultiplier;
            RemapYaw();
        }
        public void LookUp(float dy)
        {
            float dt = ViewRotationAffectedByTimeDilation ? Engine.Delta : Engine.UndilatedDelta;
            _viewRotation.Pitch += dt * dy * GamePadYLookInputMultiplier;
            ClampPitch();
        }

        private void ClampPitch()
        {
            if (_viewRotation.Pitch > 89.0f)
                _viewRotation.Pitch = 89.0f;
            else if (_viewRotation.Pitch < -89.0f)
                _viewRotation.Pitch = -89.0f;
        }
        private void RemapYaw()
        {
            //don't let the yaw or pitch exceed 180 or -180
            if (_viewRotation.Yaw > 180.0f)
                _viewRotation.Yaw -= 360.0f;
            else if (_viewRotation.Yaw < -180.0f)
                _viewRotation.Yaw += 360.0f;
        }

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

        //    _respawnTimer.Stop();

        //    if (IsSpawned)
        //        Engine.World.DespawnActor(this);

        //    RootComponent.WorldMatrix.Value = transform;
        //    World.SpawnActor(this);
        //}
    }
}
