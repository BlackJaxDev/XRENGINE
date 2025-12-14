using Extensions;
using MagicPhysX;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Movement
{
    [OneComponentAllowed]
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Gameplay")]
    [DisplayName("Character Movement")]
    [Description("Full-featured first/third-person character controller with jumping, crouching, and slope handling.")]
    public class CharacterMovement3DComponent : PlayerMovementComponentBase
    {
        //public RigidBodyTransform RigidBodyTransform
        //    => SceneNode.GetTransformAs<RigidBodyTransform>(true)!;
        //public Transform ControllerTransform
        //    => SceneNode.GetTransformAs<Transform>(true)!;

        private float _stepOffset = 0.0f;
        private float _slopeLimitCosine = 0.707f;
        private float _walkingMovementSpeed = 50f;
        private float _airMovementAcceleration = 10f;
        private float _maxJumpHeight = 10.0f;
        private Func<Vector3, Vector3>? _subUpdateTick;
        private ECrouchState _crouchState = ECrouchState.Standing;
        private EMovementMode _movementMode = EMovementMode.Falling;
        private float _invisibleWallHeight = 0.0f;
        private float _density = 0.5f;
        private float _scaleCoeff = 0.8f;
        private float _volumeGrowth = 1.5f;
        private bool _slideOnSteepSlopes = true;
        private PhysxMaterial _material = new(0.9f, 0.9f, 0.1f);
        private float _radius = 0.6f;
        private float _standingHeight = new FeetInches(5, 2.0f).ToMeters();
        private float _crouchedHeight = new FeetInches(3, 0.0f).ToMeters();
        private float _proneHeight = new FeetInches(1, 0.0f).ToMeters();
        private bool _constrainedClimbing = false;
        private CapsuleController? _controller;

        private AbstractPhysicsScene? _subscribedPhysicsScene;
        private PhysxControllerActorProxy? _controllerActorProxy;
        private float _minMoveDistance = 0.00001f;
        private float _contactOffset = 0.001f;
        private Vector3 _upDirection = Globals.Up;
        private Vector3 _spawnPosition = Vector3.Zero;
        private Vector3 _velocity = Vector3.Zero;
        private Vector3? _gravityOverride = null;
        private bool _rotateGravityToMatchCharacterUp = false;

        private float _jumpForce = 15.0f;
        private float _jumpHoldForce = 5.0f;
        private float _jumpElapsed = 0.0f;
        private float _maxJumpDuration = 0.3f;
        private bool _isJumping = false;
        private bool _canJump = true;
        private float _coyoteTime = 0.2f;
        private float _coyoteTimer = 0.0f;

        /// <summary>
        /// The current movement mode of the character.
        /// </summary>
        public enum EMovementMode
        {
            Walking,
            Falling,
            Swimming,
            Flying,
        }

        /// <summary>
        /// Determines the capsule's height.
        /// </summary>
        public enum ECrouchState
        {
            Standing,
            Crouched,
            Prone
        }

        /// <summary>
        /// This time is the amount of time the character can still jump after leaving the ground, like a cartoon coyote running off a cliff.
        /// Makes jumping feel more responsive and forgiving.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Coyote Time")]
        [Description("Grace period after leaving ground where jump is still allowed.")]
        public float CoyoteTime
        {
            get => _coyoteTime;
            set => SetField(ref _coyoteTime, value);
        }

        /// <summary>
        /// How much force is applied when jumping.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Jump Force")]
        [Description("Initial upward force applied when jumping.")]
        public float JumpForce
        {
            get => _jumpForce;
            set => SetField(ref _jumpForce, value);
        }

        /// <summary>
        /// How much force is applied when sustaining a jump for longer than the initial jump force.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Jump Hold Force")]
        [Description("Additional force while jump button is held.")]
        public float JumpHoldForce
        {
            get => _jumpHoldForce;
            set => SetField(ref _jumpHoldForce, value);
        }

        /// <summary>
        /// How much acceleration is applied to the character when moving in the air.
        /// Air movement should be less responsive than ground movement,
        /// but still allow for some control over the character's movement in the air.
        /// </summary>
        [Category("Movement")]
        [DisplayName("Air Acceleration")]
        [Description("Movement acceleration while airborne.")]
        public float AirMovementAcceleration
        {
            get => _airMovementAcceleration;
            set => SetField(ref _airMovementAcceleration, value);
        }

        /// <summary>
        /// Half the current height of the character controller, depending on standing, crouch or prone state.
        /// Also includes the radius and contact offset.
        /// </summary>
        public float HalfHeight => CurrentHeight * 0.5f + Radius + ContactOffset;

        /// <summary>
        /// The position of the character's feet.
        /// This exists because the center of the capsule is off the ground.
        /// </summary>
        public Vector3 FootPosition
        {
            get => Controller?.FootPosition ?? (Position - UpDirection * HalfHeight);
            set
            {
                Controller?.FootPosition = value;
            }
        }

        /// <summary>
        /// The position of the character controller.
        /// This is the center of the capsule.
        /// </summary>
        public Vector3 Position
        {
            get => Controller?.Position ?? Transform.WorldTranslation;
            set
            {
                Controller?.Position = value;
            }
        }

        /// <summary>
        /// The direction the character stands up in.
        /// </summary>
        [Category("Orientation")]
        [DisplayName("Up Direction")]
        [Description("The up vector for the character.")]
        public Vector3 UpDirection
        { 
            get => _upDirection;
            set => SetField(ref _upDirection, value);
        }

        /// <summary>
        /// If true, gravity will be rotated to match the character's <see cref="UpDirection"/>.
        /// This is useful if you want gravity to always be relative to the character.
        /// </summary>
        [Category("Orientation")]
        [DisplayName("Rotate Gravity To Up")]
        [Description("Apply gravity relative to character up.")]
        public bool RotateGravityToMatchCharacterUp
        {
            get => _rotateGravityToMatchCharacterUp;
            set => SetField(ref _rotateGravityToMatchCharacterUp, value);
        }

        /// <summary>
        /// This allows for overriding the gravity applied to just this character controller.
        /// </summary>
        [Category("Forces")]
        [DisplayName("Gravity Override")]
        [Description("Custom gravity vector (null uses scene gravity).")]
        public Vector3? GravityOverride
        {
            get => _gravityOverride;
            set => SetField(ref _gravityOverride, value);
        }

        /// <summary>
        /// How high the character can step up onto a ledge.
        /// </summary>
        [Category("Ground")]
        [DisplayName("Step Offset")]
        [Description("Maximum height character can step up automatically.")]
        public float StepOffset
        {
            get => _stepOffset;
            set => SetField(ref _stepOffset, value);
        }

        /// <summary>
        /// The maximum slope which the character can walk up, expressed as the cosine of desired limit angle.
        /// In general it is desirable to limit where the character can walk, in particular it is unrealistic for the character to be able to climb arbitary slopes.
        /// A value of 0 disables this feature.
        /// </summary>
        [Category("Ground")]
        [DisplayName("Slope Limit (Cosine)")]
        [Description("Cosine of max walkable slope angle.")]
        public float SlopeLimitCosine
        {
            get => _slopeLimitCosine;
            set => SetField(ref _slopeLimitCosine, value);
        }

        /// <summary>
        /// The maximum slope which the character can walk up, expressed in radians.
        /// In general it is desirable to limit where the character can walk, in particular it is unrealistic for the character to be able to climb arbitary slopes.
        /// A value of 0 disables this feature.
        /// </summary>
        [Browsable(false)]
        public float SlopeLimitAngleRad
        {
            get => (float)Math.Acos(SlopeLimitCosine);
            set => SlopeLimitCosine = (float)Math.Cos(value);
        }

        /// <summary>
        /// The maximum slope which the character can walk up, expressed in degrees.
        /// In general it is desirable to limit where the character can walk, in particular it is unrealistic for the character to be able to climb arbitary slopes.
        /// A value of 0 disables this feature.
        /// </summary>
        [Category("Ground")]
        [DisplayName("Slope Limit (Degrees)")]
        [Description("Max walkable slope angle in degrees.")]
        public float SlopeLimitAngleDeg
        {
            get => XRMath.RadToDeg(SlopeLimitAngleRad);
            set => SlopeLimitAngleRad = XRMath.DegToRad(value);
        }

        /// <summary>
        /// The speed at which the character moves when walking.
        /// </summary>
        [Category("Movement")]
        [DisplayName("Walking Speed")]
        [Description("Base walking movement speed.")]
        public float WalkingMovementSpeed
        {
            get => _walkingMovementSpeed;
            set => SetField(ref _walkingMovementSpeed, value);
        }

        /// <summary>
        /// Maximum height a jumping character can reach.
        /// This is only used if invisible walls are created(‘invisibleWallHeight’ is non zero).
        /// When a character jumps, the non-walkable triangles he might fly over are not found by the collision queries
        /// (since the character’s bounding volume does not touch them).
        /// Thus those non-walkable triangles do not create invisible walls, and it is possible for a jumping character to land on a non-walkable triangle,
        /// while he wouldn’t have reached that place by just walking.
        /// The ‘maxJumpHeight’ variable is used to extend the size of the collision volume downward.
        /// This way, all the non-walkable triangles are properly found by the collision queries and it becomes impossible to ‘jump over’ invisible walls.
        /// If the character in your game can not jump, it is safe to use 0.0 here.
        /// Otherwise it is best to keep this value as small as possible, 
        /// since a larger collision volume means more triangles to process.
        /// </summary>
        public float MaxJumpHeight
        {
            get => _maxJumpHeight;
            set => SetField(ref _maxJumpHeight, value);
        }

        /// <summary>
        /// The contact offset used by the controller.
        /// Specifies a skin around the object within which contacts will be generated.
        /// Use it to avoid numerical precision issues.
        /// This is dependant on the scale of the users world, but should be a small, positive non zero value.
        /// </summary>
        [Category("Collision")]
        [DisplayName("Contact Offset")]
        [Description("Contact skin width for numerical precision.")]
        public float ContactOffset
        {
            get => _contactOffset;
            set => SetField(ref _contactOffset, value);
        }

        /// <summary>
        /// The crouch state of the character.
        /// </summary>
        [Category("Crouch")]
        [DisplayName("Crouch State")]
        [Description("Current crouch posture.")]
        public ECrouchState CrouchState
        {
            get => _crouchState;
            set => SetField(ref _crouchState, value);
        }

        /// <summary>
        /// The current movement mode of the character.
        /// </summary>
        [Category("Movement")]
        [DisplayName("Movement Mode")]
        [Description("Current movement context (walking, falling, etc.).")]
        [Browsable(false)]
        public EMovementMode MovementMode
        {
            get => _movementMode;
            private set => SetField(ref _movementMode, value);
        }

        /// <summary>
        /// Height of invisible walls created around non-walkable triangles.
        /// The library can automatically create invisible walls around non-walkable triangles defined by the 'slopeLimit' parameter.
        /// This defines the height of those walls.
        /// If it is 0.0, then no extra triangles are created.
        /// </summary>
        [Category("Ground")]
        [DisplayName("Invisible Wall Height")]
        [Description("Height of walls around non-walkable surfaces.")]
        public float InvisibleWallHeight
        {
            get => _invisibleWallHeight;
            set => SetField(ref _invisibleWallHeight, value);
        }

        /// <summary>
        /// Scale coefficient for underlying kinematic actor.
        /// The CCT creates a PhysX’s kinematic actor under the hood.
        /// This controls its scale factor.
        /// This should be a number a bit smaller than 1.0.
        /// This scale factor affects how the character interacts with dynamic rigid bodies around it (e.g.pushing them, etc).
        /// With a scale factor < 1, the underlying kinematic actor will not touch surrounding rigid bodies - they will only interact with the character controller’s shapes (capsules or boxes),
        /// and users will have full control over the interactions(i.e.they will have to push the objects with explicit forces themselves).
        /// With a scale factor >=1, the underlying kinematic actor will touch and push surrounding rigid bodies based on PhysX’s computations, 
        /// as if there would be no character controller involved.This works fine except when you push objects into a wall.
        /// PhysX has no control over kinematic actors(since they are kinematic) so they would freely push dynamic objects into walls, and make them tunnel / explode / behave badly.
        /// With a smaller kinematic actor however, the character controller’s swept shape touches dynamic rigid bodies first, 
        /// and can apply forces to them to move them away (or not, depending on what the gameplay needs).
        /// Meanwhile the character controller’s swept shape itself is stopped by these dynamic bodies.
        /// Setting the scale factor to 1 could still work, but it is unreliable.
        /// Depending on FPU accuracy you could end up with either the CCT’s volume or the underlying kinematic actor touching the dynamic bodies first, and this could change from one moment to the next.
        /// </summary>
        public float ScaleCoeff
        {
            get => _scaleCoeff;
            set => SetField(ref _scaleCoeff, value);
        }

        /// <summary>
        /// Cached volume growth.
        /// Amount of space around the controller we cache to improve performance.
        /// This is a scale factor that should be higher than 1.0f but not too big, ideally lower than 2.0f.
        /// </summary>
        [Category("Capsule")]
        [DisplayName("Volume Growth")]
        [Description("Cached volume scale for performance.")]
        public float VolumeGrowth
        {
            get => _volumeGrowth;
            set => SetField(ref _volumeGrowth, value);
        }

        /// <summary>
        /// The non-walkable mode controls if a character controller slides or not on a non-walkable part.
        /// This is only used when slopeLimit is non zero.
        /// </summary>
        [Category("Ground")]
        [DisplayName("Slide On Steep Slopes")]
        [Description("Whether to slide on non-walkable surfaces.")]
        public bool SlideOnSteepSlopes
        {
            get => _slideOnSteepSlopes;
            set => SetField(ref _slideOnSteepSlopes, value);
        }

        [Category("Capsule")]
        [DisplayName("Radius")]
        [Description("Capsule collision radius.")]
        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        /// <summary>
        /// How tall the character is when standing.
        /// </summary>
        [Category("Capsule")]
        [DisplayName("Standing Height")]
        [Description("Character height when standing.")]
        public float StandingHeight
        {
            get => _standingHeight;
            set => SetField(ref _standingHeight, value);
        }

        /// <summary>
        /// How tall the character is when prone.
        /// </summary>
        [Category("Crouch")]
        [DisplayName("Prone Height")]
        [Description("Character height when prone.")]
        public float ProneHeight
        {
            get => _proneHeight;
            set => SetField(ref _proneHeight, value);
        }

        /// <summary>
        /// How tall the character is when crouched.
        /// </summary>
        [Category("Crouch")]
        [DisplayName("Crouched Height")]
        [Description("Character height when crouched.")]
        public float CrouchedHeight
        {
            get => _crouchedHeight;
            set => SetField(ref _crouchedHeight, value);
        }

        /// <summary>
        /// The current height of the character controller, depending on the crouch state.
        /// </summary>
        public float CurrentHeight => Controller?.Height ?? GetCurrentHeight();

        private float GetCurrentHeight()
            => CrouchState switch
            {
                ECrouchState.Standing => StandingHeight,
                ECrouchState.Crouched => CrouchedHeight,
                ECrouchState.Prone => ProneHeight,
                _ => 0.0f,
            };

        [Category("Ground")]
        [DisplayName("Constrained Climbing")]
        [Description("Use constrained climbing mode.")]
        public bool ConstrainedClimbing
        {
            get => _constrainedClimbing;
            set => SetField(ref _constrainedClimbing, value);
        }

        /// <summary>
        /// The minimum travelled distance to consider.
        /// If travelled distance is smaller, the character doesn't move.
        /// This is used to stop the recursive motion algorithm when remaining distance to travel is small.
        /// </summary>
        [Category("Movement")]
        [DisplayName("Min Move Distance")]
        [Description("Minimum movement distance threshold.")]
        public float MinMoveDistance
        {
            get => _minMoveDistance;
            set => SetField(ref _minMoveDistance, value);
        }

        [Browsable(false)]
        public CapsuleController? Controller
        {
            get => _controller;
            private set => SetField(ref _controller, value);
        }

        public PhysxDynamicRigidBody? RigidBodyReference => Controller?.Actor;
        
        public void GetState(
            out Vector3 deltaXP,
            out PhysxShape? touchedShape,
            out PhysxRigidActor? touchedActor,
            out uint touchedObstacleHandle,
            out PxControllerCollisionFlags collisionFlags,
            out bool standOnAnotherCCT,
            out bool standOnObstacle,
            out bool isMovingUp)
        {
            if (Controller is null)
            {
                deltaXP = Vector3.Zero;
                touchedShape = null;
                touchedActor = null;
                touchedObstacleHandle = 0;
                collisionFlags = 0;
                standOnAnotherCCT = false;
                standOnObstacle = false;
                isMovingUp = false;
                return;
            }

            var state = Controller.State;
            deltaXP = state.deltaXP;
            touchedShape = state.touchedShape;
            touchedActor = state.touchedActor;
            touchedObstacleHandle = state.touchedObstacleHandle;
            collisionFlags = state.collisionFlags;
            standOnAnotherCCT = state.standOnAnotherCCT;
            standOnObstacle = state.standOnObstacle;
            isMovingUp = state.isMovingUp;
            return;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(MovementMode):
                    _subUpdateTick = MovementMode switch
                    {
                        EMovementMode.Walking => GroundMovementTick,
                        _ => AirMovementTick,
                    };
                    break;
                case nameof(StandingHeight):
                    if (CrouchState == ECrouchState.Standing)
                        Controller?.Resize(StandingHeight);
                    break;
                case nameof(CrouchedHeight):
                    if (CrouchState == ECrouchState.Crouched)
                        Controller?.Resize(CrouchedHeight);
                    break;
                case nameof(ProneHeight):
                    if (CrouchState == ECrouchState.Prone)
                        Controller?.Resize(ProneHeight);
                    break;
                case nameof(CrouchState):
                    Controller?.Resize(GetCurrentHeight());
                    break;
                case nameof(Radius):
                    Controller?.Radius = Radius;
                    break;
                case nameof(SlopeLimitCosine):
                    Controller?.SlopeLimit = SlopeLimitCosine;
                    break;
                case nameof(StepOffset):
                    Controller?.StepOffset = StepOffset;
                    break;
                case nameof(ContactOffset):
                    Controller?.ContactOffset = ContactOffset;
                    break;
                case nameof(UpDirection):
                    Controller?.UpDirection = UpDirection;
                    break;
                case nameof(SlideOnSteepSlopes):
                    Controller?.ClimbingMode = ConstrainedClimbing 
                        ? PxCapsuleClimbingMode.Constrained
                        : PxCapsuleClimbingMode.Easy;
                    break;
            }
        }

        protected internal unsafe override void OnComponentActivated()
        {
            // Character movement uses a PhysX CCT (controller) and wraps its hidden rigid actor.
            // Prevent the DynamicRigidBodyComponent base from auto-creating/registering a separate rigid body.
            AutoCreateRigidBody = false;

            base.OnComponentActivated();

            _subUpdateTick = GroundMovementTick;
            RegisterTick(TickInputWithPhysics ? ETickGroup.PrePhysics : ETickGroup.Late, (int)ETickOrder.Animation, MainUpdateTick);
            
            var scene = World?.PhysicsScene as PhysxScene;
            var manager = scene?.GetOrCreateControllerManager();
            if (manager is null)
                return;

            // Keep our transform driven by the controller after each physics step.
            // Without wrapping the controller actor as a rigid body, nothing else updates the RigidBodyTransform.
            if (World?.PhysicsScene is { } physicsScene && _subscribedPhysicsScene != physicsScene)
            {
                _subscribedPhysicsScene?.OnSimulationStep -= OnPhysicsSimulationStep;
                physicsScene.OnSimulationStep += OnPhysicsSimulationStep;
                _subscribedPhysicsScene = physicsScene;
            }

            PhysxMaterial? material = ResolvePhysxMaterial();
            if (material is null)
                return;

            Vector3 pos = InitialPosition ?? Transform.WorldTranslation;
            Vector3 up = Globals.Up;
            Controller = manager.CreateCapsuleController(
                pos,
                up,
                SlopeLimitCosine,
                InvisibleWallHeight,
                MaxJumpHeight,
                ContactOffset,
                StepOffset,
                Density,
                ScaleCoeff,
                VolumeGrowth,
                SlideOnSteepSlopes 
                    ? PxControllerNonWalkableMode.PreventClimbingAndForceSliding
                    : PxControllerNonWalkableMode.PreventClimbing,
                material,
                0,
                null,
                Radius,
                StandingHeight,
                ConstrainedClimbing 
                    ? PxCapsuleClimbingMode.Constrained
                    : PxCapsuleClimbingMode.Easy);

            // Link the transform to a read-only proxy of the controller.
            // This allows the engine's normal physics->transform pipeline to run (RigidBodyTransform.OnPhysicsStepped)
            // without mutating the controller actor (which previously caused PxController::move crashes).
            // NOTE: We pass the controller pointer, not the actor, because CCT doesn't update actor pose after MoveMut.
            unsafe
            {
                _controllerActorProxy = new PhysxControllerActorProxy(Controller.ControllerPtr);
            }

            // CCT doesn't have rotation like regular physics actors, so clear the default -90° Z offset
            // that RigidBodyTransform applies for normal PhysX actors.
            RigidBodyTransform.PostRotationOffset = Quaternion.Identity;
            RigidBodyTransform.RigidBody = _controllerActorProxy;
            RigidBodyTransform.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;
            _controllerActorProxy?.RefreshFromNative();
            RigidBodyTransform.OnPhysicsStepped();
        }

        protected internal override void OnComponentDeactivated()
        {
            _subUpdateTick = null;
            _subscribedPhysicsScene?.OnSimulationStep -= OnPhysicsSimulationStep;
            _subscribedPhysicsScene = null;

            // Controller owns its internal actor via PxControllerManager; do not remove it as a normal actor.
            RigidBodyTransform.RigidBody = null;
            _controllerActorProxy = null;

            Controller?.RequestRelease();
            Controller = null;
        }

        private void OnPhysicsSimulationStep()
        {
            // Runs from the physics fixed-step.
            if (Controller is null)
                return;

            // IMPORTANT: refresh cached state only after FetchResults.
            _controllerActorProxy?.RefreshFromNative();

            // Drive the transform update via the engine's standard rigid-body sync path.
            RigidBodyTransform.OnPhysicsStepped();
        }

        private int _inputLogCount = 0;
        private unsafe void MainUpdateTick()
        {
            if (Controller is null)
                return;

            // If no rigid body is bound (expected for CCT), keep our last computed Velocity.
            Velocity = RigidBodyTransform.RigidBody?.LinearVelocity ?? Velocity;
            Acceleration = (Velocity - LastVelocity) / DeltaTime;

            RenderCapsule();

            var rawInput = ConsumeInput();
            var tickResult = _subUpdateTick?.Invoke(rawInput) ?? Vector3.Zero;
            var literalInput = ConsumeLiteralInput();
            var moveDelta = tickResult + literalInput;

            // Log first 30 input processing cycles
            if (_inputLogCount < 30 && (rawInput != Vector3.Zero || literalInput != Vector3.Zero))
            {
                _inputLogCount++;
                Debug.Log(ELogCategory.Physics,
                    "[MainTick] #{0} rawInput=({1:F4},{2:F4},{3:F4}) tickResult=({4:F4},{5:F4},{6:F4}) moveDelta=({7:F4},{8:F4},{9:F4}) dt={10:F4} tick={11}",
                    _inputLogCount,
                    rawInput.X, rawInput.Y, rawInput.Z,
                    tickResult.X, tickResult.Y, tickResult.Z,
                    moveDelta.X, moveDelta.Y, moveDelta.Z,
                    DeltaTime,
                    _subUpdateTick == GroundMovementTick ? "Ground" : "Air");
            }

            if (moveDelta.LengthSquared() > MinMoveDistance * MinMoveDistance)
                Controller.Move(moveDelta, MinMoveDistance, DeltaTime);

            if (Controller.CollidingDown)
            {
                if (_subUpdateTick == AirMovementTick)
                    _subUpdateTick = GroundMovementTick;
            }
            else
            {
                if (_subUpdateTick == GroundMovementTick)
                    _subUpdateTick = AirMovementTick;
            }
            //(Vector3 deltaXP, PhysxShape? touchedShape, PhysxRigidActor? touchedActor, uint touchedObstacleHandle, PxControllerCollisionFlags collisionFlags, bool standOnAnotherCCT, bool standOnObstacle, bool isMovingUp) state = Controller.State;
            //Debug.Out($"DeltaXP: {state.deltaXP}, TouchedShape: {state.touchedShape}, TouchedActor: {state.touchedActor}, TouchedObstacleHandle: {state.touchedObstacleHandle}, CollisionFlags: {state.collisionFlags}, StandOnAnotherCCT: {state.standOnAnotherCCT}, StandOnObstacle: {state.standOnObstacle}, IsMovingUp: {state.isMovingUp}");
            LastVelocity = Velocity;
        }

        private unsafe void RenderCapsule()
        {
            Vector3 pos = Position;
            Vector3 up = UpDirection;
            float halfHeight = CurrentHeight * 0.5f;
            float radius = Radius;

            Engine.Rendering.Debug.RenderCapsule(pos - up * halfHeight, pos + up * halfHeight, radius, false, ColorF4.DarkLavender);
        }

        private Vector3 _acceleration;
        [Browsable(false)]
        public Vector3 Acceleration
        {
            get => _acceleration;
            private set => SetField(ref _acceleration, value);
        }

        private Vector3 _lastVelocity;
        [Browsable(false)]
        public Vector3 LastVelocity
        {
            get => _lastVelocity;
            private set => SetField(ref _lastVelocity, value);
        }

        [Category("Movement")]
        [DisplayName("Velocity")]
        [Description("Current movement velocity.")]
        public Vector3 Velocity
        {
            get => _velocity;
            set => SetField(ref _velocity, value);
        }

        // TODO: calculate friction based on this character's material and the current surface
        private float _walkingFriction = 0.1f;
        [Category("Movement")]
        [DisplayName("Ground Friction")]
        [Description("Friction applied when grounded.")]
        public float GroundFriction
        {
            get => _walkingFriction;
            set => SetField(ref _walkingFriction, value.Clamp(0.0f, 1.0f));
        }

        public void AddForce(Vector3 force)
        {
            //Calculate acceleration from force
            float mass = RigidBodyReference?.Mass ?? 0.0f;
            if (mass > 0.0f)
                Velocity += force / mass;
        }

        public bool IsJumping => _isJumping;

        private float _maxSpeed = 20.0f;
        [Category("Movement")]
        [DisplayName("Max Speed")]
        [Description("Maximum movement speed.")]
        public float MaxSpeed
        {
            get => _maxSpeed;
            set => SetField(ref _maxSpeed, value);
        }

        /// <summary>
        /// How long jumping can be sustained.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Max Jump Duration")]
        [Description("Maximum time jump button can be held.")]
        public float MaxJumpDuration
        {
            get => _maxJumpDuration;
            set => SetField(ref _maxJumpDuration, value);
        }

        private bool _tickInputWithPhysics = false; //Seems more responsive calculating on update, separate from physics
        /// <summary>
        /// Whether to tick input with physics or not.
        /// </summary>
        [Category("Movement")]
        [DisplayName("Tick Input With Physics")]
        [Description("Whether to process input in physics tick.")]
        public bool TickInputWithPhysics
        {
            get => _tickInputWithPhysics;
            set => SetField(ref _tickInputWithPhysics, value);
        }

        private float DeltaTime => TickInputWithPhysics ? Engine.FixedDelta : Engine.Delta;

        protected override float InputDeltaTime => DeltaTime;

        protected virtual Vector3 GroundMovementTick(Vector3 posDelta)
        {
            if (Controller is null)
                return Vector3.Zero;

            float dt = DeltaTime;

            Vector3 moveDirection = Vector3.Zero;
            if (posDelta != Vector3.Zero)
            {
                // Get ground normal and align movement
                Vector3 groundNormal = Globals.Up;
                Vector3 up = Globals.Up;

                // Project movement onto ground plane
                Quaternion rotation = XRMath.RotationBetweenVectors(up, groundNormal);
                moveDirection = Vector3.Transform(posDelta.Normalized(), rotation);
            }

            // Calculate target velocity
            Vector3 targetVelocity = moveDirection * WalkingMovementSpeed;
            Vector3 velocityDelta = targetVelocity - Velocity;
            Vector3 newVelocity = Velocity + velocityDelta;
            float friction = Controller.CollidingDown ? GroundFriction : 0.0f;
            newVelocity *= (1.0f - friction);

            //Handle ground jump
            HandleJumping(dt, ref newVelocity);

            //Clamp speed to max speed for deterministic movement
            ClampSpeed(ref newVelocity);

            // Convert to position delta
            Vector3 delta = newVelocity * dt;

            if (float.IsNaN(delta.X) || float.IsNaN(delta.Y) || float.IsNaN(delta.Z))
                delta = Vector3.Zero;

            return delta;
        }

        private void HandleJumping(float dt, ref Vector3 delta)
        {
            if (Controller is null)
                return;

            if (Controller.CollidingDown)
            {
                _canJump = true;
                _coyoteTimer = _coyoteTime;
            }
            else
            {
                _coyoteTimer -= dt;
                _canJump = _coyoteTimer > 0.0f;
            }

            if (!_isJumping)
                return;

            if (CanJump)
            {
                delta.Y = JumpForce * dt;
                _jumpElapsed = 0.0f;
                _canJump = false;
                _subUpdateTick = AirMovementTick;
            }
            else
            {
                bool addingJumpForce = _isJumping && _jumpElapsed < MaxJumpDuration && !Controller.CollidingUp;
                if (!addingJumpForce)
                    return;
                
                float jumpFactor = 1.0f - (_jumpElapsed / MaxJumpDuration);
                delta.Y += JumpHoldForce * jumpFactor * dt;
                _jumpElapsed += dt;
            }
        }

        public bool CanJump => (_canJump && _coyoteTimer > 0.0f) || (Controller?.CollidingDown ?? false);

        protected virtual unsafe Vector3 AirMovementTick(Vector3 posDelta)
        {
            if (Controller is null || World?.PhysicsScene is not PhysxScene scene)
                return Vector3.Zero;

            float dt = DeltaTime;

            //Air control uses normalized input direction with reduced influence
            Vector3 airControl = posDelta;
            if (posDelta != Vector3.Zero)
            {
                airControl = new Vector3(
                    posDelta.X,
                    0,
                    posDelta.Z
                ).Normalized() * AirMovementAcceleration;
            }

            //Apply air control to current velocity
            Vector3 newVelocity = Velocity + (airControl * dt);

            //Apply gravity to the new velocity
            ApplyGravity(scene, ref newVelocity);

            //Handle coyote jump or sustained jump hold
            HandleJumping(dt, ref newVelocity);

            //Clamp speed to max speed for deterministic movement
            ClampSpeed(ref newVelocity);

            //Convert to position delta
            Vector3 delta = newVelocity * dt;

            //Apply landing friction
            if (Controller.CollidingDown)
            {
                delta *= 1.0f - GroundFriction;
                _subUpdateTick = GroundMovementTick;
            }

            if (float.IsNaN(delta.X) || float.IsNaN(delta.Y) || float.IsNaN(delta.Z))
                delta = Vector3.Zero;

            return delta;
        }

        private Vector3 VelocityToPositionDelta(Vector3 velocity)
            => velocity * DeltaTime;
        private Vector3 AccelerationToVelocityDelta(Vector3 acceleration)
            => acceleration * DeltaTime;

        private Vector3 PositionDeltaToVelocity(Vector3 delta)
            => delta / DeltaTime;
        private Vector3 VelocityDeltaToAcceleration(Vector3 delta)
            => delta / DeltaTime;

        private void ClampSpeed(ref Vector3 velocity)
        {
            // Separate vertical and horizontal movement for clamping
            float verticalDelta = velocity.Y;
            velocity.Y = 0;

            if (velocity.Length() > MaxSpeed)
                velocity = velocity.Normalized() * MaxSpeed;
            
            // Restore vertical movement
            velocity.Y = verticalDelta;
        }

        private void ApplyGravity(PhysxScene scene, ref Vector3 delta)
        {
            Vector3 gravity = GravityOverride ?? scene.Gravity;
            if (RotateGravityToMatchCharacterUp && Controller is not null)
                gravity = Vector3.Transform(gravity, XRMath.RotationBetweenVectors(Globals.Up, Controller.UpDirection));
            delta += gravity * DeltaTime;
        }

        public void Jump(bool pressed)
        {
            if (pressed)
                _isJumping = true;
            else
            {
                _isJumping = false;
                _jumpElapsed = MaxJumpDuration; // Cut the jump short when button is released
            }
        }
    }
}
