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
        private PhysicsMaterialDefinition? _materialDefinition;
        private PhysicsReplicationAuthority _replicationAuthority = PhysicsReplicationAuthority.LocalSimulation;
        private NetworkEntityId _networkEntityId;
        private string? _ownerClientId;
        private int _ownerServerPlayerIndex = -1;
        private Vector3 _upDirection = Globals.Up;
        private float _radius = 0.6f;
        private float _height = 1.5748f;
        private float _contactOffset = 0.001f;
        private float _stepOffset = 0.3f;
        private float _slopeLimit = 0.70710677f;
        private float _density = 1.0f;

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
                if (SetField(ref _upDirection, value) && Controller is not null)
                    Controller.UpDirection = value;
            }
        }

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
                if (SetField(ref _radius, value) && Controller is not null)
                    Controller.Radius = value;
            }
        }

        [Category("Controller")]
        public float Height
        {
            get => _height;
            set
            {
                if (SetField(ref _height, value))
                    Controller?.Resize(value);
            }
        }

        [Category("Controller")]
        public float ContactOffset
        {
            get => _contactOffset;
            set
            {
                if (SetField(ref _contactOffset, value) && Controller is not null)
                    Controller.ContactOffset = value;
            }
        }

        [Category("Controller")]
        public float StepOffset
        {
            get => _stepOffset;
            set
            {
                if (SetField(ref _stepOffset, value) && Controller is not null)
                    Controller.StepOffset = value;
            }
        }

        [Category("Controller")]
        public float SlopeLimit
        {
            get => _slopeLimit;
            set
            {
                if (SetField(ref _slopeLimit, value) && Controller is not null)
                    Controller.SlopeLimit = value;
            }
        }

        [Category("Controller")]
        public float Density
        {
            get => _density;
            set => SetField(ref _density, value);
        }

        public void Move(Vector3 delta, float minDist, float elapsedTime)
            => Controller?.Move(delta, minDist, elapsedTime);

        public void Resize(float height)
        {
            Height = height;
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

            IAbstractCharacterController? controller = physicsScene.BackendService.CreateCharacterController(
                new PhysicsCharacterControllerCreateInfo(
                    position,
                    UpDirection,
                    Radius,
                    Height,
                    SlopeLimit,
                    ContactOffset,
                    StepOffset,
                    Density,
                    MaterialDefinition));
            if (controller is not null)
                Engine.EnqueueUpdateThreadTask(() => BindController(physicsScene, controller, version));
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
            BindRigidBodyTransform(controller);
            ControllerCreated?.Invoke(this, controller);
        }

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
            if (up != _wasCollidingUp || down != _wasCollidingDown)
            {
                _wasCollidingUp = up;
                _wasCollidingDown = down;
                ContactStateChanged?.Invoke(this, new CharacterControllerContactState(up, down));
            }
        }

    }

    public readonly record struct CharacterControllerContactState(bool CollidingUp, bool CollidingDown);
}
