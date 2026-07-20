using XREngine.Extensions;
using MagicPhysX;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Threading;
using XREngine.Components.Movement.Modules;
using XREngine.Components.Physics;
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
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Movement
{
    [OneComponentAllowed]
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Gameplay")]
    [DisplayName("Character Movement")]
    [Description("Full-featured first/third-person character controller with jumping, crouching, and slope handling.")]
    public class CharacterMovement3DComponent : PlayerMovementComponentBase, IRenderable, IRuntimeCharacterMovementComponent
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

        private float _maxJumpHeight = 10.0f;
        private Func<Vector3, Vector3>? _subUpdateTick;
        private ECrouchState _crouchState = ECrouchState.Standing;
        private EMovementMode _movementMode = EMovementMode.Falling;
        private float _invisibleWallHeight = 0.0f;
        private float _scaleCoeff = 0.8f;
        private float _volumeGrowth = 1.5f;
        private readonly PhysicsMaterialDefinition _materialDefinition = new()
        {
            StaticFriction = 0.9f,
            DynamicFriction = 0.9f,
            Restitution = 0.1f,
        };
        private float _radius = 0.3f;
        private float _standingHeight = new FeetInches(5, 2.0f).ToMeters();
        private float _crouchedHeight = new FeetInches(3, 0.0f).ToMeters();
        private float _proneHeight = new FeetInches(1, 0.0f).ToMeters();
        private bool _constrainedClimbing = false;
        private IAbstractCharacterController? _controller;
        private PhysxCapsuleController? _physxController;
        private int _controllerInitVersion;

        private AbstractPhysicsScene? _subscribedPhysicsScene;
        private CharacterControllerComponent? _externalControllerComponent;
        private bool _ownsActiveController;
        private IAbstractRigidPhysicsActor? _controllerActorProxy;
        private float _minMoveDistance = 0.00001f;
        private float _contactOffset = 0.02f;
        private Vector3 _upDirection = Globals.Up;
        private Vector3 _spawnPosition = Vector3.Zero;
        private Vector3 _velocity = Vector3.Zero;
        private Vector3? _gravityOverride = null;
        private bool _rotateGravityToMatchCharacterUp = false;
        private CharacterMotionInputModel _motionInputModel = CharacterMotionInputModel.Velocity;
        private float _predictiveContactDistance = 0.1f;
        private float _collisionTolerance = 0.001f;
        private float _stickToFloorDistance = 0.1f;
        private float _stepDownExtra;
        private float _maxStrength = 100.0f;

        // Jump state (runtime only, not settings)
        private float _jumpElapsed = 0.0f;
        private bool _isJumping = false;
        private int _jumpHeld;
        private int _pendingJumpPresses;
        private bool _jumpPressedThisProducerTick;
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
        public float HalfHeight => CurrentHeight * 0.5f + ContactOffset;

        /// <summary>
        /// The position of the character's feet.
        /// This exists because the center of the capsule is off the ground.
        /// </summary>
        public Vector3 FootPosition
        {
            get => ActiveController?.FootPosition ?? (Position - UpDirection * HalfHeight);
            set
            {
                ActiveController?.FootPosition = value;
            }
        }

        /// <summary>
        /// The position of the character controller.
        /// This is the center of the capsule.
        /// </summary>
        public Vector3 Position
        {
            get => ActiveController?.Position ?? Transform.WorldTranslation;
            set
            {
                ActiveController?.Position = value;
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
            set
            {
                if (!IsFinite(value) || value.LengthSquared() < 1e-8f)
                    return;
                SetField(ref _upDirection, Vector3.Normalize(value));
            }
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
        /// This is only used if invisible walls are created(�invisibleWallHeight� is non zero).
        /// When a character jumps, the non-walkable triangles he might fly over are not found by the collision queries
        /// (since the character�s bounding volume does not touch them).
        /// Thus those non-walkable triangles do not create invisible walls, and it is possible for a jumping character to land on a non-walkable triangle,
        /// while he wouldn�t have reached that place by just walking.
        /// The �maxJumpHeight� variable is used to extend the size of the collision volume downward.
        /// This way, all the non-walkable triangles are properly found by the collision queries and it becomes impossible to �jump over� invisible walls.
        /// If the character in your game can not jump, it is safe to use 0.0 here.
        /// Otherwise it is best to keep this value as small as possible, 
        /// since a larger collision volume means more triangles to process.
        /// </summary>
        public float MaxJumpHeight
        {
            get => _maxJumpHeight;
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _maxJumpHeight, MathF.Max(0.0f, value));
            }
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
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _contactOffset, MathF.Max(0.0f, value));
            }
        }

        [Category("Movement")]
        [DisplayName("Motion Input Model")]
        [Description("Whether processed movement is submitted as velocity or per-tick displacement.")]
        public CharacterMotionInputModel MotionInputModel
        {
            get => _motionInputModel;
            set
            {
                if (SetField(ref _motionInputModel, value) && ActiveController is not null)
                    ActiveController.MotionInputModel = value;
            }
        }

        [Category("Jolt Controller")]
        public float PredictiveContactDistance
        {
            get => _predictiveContactDistance;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _predictiveContactDistance, sanitized)
                    && ActiveController is IAdvancedCharacterControllerSettings advanced)
                    advanced.PredictiveContactDistance = sanitized;
            }
        }

        [Category("Jolt Controller")]
        public float CollisionTolerance
        {
            get => _collisionTolerance;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.000001f, value);
                if (SetField(ref _collisionTolerance, sanitized)
                    && ActiveController is IAdvancedCharacterControllerSettings advanced)
                    advanced.CollisionTolerance = sanitized;
            }
        }

        [Category("Jolt Controller")]
        public float StickToFloorDistance
        {
            get => _stickToFloorDistance;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _stickToFloorDistance, sanitized)
                    && ActiveController is IAdvancedCharacterControllerSettings advanced)
                    advanced.StickToFloorDistance = sanitized;
            }
        }

        [Category("Jolt Controller")]
        public float StepDownExtra
        {
            get => _stepDownExtra;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _stepDownExtra, sanitized)
                    && ActiveController is IAdvancedCharacterControllerSettings advanced)
                    advanced.StepDownExtra = sanitized;
            }
        }

        [Category("Physics")]
        public float MaxStrength
        {
            get => _maxStrength;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _maxStrength, sanitized)
                    && ActiveController is IAdvancedCharacterControllerSettings advanced)
                    advanced.MaxStrength = sanitized;
            }
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
        /// The CCT creates a PhysX�s kinematic actor under the hood.
        /// This controls its scale factor.
        /// This should be a number a bit smaller than 1.0.
        /// This scale factor affects how the character interacts with dynamic rigid bodies around it (e.g.pushing them, etc).
        /// With a scale factor < 1, the underlying kinematic actor will not touch surrounding rigid bodies - they will only interact with the character controller�s shapes (capsules or boxes),
        /// and users will have full control over the interactions(i.e.they will have to push the objects with explicit forces themselves).
        /// With a scale factor >=1, the underlying kinematic actor will touch and push surrounding rigid bodies based on PhysX�s computations, 
        /// as if there would be no character controller involved.This works fine except when you push objects into a wall.
        /// PhysX has no control over kinematic actors(since they are kinematic) so they would freely push dynamic objects into walls, and make them tunnel / explode / behave badly.
        /// With a smaller kinematic actor however, the character controller�s swept shape touches dynamic rigid bodies first, 
        /// and can apply forces to them to move them away (or not, depending on what the gameplay needs).
        /// Meanwhile the character controller�s swept shape itself is stopped by these dynamic bodies.
        /// Setting the scale factor to 1 could still work, but it is unreliable.
        /// Depending on FPU accuracy you could end up with either the CCT�s volume or the underlying kinematic actor touching the dynamic bodies first, and this could change from one moment to the next.
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
            set
            {
                MovementModule.SlideOnSteepSlopes = value;
                if (ActiveController is ICharacterControllerCollisionSettings collisionSettings)
                    collisionSettings.SlideOnSteepSlopes = value;
            }
        }

        [Category("Capsule")]
        [DisplayName("Radius")]
        [Description("Capsule collision radius.")]
        public float Radius
        {
            get => _radius;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.001f, value);
                if (!SetField(ref _radius, sanitized))
                    return;
                float minimumHeight = 2.0f * sanitized;
                StandingHeight = MathF.Max(StandingHeight, minimumHeight);
                CrouchedHeight = MathF.Max(CrouchedHeight, minimumHeight);
                ProneHeight = MathF.Max(ProneHeight, minimumHeight);
            }
        }

        /// <summary>
        /// How tall the character is when standing.
        /// </summary>
        [Category("Capsule")]
        [DisplayName("Standing Height")]
        [Description("Total capsule height when standing, including both hemispheres.")]
        public float StandingHeight
        {
            get => _standingHeight;
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _standingHeight, MathF.Max(2.0f * Radius, value));
            }
        }

        /// <summary>
        /// How tall the character is when prone.
        /// </summary>
        [Category("Crouch")]
        [DisplayName("Prone Height")]
        [Description("Total capsule height when prone, including both hemispheres.")]
        public float ProneHeight
        {
            get => _proneHeight;
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _proneHeight, MathF.Max(2.0f * Radius, value));
            }
        }

        /// <summary>
        /// How tall the character is when crouched.
        /// </summary>
        [Category("Crouch")]
        [DisplayName("Crouched Height")]
        [Description("Total capsule height when crouched, including both hemispheres.")]
        public float CrouchedHeight
        {
            get => _crouchedHeight;
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _crouchedHeight, MathF.Max(2.0f * Radius, value));
            }
        }

        /// <summary>
        /// The current height of the character controller, depending on the crouch state.
        /// </summary>
        public float CurrentHeight => ActiveController?.TotalHeight ?? GetCurrentHeight();

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
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _minMoveDistance, MathF.Max(0.0f, value));
            }
        }

        [Browsable(false)]
        [YamlIgnore]
        private IAbstractCharacterController? ActiveController
        {
            get => _controller;
            set => SetField(ref _controller, value);
        }

        [Browsable(false)]
        public IAbstractCharacterController? CharacterController => ActiveController;

        [Browsable(false)]
        public PhysicsCharacterControllerCapabilities BackendCapabilities
            => ActiveController?.Capabilities ?? PhysicsCharacterControllerCapabilities.None;

        public IAbstractDynamicRigidBody? RigidBodyReference => _physxController?.Actor;

        [Category("Physics / PhysX Extensions")]
        [Description("PhysX-only raw capsule controller. Prefer CharacterController for backend-neutral gameplay code.")]
        public PhysxCapsuleController? PhysxControllerExtension => _physxController;
        
        [Category("Physics / PhysX Extensions")]
        [Description("PhysX-only controller state details. Backend-neutral movement should use CharacterController collision properties/events.")]
        public void GetPhysxStateExtension(
            out Vector3 deltaXP,
            out PhysxShape? touchedShape,
            out PhysxRigidActor? touchedActor,
            out uint touchedObstacleHandle,
            out PxControllerCollisionFlags collisionFlags,
            out bool standOnAnotherCCT,
            out bool standOnObstacle,
            out bool isMovingUp)
        {
            if (_physxController is null)
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

            var state = _physxController.State;
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
                        ActiveController?.Resize(StandingHeight);
                    _capsuleMeshDirty = true;
                    break;
                case nameof(CrouchedHeight):
                    if (CrouchState == ECrouchState.Crouched)
                        ActiveController?.Resize(CrouchedHeight);
                    _capsuleMeshDirty = true;
                    break;
                case nameof(ProneHeight):
                    if (CrouchState == ECrouchState.Prone)
                        ActiveController?.Resize(ProneHeight);
                    _capsuleMeshDirty = true;
                    break;
                case nameof(CrouchState):
                    ActiveController?.Resize(GetCurrentHeight());
                    _capsuleMeshDirty = true;
                    break;
                case nameof(Radius):
                    ActiveController?.Radius = Radius;
                    _capsuleMeshDirty = true;
                    break;
                case nameof(MovementModule.SlopeLimitCosine):
                    ActiveController?.SlopeLimit = MovementModule.SlopeLimitCosine;
                    break;
                case nameof(MovementModule.StepOffset):
                    ActiveController?.StepOffset = MovementModule.StepOffset;
                    break;
                case nameof(ContactOffset):
                    ActiveController?.ContactOffset = ContactOffset;
                    break;
                case nameof(UpDirection):
                    ActiveController?.UpDirection = UpDirection;
                    break;
                case nameof(CollisionGroup):
                    if (ActiveController is ICharacterControllerCollisionSettings collisionSettings)
                        collisionSettings.CollisionLayerMask = CollisionGroup == 0
                            ? new LayerMask(1)
                            : new LayerMask(1 << CollisionGroup);
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
            float totalHeight = MathF.Max(CurrentHeight, 2.0f * radius);
            float halfCylinderHeight = MathF.Max(0.0f, totalHeight - 2.0f * radius) * 0.5f;

            if (!_capsuleMeshDirty
                && radius == _lastCapsuleRenderRadius
                && halfCylinderHeight == _lastCapsuleRenderHalfHeight)
                return;

            _lastCapsuleRenderRadius = radius;
            _lastCapsuleRenderHalfHeight = halfCylinderHeight;
            _capsuleMeshDirty = false;

            _capsuleRenderMeshRenderer.Mesh = XRMesh.Shapes.WireframeCapsule(
                Vector3.Zero,
                Globals.Up,
                radius,
                halfCylinderHeight,
                CapsuleRenderPointCountHalfCircle);

            float yHalfExtent = totalHeight * 0.5f;
            _capsuleRenderInfo.LocalCullingVolume = AABB.FromSize(new Vector3(radius * 2.0f, yHalfExtent * 2.0f, radius * 2.0f));
        }

        protected unsafe override void OnComponentActivated()
        {
            // Character movement uses a PhysX CCT (controller) and wraps its hidden rigid actor.
            // Prevent the DynamicRigidBodyComponent base from auto-creating/registering a separate rigid body.
            AutoCreateRigidBody = false;

            base.OnComponentActivated();

            _subUpdateTick = GroundMovementTick;
            RegisterMovementTick();

            var physicsScene = WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene;
            if (physicsScene is null)
                return;

            // Keep our transform driven by the controller after each physics step.
            // Without wrapping the controller actor as a rigid body, nothing else updates the RigidBodyTransform.
            if (WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene is { } sceneForEvents && _subscribedPhysicsScene != sceneForEvents)
            {
                _subscribedPhysicsScene?.OnSimulationStep -= OnPhysicsSimulationStep;
                sceneForEvents.OnSimulationStep += OnPhysicsSimulationStep;
                _subscribedPhysicsScene = sceneForEvents;
            }

            CharacterControllerComponent? reusableController = GetSiblingComponent<CharacterControllerComponent>();
            if (reusableController is not null)
            {
                _externalControllerComponent = reusableController;
                reusableController.ControllerCreated += ExternalControllerCreated;
                reusableController.ControllerReleased += ExternalControllerReleased;
                if (reusableController.Controller is not null)
                    BindExternalController(reusableController.Controller);
                return;
            }

            _ownsActiveController = true;
            Vector3 pos = Transform.WorldTranslation;
            QueueControllerCreation(physicsScene, pos);
        }

        private void ExternalControllerCreated(CharacterControllerComponent component, IAbstractCharacterController controller)
            => BindExternalController(controller);

        private void ExternalControllerReleased(CharacterControllerComponent component)
        {
            if (ReferenceEquals(component, _externalControllerComponent))
            {
                ActiveController = null;
                _controllerActorProxy = null;
                _physxController = null;
            }
        }

        private void BindExternalController(IAbstractCharacterController controller)
        {
            _ownsActiveController = false;
            _physxController = (controller as IPhysxCharacterControllerExtension)?.NativeController;
            ApplyControllerSettings(controller);
            ActiveController = controller;
            _controllerActorProxy = controller;
            RigidBodyTransform.PostRotationOffset = Quaternion.Identity;
            RigidBodyTransform.RigidBody = controller;
            RigidBodyTransform.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;
            RigidBodyTransform.OnPhysicsStepped();
        }

        private void QueueControllerCreation(AbstractPhysicsScene physicsScene, Vector3 position)
        {
            if (ActiveController is not null)
                return;

            int initVersion = Interlocked.Increment(ref _controllerInitVersion);
            RuntimeThreadServices.Current.EnqueuePhysicsThread(() => CreateControllerOnPhysicsThread(physicsScene, position, initVersion));
        }

        private void CreateControllerOnPhysicsThread(
            AbstractPhysicsScene physicsScene,
            Vector3 position,
            int initVersion)
        {
            if (initVersion != Volatile.Read(ref _controllerInitVersion))
                return;

            if (!IsActiveInHierarchy)
                return;

            if (WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene != physicsScene)
                return;

            if (ActiveController is not null)
                return;

            ReportUnsupportedSettings(physicsScene.BackendService.CharacterControllerCapabilities);
            IAbstractCharacterController? controller = physicsScene.BackendService.CreateCharacterController(
                new PhysicsCharacterControllerCreateInfo(
                    position,
                    UpDirection,
                    Radius,
                    GetCurrentHeight(),
                    MovementModule.SlopeLimitCosine,
                    ContactOffset,
                    MovementModule.StepOffset,
                    Density,
                    _materialDefinition)
                {
                    MotionInputModel = MotionInputModel,
                    CollisionLayerMask = CollisionGroup == 0
                        ? new LayerMask(1)
                        : new LayerMask(1 << CollisionGroup),
                    PredictiveContactDistance = PredictiveContactDistance,
                    CollisionTolerance = CollisionTolerance,
                    StickToFloorDistance = StickToFloorDistance,
                    StepDownExtra = StepDownExtra,
                    MaxStrength = MaxStrength,
                    InvisibleWallHeight = InvisibleWallHeight,
                    MaxJumpHeight = MaxJumpHeight,
                    ScaleCoefficient = ScaleCoeff,
                    VolumeGrowth = VolumeGrowth,
                    SlideOnSteepSlopes = SlideOnSteepSlopes,
                    ConstrainedClimbing = ConstrainedClimbing,
                });
            if (controller is null)
                return;

            if (initVersion != Volatile.Read(ref _controllerInitVersion))
            {
                controller.RequestRelease();
                return;
            }

            RuntimeThreadServices.Current.EnqueueUpdateThread(() => BindOwnedController(physicsScene, controller, initVersion));
        }

        private void ReportUnsupportedSettings(PhysicsCharacterControllerCapabilities capabilities)
        {
            string controllerName = SceneNode?.Name ?? "<unnamed>";
            if (_materialDefinition is not null
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.Materials))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' controller material is not supported by the active physics backend.");
            if (InvisibleWallHeight > 0.0f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.InvisibleWalls))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' invisible walls are not supported by the active physics backend.");
            if (InvisibleWallHeight > 0.0f && MaxJumpHeight > 0.0f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.MaximumJumpHeight))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' maximum jump height for invisible walls is not supported by the active physics backend.");
            if (ConstrainedClimbing
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.ConstrainedClimbing))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' constrained capsule climbing is not supported by the active physics backend.");
            if (!capabilities.HasFlag(PhysicsCharacterControllerCapabilities.ScaleCoefficient))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' PhysX scale coefficient is not supported by the active physics backend.");
            if (!capabilities.HasFlag(PhysicsCharacterControllerCapabilities.VolumeGrowth))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' PhysX volume growth is not supported by the active physics backend.");
            if (MathF.Abs(PredictiveContactDistance - 0.1f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.PredictiveContacts))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' predictive contact distance is not supported by the active physics backend.");
            if (MathF.Abs(CollisionTolerance - 0.001f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.IndependentCollisionTolerance))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' independent collision tolerance is not supported by the active physics backend.");
            if (MathF.Abs(StickToFloorDistance - 0.1f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.FloorStickDistance))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' floor-stick distance is not supported by the active physics backend.");
            if (StepDownExtra > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.IndependentStepDown))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' independent step-down distance is not supported by the active physics backend.");
            if (MathF.Abs(MaxStrength - 100.0f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.MaximumStrength))
                Debug.LogWarning($"[CharacterMovement] '{controllerName}' maximum strength is not supported by the active physics backend.");
        }

        private void BindOwnedController(
            AbstractPhysicsScene physicsScene,
            IAbstractCharacterController controller,
            int initVersion)
        {
            if (initVersion != Volatile.Read(ref _controllerInitVersion)
                || !IsActiveInHierarchy
                || WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene != physicsScene)
            {
                controller.RequestRelease();
                return;
            }

            _ownsActiveController = true;
            _physxController = (controller as IPhysxCharacterControllerExtension)?.NativeController;
            ApplyControllerSettings(controller);
            ActiveController = controller;

            // CCT doesn't have rotation like regular physics actors, so clear the default -90� Z offset
            // that RigidBodyTransform applies for normal PhysX actors.
            _controllerActorProxy = controller;
            RigidBodyTransform.PostRotationOffset = Quaternion.Identity;
            RigidBodyTransform.RigidBody = controller;
            RigidBodyTransform.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;
            RigidBodyTransform.OnPhysicsStepped();
        }

        private void ApplyControllerSettings(IAbstractCharacterController controller)
        {
            controller.MotionInputModel = MotionInputModel;
            if (controller is ICharacterControllerCollisionSettings collisionSettings)
            {
                collisionSettings.CollisionLayerMask = CollisionGroup == 0
                    ? new LayerMask(1)
                    : new LayerMask(1 << CollisionGroup);
                collisionSettings.SlideOnSteepSlopes = SlideOnSteepSlopes;
            }
            if (controller is not IAdvancedCharacterControllerSettings advanced)
                return;

            advanced.PredictiveContactDistance = PredictiveContactDistance;
            advanced.CollisionTolerance = CollisionTolerance;
            advanced.StickToFloorDistance = StickToFloorDistance;
            advanced.StepDownExtra = StepDownExtra;
            advanced.MaxStrength = MaxStrength;
        }

        protected override void OnComponentDeactivated()
        {
            Interlocked.Increment(ref _controllerInitVersion);
            UnregisterMovementTick();
            _subUpdateTick = null;
            _subscribedPhysicsScene?.OnSimulationStep -= OnPhysicsSimulationStep;
            _subscribedPhysicsScene = null;
            if (_externalControllerComponent is not null)
            {
                _externalControllerComponent.ControllerCreated -= ExternalControllerCreated;
                _externalControllerComponent.ControllerReleased -= ExternalControllerReleased;
                _externalControllerComponent = null;
            }

            // The controller owns its internal actor; do not remove it as a normal rigid body.
            var rigidBodyTransform = SceneNode?.GetTransformAs<RigidBodyTransform>(false);
            rigidBodyTransform?.RigidBody = null;

            _controllerActorProxy = null;

            if (_ownsActiveController)
                ActiveController?.RequestRelease();
            _physxController = null;
            _ownsActiveController = false;

            ActiveController = null;
        }

        private void OnPhysicsSimulationStep()
        {
            // Runs from the physics fixed-step.
            if (_controllerActorProxy is null)
                return;

            ActiveController?.Synchronize();

            // Drive the transform update via the engine's standard rigid-body sync path.
            RigidBodyTransform.OnPhysicsStepped();
        }

        private unsafe void MainUpdateTick()
        {
            if (ActiveController is null)
                return;
            float dt = DeltaTime;
            if (!float.IsFinite(dt) || dt <= 0.0f)
                return;

            Acceleration = (Velocity - LastVelocity) / dt;

            UpdateCapsuleRenderMeshIfNeeded();

            var rawInput = ConsumeInput();
            _jumpPressedThisProducerTick = TryConsumeJumpPress();
            bool wasJumping = _isJumping;
            _isJumping = Volatile.Read(ref _jumpHeld) != 0;
            if (wasJumping && !_isJumping)
                _jumpElapsed = MovementModule.MaxJumpDuration;
            Vector3 requestedVelocity = _subUpdateTick?.Invoke(rawInput) ?? Vector3.Zero;
            Vector3 literalDisplacement = ConsumeLiteralInput();
            if (literalDisplacement != Vector3.Zero)
                requestedVelocity += literalDisplacement / dt;

            Vector3 commandValue = MotionInputModel == CharacterMotionInputModel.Velocity
                ? requestedVelocity
                : requestedVelocity * dt;
            ActiveController.SubmitMotion(new CharacterMotionCommand(
                commandValue,
                MotionInputModel,
                MinMoveDistance,
                dt));

            if (ActiveController.IsGrounded)
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

        [Browsable(false)]
        public Vector3 EffectiveVelocity
            => ActiveController?.EffectiveVelocity ?? Vector3.Zero;

        [Browsable(false)]
        public Vector3 GroundVelocity
            => ActiveController?.GroundVelocity ?? Vector3.Zero;

        [Browsable(false)]
        public Vector3 GroundNormal
            => ActiveController?.GroundNormal ?? NormalizedUpDirection;

        [Browsable(false)]
        public CharacterSupportState SupportState
            => ActiveController?.SupportState ?? CharacterSupportState.Unknown;

        public void AddForce(Vector3 force)
        {
            //Calculate acceleration from force
            float mass = RigidBodyReference is PhysxRigidBody body ? body.Mass : 0.0f;
            if (mass > 0.0f)
                Velocity += force / mass;
        }

        public bool IsJumping => Volatile.Read(ref _jumpHeld) != 0;

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
            set
            {
                if (_tickInputWithPhysics == value)
                    return;

                if (IsActiveInHierarchy)
                    UnregisterMovementTick();
                SetField(ref _tickInputWithPhysics, value);
                if (IsActiveInHierarchy)
                    RegisterMovementTick();
            }
        }

        private float DeltaTime => TickInputWithPhysics
            ? RuntimePhysicsServices.Current.FixedDeltaSeconds
            : RuntimeTransformServices.Current?.DilatedUpdateDeltaSeconds ?? 0.0f;

        private ETickGroup MovementTickGroup
            => TickInputWithPhysics ? ETickGroup.PrePhysics : ETickGroup.Late;

        private void RegisterMovementTick()
            => RegisterTick(MovementTickGroup, (int)ETickOrder.Animation, MainUpdateTick);

        private void UnregisterMovementTick()
            => UnregisterTick(MovementTickGroup, (int)ETickOrder.Animation, MainUpdateTick);

        protected override float InputDeltaTime => DeltaTime;

        protected virtual Vector3 GroundMovementTick(Vector3 posDelta)
        {
            if (ActiveController is null)
                return Vector3.Zero;

            float dt = DeltaTime;
            Vector3 up = NormalizedUpDirection;

            Vector3 moveDirection = Vector3.Zero;
            if (posDelta != Vector3.Zero)
            {
                Vector3 groundNormal = ActiveController.IsGrounded
                    ? ActiveController.GroundNormal
                    : up;
                Vector3 planarInput = ProjectOntoPlane(posDelta, up);
                Vector3 alongGround = ProjectOntoPlane(planarInput, groundNormal);
                if (alongGround.LengthSquared() > 1e-8f)
                    moveDirection = Vector3.Normalize(alongGround);
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

            return IsFinite(newVelocity) ? newVelocity : Vector3.Zero;
        }

        /// <summary>
        /// Creates a MovementContext for use with movement modules.
        /// </summary>
        private MovementModule.MovementContext CreateMovementContext(Vector3 inputDirection, float dt, bool isGrounded)
        {
            Vector3 gravity = Vector3.Zero;
            if (WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene is { } scene)
                gravity = (GravityOverride ?? scene.Gravity) * MovementModule.GravityScale;

            return new MovementModule.MovementContext(
                inputDirection,
                Velocity,
                CurrentGroundSpeed,
                MovementModule.MaxSpeed,
                dt,
                isGrounded,
                ActiveController?.CollidingUp ?? false,
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
            if (ActiveController is null)
                return;

            // Don't process jumping if the module doesn't allow it
            if (!MovementModule.CanJump)
                return;

            if (ActiveController.IsGrounded)
            {
                _canJumpState = true;
                _coyoteTimer = MovementModule.CoyoteTime;
            }
            else
            {
                _coyoteTimer -= dt;
                _canJumpState = _coyoteTimer > 0.0f;
            }

            if (_jumpPressedThisProducerTick && CanPerformJump)
            {
                // Apply jump as an impulse (direct velocity change), not scaled by dt
                // The velocity will be converted to position delta later
                Vector3 up = NormalizedUpDirection;
                delta = ProjectOntoPlane(delta, up) + up * MovementModule.JumpForce;
                _jumpElapsed = 0.0f;
                _canJumpState = false;
                _subUpdateTick = AirMovementTick;
            }
            else if (_isJumping)
            {
                bool addingJumpForce = _isJumping && _jumpElapsed < MovementModule.MaxJumpDuration && !(ActiveController?.CollidingUp ?? false);
                if (!addingJumpForce)
                    return;
                
                // Sustained jump hold adds a small amount of upward acceleration
                // This is acceleration (m/s�), so multiply by dt to get velocity change
                float jumpFactor = 1.0f - (_jumpElapsed / MovementModule.MaxJumpDuration);
                delta += NormalizedUpDirection * MovementModule.JumpHoldForce * jumpFactor * dt;
                _jumpElapsed += dt;
            }
        }

        /// <summary>
        /// Whether the character can currently perform a jump (grounded or in coyote time).
        /// </summary>
        public bool CanPerformJump => MovementModule.CanJump
            && ((_canJumpState && _coyoteTimer > 0.0f)
                || (ActiveController?.IsGrounded ?? false));

        protected virtual unsafe Vector3 AirMovementTick(Vector3 posDelta)
        {
            if (ActiveController is null || WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene is not { } scene)
                return Vector3.Zero;

            float dt = DeltaTime;

            // Process air movement through the module
            Vector3 planarInput = ProjectOntoPlane(posDelta, NormalizedUpDirection);
            Vector3 inputDirection = planarInput.LengthSquared() > 1e-8f
                ? Vector3.Normalize(planarInput)
                : Vector3.Zero;
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

            // Apply landing friction when hitting ground
            if (ActiveController.IsGrounded)
            {
                Vector3 up = NormalizedUpDirection;
                float frictionFactor = 1.0f - MovementModule.GroundFriction;
                float verticalSpeed = Vector3.Dot(newVelocity, up);
                Vector3 tangentialVelocity = newVelocity - up * verticalSpeed;
                newVelocity = tangentialVelocity * frictionFactor + up * verticalSpeed;
                Velocity = newVelocity;
                _subUpdateTick = GroundMovementTick;
            }

            return IsFinite(newVelocity) ? newVelocity : Vector3.Zero;
        }

        protected virtual unsafe Vector3 SwimmingMovementTick(Vector3 posDelta)
        {
            if (ActiveController is null || WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene is not { } scene)
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
                if (RotateGravityToMatchCharacterUp && ActiveController is not null)
                    gravity = Vector3.Transform(gravity, XRMath.RotationBetweenVectors(Globals.Up, ActiveController.UpDirection));
                // Reduced gravity underwater for buoyancy effect
                newVelocity += gravity * MovementModule.GravityScale * 0.25f * dt;
            }

            // Clamp speed to max speed
            ClampSpeed(ref newVelocity);

            // Update stored velocity
            Velocity = newVelocity;

            return IsFinite(newVelocity) ? newVelocity : Vector3.Zero;
        }

        private void ClampSpeed(ref Vector3 velocity)
        {
            Vector3 up = NormalizedUpDirection;
            float verticalSpeed = Vector3.Dot(velocity, up);
            Vector3 tangentialVelocity = velocity - up * verticalSpeed;
            float tangentialSpeed = tangentialVelocity.Length();
            if (tangentialSpeed > MovementModule.MaxSpeed)
                tangentialVelocity *= MovementModule.MaxSpeed / tangentialSpeed;
            velocity = tangentialVelocity + up * verticalSpeed;
        }

        private Vector3 NormalizedUpDirection
        {
            get
            {
                Vector3 up = ActiveController?.UpDirection ?? UpDirection;
                return IsFinite(up) && up.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(up)
                    : Globals.Up;
            }
        }

        private static Vector3 ProjectOntoPlane(in Vector3 value, in Vector3 planeNormal)
            => value - planeNormal * Vector3.Dot(value, planeNormal);

        private static bool IsFinite(in Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private void ApplyGravity(AbstractPhysicsScene scene, ref Vector3 delta)
        {
            Vector3 gravity = GravityOverride ?? scene.Gravity;
            if (RotateGravityToMatchCharacterUp && ActiveController is not null)
                gravity = Vector3.Transform(gravity, XRMath.RotationBetweenVectors(Globals.Up, ActiveController.UpDirection));
            // Apply gravity scale for snappier game feel
            delta += gravity * MovementModule.GravityScale * DeltaTime;
        }

        public void Jump(bool pressed)
        {
            if (pressed)
            {
                if (Interlocked.Exchange(ref _jumpHeld, 1) == 0)
                    QueueJumpPress();
            }
            else
            {
                Volatile.Write(ref _jumpHeld, 0);
            }
        }

        private void QueueJumpPress()
        {
            int current = Volatile.Read(ref _pendingJumpPresses);
            while (current < int.MaxValue)
            {
                int observed = Interlocked.CompareExchange(ref _pendingJumpPresses, current + 1, current);
                if (observed == current)
                    return;
                current = observed;
            }
            // Saturation is deterministic: existing press edges are preserved and
            // additional edges are ignored until the producer drains the counter.
        }

        private bool TryConsumeJumpPress()
        {
            int current = Volatile.Read(ref _pendingJumpPresses);
            while (current > 0)
            {
                int observed = Interlocked.CompareExchange(ref _pendingJumpPresses, current - 1, current);
                if (observed == current)
                    return true;
                current = observed;
            }
            return false;
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
