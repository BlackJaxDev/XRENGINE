using System.ComponentModel;
using System.Numerics;
using System.Threading;
using MagicPhysX;
using XREngine;
using XREngine.Core.Attributes;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Physics
{
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Physics")]
    [DisplayName("Character Controller")]
    [Description("Reusable backend-neutral capsule character controller owner for movement and gameplay components.")]
    public class CharacterControllerComponent : XRComponent
    {
        private sealed class PhysxAdapter(PhysxCapsuleController controller) : IAbstractCharacterController
        {
            public PhysxCapsuleController Controller { get; } = controller;
            public Vector3 Position { get => Controller.Position; set => Controller.Position = value; }
            public Vector3 FootPosition { get => Controller.FootPosition; set => Controller.FootPosition = value; }
            public Vector3 UpDirection { get => Controller.UpDirection; set => Controller.UpDirection = value; }
            public float Radius { get => Controller.Radius; set => Controller.Radius = value; }
            public float Height => Controller.Height;
            public float SlopeLimit { get => Controller.SlopeLimit; set => Controller.SlopeLimit = value; }
            public float StepOffset { get => Controller.StepOffset; set => Controller.StepOffset = value; }
            public float ContactOffset { get => Controller.ContactOffset; set => Controller.ContactOffset = value; }
            public bool CollidingUp => Controller.CollidingUp;
            public bool CollidingDown => Controller.CollidingDown;
            public Vector3 LinearVelocity => Controller.Actor?.LinearVelocity ?? Vector3.Zero;
            public Vector3 AngularVelocity => Controller.Actor?.AngularVelocity ?? Vector3.Zero;
            public bool IsSleeping => Controller.Actor?.IsSleeping ?? false;
            public (Vector3 position, Quaternion rotation) Transform => Controller.Actor?.Transform ?? (Position, Quaternion.Identity);
            public void Move(Vector3 delta, float minDist, float elapsedTime) => Controller.Move(delta, minDist, elapsedTime);
            public void Resize(float height) => Controller.Resize(height);
            public void Destroy(bool wakeOnLostTouch = false) => RequestRelease();
            public void RequestRelease() => Controller.RequestRelease();
        }

        private int _initVersion;
        private IAbstractCharacterController? _controller;
        private IAbstractRigidPhysicsActor? _controllerActorProxy;
        private PhysxCapsuleController? _physxController;
        private IJoltCharacterController? _joltController;
        private AbstractPhysicsScene? _subscribedPhysicsScene;
        private bool _wasCollidingUp;
        private bool _wasCollidingDown;
        private PhysicsMaterialDefinition? _materialDefinition;
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
            set => SetField(ref _upDirection, value);
        }

        [Category("Controller")]
        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        [Category("Controller")]
        public float Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }

        [Category("Controller")]
        public float ContactOffset
        {
            get => _contactOffset;
            set => SetField(ref _contactOffset, value);
        }

        [Category("Controller")]
        public float StepOffset
        {
            get => _stepOffset;
            set => SetField(ref _stepOffset, value);
        }

        [Category("Controller")]
        public float SlopeLimit
        {
            get => _slopeLimit;
            set => SetField(ref _slopeLimit, value);
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
            Controller?.Resize(height);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            var physicsScene = WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene;
            if (physicsScene is null)
                return;

            physicsScene.OnSimulationStep += OnPhysicsSimulationStep;
            _subscribedPhysicsScene = physicsScene;

            Vector3 position = Transform.WorldTranslation;
            if (physicsScene is PhysxScene physxScene)
                QueuePhysxControllerCreation(physxScene, position);
            else if (physicsScene is JoltScene joltScene)
                BindJoltController(new JoltCharacterVirtualController(joltScene, position));
        }

        protected override void OnComponentDeactivated()
        {
            Interlocked.Increment(ref _initVersion);
            _subscribedPhysicsScene?.OnSimulationStep -= OnPhysicsSimulationStep;
            _subscribedPhysicsScene = null;

            var rigidBodyTransform = SceneNode?.GetTransformAs<RigidBodyTransform>(false);
            if (rigidBodyTransform is not null)
                rigidBodyTransform.RigidBody = null;
            _controllerActorProxy = null;
            _physxController?.RequestRelease();
            _physxController = null;
            _joltController?.RequestRelease();
            _joltController = null;
            if (Controller is not null)
                ControllerReleased?.Invoke(this);
            Controller = null;
            base.OnComponentDeactivated();
        }

        private void QueuePhysxControllerCreation(PhysxScene physxScene, Vector3 position)
        {
            int version = Interlocked.Increment(ref _initVersion);
            Engine.EnqueuePhysicsThreadTask(() => CreatePhysxController(physxScene, position, version));
        }

        private unsafe void CreatePhysxController(PhysxScene physxScene, Vector3 position, int version)
        {
            if (version != Volatile.Read(ref _initVersion) || !IsActiveInHierarchy)
                return;

            var material = ResolvePhysxMaterial();
            ControllerManager manager = physxScene.GetOrCreateControllerManager();
            PhysxCapsuleController controller = manager.CreateCapsuleController(
                position,
                UpDirection,
                SlopeLimit,
                0.0f,
                1.0f,
                ContactOffset,
                StepOffset,
                Density,
                0.8f,
                1.5f,
                PxControllerNonWalkableMode.PreventClimbing,
                material,
                0,
                null,
                Radius,
                Height,
                PxCapsuleClimbingMode.Easy);
            var proxy = new PhysxControllerActorProxy(controller.ControllerPtr);
            Engine.EnqueueUpdateThreadTask(() => BindPhysxController(physxScene, controller, proxy, version));
        }

        private void BindPhysxController(PhysxScene physxScene, PhysxCapsuleController controller, PhysxControllerActorProxy proxy, int version)
        {
            if (version != Volatile.Read(ref _initVersion) || !IsActiveInHierarchy || WorldAs<XREngine.Rendering.XRWorldInstance>()?.PhysicsScene != physxScene)
            {
                controller.RequestRelease();
                return;
            }

            _physxController = controller;
            _controllerActorProxy = proxy;
            Controller = new PhysxAdapter(controller);
            BindRigidBodyTransform(proxy);
            ControllerCreated?.Invoke(this, Controller);
        }

        private void BindJoltController(IJoltCharacterController controller)
        {
            controller.Radius = Radius;
            controller.Height = Height;
            controller.ContactOffset = ContactOffset;
            controller.StepOffset = StepOffset;
            controller.SlopeLimit = SlopeLimit;
            controller.UpDirection = UpDirection;
            _joltController = controller;
            _controllerActorProxy = controller;
            Controller = controller;
            BindRigidBodyTransform(controller);
            ControllerCreated?.Invoke(this, Controller);
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
            (_controllerActorProxy as PhysxControllerActorProxy)?.RefreshFromNative();
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

        private PhysxMaterial ResolvePhysxMaterial()
        {
            var definition = MaterialDefinition;
            return definition is null
                ? new PhysxMaterial(0.5f, 0.5f, 0.1f)
                : new PhysxMaterial(definition.StaticFriction, definition.DynamicFriction, definition.Restitution)
                {
                    Damping = definition.Damping,
                };
        }
    }

    public readonly record struct CharacterControllerContactState(bool CollidingUp, bool CollidingDown);
}
