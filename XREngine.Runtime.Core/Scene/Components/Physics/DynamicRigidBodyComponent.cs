using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine;
using XREngine.Networking;

namespace XREngine.Components.Physics
{
    [RequiresTransform(typeof(RigidBodyTransform))]
    [Category("Physics")]
    [DisplayName("Dynamic Rigid Body")]
    [Description("Simulated rigid body that responds to forces, collisions, and networking state.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.DynamicRigidBodyComponentEditor")]
    public class DynamicRigidBodyComponent : ConvexPhysicsActorComponent, IPhysicsReplicationTarget, IRuntimePhysicsStepListener, IRuntimeDynamicRigidBodyComponent
    {
        private const float DefaultDensity = 1.0f;
        private const float DefaultLinearDamping = 0.05f;
        private const float DefaultAngularDamping = 0.05f;

        private int _rigidBodyOwnershipSyncDepth;

        public RigidBodyTransform RigidBodyTransform => SceneNode.GetTransformAs<RigidBodyTransform>(true)!;

        /// <summary>
        /// Synchronizes the scene representation after a Core-owned physics
        /// reset. This is deliberately immediate so the renderer and gameplay
        /// observe the same pose as the restored native body in this step.
        /// </summary>
        public void SynchronizeSceneTransform(Vector3 position, Quaternion rotation)
            => RigidBodyTransform.SetPositionAndRotation(position, rotation);

        void IRuntimePhysicsStepListener.OnPhysicsStepped()
            => RigidBodyTransform.OnPhysicsStepped();

        private IAbstractDynamicRigidBody? _rigidBody;
        private bool _autoCreateRigidBody = true;
        private bool _gravityEnabled = true;
        private bool _simulationEnabled = true;
        private bool _debugVisualization;
        private bool _sendSleepNotifies;
        private ushort _collisionGroup;
        private PhysicsGroupsMask _groupsMask = PhysicsGroupsMask.Empty;
        private byte _dominanceGroup;
        private byte _physxOwnerClient;
        private PhysicsReplicationAuthority _replicationAuthority = PhysicsReplicationAuthority.LocalSimulation;
        private NetworkEntityId _networkEntityId;
        private string? _ownerClientId;
        private int _ownerServerPlayerIndex = -1;
        private string? _actorName;
        private AbstractPhysicsMaterial? _material;
        private PhysicsMaterialDefinition? _materialDefinition;
        private IPhysicsGeometry? _geometry;
        private List<PhysicsColliderShape> _colliderShapes = [];
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
        // PhysX interprets zero as "contacts may apply no impulse", so a zero-initialized
        // component silently falls through every collider. Match the native unlimited default.
        private float _maxContactImpulse = float.MaxValue;
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

        public void SetRigidBodyFromRigidBodyOwner(IAbstractDynamicRigidBody? body)
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
        [DisplayName("Material Definition")]
        [Description("Backend-neutral authored material settings. Native backend materials are generated from this definition when needed.")]
        public PhysicsMaterialDefinition? MaterialDefinition
        {
            get => _materialDefinition;
            set => SetField(ref _materialDefinition, value);
        }

        [Category("Shape")]
        [DisplayName("Compound Colliders")]
        [Description("Backend-neutral collider shape list. The first enabled shape is the primary shape; PhysX attaches additional enabled shapes as a compound actor.")]
        public List<PhysicsColliderShape> ColliderShapes
        {
            get => _colliderShapes;
            set => SetField(ref _colliderShapes, value ?? []);
        }

        [Category("Shape")]
        [DisplayName("Geometry")]
        [Description("The legacy single collision geometry shape. Used when Compound Colliders is empty.")]
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
            get => RigidBody?.GravityEnabled ?? _gravityEnabled;
            set
            {
                if (!SetField(ref _gravityEnabled, value))
                    return;
                if (RigidBody is not null)
                    RigidBody.GravityEnabled = value;
            }
        }

        [Category("Simulation")]
        [DisplayName("Simulation Enabled")]
        [Description("Whether the physics simulation is enabled.")]
        public bool SimulationEnabled
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.SimulationEnabled : _simulationEnabled;
            set
            {
                if (!SetField(ref _simulationEnabled, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.SimulationEnabled = value;
            }
        }

        [Category("Debug")]
        [DisplayName("Debug Visualization")]
        [Description("Show physics debug visualization.")]
        public bool DebugVisualization
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.DebugVisualize : _debugVisualization;
            set
            {
                if (!SetField(ref _debugVisualization, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.DebugVisualize = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Send Sleep Notifies")]
        [Description("Whether to notify when sleep state changes.")]
        public bool SendSleepNotifies
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.SendSleepNotifies : _sendSleepNotifies;
            set
            {
                if (!SetField(ref _sendSleepNotifies, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.SendSleepNotifies = value;
            }
        }

        [Category("Collision")]
        [DisplayName("Collision Group")]
        [Description("The collision group this body belongs to.")]
        public ushort CollisionGroup
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.CollisionGroup : _collisionGroup;
            set
            {
                if (!SetField(ref _collisionGroup, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.CollisionGroup = value;
            }
        }

        [Category("Collision")]
        [DisplayName("Groups Mask")]
        [Description("Collision filter mask for collision filtering.")]
        public PhysicsGroupsMask GroupsMask
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.GroupsMask : _groupsMask;
            set
            {
                if (!SetField(ref _groupsMask, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.GroupsMask = value;
            }
        }

        [Category("Collision")]
        [DisplayName("Dominance Group")]
        [Description("Collision resolution dominance (higher wins).")]
        public byte DominanceGroup
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.DominanceGroup : _dominanceGroup;
            set
            {
                if (!SetField(ref _dominanceGroup, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.DominanceGroup = value;
            }
        }

        [Category("Physics / PhysX Extensions")]
        [DisplayName("PhysX Owner Client")]
        [Description("Legacy PhysX owner-client byte. Network authority uses OwnerClientId and OwnerServerPlayerIndex.")]
        public byte PhysxOwnerClient
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.OwnerClient : _physxOwnerClient;
            set
            {
                if (!SetField(ref _physxOwnerClient, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                {
                    // PhysX disallows changing ownerClient while the actor is already in a scene.
                    if (!properties.IsInScene)
                        properties.OwnerClient = value;
                }
            }
        }


        [Category("Networking")]
        [DisplayName("Replication Authority")]
        [Description("Defines which peer is authoritative for replicated physics state. This is metadata for networking handoff and does not change local simulation by itself.")]
        public PhysicsReplicationAuthority ReplicationAuthority
        {
            get => _replicationAuthority;
            set => SetField(ref _replicationAuthority, value);
        }

        [Category("Debug")]
        [DisplayName("Actor Name")]
        [Description("Debug name for the physics actor.")]
        public string? ActorName
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties actor ? actor.Name : _actorName;
            set
            {
                if (!SetField(ref _actorName, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.Name = value ?? string.Empty;
            }
        }

        [Category("Flags")]
        [DisplayName("Body Flags")]
        [Description("Rigid body behavior flags (kinematic, CCD, etc).")]
        public PhysicsRigidBodyFlags BodyFlags
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.BodyFlags : _bodyFlags;
            set
            {
                if (!SetField(ref _bodyFlags, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.BodyFlags = value;
            }
        }

        [Category("Flags")]
        [DisplayName("Lock Flags")]
        [Description("Axis lock flags for constrained motion.")]
        public PhysicsLockFlags LockFlags
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.LockFlags : _lockFlags;
            set
            {
                if (!SetField(ref _lockFlags, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.LockFlags = value;
            }
        }

        [Category("Damping")]
        [DisplayName("Linear Damping")]
        [Description("Damping factor for linear velocity.")]
        public float LinearDamping
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.LinearDamping : _linearDamping;
            set
            {
                if (!SetField(ref _linearDamping, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.LinearDamping = value;
            }
        }

        [Category("Damping")]
        [DisplayName("Angular Damping")]
        [Description("Damping factor for angular velocity.")]
        public float AngularDamping
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.AngularDamping : _angularDamping;
            set
            {
                if (!SetField(ref _angularDamping, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.AngularDamping = value;
            }
        }

        [Category("Velocity")]
        [DisplayName("Max Linear Velocity")]
        [Description("Maximum linear velocity clamp.")]
        public float MaxLinearVelocity
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.MaxLinearVelocity : _maxLinearVelocity;
            set
            {
                if (!SetField(ref _maxLinearVelocity, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.MaxLinearVelocity = value;
            }
        }

        [Category("Velocity")]
        [DisplayName("Max Angular Velocity")]
        [Description("Maximum angular velocity clamp.")]
        public float MaxAngularVelocity
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.MaxAngularVelocity : _maxAngularVelocity;
            set
            {
                if (!SetField(ref _maxAngularVelocity, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.MaxAngularVelocity = value;
            }
        }

        [Category("Mass")]
        [DisplayName("Mass")]
        [Description("The mass of the rigid body in kg.")]
        public float Mass
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.Mass : _mass;
            set
            {
                if (!SetField(ref _mass, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.Mass = value;
            }
        }

        [Category("Mass")]
        [DisplayName("Inertia Tensor")]
        [Description("Mass-space inertia tensor diagonal.")]
        public Vector3 MassSpaceInertiaTensor
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.MassSpaceInertiaTensor : _massSpaceInertiaTensor;
            set
            {
                if (!SetField(ref _massSpaceInertiaTensor, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.MassSpaceInertiaTensor = value;
            }
        }

        [Category("Mass")]
        [DisplayName("Center Of Mass Pose")]
        [Description("Local pose of the center of mass.")]
        public PhysicsMassFrame CenterOfMassLocalPose
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.CenterOfMassLocalPose : _centerOfMassPose;
            set
            {
                if (!SetField(ref _centerOfMassPose, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.CenterOfMassLocalPose = value;
            }
        }

        [Category("CCD")]
        [DisplayName("Min CCD Advance")]
        [Description("Minimum CCD advance coefficient.")]
        public float MinCcdAdvanceCoefficient
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.MinCcdAdvanceCoefficient : _minCcdAdvanceCoefficient;
            set
            {
                if (!SetField(ref _minCcdAdvanceCoefficient, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.MinCcdAdvanceCoefficient = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Max Depenetration Velocity")]
        [Description("Maximum velocity for depenetration.")]
        public float MaxDepenetrationVelocity
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.MaxDepenetrationVelocity : _maxDepenetrationVelocity;
            set
            {
                if (!SetField(ref _maxDepenetrationVelocity, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.MaxDepenetrationVelocity = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Max Contact Impulse")]
        [Description("Maximum contact impulse applied.")]
        public float MaxContactImpulse
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.MaxContactImpulse : _maxContactImpulse;
            set
            {
                if (!SetField(ref _maxContactImpulse, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.MaxContactImpulse = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Contact Slop")]
        [Description("Contact slop coefficient for solver.")]
        public float ContactSlopCoefficient
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.ContactSlopCoefficient : _contactSlopCoefficient;
            set
            {
                if (!SetField(ref _contactSlopCoefficient, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.ContactSlopCoefficient = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Stabilization Threshold")]
        [Description("Threshold for solver stabilization.")]
        public float StabilizationThreshold
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.StabilizationThreshold : _stabilizationThreshold;
            set
            {
                if (!SetField(ref _stabilizationThreshold, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.StabilizationThreshold = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Sleep Threshold")]
        [Description("Energy threshold to enter sleep state.")]
        public float SleepThreshold
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.SleepThreshold : _sleepThreshold;
            set
            {
                if (!SetField(ref _sleepThreshold, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.SleepThreshold = value;
            }
        }

        [Category("Contact")]
        [DisplayName("Contact Report Threshold")]
        [Description("Impulse threshold to trigger contact reports.")]
        public float ContactReportThreshold
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.ContactReportThreshold : _contactReportThreshold;
            set
            {
                if (!SetField(ref _contactReportThreshold, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.ContactReportThreshold = value;
            }
        }

        [Category("Sleep")]
        [DisplayName("Wake Counter")]
        [Description("Time before body goes to sleep when inactive.")]
        public float WakeCounter
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.WakeCounter : _wakeCounter;
            set
            {
                if (!SetField(ref _wakeCounter, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.WakeCounter = value;
            }
        }

        [Category("Solver")]
        [DisplayName("Solver Iterations")]
        [Description("Position and velocity solver iteration counts. PhysX updates live; Jolt applies these overrides when the body is created or rebuilt.")]
        public PhysicsSolverIterations SolverIterations
        {
            get => RigidBody is IPhysicsRuntimeBodyProperties properties ? properties.SolverIterations : _solverIterations;
            set
            {
                if (!SetField(ref _solverIterations, value))
                    return;
                if (RigidBody is IPhysicsRuntimeBodyProperties properties)
                    properties.SolverIterations = value;
            }
        }

        [Category("Kinematic")]
        [DisplayName("Kinematic Target")]
        [Description("Target pose for kinematic bodies.")]
        public (Vector3 position, Quaternion rotation)? KinematicTarget
        {
            get => RigidBody?.KinematicTarget ?? _kinematicTarget;
            set
            {
                if (!SetField(ref _kinematicTarget, value))
                    return;
                if (RigidBody is not null)
                    RigidBody.KinematicTarget = value;
            }
        }

        [Category("Velocity")]
        [DisplayName("Linear Velocity")]
        [Description("Current linear velocity.")]
        public Vector3 LinearVelocity
        {
            get => RigidBody?.LinearVelocity ?? _cachedLinearVelocity;
            set
            {
                SetField(ref _cachedLinearVelocity, value);
                RigidBody?.SetLinearVelocity(value);
            }
        }

        [Category("Velocity")]
        [DisplayName("Angular Velocity")]
        [Description("Current angular velocity.")]
        public Vector3 AngularVelocity
        {
            get => RigidBody?.AngularVelocity ?? _cachedAngularVelocity;
            set
            {
                SetField(ref _cachedAngularVelocity, value);
                RigidBody?.SetAngularVelocity(value);
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Debug.Physics("[DynamicRigidBodyComponent] Activating component on {0}", SceneNode?.Name ?? "<unnamed>");
            bool hadRigidBody = RigidBody is not null;
            EnsureRigidBodyConstructed();

            if (!hadRigidBody)
                return;

            RuntimeThreadServices.Current.EnqueuePhysicsThread(() =>
            {
                if (!IsActive || RigidBody is null)
                    return;
                ApplyAllCachedProperties();
            });

            TryRegisterRigidBodyWithScene();
        }

        /// <summary>
        /// Called when play mode begins. Resets the rigid body to its initial pose and clears velocities,
        /// but only if InitialPosition or InitialRotation were explicitly set.
        /// </summary>
        protected override void OnBeginPlay()
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
            RigidBody.SetTransform(position, rotation);
            RigidBody.SetLinearVelocity(Vector3.Zero);
            RigidBody.SetAngularVelocity(Vector3.Zero);
            if ((_bodyFlags & PhysicsRigidBodyFlags.Kinematic) == 0)
                RigidBody.WakeUp();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Debug.Physics("[DynamicRigidBodyComponent] Deactivating component on {0}", SceneNode?.Name ?? "<unnamed>");
            RemoveRigidBodyFromScene();
        }

        private void EnsureRigidBodyConstructed()
        {
            AbstractPhysicsScene? physicsScene = WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene;
            if (!AutoCreateRigidBody || RigidBody is not null || physicsScene is null)
                return;

            RigidBody = physicsScene.BackendService.CreateDynamicRigidBody(BuildRigidBodyCreateInfo());
        }

        private PhysicsRigidBodyCreateInfo BuildRigidBodyCreateInfo()
        {
            LayerMask layerMask = CollisionGroup == 0
                ? new LayerMask(1)
                : new LayerMask(1 << CollisionGroup);

            return new PhysicsRigidBodyCreateInfo(
                    ColliderShapes,
                    Geometry,
                    Material,
                    MaterialDefinition,
                    GetSpawnPose(),
                    ShapeOffsetTranslation,
                    ShapeOffsetRotation,
                    Density,
                    layerMask)
                {
                    GravityEnabled = GravityEnabled,
                    MaxLinearVelocity = MaxLinearVelocity,
                    MaxAngularVelocity = MaxAngularVelocity,
                    SolverIterations = SolverIterations,
                    BodyFlags = BodyFlags,
                    LockFlags = LockFlags,
                };
        }

        [Browsable(false)]
        public override IAbstractPhysicsActor? PhysicsActor => RigidBody;

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


        public void RebuildCollisionShapes(bool wakeOnLostTouch = true)
        {
            IAbstractDynamicRigidBody? oldBody = RigidBody;
            Vector3 linearVelocity = oldBody?.LinearVelocity ?? _cachedLinearVelocity;
            Vector3 angularVelocity = oldBody?.AngularVelocity ?? _cachedAngularVelocity;
            AbstractPhysicsScene? physicsScene = WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene;
            if (oldBody is not null
                && physicsScene is not null
                && physicsScene.BackendService.TryReplaceCollisionShapes(oldBody, BuildRigidBodyCreateInfo()))
            {
                oldBody.SetLinearVelocity(linearVelocity);
                oldBody.SetAngularVelocity(angularVelocity);
                return;
            }

            RigidBody = null;
            oldBody?.Destroy(wakeOnLostTouch);
            EnsureRigidBodyConstructed();
            RigidBody?.SetLinearVelocity(linearVelocity);
            RigidBody?.SetAngularVelocity(angularVelocity);
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
            if (propName == nameof(RigidBody))
            {
                NotifyPhysicsActorChanged(
                    prev is IAbstractPhysicsActor previousActor ? previousActor : null,
                    RigidBody);
                if (RigidBody is null)
                    return;

                if (_rigidBodyOwnershipSyncDepth == 0)
                    RigidBody.OwningComponent = this;
                RigidBodyTransform.RigidBody = RigidBody;
                ApplyAllCachedProperties();
                TryRegisterRigidBodyWithScene();
                return;
            }

            if (RigidBody is not null
                && (propName == nameof(Geometry)
                    || propName == nameof(Material)
                    || propName == nameof(MaterialDefinition)
                    || propName == nameof(ColliderShapes)))
            {
                RebuildCollisionShapes();
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
            if (body is IPhysicsRuntimeBodyProperties actor)
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
            body.GravityEnabled = _gravityEnabled;
            if (body is IPhysicsRuntimeBodyProperties actor)
            {
                actor.SimulationEnabled = _simulationEnabled;
                actor.DebugVisualize = _debugVisualization;
                actor.SendSleepNotifies = _sendSleepNotifies;
                actor.CollisionGroup = _collisionGroup;
                actor.GroupsMask = _groupsMask;
                actor.DominanceGroup = _dominanceGroup;
                // PhysX disallows setting ownerClient once the actor is inserted into a scene.
                if (!actor.IsInScene)
                    actor.OwnerClient = _physxOwnerClient;
                if (_actorName is not null)
                    actor.Name = _actorName;
            }
            else if (body is IPhysicsDynamicBodySettings settings)
            {
                settings.SetCollisionFiltering(_collisionGroup, _groupsMask);

                if (!_simulationEnabled)
                {
                    Debug.Physics("[DynamicRigidBodyComponent] Jolt does not currently support SimulationEnabled parity; value cached only.");
                }
            }
        }

        private void ApplyDynamicBodyProperties(IAbstractDynamicRigidBody body)
        {
            if (body is IPhysicsRuntimeBodyProperties properties)
            {
                properties.BodyFlags = _bodyFlags;
                properties.LockFlags = _lockFlags;
                properties.LinearDamping = _linearDamping;
                properties.AngularDamping = _angularDamping;
                properties.MaxLinearVelocity = _maxLinearVelocity;
                properties.MaxAngularVelocity = _maxAngularVelocity;
                properties.Mass = _mass;
                properties.MassSpaceInertiaTensor = _massSpaceInertiaTensor;
                properties.CenterOfMassLocalPose = _centerOfMassPose;
                properties.MinCcdAdvanceCoefficient = _minCcdAdvanceCoefficient;
                properties.MaxDepenetrationVelocity = _maxDepenetrationVelocity;
                properties.MaxContactImpulse = _maxContactImpulse;
                properties.ContactSlopCoefficient = _contactSlopCoefficient;
                properties.StabilizationThreshold = _stabilizationThreshold;
                properties.SleepThreshold = _sleepThreshold;
                properties.ContactReportThreshold = _contactReportThreshold;
                properties.WakeCounter = _wakeCounter;
                properties.SolverIterations = _solverIterations;
                if (_kinematicTarget.HasValue)
                    properties.KinematicTarget = _kinematicTarget;
            }
            else if (body is IPhysicsDynamicBodySettings settings)
            {
                settings.SetMotionQuality(_bodyFlags);
                settings.SetLockFlags(_lockFlags);
                settings.SetDamping(_linearDamping, _angularDamping);
                settings.SetMass(_mass);

                if (_kinematicTarget.HasValue)
                    body.SetTransform(_kinematicTarget.Value.position, _kinematicTarget.Value.rotation);

                Debug.Physics("[DynamicRigidBodyComponent] Jolt applies max velocities and solver-step overrides at body creation; unsupported PhysX-only contact, sleep, COM/inertia, and advanced CCD fields remain authored data only.");
            }
        }

        private void ApplyShapeOffsets(IAbstractDynamicRigidBody body)
        {
            if (body is IPhysicsRuntimeBodyProperties properties)
                properties.ApplyShapeOffset(ShapeOffsetTranslation, ShapeOffsetRotation);
        }

        private void TryRegisterRigidBodyWithScene()
        {
            if (!IsActive || RigidBody is null)
                return;

            var scene = WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene;
            if (scene is null)
                return;

            if (RigidBody is IPhysicsSceneAttachedActor attachedActor)
            {
                // Character controllers (CCT) create a hidden rigid actor that is already attached to the PhysX scene
                // by the controller manager. Attempting to add it again can assert/crash in PhysX.
                if (attachedActor.AttachedScene is not null)
                {
                    if (attachedActor.AttachedScene == scene)
                    {
                        Debug.Physics(
                            "[DynamicRigidBodyComponent] Actor already registered with PhysxScene; skipping add actorType={0}",
                            RigidBody.GetType().Name);
                        return;
                    }

                    Debug.Physics(
                        "[DynamicRigidBodyComponent] Actor belongs to a different PhysxScene; skipping add actorType={0}",
                        RigidBody.GetType().Name);
                    return;
                }
            }

            scene.AddActor(RigidBody);

            if (RigidBody is IPhysicsRuntimeBodyProperties actor)
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

            var scene = WorldAs<IRuntimePhysicsWorldContext>()?.PhysicsScene;
            if (scene is null)
                return;

            if (RigidBody is IPhysicsSceneAttachedActor attachedActor)
            {
                // If the actor is attached to another PhysX scene, remove it from that scene instead of blindly
                // calling remove on the current scene.
                if (attachedActor.AttachedScene is { } owningScene && owningScene != scene)
                {
                    owningScene.RemoveActor(RigidBody);
                    Debug.Physics(
                        "[DynamicRigidBodyComponent] Removed actorType={0} from foreign PhysxScene",
                        RigidBody.GetType().Name);
                    return;
                }

                // If the actor isn't in any scene, there's nothing to remove.
                if (attachedActor.AttachedScene is null)
                    return;
            }

            scene.RemoveActor(RigidBody);
            if (RigidBody is IPhysicsRuntimeBodyProperties actor)
            {
                Debug.Physics(
                    "[DynamicRigidBodyComponent] Removed actorType={0} from scene {1}",
                    actor.GetType().Name,
                    scene.GetType().Name);
            }
        }

        private static string FormatGroupsMask(PhysicsGroupsMask mask)
            => $"{mask.Word0:X4}:{mask.Word1:X4}:{mask.Word2:X4}:{mask.Word3:X4}";
    }
}
