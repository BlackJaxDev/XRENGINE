using XREngine.Extensions;
using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Physics.DebugVisualization;
using XREngine.Data.Colors;
using Ray = JoltPhysicsSharp.Ray;

namespace XREngine.Scene.Physics.Jolt
{
    public class JoltScene : AbstractPhysicsScene
    {
        private IPhysicsBackendService? _backendService;

        public override IPhysicsBackendService BackendService
            => _backendService ??= new JoltBackendService(this);

        private readonly HashSet<IJoltCharacterController> _characterControllers = new();
        private readonly object _debugContactsLock = new();
        private readonly JoltDebugContact[] _debugContacts = new JoltDebugContact[256];
        private int _debugContactCount;
        private bool _captureDebugContacts;
        private JoltEngineDebugRenderer? _debugRenderer;

        internal void RegisterCharacterController(IJoltCharacterController controller)
        {
            if (controller is null)
                return;
            _characterControllers.Add(controller);
        }

        internal void UnregisterCharacterController(IJoltCharacterController controller)
        {
            if (controller is null)
                return;
            _characterControllers.Remove(controller);
        }

        public override Vector3 Gravity
        {
            get => _physicsSystem?.Gravity ?? Vector3.Zero;
            set
            {
                if (_physicsSystem is null)
                    return;
                
                _physicsSystem.Gravity = value;
            }
        }

        private PhysicsSystem? _physicsSystem;
        private JobSystem? _jobSystem;
        private Dictionary<BodyID, JoltActor> _actors = new();
        private Dictionary<BodyID, JoltRigidActor> _rigidActors = new();
        private Dictionary<BodyID, JoltStaticRigidBody> _staticBodies = new();
        private Dictionary<BodyID, JoltDynamicRigidBody> _dynamicBodies = new();
        private readonly HashSet<IAbstractJoint> _joints = [];
        private BodyID _worldAnchorBodyID = BodyID.Invalid;

        public PhysicsSystem? PhysicsSystem => _physicsSystem;
        public JobSystem? JobSystem => _jobSystem;

        public JoltStaticRigidBody? CreateStaticRigidBody(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 shapeOffsetTranslation,
            Quaternion shapeOffsetRotation,
            LayerMask layerMask)
        {
            if (_physicsSystem is null)
                return null;

            JoltShapeMetadata metadata;
            try
            {
                metadata = JoltShapeFactory.Create(geometry, shapeOffsetTranslation, shapeOffsetRotation);
            }
            catch (NotImplementedException)
            {
                System.Diagnostics.Debug.WriteLine($"[JoltScene] CreateStaticRigidBody failed - geometry type {geometry.GetType().Name} not implemented for Jolt");
                return null;
            }

            return CreateStaticRigidBody(metadata, pose, layerMask, default);
        }

        public JoltStaticRigidBody? CreateStaticRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
        {
            if (_physicsSystem is null)
                return null;

            JoltShapeMetadata? metadata = JoltShapeFactory.Create(
                createInfo.ColliderShapes,
                createInfo.FallbackGeometry,
                createInfo.FallbackShapeOffsetTranslation,
                createInfo.FallbackShapeOffsetRotation);
            return metadata is null
                ? null
                : CreateStaticRigidBody(metadata, createInfo.Pose, createInfo.LayerMask, createInfo);
        }

        public JoltDynamicRigidBody? CreateDynamicRigidBody(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 shapeOffsetTranslation,
            Quaternion shapeOffsetRotation,
            LayerMask layerMask)
        {
            if (_physicsSystem is null)
                return null;

            JoltShapeMetadata metadata;
            try
            {
                metadata = JoltShapeFactory.Create(geometry, shapeOffsetTranslation, shapeOffsetRotation);
            }
            catch (NotImplementedException)
            {
                System.Diagnostics.Debug.WriteLine($"[JoltScene] CreateDynamicRigidBody failed - geometry type {geometry.GetType().Name} not implemented for Jolt");
                return null;
            }

            return CreateDynamicRigidBody(metadata, pose, layerMask, default);
        }

        public JoltDynamicRigidBody? CreateDynamicRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
        {
            if (_physicsSystem is null)
                return null;

            JoltShapeMetadata? metadata = JoltShapeFactory.Create(
                createInfo.ColliderShapes,
                createInfo.FallbackGeometry,
                createInfo.FallbackShapeOffsetTranslation,
                createInfo.FallbackShapeOffsetRotation);
            return metadata is null
                ? null
                : CreateDynamicRigidBody(metadata, createInfo.Pose, createInfo.LayerMask, createInfo);
        }

        private JoltStaticRigidBody? CreateStaticRigidBody(
            JoltShapeMetadata metadata,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            PhysicsRigidBodyCreateInfo? createInfo)
        {
            try
            {
                using BodyCreationSettings bodySettings = new(
                    metadata.Shape,
                    pose.position,
                    JoltShapeFactory.NormalizeRotation(pose.rotation, nameof(pose)),
                    MotionType.Static,
                    layerMask.AsJoltObjectLayer());
                if (createInfo is PhysicsRigidBodyCreateInfo authored)
                    ApplyCreationSettings(bodySettings, metadata.Shape, authored, dynamic: false);

                Body body = _physicsSystem!.BodyInterface.CreateBody(bodySettings);
                BodyID bodyID = body.ID;
                if (bodyID.IsInvalid)
                {
                    System.Diagnostics.Debug.WriteLine("[JoltScene] CreateStaticRigidBody failed - body ID is invalid (max bodies reached?)");
                    metadata.Dispose();
                    return null;
                }

                JoltStaticRigidBody joltBody = new(bodyID);
                joltBody.AttachShapeMetadata(metadata);
                AddActor(joltBody);
                return joltBody;
            }
            catch
            {
                metadata.Dispose();
                throw;
            }
        }

        private JoltDynamicRigidBody? CreateDynamicRigidBody(
            JoltShapeMetadata metadata,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            PhysicsRigidBodyCreateInfo? createInfo)
        {
            try
            {
                MotionType motionType = createInfo is PhysicsRigidBodyCreateInfo authored
                    && authored.BodyFlags.HasFlag(PhysicsRigidBodyFlags.Kinematic)
                        ? MotionType.Kinematic
                        : MotionType.Dynamic;
                using BodyCreationSettings bodySettings = new(
                    metadata.Shape,
                    pose.position,
                    JoltShapeFactory.NormalizeRotation(pose.rotation, nameof(pose)),
                    motionType,
                    layerMask.AsJoltObjectLayer());
                if (createInfo is PhysicsRigidBodyCreateInfo settings)
                    ApplyCreationSettings(bodySettings, metadata.Shape, settings, dynamic: true);

                Body body = _physicsSystem!.BodyInterface.CreateBody(bodySettings);
                BodyID bodyID = body.ID;
                if (bodyID.IsInvalid)
                {
                    System.Diagnostics.Debug.WriteLine("[JoltScene] CreateDynamicRigidBody failed - body ID is invalid (max bodies reached?)");
                    metadata.Dispose();
                    return null;
                }

                JoltDynamicRigidBody joltBody = new(bodyID, createInfo?.GravityEnabled ?? true);
                joltBody.AttachShapeMetadata(metadata);
                AddActor(joltBody);
                return joltBody;
            }
            catch
            {
                metadata.Dispose();
                throw;
            }
        }

        private static void ApplyCreationSettings(
            BodyCreationSettings bodySettings,
            Shape shape,
            in PhysicsRigidBodyCreateInfo createInfo,
            bool dynamic)
        {
            (float friction, float restitution, float damping) = ResolveMaterial(createInfo);
            bodySettings.Friction = friction;
            bodySettings.Restitution = restitution;
            bodySettings.LinearDamping = damping;
            bodySettings.AngularDamping = damping;

            if (!dynamic)
                return;

            ValidateDynamicSettings(createInfo);
            bodySettings.GravityFactor = createInfo.GravityEnabled ? 1.0f : 0.0f;
            bodySettings.MaxLinearVelocity = createInfo.MaxLinearVelocity;
            bodySettings.MaxAngularVelocity = createInfo.MaxAngularVelocity;
            bodySettings.NumPositionStepsOverride = createInfo.SolverIterations.MinPositionIterations;
            bodySettings.NumVelocityStepsOverride = createInfo.SolverIterations.MinVelocityIterations;
            bodySettings.AllowedDOFs = ResolveAllowedDegreesOfFreedom(createInfo.LockFlags);
            bodySettings.MotionQuality = createInfo.BodyFlags.HasFlag(PhysicsRigidBodyFlags.EnableCcd)
                ? MotionQuality.LinearCast
                : MotionQuality.Discrete;

            float volume = shape.Volume;
            if (float.IsFinite(volume) && volume > 0.0f)
            {
                MassProperties massProperties = shape.MassProperties;
                massProperties.ScaleToMass(createInfo.Density * volume);
                bodySettings.OverrideMassProperties = OverrideMassProperties.MassAndInertiaProvided;
                bodySettings.MassPropertiesOverride = massProperties;
            }
        }

        private static (float friction, float restitution, float damping) ResolveMaterial(
            in PhysicsRigidBodyCreateInfo createInfo)
        {
            PhysicsMaterialDefinition? definition = createInfo.MaterialDefinition;
            if (definition is null)
            {
                for (int index = 0; index < createInfo.ColliderShapes.Count; index++)
                {
                    PhysicsColliderShape shape = createInfo.ColliderShapes[index];
                    if (shape.Enabled && shape.Material is not null)
                    {
                        definition = shape.Material;
                        break;
                    }
                }
            }

            float friction = definition?.DynamicFriction
                ?? createInfo.RuntimeMaterial?.DynamicFriction
                ?? 0.2f;
            float restitution = definition?.Restitution
                ?? createInfo.RuntimeMaterial?.Restitution
                ?? 0.0f;
            float damping = definition?.Damping
                ?? createInfo.RuntimeMaterial?.Damping
                ?? 0.05f;
            if (!float.IsFinite(friction) || friction < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(createInfo), "Physics friction must be finite and non-negative.");
            if (!float.IsFinite(restitution) || restitution < 0.0f || restitution > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(createInfo), "Physics restitution must be in [0, 1].");
            if (!float.IsFinite(damping) || damping < 0.0f || damping > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(createInfo), "Physics damping must be in [0, 1].");

            return (friction, restitution, damping);
        }

        private static void ValidateDynamicSettings(in PhysicsRigidBodyCreateInfo createInfo)
        {
            if (!float.IsFinite(createInfo.Density) || createInfo.Density <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(createInfo), "Rigid-body density must be finite and positive.");
            if (!float.IsFinite(createInfo.MaxLinearVelocity) || createInfo.MaxLinearVelocity < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(createInfo), "Maximum linear velocity must be finite and non-negative.");
            if (!float.IsFinite(createInfo.MaxAngularVelocity) || createInfo.MaxAngularVelocity < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(createInfo), "Maximum angular velocity must be finite and non-negative.");
        }

        private static AllowedDOFs ResolveAllowedDegreesOfFreedom(PhysicsLockFlags lockFlags)
        {
            AllowedDOFs allowed = AllowedDOFs.All;
            if (lockFlags.HasFlag(PhysicsLockFlags.LinearX)) allowed &= ~AllowedDOFs.TranslationX;
            if (lockFlags.HasFlag(PhysicsLockFlags.LinearY)) allowed &= ~AllowedDOFs.TranslationY;
            if (lockFlags.HasFlag(PhysicsLockFlags.LinearZ)) allowed &= ~AllowedDOFs.TranslationZ;
            if (lockFlags.HasFlag(PhysicsLockFlags.AngularX)) allowed &= ~AllowedDOFs.RotationX;
            if (lockFlags.HasFlag(PhysicsLockFlags.AngularY)) allowed &= ~AllowedDOFs.RotationY;
            if (lockFlags.HasFlag(PhysicsLockFlags.AngularZ)) allowed &= ~AllowedDOFs.RotationZ;
            return allowed;
        }

        internal bool TryReplaceCollisionShapes(
            JoltRigidActor actor,
            in PhysicsRigidBodyCreateInfo createInfo)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (_physicsSystem is null || actor.Scene != this || actor.BodyID.IsInvalid)
                return false;

            JoltShapeMetadata? metadata = JoltShapeFactory.Create(
                createInfo.ColliderShapes,
                createInfo.FallbackGeometry,
                createInfo.FallbackShapeOffsetTranslation,
                createInfo.FallbackShapeOffsetRotation);
            if (metadata is null)
                return false;

            try
            {
                Activation activation = _physicsSystem.BodyInterface.IsActive(actor.BodyID)
                    ? Activation.Activate
                    : Activation.DontActivate;
                _physicsSystem.BodyInterface.SetShape(
                    actor.BodyID,
                    metadata.Shape,
                    updateMassProperties: true,
                    activation);
                actor.ReplaceShapeMetadata(metadata);
                ApplyRuntimeBodySettings(actor, createInfo, activation);
                return true;
            }
            catch
            {
                metadata.Dispose();
                throw;
            }
        }

        private void ApplyRuntimeBodySettings(
            JoltRigidActor actor,
            in PhysicsRigidBodyCreateInfo createInfo,
            Activation activation)
        {
            PhysicsSystem physicsSystem = _physicsSystem
                ?? throw new InvalidOperationException("Cannot update a rigid body without an active Jolt physics system.");
            (float friction, float restitution, float damping) = ResolveMaterial(createInfo);
            physicsSystem.BodyInterface.SetFriction(actor.BodyID, friction);
            physicsSystem.BodyInterface.SetRestitution(actor.BodyID, restitution);
            physicsSystem.BodyInterface.SetObjectLayer(actor.BodyID, createInfo.LayerMask.AsJoltObjectLayer());

            if (actor is not JoltDynamicRigidBody dynamicBody)
                return;

            ValidateDynamicSettings(createInfo);
            physicsSystem.BodyInterface.SetMotionType(
                actor.BodyID,
                createInfo.BodyFlags.HasFlag(PhysicsRigidBodyFlags.Kinematic)
                    ? MotionType.Kinematic
                    : MotionType.Dynamic,
                activation);
            dynamicBody.SetGravityEnabled(createInfo.GravityEnabled);
            dynamicBody.SetMotionQualityFromFlags(createInfo.BodyFlags);
            dynamicBody.SetLinearAndAngularDamping(damping, damping);
            dynamicBody.SetLockFlags(createInfo.LockFlags);

            Shape? replacementShape = physicsSystem.BodyInterface.GetShape(actor.BodyID);
            float volume = replacementShape?.Volume ?? 0.0f;
            if (float.IsFinite(volume) && volume > 0.0f)
                dynamicBody.SetMass(createInfo.Density * volume);
        }

        public override void AddActor(IAbstractPhysicsActor actor)
        {
            if (actor is not JoltActor joltActor)
                return;
            
            if (_physicsSystem is null)
                return;

            if (_actors.TryGetValue(joltActor.BodyID, out JoltActor? existing))
            {
                if (ReferenceEquals(existing, joltActor))
                    return;

                throw new InvalidOperationException(
                    $"Jolt body ID {joltActor.BodyID} is already owned by another actor wrapper.");
            }

            if (joltActor.Scene is not null && joltActor.Scene != this)
                throw new InvalidOperationException("Cannot add a Jolt actor that belongs to another scene.");

            // Validate body ID before adding
            if (joltActor.BodyID.IsInvalid)
            {
                System.Diagnostics.Debug.WriteLine("[JoltScene] AddActor failed - body ID is invalid");
                return;
            }

            _physicsSystem.BodyInterface.AddBody(joltActor.BodyID, Activation.Activate);
            _actors[joltActor.BodyID] = joltActor;
            
            if (joltActor is JoltRigidActor rigidActor)
            {
                _rigidActors[joltActor.BodyID] = rigidActor;
                
                if (rigidActor is JoltStaticRigidBody staticBody)
                    _staticBodies[joltActor.BodyID] = staticBody;
                else if (rigidActor is JoltDynamicRigidBody dynamicBody)
                    _dynamicBodies[joltActor.BodyID] = dynamicBody;
            }
            
            joltActor.OnAddedToScene(this);
        }

        public override void Destroy()
        {
            if (_physicsSystem is null)
                return;

            _physicsSystem.OnContactAdded -= OnContactAdded;
            _physicsSystem.OnContactPersisted -= OnContactPersisted;

            foreach (IAbstractJoint joint in _joints.ToArray())
                joint.Release();

            foreach (IJoltCharacterController controller in _characterControllers.ToArray())
                controller.RequestRelease();

            // Remove all actors
            foreach (var actor in _actors.Values.ToArray())
                actor.Destroy();
            
            _actors.Clear();
            _rigidActors.Clear();
            _staticBodies.Clear();
            _dynamicBodies.Clear();
            _joints.Clear();
            _characterControllers.Clear();
            lock (_debugContactsLock)
                _debugContactCount = 0;
            if (!_worldAnchorBodyID.IsInvalid && _physicsSystem.BodyInterface.IsAdded(_worldAnchorBodyID))
                _physicsSystem.BodyInterface.RemoveAndDestroyBody(_worldAnchorBodyID);
            _worldAnchorBodyID = BodyID.Invalid;

            _debugRenderer?.Dispose();
            _debugRenderer = null;

            (_physicsSystem as IDisposable)?.Dispose();
            _physicsSystem = null;

            (_jobSystem as IDisposable)?.Dispose();
            _jobSystem = null;
        }

        // Collision filtering based on Jolt object-layer masks.
        private const int NumBroadPhaseLayers = 2;
        private ObjectLayerPairFilterMask? _objectLayerPairFilter;
        private BroadPhaseLayerInterfaceMask? _broadPhaseLayerInterface;
        private ObjectVsBroadPhaseLayerFilterMask? _objectVsBroadPhaseLayerFilter;

        public override void Initialize()
        {
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jolt_init.log");
            
            try
            {
                Console.WriteLine("[JoltScene] Initialize() starting...");
                try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] JoltScene.Initialize() starting...{Environment.NewLine}"); } catch { }
                
                JoltBootstrap.EnsureInitialized();
                Console.WriteLine("[JoltScene] JoltBootstrap.EnsureInitialized() completed.");
                try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] JoltBootstrap.EnsureInitialized() completed.{Environment.NewLine}"); } catch { }

                // Create job system with default config (simpler, less likely to fail)
                Console.WriteLine("[JoltScene] Creating JobSystemThreadPool...");
                try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] Creating JobSystemThreadPool...{Environment.NewLine}"); } catch { }
                
                _jobSystem = new JobSystemThreadPool();
                
                Console.WriteLine("[JoltScene] JobSystemThreadPool created successfully.");
                try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] JobSystemThreadPool created successfully.{Environment.NewLine}"); } catch { }

                // Set up collision filtering (required by PhysicsSystem)
                _objectLayerPairFilter = new ObjectLayerPairFilterMask();
                _broadPhaseLayerInterface = new BroadPhaseLayerInterfaceMask(NumBroadPhaseLayers);
                var staticLayer = new BroadPhaseLayer(0);
                var movingLayer = new BroadPhaseLayer(1);
                _broadPhaseLayerInterface.ConfigureLayer(staticLayer, 1u << 0, 0u);
                _broadPhaseLayerInterface.ConfigureLayer(movingLayer, 0xFFFFFFFEu, 0u);
                _objectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterMask(_broadPhaseLayerInterface);

                PhysicsSystemSettings settings = new()
                {
                    MaxBodies = 1024,
                    NumBodyMutexes = 0,
                    MaxBodyPairs = 1024,
                    MaxContactConstraints = 1024,
                    ObjectLayerPairFilter = _objectLayerPairFilter,
                    BroadPhaseLayerInterface = _broadPhaseLayerInterface,
                    ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhaseLayerFilter,
                };

                System.Diagnostics.Debug.WriteLine("[JoltScene] Creating PhysicsSystem...");
                PhysicsSystem system = new(settings);
                System.Diagnostics.Debug.WriteLine("[JoltScene] PhysicsSystem created successfully.");

                system.Gravity = new Vector3(0, -9.81f, 0);
                system.Settings = new PhysicsSettings
                {
                    DeterministicSimulation = true,
                    NumPositionSteps = 1,
                    NumVelocitySteps = 1,
                    MaxInFlightBodyPairs = 1024,
                    MinVelocityForRestitution = 0.1f,
                    AllowSleeping = true,
                    SpeculativeContactDistance = 0.1f,
                    Baumgarte = 1.0f,
                    BodyPairCacheCosMaxDeltaRotationDiv2 = 0.1f,
                    BodyPairCacheMaxDeltaPositionSq = 0.1f * 0.1f,
                    CheckActiveEdges = true,
                    ConstraintWarmStart = true,
                    ContactNormalCosMaxDeltaRotation = 0.1f,
                    LinearCastMaxPenetration = 0.1f,
                    LinearCastThreshold = 0.1f,
                    ManifoldTolerance = 0.1f,
                    ContactPointPreserveLambdaMaxDistSq = 0.1f * 0.1f,
                    MaxPenetrationDistance = 0.1f,
                    PenetrationSlop = 0.1f,
                    PointVelocitySleepThreshold = 0.1f,
                    StepListenerBatchesPerJob = 1,
                    StepListenersBatchSize = 1,
                    TimeBeforeSleep = 0.1f,
                    UseLargeIslandSplitter = true,
                    UseBodyPairContactCache = true,
                    UseManifoldReduction = true,
                };
                _physicsSystem = system;
                system.OnContactAdded += OnContactAdded;
                system.OnContactPersisted += OnContactPersisted;
                System.Diagnostics.Debug.WriteLine("[JoltScene] Initialize() completed successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JoltScene] Initialize() FAILED: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[JoltScene] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public override void NotifyShapeChanged(IAbstractPhysicsActor actor)
        {
            if (actor is not JoltActor joltActor || _physicsSystem is null || joltActor.BodyID.IsInvalid)
                return;

            Vector3 previousCenterOfMass = _physicsSystem.BodyInterface.GetCenterOfMassPosition(joltActor.BodyID);
            _physicsSystem.BodyInterface.NotifyShapeChanged(
                joltActor.BodyID,
                previousCenterOfMass,
                updateMassProperties: true,
                Activation.Activate);
        }

        internal bool TrySetShapeInPlace(
            JoltActor actor,
            Shape shape,
            bool updateMassProperties = true,
            bool activate = true)
        {
            ArgumentNullException.ThrowIfNull(actor);
            ArgumentNullException.ThrowIfNull(shape);

            if (_physicsSystem is null || actor.BodyID.IsInvalid || actor.Scene != this)
                return false;

            _physicsSystem.BodyInterface.SetShape(
                actor.BodyID,
                shape,
                updateMassProperties,
                activate ? Activation.Activate : Activation.DontActivate);
            return true;
        }

        private const uint InvalidFaceIndex = uint.MaxValue;

        private static bool ShouldPopulateHitDetail(IAbstractQueryFilter? filter, PhysicsQueryHitDetail detail)
        {
            PhysicsQueryHitDetail requested = filter?.HitDetail ?? PhysicsQueryHitDetail.Default;
            return requested == PhysicsQueryHitDetail.Default || requested.HasFlag(detail);
        }

        private bool TryGetIncludedQueryActor(
            BodyID bodyID,
            LayerMask layerMask,
            bool includeStatic,
            bool includeDynamic,
            out JoltActor? actor)
        {
            actor = null;
            if (_physicsSystem is null || !_actors.TryGetValue(bodyID, out JoltActor? candidate))
                return false;

            if (!IsBodyLayerIncluded(bodyID, layerMask))
                return false;

            MotionType motionType = _physicsSystem.BodyInterface.GetMotionType(bodyID);
            if (motionType == MotionType.Static ? !includeStatic : !includeDynamic)
                return false;

            actor = candidate;
            return true;
        }

        private uint ResolveFaceIndex(BodyID bodyID, SubShapeID subShapeID)
            => _rigidActors.TryGetValue(bodyID, out JoltRigidActor? actor)
                ? actor.ResolveFaceIndex(subShapeID)
                : InvalidFaceIndex;

        private Vector2 ResolveBarycentricUV(
            BodyID bodyID,
            SubShapeID subShapeID,
            Vector3 worldPosition,
            IAbstractQueryFilter? filter)
        {
            if (filter?.HitDetail.HasFlag(PhysicsQueryHitDetail.UV) != true)
                return Vector2.Zero;

            return _rigidActors.TryGetValue(bodyID, out JoltRigidActor? actor)
                && actor.TryResolveBarycentricUV(subShapeID, worldPosition, out Vector2 uv)
                    ? uv
                    : Vector2.Zero;
        }

        private bool TryGetWorldSpaceSurfaceNormal(
            BodyID bodyID,
            SubShapeID subShapeID,
            Vector3 worldPosition,
            out Vector3 normal)
        {
            normal = Vector3.Zero;
            if (_physicsSystem is null)
                return false;

            BodyLockRead bodyLock = default;
            BodyLockInterface lockInterface = _physicsSystem.BodyLockInterface;
            lockInterface.LockRead(bodyID, out bodyLock);
            try
            {
                if (!bodyLock.Succeeded || bodyLock.Body is not Body body)
                    return false;

                normal = NormalizeOrZero(body.GetWorldSpaceSurfaceNormal(subShapeID, worldPosition));
                return normal != Vector3.Zero;
            }
            finally
            {
                lockInterface.UnlockRead(bodyLock);
            }
        }

        private static Vector3 NormalizeOrZero(Vector3 value)
        {
            float lengthSquared = value.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
                return Vector3.Zero;

            return value / MathF.Sqrt(lengthSquared);
        }

        private static Matrix4x4 CreateQueryCenterOfMassTransform(
            Shape shape,
            (Vector3 position, Quaternion rotation) pose)
        {
            Quaternion rotation = JoltShapeFactory.NormalizeRotation(pose.rotation, nameof(pose));
            Vector3 centerOfMassPosition = pose.position + Vector3.Transform(shape.CenterOfMass, rotation);
            Matrix4x4 systemNumericsTransform =
                Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(centerOfMassPosition);

            // Jolt consumes column-vector transforms while System.Numerics composes row-vector transforms.
            return Matrix4x4.Transpose(systemNumericsTransform);
        }

        private static int CompareRayHits(RayCastResult left, RayCastResult right)
        {
            int comparison = left.Fraction.CompareTo(right.Fraction);
            if (comparison != 0)
                return comparison;

            comparison = left.BodyID.ID.CompareTo(right.BodyID.ID);
            return comparison != 0 ? comparison : left.subShapeID2.CompareTo(right.subShapeID2);
        }

        private static int CompareShapeCastHits(ShapeCastResult left, ShapeCastResult right)
        {
            int comparison = left.Fraction.CompareTo(right.Fraction);
            if (comparison != 0)
                return comparison;

            if (left.Fraction <= 0.0f)
            {
                comparison = right.PenetrationDepth.CompareTo(left.PenetrationDepth);
                if (comparison != 0)
                    return comparison;
            }

            comparison = left.BodyID2.ID.CompareTo(right.BodyID2.ID);
            return comparison != 0 ? comparison : left.SubShapeID2.Value.CompareTo(right.SubShapeID2.Value);
        }

        private static int CompareOverlapHits(CollideShapeResult left, CollideShapeResult right)
        {
            int comparison = left.BodyID2.ID.CompareTo(right.BodyID2.ID);
            if (comparison != 0)
                return comparison;

            comparison = left.SubShapeID2.Value.CompareTo(right.SubShapeID2.Value);
            return comparison != 0 ? comparison : right.PenetrationDepth.CompareTo(left.PenetrationDepth);
        }

        public override bool OverlapAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            if (_physicsSystem is null)
                return false;

            using Shape shape = JoltShapeFactory.CreateShape(geometry);
            if (shape is null)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);
            Matrix4x4 worldTransform = CreateQueryCenterOfMassTransform(shape, pose);

            List<CollideShapeResult> collideResults = [];
            _physicsSystem.NarrowPhaseQuery.CollideShape(
                shape,
                Vector3.One,
                worldTransform,
                Vector3.Zero,
                CollisionCollectorType.AllHit,
                collideResults);
            collideResults.Sort(CompareOverlapHits);

            foreach (CollideShapeResult result in collideResults)
            {
                if (!TryGetIncludedQueryActor(
                    result.BodyID2,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out JoltActor? hitActor))
                    continue;

                if (!results.TryGetValue(0.0f, out var list))
                    results.Add(0.0f, list = []);

                uint faceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                    ? ResolveFaceIndex(result.BodyID2, result.SubShapeID2)
                    : InvalidFaceIndex;
                list.Add((hitActor!.GetOwningComponent(), new OverlapHit { FaceIndex = faceIndex }));
                return true;
            }

            return false;
        }

        public override bool OverlapMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            if (_physicsSystem is null)
                return false;

            using Shape shape = JoltShapeFactory.CreateShape(geometry);
            if (shape is null)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);
            Matrix4x4 worldTransform = CreateQueryCenterOfMassTransform(shape, pose);

            List<CollideShapeResult> collideResults = [];
            _physicsSystem.NarrowPhaseQuery.CollideShape(
                shape,
                Vector3.One,
                worldTransform,
                Vector3.Zero,
                CollisionCollectorType.AllHit,
                collideResults);
            collideResults.Sort(CompareOverlapHits);

            bool hasHit = false;
            foreach (CollideShapeResult result in collideResults)
            {
                if (!TryGetIncludedQueryActor(
                    result.BodyID2,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out JoltActor? hitActor))
                    continue;

                if (!results.TryGetValue(0.0f, out var list))
                    results.Add(0.0f, list = []);

                uint faceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                    ? ResolveFaceIndex(result.BodyID2, result.SubShapeID2)
                    : InvalidFaceIndex;
                list.Add((hitActor!.GetOwningComponent(), new OverlapHit { FaceIndex = faceIndex }));
                hasHit = true;
            }

            return hasHit;
        }

        public override bool RaycastAny(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex)
        {
            hitFaceIndex = InvalidFaceIndex;
            
            if (_physicsSystem is null)
                return false;

            Vector3 start = worldSegment.Start;
            Vector3 direction = worldSegment.End - start;
            float distance = direction.Length();
            if (!float.IsFinite(distance) || distance <= float.Epsilon)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);

            List<RayCastResult> hits = [];
            _physicsSystem.NarrowPhaseQuery.CastRay(
                new Ray(start, direction),
                new RayCastSettings(),
                CollisionCollectorType.AllHit,
                hits);
            hits.Sort(CompareRayHits);

            foreach (RayCastResult hit in hits)
            {
                if (!TryGetIncludedQueryActor(
                    hit.BodyID,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out _))
                    continue;

                hitFaceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                    ? ResolveFaceIndex(hit.BodyID, new SubShapeID(hit.subShapeID2))
                    : InvalidFaceIndex;
                return true;
            }

            return false;
        }

        public override bool RaycastMultiple(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            if (_physicsSystem is null)
                return false;

            Vector3 start = worldSegment.Start;
            Vector3 rayVector = worldSegment.End - start;
            float distance = rayVector.Length();
            if (!float.IsFinite(distance) || distance <= float.Epsilon)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);

            List<RayCastResult> hits = [];
            _physicsSystem.NarrowPhaseQuery.CastRay(
                new Ray(start, rayVector),
                new RayCastSettings(),
                CollisionCollectorType.AllHit,
                hits);
            hits.Sort(CompareRayHits);
            
            bool hasHit = false;
            foreach (RayCastResult hit in hits)
            {
                if (!TryGetIncludedQueryActor(
                    hit.BodyID,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out JoltActor? hitActor))
                    continue;

                float hitDistance = hit.Fraction * distance;
                Vector3 hitPosition = start + hit.Fraction * rayVector;
                SubShapeID subShapeID = new(hit.subShapeID2);
                Vector3 normal = Vector3.Zero;
                if (ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.Normal))
                    TryGetWorldSpaceSurfaceNormal(hit.BodyID, subShapeID, hitPosition, out normal);

                if (!results.TryGetValue(hitDistance, out var list))
                    results.Add(hitDistance, list = []);

                list.Add((hitActor!.GetOwningComponent(), new RaycastHit
                {
                    Position = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.Position) ? hitPosition : Vector3.Zero,
                    Normal = normal,
                    Distance = hitDistance,
                    FaceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                        ? ResolveFaceIndex(hit.BodyID, subShapeID)
                        : InvalidFaceIndex,
                    UV = ResolveBarycentricUV(hit.BodyID, subShapeID, hitPosition, filter),
                }));
                hasHit = true;
            }
            
            return hasHit;
        }

        public override bool RaycastSingleAsync(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> items,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback)
        {
            if (_physicsSystem is null)
            {
                finishedCallback?.Invoke(items);
                return false;
            }

            Vector3 start = worldSegment.Start;
            Vector3 rayVector = worldSegment.End - start;
            float distance = rayVector.Length();
            if (!float.IsFinite(distance) || distance <= float.Epsilon)
            {
                finishedCallback?.Invoke(items);
                return false;
            }

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);

            List<RayCastResult> hits = [];
            _physicsSystem.NarrowPhaseQuery.CastRay(
                new Ray(start, rayVector),
                new RayCastSettings(),
                CollisionCollectorType.AllHit,
                hits);
            hits.Sort(CompareRayHits);

            foreach (RayCastResult hit in hits)
            {
                if (!TryGetIncludedQueryActor(
                    hit.BodyID,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out JoltActor? hitActor))
                    continue;

                float hitDistance = hit.Fraction * distance;
                Vector3 hitPosition = start + hit.Fraction * rayVector;
                SubShapeID subShapeID = new(hit.subShapeID2);
                Vector3 normal = Vector3.Zero;
                if (ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.Normal))
                    TryGetWorldSpaceSurfaceNormal(hit.BodyID, subShapeID, hitPosition, out normal);

                if (!items.TryGetValue(hitDistance, out var list))
                    items.Add(hitDistance, list = []);

                list.Add((hitActor!.GetOwningComponent(), new RaycastHit
                {
                    Position = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.Position) ? hitPosition : Vector3.Zero,
                    Normal = normal,
                    Distance = hitDistance,
                    FaceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                        ? ResolveFaceIndex(hit.BodyID, subShapeID)
                        : InvalidFaceIndex,
                    UV = ResolveBarycentricUV(hit.BodyID, subShapeID, hitPosition, filter),
                }));

                finishedCallback?.Invoke(items);
                return true;
            }
            
            finishedCallback?.Invoke(items);
            return false;
        }

        public override void RemoveActor(IAbstractPhysicsActor actor)
        {
            if (actor is not JoltActor joltActor)
                return;
            
            if (_physicsSystem is null)
                return;

            _physicsSystem.BodyInterface.RemoveBody(joltActor.BodyID);
            _actors.Remove(joltActor.BodyID);
            _rigidActors.Remove(joltActor.BodyID);
            _staticBodies.Remove(joltActor.BodyID);
            _dynamicBodies.Remove(joltActor.BodyID);
            
            joltActor.OnRemovedFromScene(this);
        }

        internal void DestroyActor(JoltActor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);

            if (_physicsSystem is null || actor.Scene != this || actor.BodyID.IsInvalid)
                return;

            BodyID bodyID = actor.BodyID;
            if (_physicsSystem.BodyInterface.IsAdded(bodyID))
                _physicsSystem.BodyInterface.RemoveAndDestroyBody(bodyID);
            else
                _physicsSystem.BodyInterface.DestroyBody(bodyID);

            _actors.Remove(bodyID);
            _rigidActors.Remove(bodyID);
            _staticBodies.Remove(bodyID);
            _dynamicBodies.Remove(bodyID);
            if (actor is JoltRigidActor rigidActor)
                rigidActor.ReleaseShapeMetadata();
            actor.OnRemovedFromScene(this);
        }

        public static ObjectLayer GetObjectLayer(uint group, uint mask)
            => ObjectLayerPairFilterMask.GetObjectLayer(group, mask);

        internal Vector3 GetBodyLinearVelocity(BodyID bodyID)
            => _physicsSystem is null ? Vector3.Zero : _physicsSystem.BodyInterface.GetLinearVelocity(bodyID);

        internal Vector3 GetBodyAngularVelocity(BodyID bodyID)
            => _physicsSystem is null ? Vector3.Zero : _physicsSystem.BodyInterface.GetAngularVelocity(bodyID);

        internal Vector3 GetBodyPosition(BodyID bodyID)
            => _physicsSystem is null ? Vector3.Zero : _physicsSystem.BodyInterface.GetPosition(bodyID);

        internal bool IsBodyLayerIncluded(BodyID bodyID, LayerMask layerMask)
        {
            if (_physicsSystem is null)
                return false;

            uint queryMask = unchecked((uint)layerMask.Value);
            if (queryMask == 0)
                return true;

            ObjectLayer objectLayer = _physicsSystem.BodyInterface.GetObjectLayer(bodyID);
            uint bodyGroup = ObjectLayerPairFilterMask.GetGroup(objectLayer);
            return (queryMask & bodyGroup) != 0;
        }

        internal BodyID EnsureWorldAnchorBody()
        {
            if (_physicsSystem is null)
                return BodyID.Invalid;

            if (!_worldAnchorBodyID.IsInvalid)
                return _worldAnchorBodyID;

            using BoxShape shape = new(new Vector3(0.05f), 0.0f);
            using BodyCreationSettings settings = new(
                shape,
                Vector3.Zero,
                Quaternion.Identity,
                MotionType.Static,
                ObjectLayerPairFilterMask.GetObjectLayer(0u, 0u));

            _worldAnchorBodyID = _physicsSystem.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate);
            return _worldAnchorBodyID;
        }

        private bool TryGetBody(BodyID bodyID, out Body? body)
        {
            body = default;
            if (_physicsSystem is null || bodyID.IsInvalid)
                return false;

            var lockInterface = _physicsSystem.BodyLockInterface;
            BodyLockRead bodyLock = default;
            lockInterface.LockRead(bodyID, out bodyLock);
            try
            {
                if (!bodyLock.Succeeded)
                    return false;

                body = bodyLock.Body;
                return body is not null;
            }
            finally
            {
                lockInterface.UnlockRead(bodyLock);
            }
        }

        internal bool WithBodyWrite(BodyID bodyID, Action<Body> action)
        {
            if (_physicsSystem is null || bodyID.IsInvalid)
                return false;

            var lockInterface = _physicsSystem.BodyLockInterface;
            BodyLockWrite bodyLock = default;
            lockInterface.LockWrite(bodyID, out bodyLock);
            try
            {
                if (!bodyLock.Succeeded)
                    return false;

                Body? body = bodyLock.Body;
                if (body is null)
                    return false;

                action(body);
                return true;
            }
            finally
            {
                lockInterface.UnlockWrite(bodyLock);
            }
        }

        private BodyID ResolveJoltBody(IAbstractPhysicsActor? actor)
            => actor is JoltActor jolt ? jolt.BodyID : EnsureWorldAnchorBody();

        private bool TryBuildConstraint<TConstraint>(
            BodyID bodyA,
            BodyID bodyB,
            Func<Body, Body, TConstraint> create,
            out TConstraint? constraint)
            where TConstraint : Constraint
        {
            constraint = null;

            if (_physicsSystem is null)
                return false;

            if (!TryGetBody(bodyA, out Body? a) || a is null || !TryGetBody(bodyB, out Body? b) || b is null)
                return false;

            constraint = create(a, b);
            _physicsSystem.AddConstraint(constraint);
            return true;
        }

        public override void StepSimulation()
        {
            if (_physicsSystem is null || _jobSystem is null)
                return;

            // Mirror PhysX behavior: consume queued character controller movement on the fixed step
            // so movement + collision resolution happen deterministically with the physics update.
            float dt = RuntimePhysicsServices.Current.FixedDeltaSeconds;
            if (dt > 0.0f && _characterControllers.Count > 0)
            {
                foreach (var controller in _characterControllers)
                    controller.ConsumeInputBuffer(dt);
            }

            _captureDebugContacts = RuntimePhysicsServices.Current.VisualizeSettings.VisualizeEnabled
                || RuntimePhysicsServices.Current.JoltDebugRenderDiagnostics;
            if (_captureDebugContacts)
            {
                lock (_debugContactsLock)
                    _debugContactCount = 0;
            }

            _physicsSystem.Update(RuntimePhysicsServices.Current.FixedDeltaSeconds, 3, _jobSystem);
            PublishDebugFrame();

            foreach (JoltDynamicRigidBody body in _dynamicBodies.Values)
                if (body.OwningComponent is IRuntimePhysicsStepListener listener)
                    listener.OnPhysicsStepped();

            NotifySimulationStepped();
        }


        private static void GetQueryActorTypeInclusion(
            IAbstractQueryFilter? filter,
            out bool includeStatic,
            out bool includeDynamic)
        {
            PhysicsQueryActorTypes actorTypes = filter?.ActorTypes ?? PhysicsQueryActorTypes.All;
            includeStatic = actorTypes.HasFlag(PhysicsQueryActorTypes.Static);
            includeDynamic = actorTypes.HasFlag(PhysicsQueryActorTypes.Dynamic);

            if (!includeStatic && !includeDynamic)
            {
                includeStatic = true;
                includeDynamic = true;
            }
        }

        private static Shape CreateSweepQueryShape(IPhysicsGeometry geometry, IAbstractQueryFilter? filter)
        {
            float inflation = filter?.SweepInflation ?? 0.0f;
            if (!float.IsFinite(inflation) || inflation < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(filter), inflation, "Sweep inflation must be finite and non-negative.");

            if (inflation == 0.0f)
                return JoltShapeFactory.CreateShape(geometry);

            return geometry switch
            {
                IPhysicsGeometry.Sphere sphere => new SphereShape(sphere.Radius + inflation),
                IPhysicsGeometry.Box box => new BoxShape(box.HalfExtents + new Vector3(inflation)),
                IPhysicsGeometry.Capsule capsule => new CapsuleShape(capsule.HalfHeight, capsule.Radius + inflation),
                _ => throw new NotSupportedException(
                    $"Jolt sweep inflation is exact only for sphere, box, and capsule query geometry; {geometry.GetType().Name} cannot be inflated without changing its authored shape."),
            };
        }

        private static bool TryCreateSweepDisplacement(Vector3 direction, float distance, out Vector3 displacement)
        {
            displacement = Vector3.Zero;
            if (!float.IsFinite(distance) || distance <= 0.0f)
                return false;

            Vector3 normalizedDirection = NormalizeOrZero(direction);
            if (normalizedDirection == Vector3.Zero)
                return false;

            displacement = normalizedDirection * distance;
            return true;
        }

        private SweepHit CreateSweepHit(ShapeCastResult result, float distance, IAbstractQueryFilter? filter)
            => new()
            {
                Position = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.Position)
                    ? result.ContactPointOn2
                    : Vector3.Zero,
                Normal = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.Normal)
                    ? NormalizeOrZero(-result.PenetrationAxis)
                    : Vector3.Zero,
                Distance = result.Fraction * distance,
                FaceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                    ? ResolveFaceIndex(result.BodyID2, result.SubShapeID2)
                    : InvalidFaceIndex,
            };

        public override bool SweepAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex)
        {
            hitFaceIndex = InvalidFaceIndex;
            
            if (_physicsSystem is null)
                return false;

            if (!TryCreateSweepDisplacement(unitDir, distance, out Vector3 displacement))
                return false;

            using Shape shape = CreateSweepQueryShape(geometry, filter);
            if (shape is null)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);
            Matrix4x4 worldTransform = CreateQueryCenterOfMassTransform(shape, pose);

            List<ShapeCastResult> sweepResults = [];
            _physicsSystem.NarrowPhaseQuery.CastShape(
                shape,
                worldTransform,
                displacement,
                Vector3.Zero,
                CollisionCollectorType.AllHit,
                sweepResults);
            sweepResults.Sort(CompareShapeCastHits);

            foreach (ShapeCastResult result in sweepResults)
            {
                if (!TryGetIncludedQueryActor(
                    result.BodyID2,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out _))
                    continue;

                hitFaceIndex = ShouldPopulateHitDetail(filter, PhysicsQueryHitDetail.FaceIndex)
                    ? ResolveFaceIndex(result.BodyID2, result.SubShapeID2)
                    : InvalidFaceIndex;
                return true;
            }
            
            return false;
        }

        public override bool SweepMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            if (_physicsSystem is null)
                return false;

            if (!TryCreateSweepDisplacement(unitDir, distance, out Vector3 displacement))
                return false;

            using Shape shape = CreateSweepQueryShape(geometry, filter);
            if (shape is null)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);
            Matrix4x4 worldTransform = CreateQueryCenterOfMassTransform(shape, pose);

            List<ShapeCastResult> sweepResults = [];
            _physicsSystem.NarrowPhaseQuery.CastShape(
                shape,
                worldTransform,
                displacement,
                Vector3.Zero,
                CollisionCollectorType.AllHit,
                sweepResults);
            sweepResults.Sort(CompareShapeCastHits);

            bool hasHit = false;
            foreach (ShapeCastResult result in sweepResults)
            {
                if (!TryGetIncludedQueryActor(
                    result.BodyID2,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out JoltActor? hitActor))
                    continue;

                float hitDistance = result.Fraction * distance;
                if (!results.TryGetValue(hitDistance, out var list))
                    results.Add(hitDistance, list = []);

                list.Add((hitActor!.GetOwningComponent(), CreateSweepHit(result, distance, filter)));

                hasHit = true;
            }

            return hasHit;
        }

        public override bool SweepSingle(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> items)
        {
            if (_physicsSystem is null)
                return false;

            if (!TryCreateSweepDisplacement(unitDir, distance, out Vector3 displacement))
                return false;

            using Shape shape = CreateSweepQueryShape(geometry, filter);
            if (shape is null)
                return false;

            GetQueryActorTypeInclusion(filter, out bool includeStatic, out bool includeDynamic);
            Matrix4x4 worldTransform = CreateQueryCenterOfMassTransform(shape, pose);

            List<ShapeCastResult> sweepResults = [];
            _physicsSystem.NarrowPhaseQuery.CastShape(
                shape,
                worldTransform,
                displacement,
                Vector3.Zero,
                CollisionCollectorType.AllHit,
                sweepResults);
            sweepResults.Sort(CompareShapeCastHits);

            foreach (ShapeCastResult result in sweepResults)
            {
                if (!TryGetIncludedQueryActor(
                    result.BodyID2,
                    layerMask,
                    includeStatic,
                    includeDynamic,
                    out JoltActor? hitActor))
                    continue;

                float hitDistance = result.Fraction * distance;
                if (!items.TryGetValue(hitDistance, out var list))
                    items.Add(hitDistance, list = []);

                list.Add((hitActor!.GetOwningComponent(), CreateSweepHit(result, distance, filter)));

                return true;
            }

            return false;
        }

        // Helper methods for actor management
        public void AddActor(JoltActor actor) => AddActor((IAbstractPhysicsActor)actor);
        public void RemoveActor(JoltActor actor) => RemoveActor((IAbstractPhysicsActor)actor);
        
        public JoltActor? GetActor(BodyID bodyID)
            => _actors.TryGetValue(bodyID, out var actor) ? actor : null;
        
        public JoltRigidActor? GetRigidActor(BodyID bodyID)
            => _rigidActors.TryGetValue(bodyID, out var actor) ? actor : null;

        public JoltStaticRigidBody? GetStaticBody(BodyID bodyID)
            => _staticBodies.TryGetValue(bodyID, out var body) ? body : null;

        public JoltDynamicRigidBody? GetDynamicBody(BodyID bodyID)
            => _dynamicBodies.TryGetValue(bodyID, out var body) ? body : null;

        private static (Vector3 position, Quaternion rotation) ApplyShapeOffsetToPose(
            (Vector3 position, Quaternion rotation) pose,
            Vector3 offsetTranslation,
            Quaternion offsetRotation)
        {
            if (offsetTranslation == Vector3.Zero && offsetRotation == Quaternion.Identity)
                return pose;

            var adjustedRotation = pose.rotation * offsetRotation;
            var adjustedPosition = pose.position + Vector3.Transform(offsetTranslation, pose.rotation);
            return (adjustedPosition, adjustedRotation);
        }

        #region Joint Factory

        public override IAbstractFixedJoint CreateFixedJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
        {
            BodyID a = ResolveJoltBody(actorA);
            BodyID b = ResolveJoltBody(actorB);

            FixedConstraintSettings settings = new()
            {
                Space = ConstraintSpace.LocalToBodyCOM,
                AutoDetectPoint = false,
                Point1 = localFrameA.Position,
                Point2 = localFrameB.Position,
                AxisX1 = Vector3.UnitX,
                AxisY1 = Vector3.UnitY,
                AxisX2 = Vector3.UnitX,
                AxisY2 = Vector3.UnitY,
            };

            if (!TryBuildConstraint(a, b, (body1, body2) => new FixedConstraint(settings, body1, body2), out FixedConstraint? constraint)
                || constraint is null)
                throw new InvalidOperationException("Failed to create Jolt fixed joint.");

            var joint = new JoltFixedJoint(this, constraint, a, b, localFrameA, localFrameB);
            _joints.Add(joint);
            return joint;
        }

        public override IAbstractDistanceJoint CreateDistanceJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
        {
            BodyID a = ResolveJoltBody(actorA);
            BodyID b = ResolveJoltBody(actorB);

            DistanceConstraintSettings settings = new()
            {
                Space = ConstraintSpace.LocalToBodyCOM,
                Point1 = localFrameA.Position,
                Point2 = localFrameB.Position,
                MinDistance = 0.0f,
                MaxDistance = float.MaxValue,
                LimitsSpringSettings = new SpringSettings(SpringMode.StiffnessAndDamping, 0.0f, 0.0f)
            };

            if (!TryBuildConstraint(a, b, (body1, body2) => new DistanceConstraint(settings, body1, body2), out DistanceConstraint? constraint)
                || constraint is null)
                throw new InvalidOperationException("Failed to create Jolt distance joint.");

            var joint = new JoltDistanceJoint(this, constraint, a, b, localFrameA, localFrameB);
            _joints.Add(joint);
            return joint;
        }

        public override IAbstractHingeJoint CreateHingeJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
        {
            BodyID a = ResolveJoltBody(actorA);
            BodyID b = ResolveJoltBody(actorB);

            HingeConstraintSettings settings = new()
            {
                Space = ConstraintSpace.LocalToBodyCOM,
                Point1 = localFrameA.Position,
                Point2 = localFrameB.Position,
                HingeAxis1 = Vector3.UnitX,
                HingeAxis2 = Vector3.UnitX,
                NormalAxis1 = Vector3.UnitY,
                NormalAxis2 = Vector3.UnitY,
                LimitsMin = -float.Pi,
                LimitsMax = float.Pi,
                LimitsSpringSettings = new SpringSettings(SpringMode.StiffnessAndDamping, 0.0f, 0.0f),
                MotorSettings = new MotorSettings(),
                MaxFrictionTorque = float.MaxValue,
            };

            if (!TryBuildConstraint(a, b, (body1, body2) => new HingeConstraint(settings, body1, body2), out HingeConstraint? constraint)
                || constraint is null)
                throw new InvalidOperationException("Failed to create Jolt hinge joint.");

            var joint = new JoltHingeJoint(this, constraint, a, b, localFrameA, localFrameB);
            _joints.Add(joint);
            return joint;
        }

        public override IAbstractPrismaticJoint CreatePrismaticJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
        {
            BodyID a = ResolveJoltBody(actorA);
            BodyID b = ResolveJoltBody(actorB);

            SliderConstraintSettings settings = new()
            {
                Space = ConstraintSpace.LocalToBodyCOM,
                AutoDetectPoint = false,
                Point1 = localFrameA.Position,
                Point2 = localFrameB.Position,
                SliderAxis1 = Vector3.UnitX,
                SliderAxis2 = Vector3.UnitX,
                NormalAxis1 = Vector3.UnitY,
                NormalAxis2 = Vector3.UnitY,
                LimitsMin = 0.0f,
                LimitsMax = 0.0f,
                LimitsSpringSettings = new SpringSettings(SpringMode.StiffnessAndDamping, 0.0f, 0.0f),
                MotorSettings = new MotorSettings(),
                MaxFrictionForce = float.MaxValue,
            };

            if (!TryBuildConstraint(a, b, (body1, body2) => new SliderConstraint(settings, body1, body2), out SliderConstraint? constraint)
                || constraint is null)
                throw new InvalidOperationException("Failed to create Jolt prismatic joint.");

            var joint = new JoltPrismaticJoint(this, constraint, a, b, localFrameA, localFrameB);
            _joints.Add(joint);
            return joint;
        }

        public override IAbstractSphericalJoint CreateSphericalJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
        {
            BodyID a = ResolveJoltBody(actorA);
            BodyID b = ResolveJoltBody(actorB);

            SwingTwistConstraintSettings settings = new()
            {
                Space = ConstraintSpace.LocalToBodyCOM,
                Position1 = localFrameA.Position,
                Position2 = localFrameB.Position,
                TwistAxis1 = Vector3.UnitX,
                TwistAxis2 = Vector3.UnitX,
                PlaneAxis1 = Vector3.UnitY,
                PlaneAxis2 = Vector3.UnitY,
                SwingType = SwingType.Cone,
                NormalHalfConeAngle = float.Pi,
                PlaneHalfConeAngle = float.Pi,
                TwistMinAngle = -float.Pi,
                TwistMaxAngle = float.Pi,
                MaxFrictionTorque = float.MaxValue,
                SwingMotorSettings = new MotorSettings(),
                TwistMotorSettings = new MotorSettings(),
            };

            if (!TryBuildConstraint(a, b, (body1, body2) => new SwingTwistConstraint(settings, body1, body2), out SwingTwistConstraint? constraint)
                || constraint is null)
                throw new InvalidOperationException("Failed to create Jolt spherical joint.");

            var joint = new JoltSphericalJoint(this, constraint, a, b, localFrameA, localFrameB);
            _joints.Add(joint);
            return joint;
        }

        public override IAbstractD6Joint CreateD6Joint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB)
        {
            BodyID a = ResolveJoltBody(actorA);
            BodyID b = ResolveJoltBody(actorB);

            SixDOFConstraintSettings settings = new()
            {
                Space = ConstraintSpace.LocalToBodyCOM,
                Position1 = localFrameA.Position,
                Position2 = localFrameB.Position,
                AxisX1 = Vector3.UnitX,
                AxisY1 = Vector3.UnitY,
                AxisX2 = Vector3.UnitX,
                AxisY2 = Vector3.UnitY,
                SwingType = SwingType.Cone,
            };

            for (int i = 0; i < (int)SixDOFConstraintAxis.Count; i++)
                settings.MakeFixedAxis((SixDOFConstraintAxis)i);

            if (!TryBuildConstraint(a, b, (body1, body2) => new SixDOFConstraint(settings, body1, body2), out SixDOFConstraint? constraint)
                || constraint is null)
                throw new InvalidOperationException("Failed to create Jolt D6 joint.");

            var joint = new JoltD6Joint(this, constraint, a, b, localFrameA, localFrameB);
            _joints.Add(joint);
            return joint;
        }

        public override void RemoveJoint(IAbstractJoint joint)
        {
            if (joint is not JoltJointBase jolt)
                return;
            if (!_joints.Remove(joint))
                return;

            if (_physicsSystem is not null)
            {
                _physicsSystem.RemoveConstraint(jolt.NativeConstraint);
                (jolt.NativeConstraint as IDisposable)?.Dispose();
            }
        }


        public JoltPhysicsDiagnostics GetDiagnostics()
            => new(
                _actors.Count,
                _rigidActors.Count,
                _staticBodies.Count,
                _dynamicBodies.Count,
                _characterControllers.Count,
                _joints.Count);

        public JoltDebugRenderSnapshot GetDebugRenderSnapshot()
        {
            lock (_debugContactsLock)
            {
                return new JoltDebugRenderSnapshot(
                    _actors.Count,
                    _characterControllers.Count,
                    _joints.Count,
                    _debugContactCount);
            }
        }

        private void PublishDebugFrame()
        {
            PhysicsVisualizeSettings settings = RuntimePhysicsServices.Current.VisualizeSettings;
            bool diagnosticsEnabled = RuntimePhysicsServices.Current.JoltDebugRenderDiagnostics;

            JoltPhysicsDiagnostics diagnostics = GetDiagnostics();
            if (diagnosticsEnabled)
            {
                JoltDebugRenderSnapshot snapshot = GetDebugRenderSnapshot();
                System.Diagnostics.Debug.WriteLine(
                    $"[JoltScene] DebugRenderCollect actors={diagnostics.ActorCount} rigid={diagnostics.RigidActorCount} static={diagnostics.StaticBodyCount} dynamic={diagnostics.DynamicBodyCount} controllers={diagnostics.CharacterControllerCount} joints={diagnostics.JointCount} contacts={snapshot.ContactCount}");
            }

            PhysicsDebugFrameWriter? writer = DebugFrames.BeginWrite(
                PhysicsDebugSource.Jolt,
                PhysicsDebugDepthMode.DepthTested);
            if (writer is null)
                return;

            if (!settings.VisualizeEnabled || _physicsSystem is null)
            {
                writer.CompleteSourceCountsFromPublished();
                writer.Publish();
                return;
            }

            _debugRenderer ??= new JoltEngineDebugRenderer();
            _debugRenderer.NextFrame();
            _debugRenderer.BeginFrame(writer);

            try
            {
                DrawSettings drawSettings = new()
                {
                    DrawShape = settings.VisualizeCollisionShapes || settings.VisualizeSimulationMesh,
                    DrawShapeWireframe = true,
                    DrawBoundingBox = settings.VisualizeCollisionAabbs,
                    DrawCenterOfMassTransform = settings.VisualizeBodyMassAxes,
                    DrawWorldTransform = settings.VisualizeBodyAxes || settings.VisualizeActorAxes,
                    DrawVelocity = settings.VisualizeBodyLinearVelocity || settings.VisualizeBodyAngularVelocity,
                };
                _physicsSystem.DrawBodies(drawSettings, _debugRenderer);

                if (settings.VisualizeJointLocalFrames || settings.VisualizeJointLimits)
                {
                    _physicsSystem.DrawConstraints(_debugRenderer);
                    if (settings.VisualizeJointLocalFrames)
                        _physicsSystem.DrawConstraintReferenceFrame(_debugRenderer);
                    if (settings.VisualizeJointLimits)
                        _physicsSystem.DrawConstraintLimits(_debugRenderer);
                }

                uint cyan = PhysicsDebugColor.Pack(ColorF4.Cyan);
                foreach (IJoltCharacterController controller in _characterControllers)
                {
                    Vector3 up = controller.UpDirection;
                    float halfCylinderHeight = MathF.Max(
                        0.0f,
                        controller.TotalHeight - 2.0f * controller.Radius) * 0.5f;
                    Vector3 start = controller.Position - up * halfCylinderHeight;
                    Vector3 end = controller.Position + up * halfCylinderHeight;
                    PhysicsDebugGeometryWriter.AddCapsule(
                        writer,
                        start,
                        end,
                        controller.Radius,
                        cyan);
                }

                if (settings.VisualizeContactPoint || settings.VisualizeContactNormal || settings.VisualizeContactError)
                {
                    uint yellow = PhysicsDebugColor.Pack(ColorF4.Yellow);
                    uint green = PhysicsDebugColor.Pack(ColorF4.Green);
                    uint red = PhysicsDebugColor.Pack(ColorF4.Red);
                    lock (_debugContactsLock)
                    {
                        for (int index = 0; index < _debugContactCount; index++)
                        {
                            JoltDebugContact contact = _debugContacts[index];
                            if (settings.VisualizeContactPoint)
                                PhysicsDebugGeometryWriter.AddSphere(writer, contact.Position, 0.015f, yellow);
                            if (settings.VisualizeContactNormal)
                                writer.AddLine(new PhysicsDebugLine(
                                    contact.Position,
                                    contact.Position + contact.Normal * 0.2f,
                                    green));
                            if (settings.VisualizeContactError && contact.PenetrationDepth > 0.0f)
                                writer.AddLine(new PhysicsDebugLine(
                                    contact.Position,
                                    contact.Position - contact.Normal * contact.PenetrationDepth,
                                    red));
                        }
                    }
                }
            }
            finally
            {
                _debugRenderer.EndFrame();
            }

            writer.CompleteSourceCountsFromPublished();
            writer.Publish();
        }

        public override void DebugRenderCollect()
            => PublishDebugFrame();

        public override void DebugRender()
        {
            // Fixed-step publication owns collection; render views only consume DebugFrames.
        }

        private void OnContactAdded(
            PhysicsSystem system,
            in Body body1,
            in Body body2,
            in ContactManifold manifold,
            ref ContactSettings settings)
            => CaptureDebugContacts(in manifold);

        private void OnContactPersisted(
            PhysicsSystem system,
            in Body body1,
            in Body body2,
            in ContactManifold manifold,
            ref ContactSettings settings)
            => CaptureDebugContacts(in manifold);

        private void CaptureDebugContacts(in ContactManifold manifold)
        {
            if (!_captureDebugContacts)
                return;

            Vector3 normal = manifold.WorldSpaceNormal;
            float penetrationDepth = manifold.PenetrationDepth;
            lock (_debugContactsLock)
            {
                for (uint index = 0; index < manifold.PointCount; index++)
                {
                    if (_debugContactCount >= _debugContacts.Length)
                        break;

                    Vector3 onBody1 = manifold.GetWorldSpaceContactPointOn1(index);
                    Vector3 onBody2 = manifold.GetWorldSpaceContactPointOn2(index);
                    _debugContacts[_debugContactCount++] = new JoltDebugContact(
                        (onBody1 + onBody2) * 0.5f,
                        normal,
                        penetrationDepth);
                }
            }
        }

        #endregion
    }

    public readonly record struct JoltPhysicsDiagnostics(
        int ActorCount,
        int RigidActorCount,
        int StaticBodyCount,
        int DynamicBodyCount,
        int CharacterControllerCount,
        int JointCount);

    public readonly record struct JoltDebugRenderSnapshot(
        int BodyCount,
        int CharacterControllerCount,
        int JointCount,
        int ContactCount);

    internal readonly record struct JoltDebugContact(
        Vector3 Position,
        Vector3 Normal,
        float PenetrationDepth);
}
