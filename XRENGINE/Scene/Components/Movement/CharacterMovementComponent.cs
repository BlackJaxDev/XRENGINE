using Extensions;
using MagicPhysX;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using XREngine.Components.Movement.Modules;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
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
    public class CharacterMovement3DComponent : PlayerMovementComponentBase, IRenderable
    {
        private const int CapsuleRenderPointCountHalfCircle = 8;

        private readonly XRMaterial _capsuleRenderMaterial;
        private readonly XRMeshRenderer _capsuleRenderMeshRenderer;
        private readonly RenderCommandMesh3D _capsuleRenderCommand;
        private readonly RenderInfo3D _capsuleRenderInfo;

        private bool _capsuleMeshDirty = true;
        private float _lastCapsuleRenderRadius = float.NaN;
        private float _lastCapsuleRenderHalfHeight = float.NaN;

        public RenderInfo[] RenderedObjects { get; }

        public CharacterMovement3DComponent()
        {
            _capsuleRenderMaterial = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.DarkLavender);
            _capsuleRenderMaterial.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            _capsuleRenderMaterial.RenderOptions.CullMode = ECullMode.None;
            _capsuleRenderMaterial.EnableTransparency();

            _capsuleRenderMeshRenderer = new XRMeshRenderer(null, _capsuleRenderMaterial);
            _capsuleRenderCommand = new RenderCommandMesh3D((int)EDefaultRenderPass.OnTopForward, _capsuleRenderMeshRenderer, Matrix4x4.Identity, null);

            _capsuleRenderInfo = RenderInfo3D.New(this, _capsuleRenderCommand);
            _capsuleRenderInfo.CastsShadows = false;
            _capsuleRenderInfo.ReceivesShadows = false;

            RenderedObjects = [_capsuleRenderInfo];

            UpdateCapsuleRenderMeshIfNeeded();
        }

        //public RigidBodyTransform RigidBodyTransform
        //    => SceneNode.GetTransformAs<RigidBodyTransform>(true)!;
        //public Transform ControllerTransform
        //    => SceneNode.GetTransformAs<Transform>(true)!;

        private float _airMovementAcceleration = 10f;
        private float _maxJumpHeight = 10.0f;
        private Func<Vector3, Vector3>? _subUpdateTick;
        private ECrouchState _crouchState = ECrouchState.Standing;
        private EMovementMode _movementMode = EMovementMode.Falling;
        private float _invisibleWallHeight = 0.0f;
        private float _density = 0.5f;
        private float _scaleCoeff = 0.8f;
        private float _volumeGrowth = 1.5f;
        private PhysxMaterial _material = new(0.9f, 0.9f, 0.1f);
        private float _radius = 0.6f;
        private float _standingHeight = new FeetInches(5, 2.0f).ToMeters();
        private float _crouchedHeight = new FeetInches(3, 0.0f).ToMeters();
        private float _proneHeight = new FeetInches(1, 0.0f).ToMeters();
        private bool _constrainedClimbing = false;
        private PhysxCapsuleController? _controller;

        private AbstractPhysicsScene? _subscribedPhysicsScene;
        private PhysxControllerActorProxy? _controllerActorProxy;
        private float _minMoveDistance = 0.00001f;
        private float _contactOffset = 0.001f;
        private Vector3 _upDirection = Globals.Up;
        private Vector3 _spawnPosition = Vector3.Zero;
        private Vector3 _velocity = Vector3.Zero;
        private Vector3? _gravityOverride = null;
        private bool _rotateGravityToMatchCharacterUp = false;

        // Jump state (runtime only, not settings)
        private float _jumpElapsed = 0.0f;
        private bool _isJumping = false;
        private bool _canJumpState = true;
        private float _coyoteTimer = 0.0f;

        // Running state (runtime)
        private bool _isRunning = false;

        // Movement module system - always uses a module, defaults to Modern
        private MovementModule _movementModule = new ModernMovementModule();

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
        /// The movement module that controls how the character responds to input.
        /// Defaults to ModernMovementModule. Use ArcadeMovementModule or PhysicalMovementModule
        /// for different movement feels, or create custom modules by inheriting from MovementModule.
        /// </summary>
        [Category("Movement")]
        [DisplayName("Movement Module")]
        [Description("Movement behavior module. Defaults to Modern style.")]
        public MovementModule MovementModule
        {
            get => _movementModule;
            set => SetField(ref _movementModule, value ?? new ModernMovementModule());
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
        /// Whether the character is currently running/sprinting.
        /// </summary>
        [Browsable(false)]
        public bool IsRunning
        {
            get => _isRunning;
            private set => SetField(ref _isRunning, value);
        }

        /// <summary>
        /// The current ground movement speed, accounting for running state.
        /// </summary>
        [Browsable(false)]
        public float CurrentGroundSpeed => MovementModule.WalkingSpeed * (IsRunning ? MovementModule.RunSpeedMultiplier : 1.0f);

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
            get => MovementModule.SlideOnSteepSlopes;
            set => MovementModule.SlideOnSteepSlopes = value;
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
        public PhysxCapsuleController? Controller
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
                    _capsuleMeshDirty = true;
                    break;
                case nameof(CrouchedHeight):
                    if (CrouchState == ECrouchState.Crouched)
                        Controller?.Resize(CrouchedHeight);
                    _capsuleMeshDirty = true;
                    break;
                case nameof(ProneHeight):
                    if (CrouchState == ECrouchState.Prone)
                        Controller?.Resize(ProneHeight);
                    _capsuleMeshDirty = true;
                    break;
                case nameof(CrouchState):
                    Controller?.Resize(GetCurrentHeight());
                    _capsuleMeshDirty = true;
                    break;
                case nameof(Radius):
                    Controller?.Radius = Radius;
                    _capsuleMeshDirty = true;
                    break;
                case nameof(MovementModule.SlopeLimitCosine):
                    Controller?.SlopeLimit = MovementModule.SlopeLimitCosine;
                    break;
                case nameof(MovementModule.StepOffset):
                    Controller?.StepOffset = MovementModule.StepOffset;
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

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
            _capsuleRenderCommand.WorldMatrix = renderMatrix;
            _capsuleRenderInfo.CullingOffsetMatrix = renderMatrix;
        }

        private void UpdateCapsuleRenderMeshIfNeeded()
        {
            float radius = Radius;
            float halfHeight = CurrentHeight * 0.5f;

            if (!_capsuleMeshDirty && radius == _lastCapsuleRenderRadius && halfHeight == _lastCapsuleRenderHalfHeight)
                return;

            _lastCapsuleRenderRadius = radius;
            _lastCapsuleRenderHalfHeight = halfHeight;
            _capsuleMeshDirty = false;

            _capsuleRenderMeshRenderer.Mesh = XRMesh.Shapes.WireframeCapsule(
                Vector3.Zero,
                Globals.Up,
                radius,
                halfHeight,
                CapsuleRenderPointCountHalfCircle);

            float yHalfExtent = halfHeight + radius;
            _capsuleRenderInfo.LocalCullingVolume = AABB.FromSize(new Vector3(radius * 2.0f, yHalfExtent * 2.0f, radius * 2.0f));
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
                MovementModule.SlopeLimitCosine,
                InvisibleWallHeight,
                MaxJumpHeight,
                ContactOffset,
                MovementModule.StepOffset,
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

            UpdateCapsuleRenderMeshIfNeeded();

            var rawInput = ConsumeInput();
            var tickResult = _subUpdateTick?.Invoke(rawInput) ?? Vector3.Zero;
            var literalInput = ConsumeLiteralInput();
            var moveDelta = tickResult + literalInput;

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
            
            LastVelocity = Velocity;
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

        public void AddForce(Vector3 force)
        {
            //Calculate acceleration from force
            float mass = RigidBodyReference?.Mass ?? 0.0f;
            if (mass > 0.0f)
                Velocity += force / mass;
        }

        public bool IsJumping => _isJumping;

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

            // Process ground movement through the module
            var context = CreateMovementContext(moveDirection, dt, true);
            var result = MovementModule.ProcessGroundMovement(in context);
            Vector3 newVelocity = result.NewVelocity;

            // Handle requested mode change from module
            ApplyRequestedModeChange(result.RequestedMode);

            // Handle ground jump
            HandleJumping(dt, ref newVelocity);

            // Clamp speed to max speed for deterministic movement
            ClampSpeed(ref newVelocity);

            // Update stored velocity
            Velocity = newVelocity;

            // Convert to position delta
            Vector3 delta = newVelocity * dt;

            if (float.IsNaN(delta.X) || float.IsNaN(delta.Y) || float.IsNaN(delta.Z))
                delta = Vector3.Zero;

            return delta;
        }

        /// <summary>
        /// Creates a MovementContext for use with movement modules.
        /// </summary>
        private MovementModule.MovementContext CreateMovementContext(Vector3 inputDirection, float dt, bool isGrounded)
        {
            Vector3 gravity = Vector3.Zero;
            if (World?.PhysicsScene is PhysxScene scene)
                gravity = (GravityOverride ?? scene.Gravity) * MovementModule.GravityScale;

            return new MovementModule.MovementContext(
                inputDirection,
                Velocity,
                CurrentGroundSpeed,
                MovementModule.MaxSpeed,
                dt,
                isGrounded,
                Controller?.CollidingUp ?? false,
                UpDirection,
                gravity);
        }

        /// <summary>
        /// Applies a movement mode change requested by the movement module.
        /// </summary>
        private void ApplyRequestedModeChange(MovementModule.ERequestedMode requestedMode)
        {
            switch (requestedMode)
            {
                case MovementModule.ERequestedMode.None:
                    // No change requested
                    break;
                case MovementModule.ERequestedMode.Ground:
                    _subUpdateTick = GroundMovementTick;
                    MovementMode = EMovementMode.Walking;
                    break;
                case MovementModule.ERequestedMode.Air:
                    _subUpdateTick = AirMovementTick;
                    MovementMode = EMovementMode.Falling;
                    break;
                case MovementModule.ERequestedMode.Swimming:
                    _subUpdateTick = SwimmingMovementTick;
                    MovementMode = EMovementMode.Swimming;
                    break;
                case MovementModule.ERequestedMode.Flying:
                    _subUpdateTick = AirMovementTick; // Use air movement for flying for now
                    MovementMode = EMovementMode.Flying;
                    break;
            }
        }

        private void HandleJumping(float dt, ref Vector3 delta)
        {
            if (Controller is null)
                return;

            // Don't process jumping if the module doesn't allow it
            if (!MovementModule.CanJump)
                return;

            if (Controller.CollidingDown)
            {
                _canJumpState = true;
                _coyoteTimer = MovementModule.CoyoteTime;
            }
            else
            {
                _coyoteTimer -= dt;
                _canJumpState = _coyoteTimer > 0.0f;
            }

            if (!_isJumping)
                return;

            if (CanPerformJump)
            {
                // Apply jump as an impulse (direct velocity change), not scaled by dt
                // The velocity will be converted to position delta later
                delta.Y = MovementModule.JumpForce;
                _jumpElapsed = 0.0f;
                _canJumpState = false;
                _subUpdateTick = AirMovementTick;
            }
            else
            {
                bool addingJumpForce = _isJumping && _jumpElapsed < MovementModule.MaxJumpDuration && !Controller.CollidingUp;
                if (!addingJumpForce)
                    return;
                
                // Sustained jump hold adds a small amount of upward acceleration
                // This is acceleration (m/s²), so multiply by dt to get velocity change
                float jumpFactor = 1.0f - (_jumpElapsed / MovementModule.MaxJumpDuration);
                delta.Y += MovementModule.JumpHoldForce * jumpFactor * dt;
                _jumpElapsed += dt;
            }
        }

        /// <summary>
        /// Whether the character can currently perform a jump (grounded or in coyote time).
        /// </summary>
        public bool CanPerformJump => MovementModule.CanJump && ((_canJumpState && _coyoteTimer > 0.0f) || (Controller?.CollidingDown ?? false));

        protected virtual unsafe Vector3 AirMovementTick(Vector3 posDelta)
        {
            if (Controller is null || World?.PhysicsScene is not PhysxScene scene)
                return Vector3.Zero;

            float dt = DeltaTime;

            // Process air movement through the module
            Vector3 inputDirection = posDelta != Vector3.Zero ? new Vector3(posDelta.X, 0, posDelta.Z).Normalized() : Vector3.Zero;
            var context = CreateMovementContext(inputDirection, dt, false);
            var result = MovementModule.ProcessAirMovement(in context);
            Vector3 newVelocity = result.NewVelocity;
            
            // Handle requested mode change from module
            ApplyRequestedModeChange(result.RequestedMode);
            
            // Apply gravity if module didn't handle it
            if (!result.GravityApplied)
                ApplyGravity(scene, ref newVelocity);

            // Handle coyote jump or sustained jump hold
            HandleJumping(dt, ref newVelocity);

            // Clamp speed to max speed for deterministic movement
            ClampSpeed(ref newVelocity);

            // Update stored velocity
            Velocity = newVelocity;

            // Convert to position delta
            Vector3 delta = newVelocity * dt;

            // Apply landing friction when hitting ground
            if (Controller.CollidingDown)
            {
                // Reduce horizontal velocity on landing based on ground friction
                float frictionFactor = 1.0f - MovementModule.GroundFriction;
                delta = new Vector3(delta.X * frictionFactor, delta.Y, delta.Z * frictionFactor);
                _subUpdateTick = GroundMovementTick;
            }

            if (float.IsNaN(delta.X) || float.IsNaN(delta.Y) || float.IsNaN(delta.Z))
                delta = Vector3.Zero;

            return delta;
        }

        protected virtual unsafe Vector3 SwimmingMovementTick(Vector3 posDelta)
        {
            if (Controller is null || World?.PhysicsScene is not PhysxScene scene)
                return Vector3.Zero;

            float dt = DeltaTime;

            // Process swimming movement through the module
            // Swimming allows full 3D directional control
            Vector3 inputDirection = posDelta != Vector3.Zero ? posDelta.Normalized() : Vector3.Zero;
            var context = CreateMovementContext(inputDirection, dt, false);
            var result = MovementModule.ProcessSwimmingMovement(in context);
            Vector3 newVelocity = result.NewVelocity;

            // Handle requested mode change from module
            ApplyRequestedModeChange(result.RequestedMode);

            // Apply gravity if module didn't handle it (reduced for buoyancy)
            if (!result.GravityApplied)
            {
                Vector3 gravity = GravityOverride ?? scene.Gravity;
                if (RotateGravityToMatchCharacterUp && Controller is not null)
                    gravity = Vector3.Transform(gravity, XRMath.RotationBetweenVectors(Globals.Up, Controller.UpDirection));
                // Reduced gravity underwater for buoyancy effect
                newVelocity += gravity * MovementModule.GravityScale * 0.25f * dt;
            }

            // Clamp speed to max speed
            ClampSpeed(ref newVelocity);

            // Update stored velocity
            Velocity = newVelocity;

            // Convert to position delta
            Vector3 delta = newVelocity * dt;

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

            if (velocity.Length() > MovementModule.MaxSpeed)
                velocity = velocity.Normalized() * MovementModule.MaxSpeed;
            
            // Restore vertical movement
            velocity.Y = verticalDelta;
        }

        private void ApplyGravity(PhysxScene scene, ref Vector3 delta)
        {
            Vector3 gravity = GravityOverride ?? scene.Gravity;
            if (RotateGravityToMatchCharacterUp && Controller is not null)
                gravity = Vector3.Transform(gravity, XRMath.RotationBetweenVectors(Globals.Up, Controller.UpDirection));
            // Apply gravity scale for snappier game feel
            delta += gravity * MovementModule.GravityScale * DeltaTime;
        }

        public void Jump(bool pressed)
        {
            if (pressed)
                _isJumping = true;
            else
            {
                _isJumping = false;
                _jumpElapsed = MovementModule.MaxJumpDuration; // Cut the jump short when button is released
            }
        }

        /// <summary>
        /// Called when the run/sprint button is pressed or released.
        /// Bind to Shift keys or gamepad face button left (X/Square).
        /// </summary>
        /// <param name="pressed">True when button is pressed, false when released.</param>
        public void Run(bool pressed)
        {
            IsRunning = pressed;
        }
    }
}
