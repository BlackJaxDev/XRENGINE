using System.ComponentModel;
using System.Numerics;
using MagicPhysX;
using XREngine.Core.Attributes;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Transforms;
using XREngine;

namespace XREngine.Components.Physics
{
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Physics")]
    [DisplayName("Dynamic Rigid Body")]
    [Description("Simulated rigid body that responds to forces, collisions, and networking state.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.DynamicRigidBodyComponentEditor")]
    public class DynamicRigidBodyComponent : PhysicsActorComponent
    {
        private const float DefaultDensity = 1.0f;
        private const float DefaultLinearDamping = 0.05f;
        private const float DefaultAngularDamping = 0.05f;

        private int _rigidBodyOwnershipSyncDepth;

        public RigidBodyTransform RigidBodyTransform => SceneNode.GetTransformAs<RigidBodyTransform>(true)!;

        private IAbstractDynamicRigidBody? _rigidBody;
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
        private float _density = DefaultDensity;
        private Vector3 _initialPosition = Vector3.Zero;
        private Quaternion _initialRotation = Quaternion.Identity;
        private PhysicsRigidBodyFlags _bodyFlags = PhysicsRigidBodyFlags.None;
        private PhysicsLockFlags _lockFlags = PhysicsLockFlags.None;
        private float _linearDamping = DefaultLinearDamping;
        private float _angularDamping = DefaultAngularDamping;
        private float _maxLinearVelocity = 100.0f;
        private float _maxAngularVelocity = 100.0f;
        private float _mass = 1.0f;
        private Vector3 _massSpaceInertiaTensor = Vector3.One;
        private PhysicsMassFrame _centerOfMassPose = PhysicsMassFrame.Identity;
        private float _minCcdAdvanceCoefficient = 0.15f;
        private float _maxDepenetrationVelocity = 10.0f;
        private float _maxContactImpulse;
        private float _contactSlopCoefficient;
        private float _stabilizationThreshold;
        private float _sleepThreshold = 0.005f;
        private float _contactReportThreshold;
        private float _wakeCounter = 0.1f;
        private PhysicsSolverIterations _solverIterations = PhysicsSolverIterations.Default;
        private (Vector3 position, Quaternion rotation)? _kinematicTarget;
        private Vector3 _cachedLinearVelocity = Vector3.Zero;
        private Vector3 _cachedAngularVelocity = Vector3.Zero;

        /// <summary>
        /// The rigid body constructed for whatever physics engine to use.
        /// </summary>
        [Browsable(false)]
        [RuntimeOnly]
        public IAbstractDynamicRigidBody? RigidBody
        {
            get => _rigidBody;
            set => SetField(ref _rigidBody, value);
        }

        internal void SetRigidBodyFromRigidBodyOwner(IAbstractDynamicRigidBody? body)
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

        [Category("Mass")]
        [DisplayName("Density")]
        [Description("Density used to calculate mass from geometry volume.")]
        public float Density
        {
            get => _density;
            set => SetField(ref _density, value);
        }

/*
        [Browsable(false)]
        [Category("Initialization")]
        [DisplayName("Initial Position")]
        [Description("Override spawn position for the rigid body.")]
        internal Vector3 InitialPosition
        {
            get => _initialPosition;
            set => SetField(ref _initialPosition, value);
        }

        [Browsable(false)]
        [Category("Initialization")]
        [DisplayName("Initial Rotation")]
        [Description("Override spawn rotation for the rigid body.")]
        internal Quaternion InitialRotation
        {
            get => _initialRotation;
            set => SetField(ref _initialRotation, value);
        }
        */

        [Category("Forces")]
        [DisplayName("Gravity Enabled")]
        [Description("Whether gravity affects this body.")]
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

        [Category("Flags")]
        [DisplayName("Body Flags")]
        [Description("Rigid body behavior flags (kinematic, CCD, etc).")]
        public PhysicsRigidBodyFlags BodyFlags
        {
            get => RigidBody switch
            {
                PhysxDynamicRigidBody physx => FromPhysxRigidBodyFlags(physx.Flags),
                _ => _bodyFlags
            };
            set
            {
                if (!SetField(ref _bodyFlags, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.Flags = ToPhysxRigidBodyFlags(value);
            }
        }

        [Category("Flags")]
        [DisplayName("Lock Flags")]
        [Description("Axis lock flags for constrained motion.")]
        public PhysicsLockFlags LockFlags
        {
            get => RigidBody switch
            {
                PhysxDynamicRigidBody physx => FromPhysxLockFlags(physx.LockFlags),
                _ => _lockFlags
            };
            set
            {
                if (!SetField(ref _lockFlags, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.LockFlags = ToPhysxLockFlags(value);
            }
        }

        [Category("Damping")]
        [DisplayName("Linear Damping")]
        [Description("Damping factor for linear velocity.")]
        public float LinearDamping
        {
            get => RigidBody is PhysxRigidBody physx ? physx.LinearDamping : _linearDamping;
            set
            {
                if (!SetField(ref _linearDamping, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.LinearDamping = value;
            }
        }

        [Category("Damping")]
        [DisplayName("Angular Damping")]
        [Description("Damping factor for angular velocity.")]
        public float AngularDamping
        {
            get => RigidBody is PhysxRigidBody physx ? physx.AngularDamping : _angularDamping;
            set
            {
                if (!SetField(ref _angularDamping, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.AngularDamping = value;
            }
        }

        [Category("Velocity")]
        [DisplayName("Max Linear Velocity")]
        [Description("Maximum linear velocity clamp.")]
        public float MaxLinearVelocity
        {
            get => RigidBody is PhysxRigidBody physx ? physx.MaxLinearVelocity : _maxLinearVelocity;
            set
            {
                if (!SetField(ref _maxLinearVelocity, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.MaxLinearVelocity = value;
            }
        }

        [Category("Velocity")]
        [DisplayName("Max Angular Velocity")]
        [Description("Maximum angular velocity clamp.")]
        public float MaxAngularVelocity
        {
            get => RigidBody is PhysxRigidBody physx ? physx.MaxAngularVelocity : _maxAngularVelocity;
            set
            {
                if (!SetField(ref _maxAngularVelocity, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.MaxAngularVelocity = value;
            }
        }

        [Category("Mass")]
        [DisplayName("Mass")]
        [Description("The mass of the rigid body in kg.")]
        public float Mass
        {
            get => RigidBody is PhysxRigidBody physx ? physx.Mass : _mass;
            set
            {
                if (!SetField(ref _mass, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.Mass = value;
            }
        }

        [Category("Mass")]
        [DisplayName("Inertia Tensor")]
        [Description("Mass-space inertia tensor diagonal.")]
        public Vector3 MassSpaceInertiaTensor
        {
            get => RigidBody is PhysxRigidBody physx ? physx.MassSpaceInertiaTensor : _massSpaceInertiaTensor;
            set
            {
                if (!SetField(ref _massSpaceInertiaTensor, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.MassSpaceInertiaTensor = value;
            }
        }

        [Category("Mass")]
        [DisplayName("Center Of Mass Pose")]
        [Description("Local pose of the center of mass.")]
        public PhysicsMassFrame CenterOfMassLocalPose
        {
            get => RigidBody is PhysxRigidBody physx ? new PhysicsMassFrame(physx.CMassLocalPose.Item2, physx.CMassLocalPose.Item1) : _centerOfMassPose;
            set
            {
                if (!SetField(ref _centerOfMassPose, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.CMassLocalPose = (value.Rotation, value.Translation);
            }
        }

        [Category("CCD")]
        [DisplayName("Min CCD Advance")]
        [Description("Minimum CCD advance coefficient.")]
        public float MinCcdAdvanceCoefficient
        {
            get => RigidBody is PhysxRigidBody physx ? physx.MinCCDAdvanceCoefficient : _minCcdAdvanceCoefficient;
            set
            {
                if (!SetField(ref _minCcdAdvanceCoefficient, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.MinCCDAdvanceCoefficient = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Max Depenetration Velocity")]
        [Description("Maximum velocity for depenetration.")]
        public float MaxDepenetrationVelocity
        {
            get => RigidBody is PhysxRigidBody physx ? physx.MaxDepenetrationVelocity : _maxDepenetrationVelocity;
            set
            {
                if (!SetField(ref _maxDepenetrationVelocity, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.MaxDepenetrationVelocity = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Max Contact Impulse")]
        [Description("Maximum contact impulse applied.")]
        public float MaxContactImpulse
        {
            get => RigidBody is PhysxRigidBody physx ? physx.MaxContactImpulse : _maxContactImpulse;
            set
            {
                if (!SetField(ref _maxContactImpulse, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.MaxContactImpulse = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Contact Slop")]
        [Description("Contact slop coefficient for solver.")]
        public float ContactSlopCoefficient
        {
            get => RigidBody is PhysxRigidBody physx ? physx.ContactSlopCoefficient : _contactSlopCoefficient;
            set
            {
                if (!SetField(ref _contactSlopCoefficient, value))
                    return;
                if (RigidBody is PhysxRigidBody physx)
                    physx.ContactSlopCoefficient = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Stabilization Threshold")]
        [Description("Threshold for solver stabilization.")]
        public float StabilizationThreshold
        {
            get => RigidBody is PhysxDynamicRigidBody physx ? physx.StabilizationThreshold : _stabilizationThreshold;
            set
            {
                if (!SetField(ref _stabilizationThreshold, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.StabilizationThreshold = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Sleep Threshold")]
        [Description("Energy threshold to enter sleep state.")]
        public float SleepThreshold
        {
            get => RigidBody is PhysxDynamicRigidBody physx ? physx.SleepThreshold : _sleepThreshold;
            set
            {
                if (!SetField(ref _sleepThreshold, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.SleepThreshold = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Contact Report Threshold")]
        [Description("Impulse threshold to trigger contact reports.")]
        public float ContactReportThreshold
        {
            get => RigidBody is PhysxDynamicRigidBody physx ? physx.ContactReportThreshold : _contactReportThreshold;
            set
            {
                if (!SetField(ref _contactReportThreshold, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.ContactReportThreshold = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Wake Counter")]
        [Description("Time before body goes to sleep when inactive.")]
        public float WakeCounter
        {
            get => RigidBody is PhysxDynamicRigidBody physx ? physx.WakeCounter : _wakeCounter;
            set
            {
                if (!SetField(ref _wakeCounter, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.WakeCounter = value;
            }
        }

        [Category("Solver")]
        [DisplayName("Solver Iterations")]
        [Description("Position and velocity solver iteration counts.")]
        public PhysicsSolverIterations SolverIterations
        {
            get => RigidBody is PhysxDynamicRigidBody physx ? new PhysicsSolverIterations(physx.SolverIterationCounts.minPositionIters, physx.SolverIterationCounts.minVelocityIters) : _solverIterations;
            set
            {
                if (!SetField(ref _solverIterations, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.SolverIterationCounts = (value.MinPositionIterations, value.MinVelocityIterations);
            }
        }

        [Category("Kinematic")]
        [DisplayName("Kinematic Target")]
        [Description("Target pose for kinematic bodies.")]
        public (Vector3 position, Quaternion rotation)? KinematicTarget
        {
            get => RigidBody is PhysxDynamicRigidBody physx ? physx.KinematicTarget : _kinematicTarget;
            set
            {
                if (!SetField(ref _kinematicTarget, value))
                    return;
                if (RigidBody is PhysxDynamicRigidBody physx)
                    physx.KinematicTarget = value;
            }
        }

        [Category("Velocity")]
        [DisplayName("Linear Velocity")]
        [Description("Current linear velocity.")]
        public Vector3 LinearVelocity
        {
            get => RigidBody switch
            {
                PhysxRigidBody physx => physx.LinearVelocity,
                JoltDynamicRigidBody jolt => jolt.LinearVelocity,
                _ => _cachedLinearVelocity
            };
            set
            {
                if (!SetField(ref _cachedLinearVelocity, value))
                    return;
                switch (RigidBody)
                {
                    case PhysxDynamicRigidBody physx:
                        physx.SetLinearVelocity(value);
                        break;
                    case JoltDynamicRigidBody jolt when jolt.Scene?.PhysicsSystem is not null:
                        jolt.SetLinearVelocity(value);
                        break;
                }
            }
        }

        [Category("Velocity")]
        [DisplayName("Angular Velocity")]
        [Description("Current angular velocity.")]
        public Vector3 AngularVelocity
        {
            get => RigidBody switch
            {
                PhysxRigidBody physx => physx.AngularVelocity,
                JoltDynamicRigidBody jolt => jolt.AngularVelocity,
                _ => _cachedAngularVelocity
            };
            set
            {
                if (!SetField(ref _cachedAngularVelocity, value))
                    return;
                switch (RigidBody)
                {
                    case PhysxDynamicRigidBody physx:
                        physx.SetAngularVelocity(value);
                        break;
                    case JoltDynamicRigidBody jolt when jolt.Scene?.PhysicsSystem is not null:
                        jolt.SetAngularVelocity(value);
                        break;
                }
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Debug.Physics("[DynamicRigidBodyComponent] Activating component on {0}", SceneNode?.Name ?? "<unnamed>");
            EnsureRigidBodyConstructed();
            ApplyAllCachedProperties();
            TryRegisterRigidBodyWithScene();
        }

        /// <summary>
        /// Called when play mode begins. Resets the rigid body to its initial pose and clears velocities,
        /// but only if InitialPosition or InitialRotation were explicitly set.
        /// </summary>
        protected internal override void OnBeginPlay()
        {
            base.OnBeginPlay();
            ResetToInitialPose();
        }

        /// <summary>
        /// Resets the rigid body to its initial position/rotation and clears velocities.
        /// </summary>
        public void ResetToInitialPose()
        {
            if (RigidBody is null)
                return;

            var (position, rotation) = GetSpawnPose();
            switch (RigidBody)
            {
                case PhysxDynamicRigidBody physx:
                    physx.SetTransform(position, rotation);
                    physx.SetLinearVelocity(Vector3.Zero);
                    physx.SetAngularVelocity(Vector3.Zero);
                    // Wake the body so it can respond to physics immediately
                    if (!physx.Flags.HasFlag(PxRigidBodyFlags.Kinematic))
                        physx.WakeUp();
                    break;
                case JoltDynamicRigidBody jolt when jolt.Scene?.PhysicsSystem is not null:
                    jolt.SetTransform(position, rotation);
                    jolt.SetLinearVelocity(Vector3.Zero);
                    jolt.SetAngularVelocity(Vector3.Zero);
                    break;
            }
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Debug.Physics("[DynamicRigidBodyComponent] Deactivating component on {0}", SceneNode?.Name ?? "<unnamed>");
            RemoveRigidBodyFromScene();
        }

        private void EnsureRigidBodyConstructed()
        {
            if (!AutoCreateRigidBody || RigidBody is not null || World?.PhysicsScene is null)
                return;

            RigidBody = World.PhysicsScene switch
            {
                PhysxScene => CreatePhysxDynamicRigidBody(),
                JoltScene joltScene => CreateJoltDynamicRigidBody(joltScene),
                _ => null
            };
        }

        private PhysxDynamicRigidBody? CreatePhysxDynamicRigidBody()
        {
            var (position, rotation) = GetSpawnPose();
            var geometry = Geometry;
            if (geometry is not null)
            {
                var mat = ResolvePhysxMaterial();
                if (mat is null)
                    return null;
                var created = new PhysxDynamicRigidBody(
                    mat,
                    geometry,
                    Density,
                    position,
                    rotation,
                    ShapeOffsetTranslation,
                    ShapeOffsetRotation);
                ApplyAllCachedProperties(created);
                return created;
            }

            var body = new PhysxDynamicRigidBody(position, rotation);
            ApplyAllCachedProperties(body);
            return body;
        }

        private JoltDynamicRigidBody? CreateJoltDynamicRigidBody(JoltScene scene)
        {
            var geometry = Geometry;
            if (geometry is null)
                return null;

            var pose = GetSpawnPose();
            LayerMask layerMask = CollisionGroup == 0
                ? new LayerMask(1)
                : new LayerMask(1 << CollisionGroup);

            var body = scene.CreateDynamicRigidBody(
                geometry,
                pose,
                ShapeOffsetTranslation,
                ShapeOffsetRotation,
                layerMask);

            if (body is null)
                return null;

            ApplyAllCachedProperties(body);
            return body;
        }

        protected PhysxMaterial? ResolvePhysxMaterial()
        {
            if (Material is PhysxMaterial physxMaterial)
                return physxMaterial;

            var created = new PhysxMaterial(0.5f, 0.5f, 0.1f);
            Material = created;
            return created;
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
                ApplyAllCachedProperties();
                TryRegisterRigidBodyWithScene();
                return;
            }

            if ((propName == nameof(ShapeOffsetTranslation) || propName == nameof(ShapeOffsetRotation)) && RigidBody is not null)
                ApplyShapeOffsets(RigidBody);
        }
/*
        /// <summary>
        /// Sets the initial position from the transform without triggering property change events
        /// that could cause sync loops. Called by RigidBodyTransform.UpdateComponentInitialPose.
        /// </summary>
        internal void SetInitialPositionFromTransform(Vector3 position)
        {
            SetField(ref _initialPosition, position, nameof(InitialPosition));
        }

        /// <summary>
        /// Sets the initial rotation from the transform without triggering property change events
        /// that could cause sync loops. Called by RigidBodyTransform.UpdateComponentInitialPose.
        /// </summary>
        internal void SetInitialRotationFromTransform(Quaternion rotation)
        {
            SetField(ref _initialRotation, rotation, nameof(InitialRotation));
        }
*/
        private void ApplyAllCachedProperties()
        {
            if (RigidBody is null)
                return;

            ApplyAllCachedProperties(RigidBody);
        }

        private void ApplyAllCachedProperties(IAbstractDynamicRigidBody body)
        {
            ApplyActorProperties(body);
            ApplyDynamicBodyProperties(body);
            ApplyShapeOffsets(body);
            if (body is PhysxActor actor)
            {
                Debug.Physics(
                    "[DynamicRigidBodyComponent] Applied cached props to {0} actorType={1} group={2} mask={3}",
                    SceneNode?.Name ?? "<unnamed>",
                    actor.GetType().Name,
                    actor.CollisionGroup,
                    FormatGroupsMask(actor.GroupsMask));
            }
        }

        private void ApplyActorProperties(IAbstractDynamicRigidBody body)
        {
            if (body is not PhysxActor actor)
                return;
            
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
        }

        private void ApplyDynamicBodyProperties(IAbstractDynamicRigidBody body)
        {
            if (body is not PhysxDynamicRigidBody physx)
                return;
            
            physx.Flags = ToPhysxRigidBodyFlags(_bodyFlags);
            physx.LockFlags = ToPhysxLockFlags(_lockFlags);
            physx.LinearDamping = _linearDamping;
            physx.AngularDamping = _angularDamping;
            physx.MaxLinearVelocity = _maxLinearVelocity;
            physx.MaxAngularVelocity = _maxAngularVelocity;
            physx.Mass = _mass;
            physx.MassSpaceInertiaTensor = _massSpaceInertiaTensor;
            physx.CMassLocalPose = (_centerOfMassPose.Rotation, _centerOfMassPose.Translation);
            physx.MinCCDAdvanceCoefficient = _minCcdAdvanceCoefficient;
            physx.MaxDepenetrationVelocity = _maxDepenetrationVelocity;
            physx.MaxContactImpulse = _maxContactImpulse;
            physx.ContactSlopCoefficient = _contactSlopCoefficient;
            physx.StabilizationThreshold = _stabilizationThreshold;
            physx.SleepThreshold = _sleepThreshold;
            physx.ContactReportThreshold = _contactReportThreshold;
            physx.WakeCounter = _wakeCounter;
            physx.SolverIterationCounts = (_solverIterations.MinPositionIterations, _solverIterations.MinVelocityIterations);
            if (_kinematicTarget.HasValue)
                physx.KinematicTarget = _kinematicTarget;
        }

        private void ApplyShapeOffsets(IAbstractDynamicRigidBody body)
        {
            if (body is not PhysxRigidActor physxActor)
                return;

            var shapes = physxActor.GetShapes();
            if (shapes.Length == 0)
                return;

            var pose = (ShapeOffsetTranslation, ShapeOffsetRotation);
            foreach (var shape in shapes)
            {
                if (shape is null)
                    continue;
                shape.LocalPose = pose;
            }
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

        private static PhysicsRigidBodyFlags FromPhysxRigidBodyFlags(PxRigidBodyFlags flags)
        {
            PhysicsRigidBodyFlags converted = PhysicsRigidBodyFlags.None;
            if (flags.HasFlag(PxRigidBodyFlags.Kinematic))
                converted |= PhysicsRigidBodyFlags.Kinematic;
            if (flags.HasFlag(PxRigidBodyFlags.UseKinematicTargetForSceneQueries))
                converted |= PhysicsRigidBodyFlags.UseKinematicTargetForQueries;
            if (flags.HasFlag(PxRigidBodyFlags.EnableCcd))
                converted |= PhysicsRigidBodyFlags.EnableCcd;
            if (flags.HasFlag(PxRigidBodyFlags.EnableSpeculativeCcd))
                converted |= PhysicsRigidBodyFlags.EnableSpeculativeCcd;
            if (flags.HasFlag(PxRigidBodyFlags.EnableCcdMaxContactImpulse))
                converted |= PhysicsRigidBodyFlags.EnableCcdMaxContactImpulse;
            if (flags.HasFlag(PxRigidBodyFlags.EnableCcdFriction))
                converted |= PhysicsRigidBodyFlags.EnableCcdFriction;
            return converted;
        }

        private static PxRigidBodyFlags ToPhysxRigidBodyFlags(PhysicsRigidBodyFlags flags)
        {
            PxRigidBodyFlags converted = 0;
            if (flags.HasFlag(PhysicsRigidBodyFlags.Kinematic))
                converted |= PxRigidBodyFlags.Kinematic;
            if (flags.HasFlag(PhysicsRigidBodyFlags.UseKinematicTargetForQueries))
                converted |= PxRigidBodyFlags.UseKinematicTargetForSceneQueries;
            if (flags.HasFlag(PhysicsRigidBodyFlags.EnableCcd))
                converted |= PxRigidBodyFlags.EnableCcd;
            if (flags.HasFlag(PhysicsRigidBodyFlags.EnableSpeculativeCcd))
                converted |= PxRigidBodyFlags.EnableSpeculativeCcd;
            if (flags.HasFlag(PhysicsRigidBodyFlags.EnableCcdMaxContactImpulse))
                converted |= PxRigidBodyFlags.EnableCcdMaxContactImpulse;
            if (flags.HasFlag(PhysicsRigidBodyFlags.EnableCcdFriction))
                converted |= PxRigidBodyFlags.EnableCcdFriction;
            return converted;
        }

        private static PhysicsLockFlags FromPhysxLockFlags(PxRigidDynamicLockFlags flags)
        {
            PhysicsLockFlags converted = PhysicsLockFlags.None;
            if (flags.HasFlag(PxRigidDynamicLockFlags.LockLinearX))
                converted |= PhysicsLockFlags.LinearX;
            if (flags.HasFlag(PxRigidDynamicLockFlags.LockLinearY))
                converted |= PhysicsLockFlags.LinearY;
            if (flags.HasFlag(PxRigidDynamicLockFlags.LockLinearZ))
                converted |= PhysicsLockFlags.LinearZ;
            if (flags.HasFlag(PxRigidDynamicLockFlags.LockAngularX))
                converted |= PhysicsLockFlags.AngularX;
            if (flags.HasFlag(PxRigidDynamicLockFlags.LockAngularY))
                converted |= PhysicsLockFlags.AngularY;
            if (flags.HasFlag(PxRigidDynamicLockFlags.LockAngularZ))
                converted |= PhysicsLockFlags.AngularZ;
            return converted;
        }

        private static PxRigidDynamicLockFlags ToPhysxLockFlags(PhysicsLockFlags flags)
        {
            PxRigidDynamicLockFlags converted = 0;
            if (flags.HasFlag(PhysicsLockFlags.LinearX))
                converted |= PxRigidDynamicLockFlags.LockLinearX;
            if (flags.HasFlag(PhysicsLockFlags.LinearY))
                converted |= PxRigidDynamicLockFlags.LockLinearY;
            if (flags.HasFlag(PhysicsLockFlags.LinearZ))
                converted |= PxRigidDynamicLockFlags.LockLinearZ;
            if (flags.HasFlag(PhysicsLockFlags.AngularX))
                converted |= PxRigidDynamicLockFlags.LockAngularX;
            if (flags.HasFlag(PhysicsLockFlags.AngularY))
                converted |= PxRigidDynamicLockFlags.LockAngularY;
            if (flags.HasFlag(PhysicsLockFlags.AngularZ))
                converted |= PxRigidDynamicLockFlags.LockAngularZ;
            return converted;
        }

        private void TryRegisterRigidBodyWithScene()
        {
            if (!IsActive || RigidBody is null)
                return;

            var scene = World?.PhysicsScene;
            if (scene is null)
                return;

            if (scene is PhysxScene physxScene && RigidBody is PhysxActor physxActor)
            {
                // Character controllers (CCT) create a hidden rigid actor that is already attached to the PhysX scene
                // by the controller manager. Attempting to add it again can assert/crash in PhysX.
                if (physxActor.Scene is not null)
                {
                    if (physxActor.Scene == physxScene)
                    {
                        Debug.Physics(
                            "[DynamicRigidBodyComponent] Actor already registered with PhysxScene; skipping add actorType={0}",
                            physxActor.GetType().Name);
                        return;
                    }

                    Debug.Physics(
                        "[DynamicRigidBodyComponent] Actor belongs to a different PhysxScene; skipping add actorType={0}",
                        physxActor.GetType().Name);
                    return;
                }
            }

            scene.AddActor(RigidBody);

            if (RigidBody is PhysxActor actor)
            {
                Debug.Physics(
                    "[DynamicRigidBodyComponent] Registered actorType={0} with scene {1} (group={2}, mask={3})",
                    actor.GetType().Name,
                    scene.GetType().Name,
                    actor.CollisionGroup,
                    FormatGroupsMask(actor.GroupsMask));
            }
        }

        private void RemoveRigidBodyFromScene()
        {
            if (RigidBody is null)
                return;

            var scene = World?.PhysicsScene;
            if (scene is null)
                return;

            if (scene is PhysxScene physxScene && RigidBody is PhysxActor physxActor)
            {
                // If the actor is attached to another PhysX scene, remove it from that scene instead of blindly
                // calling remove on the current scene.
                if (physxActor.Scene is not null && physxActor.Scene != physxScene)
                {
                    physxActor.Scene.RemoveActor(physxActor);
                    Debug.Physics(
                        "[DynamicRigidBodyComponent] Removed actorType={0} from foreign PhysxScene",
                        physxActor.GetType().Name);
                    return;
                }

                // If the actor isn't in any scene, there's nothing to remove.
                if (physxActor.Scene is null)
                    return;
            }

            scene.RemoveActor(RigidBody);
            if (RigidBody is PhysxActor actor)
            {
                Debug.Physics(
                    "[DynamicRigidBodyComponent] Removed actorType={0} from scene {1}",
                    actor.GetType().Name,
                    scene.GetType().Name);
            }
        }

        private static string FormatGroupsMask(PxGroupsMask mask)
            => $"{mask.bits0:X4}:{mask.bits1:X4}:{mask.bits2:X4}:{mask.bits3:X4}";
    }
}
