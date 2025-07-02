using Extensions;
using MagicPhysX;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Movement
{
    [OneComponentAllowed]
    [RequiresTransform(typeof(RigidBodyTransform))]
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
        public float CoyoteTime
        {
            get => _coyoteTime;
            set => SetField(ref _coyoteTime, value);
        }

        /// <summary>
        /// How much force is applied when jumping.
        /// </summary>
        public float JumpForce
        {
            get => _jumpForce;
            set => SetField(ref _jumpForce, value);
        }

        /// <summary>
        /// How much force is applied when sustaining a jump for longer than the initial jump force.
        /// </summary>
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
                if (Controller is not null)
                    Controller.FootPosition = value;
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
                if (Controller is not null)
                    Controller.Position = value;
            }
        }

        /// <summary>
        /// The direction the character stands up in.
        /// </summary>
        public Vector3 UpDirection
        { 
            get => _upDirection;
            set => SetField(ref _upDirection, value);
        }

        /// <summary>
        /// If true, gravity will be rotated to match the character's <see cref="UpDirection"/>.
        /// This is useful if you want gravity to always be relative to the character.
        /// </summary>
        public bool RotateGravityToMatchCharacterUp
        {
            get => _rotateGravityToMatchCharacterUp;
            set => SetField(ref _rotateGravityToMatchCharacterUp, value);
        }

        /// <summary>
        /// This allows for overriding the gravity applied to just this character controller.
        /// </summary>
        public Vector3? GravityOverride
        {
            get => _gravityOverride;
            set => SetField(ref _gravityOverride, value);
        }

        /// <summary>
        /// How high the character can step up onto a ledge.
        /// </summary>
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
        public float SlopeLimitAngleDeg
        {
            get => XRMath.RadToDeg(SlopeLimitAngleRad);
            set => SlopeLimitAngleRad = XRMath.DegToRad(value);
        }

        /// <summary>
        /// The speed at which the character moves when walking.
        /// </summary>
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
        public float ContactOffset
        {
            get => _contactOffset;
            set => SetField(ref _contactOffset, value);
        }

        /// <summary>
        /// The crouch state of the character.
        /// </summary>
        public ECrouchState CrouchState
        {
            get => _crouchState;
            set => SetField(ref _crouchState, value);
        }

        /// <summary>
        /// The current movement mode of the character.
        /// </summary>
        public EMovementMode MovementMode
        {
            get => _movementMode;
            private set => SetField(ref _movementMode, value);
        }

        /// <summary>
        /// Height of invisible walls created around non-walkable triangles.
        /// The library can automatically create invisible walls around non-walkable triangles defined by the ‘slopeLimit’ parameter.
        /// This defines the height of those walls.
        /// If it is 0.0, then no extra triangles are created.
        /// </summary>
        public float InvisibleWallHeight
        {
            get => _invisibleWallHeight;
            set => SetField(ref _invisibleWallHeight, value);
        }

        /// <summary>
        /// Density of underlying kinematic actor.
        /// The CCT creates a PhysX’s kinematic actor under the hood.This controls its density.
        /// </summary>
        public float Density
        {
            get => _density;
            set => SetField(ref _density, value);
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
        public float VolumeGrowth
        {
            get => _volumeGrowth;
            set => SetField(ref _volumeGrowth, value);
        }

        /// <summary>
        /// The non-walkable mode controls if a character controller slides or not on a non-walkable part.
        /// This is only used when slopeLimit is non zero.
        /// </summary>
        public bool SlideOnSteepSlopes
        {
            get => _slideOnSteepSlopes;
            set => SetField(ref _slideOnSteepSlopes, value);
        }

        public PhysxMaterial Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        /// <summary>
        /// How tall the character is when standing.
        /// </summary>
        public float StandingHeight
        {
            get => _standingHeight;
            set => SetField(ref _standingHeight, value);
        }

        /// <summary>
        /// How tall the character is when prone.
        /// </summary>
        public float ProneHeight
        {
            get => _proneHeight;
            set => SetField(ref _proneHeight, value);
        }

        /// <summary>
        /// How tall the character is when crouched.
        /// </summary>
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

        public bool ConstrainedClimbing
        {
            get => _constrainedClimbing;
            set => SetField(ref _constrainedClimbing, value);
        }

        /// <summary>
        /// The minimum travelled distance to consider.
        /// If travelled distance is smaller, the character doesn’t move.
        /// This is used to stop the recursive motion algorithm when remaining distance to travel is small.
        /// </summary>
        public float MinMoveDistance
        {
            get => _minMoveDistance;
            set => SetField(ref _minMoveDistance, value);
        }

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
                    if (Controller is not null)
                        Controller.Radius = Radius;
                    break;
                case nameof(SlopeLimitCosine):
                    if (Controller is not null)
                        Controller.SlopeLimit = SlopeLimitCosine;
                    break;
                case nameof(StepOffset):
                    if (Controller is not null)
                        Controller.StepOffset = StepOffset;
                    break;
                case nameof(ContactOffset):
                    if (Controller is not null)
                        Controller.ContactOffset = ContactOffset;
                    break;
                case nameof(UpDirection):
                    if (Controller is not null)
                        Controller.UpDirection = UpDirection;
                    break;
                case nameof(SlideOnSteepSlopes):
                    if (Controller is not null)
                        Controller.ClimbingMode = ConstrainedClimbing 
                            ? PxCapsuleClimbingMode.Constrained
                            : PxCapsuleClimbingMode.Easy;
                    break;
            }
        }

        public Vector3 SpawnPosition
        {
            get => _spawnPosition;
            set => SetField(ref _spawnPosition, value);
        }

        protected internal unsafe override void OnComponentActivated()
        {
            _subUpdateTick = GroundMovementTick;
            RegisterTick(TickInputWithPhysics ? ETickGroup.PrePhysics : ETickGroup.Late, (int)ETickOrder.Animation, MainUpdateTick);
            
            var scene = World?.PhysicsScene as PhysxScene;
            var manager = scene?.CreateOrCreateControllerManager();
            if (manager is null)
                return;

            Vector3 pos = SpawnPosition;
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
                Material,
                0,
                null,
                Radius,
                StandingHeight,
                ConstrainedClimbing 
                    ? PxCapsuleClimbingMode.Constrained
                    : PxCapsuleClimbingMode.Easy);

            //Wrap the hidden actor and apply to the transform
            //The constructor automatically caches the actor
            //We have to construct the rigid body with the hidden reference manually
            var rb = new PhysxDynamicRigidBody(Controller.ControllerPtr->GetActor());
            //var rb = RigidBodyReference;

            if (rb is not null)
            {
                rb.OwningComponent = this;
                RigidBody = rb;
                //rb.Flags |= PxRigidBodyFlags.EnableCcd | PxRigidBodyFlags.EnableSpeculativeCcd | PxRigidBodyFlags.EnableCcdFriction;

                var tfm = RigidBodyTransform;
                tfm.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;
                tfm.RigidBody = rb;
            }
            else
            {
                Debug.LogWarning("Failed to create character controller.");
            }
        }

        protected internal override void OnComponentDeactivated()
        {
            _subUpdateTick = null;

            if (World is not null && RigidBody is not null)
                World.PhysicsScene.RemoveActor(RigidBody);
            RigidBodyTransform.RigidBody = null;

            Controller?.Release();
            Controller = null;
        }

        private unsafe void MainUpdateTick()
        {
            if (Controller is null)
                return;

            var scene = World?.PhysicsScene as PhysxScene;
            var manager = scene?.CreateOrCreateControllerManager();
            if (manager is null)
                return;

            Velocity = RigidBodyTransform.RigidBody?.LinearVelocity ?? Vector3.Zero;
            Acceleration = (Velocity - LastVelocity) / DeltaTime;

            RenderCapsule();

            var moveDelta = (_subUpdateTick?.Invoke(ConsumeInput()) ?? Vector3.Zero) + ConsumeLiteralInput();
            if (moveDelta.LengthSquared() > MinMoveDistance * MinMoveDistance)
                Controller.Move(moveDelta, MinMoveDistance, DeltaTime, manager.ControllerFilters, null);

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
        public Vector3 Acceleration
        {
            get => _acceleration;
            private set => SetField(ref _acceleration, value);
        }

        private Vector3 _lastVelocity;
        public Vector3 LastVelocity
        {
            get => _lastVelocity;
            private set => SetField(ref _lastVelocity, value);
        }

        public Vector3 Velocity
        {
            get => _velocity;
            set => SetField(ref _velocity, value);
        }

        public float Friction
        {
            get => Material.DynamicFriction;
            set => Material.DynamicFriction = value;
        }

        //public float GroundFriction
        //{
        //    get => Material.StaticFriction;
        //    set => Material.StaticFriction = value;
        //}

        // TODO: calculate friction based on this character's material and the current surface
        private float _walkingFriction = 0.1f;
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
        public float MaxSpeed
        {
            get => _maxSpeed;
            set => SetField(ref _maxSpeed, value);
        }

        /// <summary>
        /// How long jumping can be sustained.
        /// </summary>
        public float MaxJumpDuration
        {
            get => _maxJumpDuration;
            set => SetField(ref _maxJumpDuration, value);
        }

        private bool _tickInputWithPhysics = false; //Seems more responsive calculating on update, separate from physics
        /// <summary>
        /// Whether to tick input with physics or not.
        /// </summary>
        public bool TickInputWithPhysics
        {
            get => _tickInputWithPhysics;
            set => SetField(ref _tickInputWithPhysics, value);
        }

        private float DeltaTime => TickInputWithPhysics ? Engine.FixedDelta : Engine.Delta;

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
