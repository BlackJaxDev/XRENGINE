using System.ComponentModel;
using System.Numerics;
using MagicPhysX;
using XREngine.Core.Attributes;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine;

namespace XREngine.Components.Physics
{
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Physics")]
    [DisplayName("Static Rigid Body")]
    [Description("Fixed collider that participates in the physics simulation without moving.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.StaticRigidBodyComponentEditor")]
    public class StaticRigidBodyComponent : PhysicsActorComponent
    {
        public RigidBodyTransform RigidBodyTransform => SceneNode.GetTransformAs<RigidBodyTransform>(true)!;

        private int _rigidBodyOwnershipSyncDepth;

        private IAbstractStaticRigidBody? _rigidBody;
        private AbstractPhysicsScene? _registeredPhysicsScene;
        private bool _autoCreateRigidBody = true;
        private bool _gravityEnabled = true;
        private bool _simulationEnabled = true;
        private bool _debugVisualization;
        private bool _sendSleepNotifies;
        private ushort _collisionGroup;
        private PhysicsGroupsMask _groupsMask = PhysicsGroupsMask.Empty;
        private byte _dominanceGroup;
        private byte _ownerClient;
        private string? _actorName;
        private AbstractPhysicsMaterial? _material;
        private IPhysicsGeometry? _geometry;
        private Vector3 _shapeOffsetTranslation = Vector3.Zero;
        private Quaternion _shapeOffsetRotation = Quaternion.Identity;
        private Vector3? _initialPosition;
        private Quaternion? _initialRotation;

        [Browsable(false)]
        public IAbstractStaticRigidBody? RigidBody
        {
            get => _rigidBody;
            set => SetField(ref _rigidBody, value);
        }

        internal void SetRigidBodyFromRigidBodyOwner(IAbstractStaticRigidBody? body)
        {
            try
            {
                _rigidBodyOwnershipSyncDepth++;
                RigidBody = body;
            }
            finally
            {
                _rigidBodyOwnershipSyncDepth--;
            }
        }

        [Category("Initialization")]
        [DisplayName("Auto Create Rigid Body")]
        [Description("Whether to auto-create the rigid body on world registration.")]
        public bool AutoCreateRigidBody
        {
            get => _autoCreateRigidBody;
            set => SetField(ref _autoCreateRigidBody, value);
        }

        [Category("Shape")]
        [DisplayName("Material")]
        [Description("The physics material defining friction and restitution.")]
        public AbstractPhysicsMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        [Category("Shape")]
        [DisplayName("Geometry")]
        [Description("The collision geometry shape.")]
        public IPhysicsGeometry? Geometry
        {
            get => _geometry;
            set => SetField(ref _geometry, value);
        }

        [Category("Shape")]
        [DisplayName("Shape Offset Translation")]
        [Description("Local translation offset for the collision shape.")]
        public Vector3 ShapeOffsetTranslation
        {
            get => _shapeOffsetTranslation;
            set => SetField(ref _shapeOffsetTranslation, value);
        }

        [Category("Shape")]
        [DisplayName("Shape Offset Rotation")]
        [Description("Local rotation offset for the collision shape.")]
        public Quaternion ShapeOffsetRotation
        {
            get => _shapeOffsetRotation;
            set => SetField(ref _shapeOffsetRotation, value);
        }

        [Category("Initialization")]
        [DisplayName("Initial Position")]
        [Description("Override spawn position for the rigid body.")]
        public Vector3? InitialPosition
        {
            get => _initialPosition;
            set => SetField(ref _initialPosition, value);
        }

        [Category("Initialization")]
        [DisplayName("Initial Rotation")]
        [Description("Override spawn rotation for the rigid body.")]
        public Quaternion? InitialRotation
        {
            get => _initialRotation;
            set => SetField(ref _initialRotation, value);
        }

        [Category("Forces")]
        [DisplayName("Gravity Enabled")]
        [Description("Whether gravity affects this body (typically false for static).")]
        public bool GravityEnabled
        {
            get => RigidBody is PhysxActor actor ? actor.GravityEnabled : _gravityEnabled;
            set
            {
                if (!SetField(ref _gravityEnabled, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.GravityEnabled = value;
            }
        }

        [Category("Simulation")]
        [DisplayName("Simulation Enabled")]
        [Description("Whether the physics simulation is enabled.")]
        public bool SimulationEnabled
        {
            get => RigidBody is PhysxActor actor ? actor.SimulationEnabled : _simulationEnabled;
            set
            {
                if (!SetField(ref _simulationEnabled, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.SimulationEnabled = value;
            }
        }

        [Category("Debug")]
        [DisplayName("Debug Visualization")]
        [Description("Show physics debug visualization.")]
        public bool DebugVisualization
        {
            get => RigidBody is PhysxActor actor ? actor.DebugVisualize : _debugVisualization;
            set
            {
                if (!SetField(ref _debugVisualization, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.DebugVisualize = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Send Sleep Notifies")]
        [Description("Whether to notify when sleep state changes.")]
        public bool SendSleepNotifies
        {
            get => RigidBody is PhysxActor actor ? actor.SendSleepNotifies : _sendSleepNotifies;
            set
            {
                if (!SetField(ref _sendSleepNotifies, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.SendSleepNotifies = value;
            }
        }

        [Category("Collision")]
        [DisplayName("Collision Group")]
        [Description("The collision group this body belongs to.")]
        public ushort CollisionGroup
        {
            get => RigidBody is PhysxActor actor ? actor.CollisionGroup : _collisionGroup;
            set
            {
                if (!SetField(ref _collisionGroup, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.CollisionGroup = value;
            }
        }

        [Category("Collision")]
        [DisplayName("Groups Mask")]
        [Description("Collision filter mask for collision filtering.")]
        public PhysicsGroupsMask GroupsMask
        {
            get => RigidBody is PhysxActor actor ? FromPhysxGroupsMask(actor.GroupsMask) : _groupsMask;
            set
            {
                if (!SetField(ref _groupsMask, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.GroupsMask = ToPhysxGroupsMask(value);
            }
        }

        [Category("Collision")]
        [DisplayName("Dominance Group")]
        [Description("Collision resolution dominance (higher wins).")]
        public byte DominanceGroup
        {
            get => RigidBody is PhysxActor actor ? actor.DominanceGroup : _dominanceGroup;
            set
            {
                if (!SetField(ref _dominanceGroup, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.DominanceGroup = value;
            }
        }

        [Category("Networking")]
        [DisplayName("Owner Client")]
        [Description("Client ID that owns this body.")]
        public byte OwnerClient
        {
            get => RigidBody is PhysxActor actor ? actor.OwnerClient : _ownerClient;
            set
            {
                if (!SetField(ref _ownerClient, value))
                    return;
                if (RigidBody is PhysxActor physx)
                {
                    // PhysX disallows changing ownerClient while the actor is already in a scene.
                    if (physx.Scene is null)
                        physx.OwnerClient = value;
                }
            }
        }

        [Category("Debug")]
        [DisplayName("Actor Name")]
        [Description("Debug name for the physics actor.")]
        public string? ActorName
        {
            get => RigidBody is PhysxActor actor ? actor.Name : _actorName;
            set
            {
                if (!SetField(ref _actorName, value))
                    return;
                if (RigidBody is PhysxActor physx)
                    physx.Name = value ?? string.Empty;
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureRigidBodyConstructed();
            ApplyCachedProperties();
            TryRegisterRigidBodyWithScene();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            RemoveRigidBodyFromScene();
        }

        private void EnsureRigidBodyConstructed()
        {
            if (!AutoCreateRigidBody || RigidBody is not null || World?.PhysicsScene is null)
                return;

            RigidBody = World.PhysicsScene switch
            {
                PhysxScene => CreatePhysxStaticRigidBody(),
                _ => null
            };
        }

        private IAbstractStaticRigidBody? CreatePhysxStaticRigidBody()
        {
            var (position, rotation) = GetSpawnPose();
            var geometry = Geometry;
            if (geometry is not null)
            {
                var mat = ResolvePhysxMaterial();
                if (mat is null)
                    return null;
                var created = new PhysxStaticRigidBody(
                    mat,
                    geometry,
                    position,
                    rotation,
                    ShapeOffsetTranslation,
                    ShapeOffsetRotation);
                ApplyCachedProperties(created);
                return created;
            }

            var body = new PhysxStaticRigidBody(position, rotation);
            ApplyCachedProperties(body);
            return body;
        }

        private PhysxMaterial? ResolvePhysxMaterial()
        {
            if (Material is PhysxMaterial physxMaterial)
                return physxMaterial;

            var created = new PhysxMaterial(0.5f, 0.5f, 0.1f);
            Material = created;
            return created;
        }

        private (Vector3 position, Quaternion rotation) GetSpawnPose()
        {
            if (InitialPosition.HasValue || InitialRotation.HasValue)
                return (InitialPosition ?? Transform.WorldTranslation, InitialRotation ?? Transform.WorldRotation);

            var matrix = Transform.WorldMatrix;
            Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 translation);
            return (translation, rotation);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change && propName == nameof(RigidBody) && RigidBody is not null)
            {
                RemoveRigidBodyFromScene();

                if (_rigidBodyOwnershipSyncDepth == 0)
                {
                    if (RigidBody.OwningComponent == this)
                        RigidBody.OwningComponent = null;
                }

                if (RigidBodyTransform.RigidBody == RigidBody)
                    RigidBodyTransform.RigidBody = null;
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(RigidBody) && RigidBody is not null)
            {
                if (_rigidBodyOwnershipSyncDepth == 0)
                    RigidBody.OwningComponent = this;
                RigidBodyTransform.RigidBody = RigidBody;
                ApplyCachedProperties();
                TryRegisterRigidBodyWithScene();
            }
        }

        private void ApplyCachedProperties()
        {
            if (RigidBody is not PhysxActor actor)
                return;

            ApplyCachedProperties(actor);
        }

        private void ApplyCachedProperties(PhysxActor actor)
        {
            actor.GravityEnabled = _gravityEnabled;
            actor.SimulationEnabled = _simulationEnabled;
            actor.DebugVisualize = _debugVisualization;
            actor.SendSleepNotifies = _sendSleepNotifies;
            actor.CollisionGroup = _collisionGroup;
            actor.GroupsMask = ToPhysxGroupsMask(_groupsMask);
            actor.DominanceGroup = _dominanceGroup;
            // PhysX disallows setting ownerClient once the actor is inserted into a scene.
            if (actor.Scene is null)
                actor.OwnerClient = _ownerClient;
            if (_actorName is not null)
                actor.Name = _actorName;

            Debug.Physics(
                "[StaticRigidBodyComponent] Applied cached props to {0} actorType={1} group={2} mask={3}",
                SceneNode?.Name ?? "<unnamed>",
                actor.GetType().Name,
                actor.CollisionGroup,
                FormatGroupsMask(actor.GroupsMask));
        }

        private static PhysicsGroupsMask FromPhysxGroupsMask(PxGroupsMask mask)
            => new(mask.bits0, mask.bits1, mask.bits2, mask.bits3);

        private static PxGroupsMask ToPhysxGroupsMask(PhysicsGroupsMask mask)
        {
            PxGroupsMask m;
            m.bits0 = (ushort)mask.Word0;
            m.bits1 = (ushort)mask.Word1;
            m.bits2 = (ushort)mask.Word2;
            m.bits3 = (ushort)mask.Word3;
            return m;
        }

        private void TryRegisterRigidBodyWithScene()
        {
            if (!IsActive || RigidBody is null)
                return;

            var scene = World?.PhysicsScene;
            if (scene is null)
                return;

            if (_registeredPhysicsScene == scene)
                return;

            if (_registeredPhysicsScene is not null && _registeredPhysicsScene != scene)
                _registeredPhysicsScene.RemoveActor(RigidBody);

            scene.AddActor(RigidBody);
            _registeredPhysicsScene = scene;
            if (RigidBody is PhysxActor actor)
            {
                Debug.Physics(
                    "[StaticRigidBodyComponent] Registered actorType={0} with scene {1} (group={2}, mask={3})",
                    actor.GetType().Name,
                    scene.GetType().Name,
                    actor.CollisionGroup,
                    FormatGroupsMask(actor.GroupsMask));
            }
        }

        private void RemoveRigidBodyFromScene()
        {
            if (RigidBody is null)
            {
                _registeredPhysicsScene = null;
                return;
            }

            if (_registeredPhysicsScene is not null)
            {
                _registeredPhysicsScene.RemoveActor(RigidBody);
                if (RigidBody is PhysxActor actor)
                {
                    Debug.Physics(
                        "[StaticRigidBodyComponent] Removed actorType={0} from scene {1}",
                        actor.GetType().Name,
                        _registeredPhysicsScene.GetType().Name);
                }
                _registeredPhysicsScene = null;
            }
        }

        private static string FormatGroupsMask(PxGroupsMask mask)
            => $"{mask.bits0:X4}:{mask.bits1:X4}:{mask.bits2:X4}:{mask.bits3:X4}";
    }
}
