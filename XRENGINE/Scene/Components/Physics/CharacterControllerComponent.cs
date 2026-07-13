using System.ComponentModel;
using System.Numerics;
using System.Threading;
using XREngine;
using XREngine.Core.Attributes;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Physics
{
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Physics")]
    [DisplayName("Character Controller")]
    [Description("Reusable backend-neutral capsule character controller owner for movement and gameplay components.")]
    public class CharacterControllerComponent : XRComponent, IPhysicsReplicationTarget
    {
        private int _initVersion;
        private IAbstractCharacterController? _controller;
        private AbstractPhysicsScene? _subscribedPhysicsScene;
        private bool _wasCollidingUp;
        private bool _wasCollidingDown;
        private bool _wasCollidingSides;
        private CharacterSupportState _previousSupportState = CharacterSupportState.Unknown;
        private PhysicsMaterialDefinition? _materialDefinition;
        private PhysicsReplicationAuthority _replicationAuthority = PhysicsReplicationAuthority.LocalSimulation;
        private NetworkEntityId _networkEntityId;
        private string? _ownerClientId;
        private int _ownerServerPlayerIndex = -1;
        private Vector3 _upDirection = Globals.Up;
        private float _radius = 0.3f;
        private float _totalHeight = 1.5748f;
        private float _contactOffset = 0.02f;
        private float _stepOffset = 0.3f;
        private float _slopeLimit = 0.70710677f;
        private float _density = 1.0f;
        private CharacterMotionInputModel _motionInputModel = CharacterMotionInputModel.Velocity;
        private LayerMask _collisionLayerMask = LayerMask.Everything;
        private float _predictiveContactDistance = 0.1f;
        private float _collisionTolerance = 0.001f;
        private float _stickToFloorDistance = 0.1f;
        private float _stepDownExtra;
        private float _maxStrength = 100.0f;
        private bool _slideOnSteepSlopes = true;

        public event Action<CharacterControllerComponent, IAbstractCharacterController>? ControllerCreated;
        public event Action<CharacterControllerComponent>? ControllerReleased;
        public event Action<CharacterControllerComponent, CharacterControllerContactState>? ContactStateChanged;

        [Browsable(false)]
        public IAbstractCharacterController? Controller
        {
            get => _controller;
            private set => SetField(ref _controller, value);
        }

        [Category("Controller")]
        public PhysicsMaterialDefinition? MaterialDefinition
        {
            get => _materialDefinition;
            set => SetField(ref _materialDefinition, value);
        }

        [Category("Controller")]
        public Vector3 UpDirection
        {
            get => _upDirection;
            set
            {
                if (!IsFinite(value) || value.LengthSquared() < 1e-8f)
                    return;
                Vector3 normalized = Vector3.Normalize(value);
                if (SetField(ref _upDirection, normalized) && Controller is not null)
                    Controller.UpDirection = normalized;
            }
        }

        [Browsable(false)]
        public PhysicsCharacterControllerCapabilities BackendCapabilities
            => Controller?.Capabilities ?? PhysicsCharacterControllerCapabilities.None;

        [Category("Networking")]
        public PhysicsReplicationAuthority ReplicationAuthority
        {
            get => _replicationAuthority;
            set => SetField(ref _replicationAuthority, value);
        }

        [Category("Networking")]
        public NetworkEntityId NetworkEntityId
        {
            get => _networkEntityId;
            set => SetField(ref _networkEntityId, value);
        }

        [Category("Networking")]
        public string? OwnerClientId
        {
            get => _ownerClientId;
            set => SetField(ref _ownerClientId, value);
        }

        [Category("Networking")]
        public int OwnerServerPlayerIndex
        {
            get => _ownerServerPlayerIndex;
            set => SetField(ref _ownerServerPlayerIndex, value);
        }

        [Category("Controller")]
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

                SetField(ref _totalHeight, MathF.Max(_totalHeight, 2.0f * sanitized), nameof(TotalHeight));
                if (Controller is not null)
                {
                    Controller.Radius = sanitized;
                    Controller.Resize(_totalHeight);
                }
            }
        }

        [Category("Controller")]
        [DisplayName("Total Height")]
        [Description("Total capsule height from bottom to top, including both hemispheres.")]
        public float TotalHeight
        {
            get => _totalHeight;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(2.0f * Radius, value);
                if (SetField(ref _totalHeight, sanitized))
                    Controller?.Resize(sanitized);
            }
        }

        [Category("Controller")]
        public CharacterMotionInputModel MotionInputModel
        {
            get => _motionInputModel;
            set
            {
                if (SetField(ref _motionInputModel, value) && Controller is not null)
                    Controller.MotionInputModel = value;
            }
        }

        [Category("Controller")]
        public LayerMask CollisionLayerMask
        {
            get => _collisionLayerMask;
            set
            {
                if (SetField(ref _collisionLayerMask, value)
                    && Controller is ICharacterControllerCollisionSettings collisionSettings)
                    collisionSettings.CollisionLayerMask = value;
            }
        }

        [Category("Controller")]
        [Description("Whether velocity into a non-walkable slope is allowed to resolve into downhill sliding.")]
        public bool SlideOnSteepSlopes
        {
            get => _slideOnSteepSlopes;
            set
            {
                if (SetField(ref _slideOnSteepSlopes, value)
                    && Controller is ICharacterControllerCollisionSettings collisionSettings)
                    collisionSettings.SlideOnSteepSlopes = value;
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
                    && Controller is IAdvancedCharacterControllerSettings advanced)
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
                    && Controller is IAdvancedCharacterControllerSettings advanced)
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
                    && Controller is IAdvancedCharacterControllerSettings advanced)
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
                    && Controller is IAdvancedCharacterControllerSettings advanced)
                    advanced.StepDownExtra = sanitized;
            }
        }

        [Category("Controller")]
        public float MaxStrength
        {
            get => _maxStrength;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _maxStrength, sanitized)
                    && Controller is IAdvancedCharacterControllerSettings advanced)
                    advanced.MaxStrength = sanitized;
            }
        }

        [Category("Controller")]
        public float ContactOffset
        {
            get => _contactOffset;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _contactOffset, sanitized) && Controller is not null)
                    Controller.ContactOffset = sanitized;
            }
        }

        [Category("Controller")]
        public float StepOffset
        {
            get => _stepOffset;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = MathF.Max(0.0f, value);
                if (SetField(ref _stepOffset, sanitized) && Controller is not null)
                    Controller.StepOffset = sanitized;
            }
        }

        [Category("Controller")]
        public float SlopeLimit
        {
            get => _slopeLimit;
            set
            {
                if (!float.IsFinite(value))
                    return;
                float sanitized = Math.Clamp(value, -1.0f, 1.0f);
                if (SetField(ref _slopeLimit, sanitized) && Controller is not null)
                    Controller.SlopeLimit = sanitized;
            }
        }

        [Category("Controller")]
        public float Density
        {
            get => _density;
            set
            {
                if (float.IsFinite(value))
                    SetField(ref _density, MathF.Max(0.0f, value));
            }
        }

        public void Move(Vector3 delta, float minDist, float elapsedTime)
            => Controller?.Move(delta, minDist, elapsedTime);

        public void SubmitMotion(in CharacterMotionCommand command)
            => Controller?.SubmitMotion(command);

        public void Resize(float height)
        {
            TotalHeight = height;
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            var physicsScene = WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene;
            if (physicsScene is null)
                return;

            physicsScene.OnSimulationStep += OnPhysicsSimulationStep;
            _subscribedPhysicsScene = physicsScene;

            QueueControllerCreation(physicsScene, Transform.WorldTranslation);
        }

        protected override void OnComponentDeactivated()
        {
            Interlocked.Increment(ref _initVersion);
            _subscribedPhysicsScene?.OnSimulationStep -= OnPhysicsSimulationStep;
            _subscribedPhysicsScene = null;

            var rigidBodyTransform = SceneNode?.GetTransformAs<RigidBodyTransform>(false);
            if (rigidBodyTransform is not null)
                rigidBodyTransform.RigidBody = null;
            Controller?.RequestRelease();
            if (Controller is not null)
                ControllerReleased?.Invoke(this);
            Controller = null;
            base.OnComponentDeactivated();
        }

        private void QueueControllerCreation(AbstractPhysicsScene physicsScene, Vector3 position)
        {
            int version = Interlocked.Increment(ref _initVersion);
            Engine.EnqueuePhysicsThreadTask(() => CreateController(physicsScene, position, version));
        }

        private void CreateController(AbstractPhysicsScene physicsScene, Vector3 position, int version)
        {
            if (version != Volatile.Read(ref _initVersion) || !IsActiveInHierarchy)
                return;

            ReportUnsupportedSettings(physicsScene.BackendService.CharacterControllerCapabilities);
            IAbstractCharacterController? controller = physicsScene.BackendService.CreateCharacterController(
                new PhysicsCharacterControllerCreateInfo(
                    position,
                    UpDirection,
                    Radius,
                    TotalHeight,
                    SlopeLimit,
                    ContactOffset,
                    StepOffset,
                    Density,
                    MaterialDefinition)
                {
                    MotionInputModel = MotionInputModel,
                    CollisionLayerMask = CollisionLayerMask,
                    PredictiveContactDistance = PredictiveContactDistance,
                    CollisionTolerance = CollisionTolerance,
                    StickToFloorDistance = StickToFloorDistance,
                    StepDownExtra = StepDownExtra,
                    MaxStrength = MaxStrength,
                    SlideOnSteepSlopes = SlideOnSteepSlopes,
                });
            if (controller is not null)
                Engine.EnqueueUpdateThreadTask(() => BindController(physicsScene, controller, version));
        }

        private void ReportUnsupportedSettings(PhysicsCharacterControllerCapabilities capabilities)
        {
            string controllerName = SceneNode?.Name ?? "<unnamed>";
            if (MaterialDefinition is not null
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.Materials))
                Debug.LogWarning($"[CharacterController] '{controllerName}' material is not supported by the active physics backend.");
            if (MathF.Abs(PredictiveContactDistance - 0.1f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.PredictiveContacts))
                Debug.LogWarning($"[CharacterController] '{controllerName}' predictive contact distance is not supported by the active physics backend.");
            if (MathF.Abs(CollisionTolerance - 0.001f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.IndependentCollisionTolerance))
                Debug.LogWarning($"[CharacterController] '{controllerName}' independent collision tolerance is not supported by the active physics backend.");
            if (MathF.Abs(StickToFloorDistance - 0.1f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.FloorStickDistance))
                Debug.LogWarning($"[CharacterController] '{controllerName}' floor-stick distance is not supported by the active physics backend.");
            if (StepDownExtra > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.IndependentStepDown))
                Debug.LogWarning($"[CharacterController] '{controllerName}' independent step-down distance is not supported by the active physics backend.");
            if (MathF.Abs(MaxStrength - 100.0f) > 1e-6f
                && !capabilities.HasFlag(PhysicsCharacterControllerCapabilities.MaximumStrength))
                Debug.LogWarning($"[CharacterController] '{controllerName}' maximum strength is not supported by the active physics backend.");
        }

        private void BindController(
            AbstractPhysicsScene physicsScene,
            IAbstractCharacterController controller,
            int version)
        {
            if (version != Volatile.Read(ref _initVersion)
                || !IsActiveInHierarchy
                || WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene != physicsScene)
            {
                controller.RequestRelease();
                return;
            }

            Controller = controller;
            if (controller is ICharacterControllerCollisionSettings collisionSettings)
            {
                collisionSettings.CollisionLayerMask = CollisionLayerMask;
                collisionSettings.SlideOnSteepSlopes = SlideOnSteepSlopes;
            }
            BindRigidBodyTransform(controller);
            ControllerCreated?.Invoke(this, controller);
        }

        private static bool IsFinite(in Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private void BindRigidBodyTransform(IAbstractRigidPhysicsActor proxy)
        {
            var rigidBodyTransform = SceneNode.GetTransformAs<RigidBodyTransform>(true)!;
            rigidBodyTransform.PostRotationOffset = Quaternion.Identity;
            rigidBodyTransform.RigidBody = proxy;
            rigidBodyTransform.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;
            rigidBodyTransform.OnPhysicsStepped();
        }

        private void OnPhysicsSimulationStep()
        {
            Controller?.Synchronize();
            SceneNode?.GetTransformAs<RigidBodyTransform>(false)?.OnPhysicsStepped();

            if (Controller is null)
                return;

            bool up = Controller.CollidingUp;
            bool down = Controller.CollidingDown;
            bool sides = Controller.CollidingSides;
            CharacterSupportState supportState = Controller.SupportState;
            if (up != _wasCollidingUp
                || down != _wasCollidingDown
                || sides != _wasCollidingSides
                || supportState != _previousSupportState)
            {
                _wasCollidingUp = up;
                _wasCollidingDown = down;
                _wasCollidingSides = sides;
                _previousSupportState = supportState;
                ContactStateChanged?.Invoke(
                    this,
                    new CharacterControllerContactState(up, down, sides, supportState));
            }
        }

    }

    public readonly record struct CharacterControllerContactState(
        bool CollidingUp,
        bool CollidingDown,
        bool CollidingSides,
        CharacterSupportState SupportState);
}
