using Extensions;
using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Rendering.Physics.Physx;
using Ray = JoltPhysicsSharp.Ray;

namespace XREngine.Scene.Physics.Jolt
{
    public class JoltScene : AbstractPhysicsScene
    {
        private readonly HashSet<IJoltCharacterController> _characterControllers = new();

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

            Shape? shape;
            try
            {
                shape = geometry.AsJoltShape();
            }
            catch (NotImplementedException)
            {
                System.Diagnostics.Debug.WriteLine($"[JoltScene] CreateStaticRigidBody failed - geometry type {geometry.GetType().Name} not implemented for Jolt");
                return null;
            }
            
            if (shape is null)
            {
                System.Diagnostics.Debug.WriteLine("[JoltScene] CreateStaticRigidBody failed - shape is null");
                return null;
            }

            pose = ApplyShapeOffsetToPose(pose, shapeOffsetTranslation, shapeOffsetRotation);

            BodyCreationSettings bodySettings = new(
                shape,
                pose.position,
                pose.rotation,
                MotionType.Static,
                layerMask.AsJoltObjectLayer());

            Body body = _physicsSystem.BodyInterface.CreateBody(bodySettings);
            BodyID bodyId = body.ID;
            
            // Check if body creation succeeded
            if (bodyId.IsInvalid)
            {
                System.Diagnostics.Debug.WriteLine("[JoltScene] CreateStaticRigidBody failed - body ID is invalid (max bodies reached?)");
                return null;
            }
            
            var joltBody = new JoltStaticRigidBody(bodyId);

            AddActor(joltBody);
            return joltBody;
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

            Shape? shape;
            try
            {
                shape = geometry.AsJoltShape();
            }
            catch (NotImplementedException)
            {
                System.Diagnostics.Debug.WriteLine($"[JoltScene] CreateDynamicRigidBody failed - geometry type {geometry.GetType().Name} not implemented for Jolt");
                return null;
            }
            
            if (shape is null)
            {
                System.Diagnostics.Debug.WriteLine("[JoltScene] CreateDynamicRigidBody failed - shape is null");
                return null;
            }

            pose = ApplyShapeOffsetToPose(pose, shapeOffsetTranslation, shapeOffsetRotation);

            BodyCreationSettings bodySettings = new(
                shape,
                pose.position,
                pose.rotation,
                MotionType.Dynamic,
                layerMask.AsJoltObjectLayer());

            Body body = _physicsSystem.BodyInterface.CreateBody(bodySettings);
            BodyID bodyId = body.ID;
            
            // Check if body creation succeeded
            if (bodyId.IsInvalid)
            {
                System.Diagnostics.Debug.WriteLine("[JoltScene] CreateDynamicRigidBody failed - body ID is invalid (max bodies reached?)");
                return null;
            }
            
            var joltBody = new JoltDynamicRigidBody(bodyId);

            AddActor(joltBody);
            return joltBody;
        }

        public override void AddActor(IAbstractPhysicsActor actor)
        {
            if (actor is not JoltActor joltActor)
                return;
            
            if (_physicsSystem is null)
                return;

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

            // Remove all actors
            foreach (var actor in _actors.Values.ToArray())
                RemoveActor(actor);
            
            _actors.Clear();
            _rigidActors.Clear();
            _staticBodies.Clear();
            _dynamicBodies.Clear();

            (_physicsSystem as IDisposable)?.Dispose();
            _physicsSystem = null;

            (_jobSystem as IDisposable)?.Dispose();
            _jobSystem = null;
        }

        // Collision layers - matching the official JoltPhysicsSharp samples
        private const int NumLayers = 2;
        private ObjectLayerPairFilterTable? _objectLayerPairFilter;
        private BroadPhaseLayerInterfaceTable? _broadPhaseLayerInterface;
        private ObjectVsBroadPhaseLayerFilterTable? _objectVsBroadPhaseLayerFilter;

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
                // Layer 0 = NonMoving (static), Layer 1 = Moving (dynamic)
                _objectLayerPairFilter = new ObjectLayerPairFilterTable(NumLayers);
                _objectLayerPairFilter.EnableCollision(0, 1); // NonMoving vs Moving
                _objectLayerPairFilter.EnableCollision(1, 1); // Moving vs Moving

                // BroadPhase: 0 = NonMoving, 1 = Moving
                _broadPhaseLayerInterface = new BroadPhaseLayerInterfaceTable(NumLayers, NumLayers);
                _broadPhaseLayerInterface.MapObjectToBroadPhaseLayer(0, 0); // ObjectLayer 0 -> BroadPhaseLayer 0
                _broadPhaseLayerInterface.MapObjectToBroadPhaseLayer(1, 1); // ObjectLayer 1 -> BroadPhaseLayer 1

                _objectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(_broadPhaseLayerInterface, NumLayers, _objectLayerPairFilter, NumLayers);

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
            if (actor is not JoltActor joltActor)
                return;

            // For Jolt, we need to recreate the body when the shape changes
            // This is a simplified approach - in a full implementation, you might want to optimize this
            RemoveActor(actor);
            AddActor(actor);
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

            // Convert geometry to Jolt shape
            var shape = geometry.AsJoltShape();
            if (shape is null)
                return false;

            // Create world transform matrix from pose
            var worldTransform = Matrix4x4.CreateFromQuaternion(pose.rotation) * Matrix4x4.CreateTranslation(pose.position);

            // Perform overlap query using CollideShape with zero-length cast
            var collideResults = new List<CollideShapeResult>();
            _physicsSystem.NarrowPhaseQuery.CollideShape(shape, Vector3.One, worldTransform, pose.position, CollisionCollectorType.AnyHit, collideResults);

            bool hasHit = false;
            foreach (CollideShapeResult result in collideResults)
            {
                var hitBodyID = result.BodyID2;
                if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                    continue;

                var component = hitActor.GetOwningComponent();
                if (component is null)
                    continue;

                if (!results.TryGetValue(0.0f, out var list))
                    results.Add(0.0f, list = []);

                list.Add((component, new OverlapHit { FaceIndex = 0 }));
                hasHit = true;
            }

            return hasHit;
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

            // Convert geometry to Jolt shape
            var shape = geometry.AsJoltShape();
            if (shape is null)
                return false;

            // Create world transform matrix from pose
            var worldTransform = Matrix4x4.CreateFromQuaternion(pose.rotation) * Matrix4x4.CreateTranslation(pose.position);

            // Perform overlap query using CollideShape
            var collideResults = new List<CollideShapeResult>();
            _physicsSystem.NarrowPhaseQuery.CollideShape(shape, Vector3.One, worldTransform, pose.position, CollisionCollectorType.AllHit, collideResults);

            bool hasHit = false;
            foreach (CollideShapeResult result in collideResults)
            {
                var hitBodyID = result.BodyID2;
                if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                    continue;

                var component = hitActor.GetOwningComponent();
                if (component is null)
                    continue;

                if (!results.TryGetValue(0.0f, out var list))
                    results.Add(0.0f, list = []);

                list.Add((component, new OverlapHit { FaceIndex = 0 }));
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
            hitFaceIndex = 0;
            
            if (_physicsSystem is null)
                return false;

            var start = worldSegment.Start;
            var end = worldSegment.End;
            var direction = (end - start).Normalized();

            var hits = new List<RayCastResult>();
            if (_physicsSystem.NarrowPhaseQuery.CastRay(new Ray(start, direction), new RayCastSettings(), CollisionCollectorType.AnyHit, hits))
            {
                if (hits.Count == 0)
                {
                    hitFaceIndex = 0;
                    return false;
                }
                var hit = hits[0];
                hitFaceIndex = hit.subShapeID2;
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

            var start = worldSegment.Start;
            var end = worldSegment.End;
            var direction = (end - start).Normalized();
            var distance = worldSegment.Length;

            var rayCast = new Ray(start, direction);
            var rayCastSettings = new RayCastSettings();
            
            List<RayCastResult> hits = [];
            _physicsSystem.NarrowPhaseQuery.CastRay(rayCast, rayCastSettings, CollisionCollectorType.AllHit, hits);
            
            bool hasHit = false;
            foreach (var hit in hits)
            {
                if (_actors.TryGetValue(hit.BodyID, out var hitActor))
                {
                    var component = hitActor.GetOwningComponent();
                    if (component is not null)
                    {
                        float dist = hit.Fraction * distance;
                        if (!results.TryGetValue(dist, out var list))
                            results.Add(dist, list = []);
                        
                        list.Add((component, new RaycastHit 
                        { 
                            Position = dist * direction + start,
                            //Normal = hit.Normal,
                            Distance = dist,
                            FaceIndex = hit.subShapeID2,
                            UV = Vector2.Zero
                        }));
                        hasHit = true;
                    }
                }
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
                return false;

            var start = worldSegment.Start;
            var end = worldSegment.End;
            var direction = (end - start).Normalized();
            var maxDist = worldSegment.Length;

            var rayCast = new JoltPhysicsSharp.Ray(start, direction);
            var rayCastSettings = new RayCastSettings();
            
            List<RayCastResult> hits = [];
            if (_physicsSystem.NarrowPhaseQuery.CastRay(rayCast, rayCastSettings, CollisionCollectorType.ClosestHit, hits))
            {
                if (hits.Count == 0)
                {
                    finishedCallback?.Invoke(items);
                    return false;
                }
                var hit = hits[0];
                if (_actors.TryGetValue(hit.BodyID, out var hitActor))
                {
                    var component = hitActor.GetOwningComponent();
                    if (component is not null)
                    {
                        var dist = hit.Fraction * maxDist;
                        if (!items.TryGetValue(dist, out var list))
                            items.Add(dist, list = []);
                        
                        list.Add((component, new RaycastHit 
                        { 
                            Position = dist * direction + start,
                            //Normal = hit.Normal,
                            Distance = dist,
                            FaceIndex = hit.subShapeID2,
                            UV = Vector2.Zero
                        }));
                        
                        finishedCallback?.Invoke(items);
                        return true;
                    }
                }
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

        public static ObjectLayer GetObjectLayer(uint group, uint mask)
            => ObjectLayerPairFilterMask.GetObjectLayer(group, mask);

        public override void StepSimulation()
        {
            if (_physicsSystem is null || _jobSystem is null)
                return;

            // Mirror PhysX behavior: consume queued character controller movement on the fixed step
            // so movement + collision resolution happen deterministically with the physics update.
            float dt = Engine.Time.Timer.FixedUpdateDelta;
            if (dt > 0.0f && _characterControllers.Count > 0)
            {
                foreach (var controller in _characterControllers)
                    controller.ConsumeInputBuffer(dt);
            }

            _physicsSystem.Update(Engine.FixedDelta, 3, _jobSystem);
            
            //// Update transforms for all active bodies
            //var activeBodies = new List<BodyID>();
            //_physicsSystem.BodyInterface.GetActiveBodies(activeBodies);

            //foreach (var bodyID in activeBodies)
            //    if (_dynamicBodies.TryGetValue(bodyID, out var body))
            //        body.OwningComponent?.RigidBodyTransform.OnPhysicsStepped();

            NotifySimulationStepped();
        }

        public override bool SweepAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex)
        {
            hitFaceIndex = 0;
            
            if (_physicsSystem is null)
                return false;

            // Convert geometry to Jolt shape
            var shape = geometry.AsJoltShape();
            if (shape is null)
                return false;

            // Parse query flags from PhysX filter for compatibility
            bool includeStatic = true;
            bool includeDynamic = true;
            if (filter is PhysxScene.PhysxQueryFilter physxFilter)
            {
                includeStatic = (physxFilter.Flags & MagicPhysX.PxQueryFlags.Static) != 0;
                includeDynamic = (physxFilter.Flags & MagicPhysX.PxQueryFlags.Dynamic) != 0;
            }

            // Create world transform matrix from pose (positions shape at starting location)
            var worldTransform = Matrix4x4.CreateFromQuaternion(pose.rotation) * Matrix4x4.CreateTranslation(pose.position);

            // Perform sweep query - direction is normalized, multiply by distance to get full sweep length
            var sweepResults = new List<ShapeCastResult>();
            _physicsSystem.NarrowPhaseQuery.CastShape(shape, worldTransform, unitDir * distance, pose.position, CollisionCollectorType.AnyHit, sweepResults);

            // Check if any bodies are in the sweep path
            foreach (ShapeCastResult result in sweepResults)
            {
                var hitBodyID = result.BodyID2;
                if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                    continue;

                // Filter by motion type to match PhysX query flags behavior
                var motionType = _physicsSystem.BodyInterface.GetMotionType(hitBodyID);
                if (motionType == MotionType.Static && !includeStatic)
                    continue;
                if (motionType != MotionType.Static && !includeDynamic)
                    continue;

                hitFaceIndex = (uint)result.SubShapeID2;
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

            // Convert geometry to Jolt shape
            var shape = geometry.AsJoltShape();
            if (shape is null)
                return false;

            // Parse query flags from PhysX filter for compatibility
            bool includeStatic = true;
            bool includeDynamic = true;
            if (filter is PhysxScene.PhysxQueryFilter physxFilter)
            {
                includeStatic = (physxFilter.Flags & MagicPhysX.PxQueryFlags.Static) != 0;
                includeDynamic = (physxFilter.Flags & MagicPhysX.PxQueryFlags.Dynamic) != 0;
            }

            // Create world transform matrix from pose (positions shape at starting location)
            var worldTransform = Matrix4x4.CreateFromQuaternion(pose.rotation) * Matrix4x4.CreateTranslation(pose.position);

            // Perform sweep query - direction is normalized, multiply by distance to get full sweep length
            var sweepResults = new List<ShapeCastResult>();
            _physicsSystem.NarrowPhaseQuery.CastShape(shape, worldTransform, unitDir * distance, pose.position, CollisionCollectorType.AllHit, sweepResults);

            bool hasHit = false;
            foreach (ShapeCastResult result in sweepResults)
            {
                var hitBodyID = result.BodyID2;
                if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                    continue;

                // Filter by motion type to match PhysX query flags behavior
                var motionType = _physicsSystem.BodyInterface.GetMotionType(hitBodyID);
                if (motionType == MotionType.Static && !includeStatic)
                    continue;
                if (motionType != MotionType.Static && !includeDynamic)
                    continue;

                var component = hitActor.GetOwningComponent();
                if (component is null)
                    continue;

                // Calculate actual hit distance using fraction
                float hitDistance = result.Fraction * distance;
                
                if (!results.TryGetValue(hitDistance, out var list))
                    results.Add(hitDistance, list = []);

                list.Add((component, new SweepHit
                {
                    Position = result.ContactPointOn2,
                    Normal = Vector3.Normalize(result.PenetrationAxis),
                    Distance = hitDistance,
                    FaceIndex = (uint)result.SubShapeID2
                }));

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

            // Convert geometry to Jolt shape
            var shape = geometry.AsJoltShape();
            if (shape is null)
                return false;

            // Parse query flags from PhysX filter for compatibility
            bool includeStatic = true;
            bool includeDynamic = true;
            if (filter is PhysxScene.PhysxQueryFilter physxFilter)
            {
                includeStatic = (physxFilter.Flags & MagicPhysX.PxQueryFlags.Static) != 0;
                includeDynamic = (physxFilter.Flags & MagicPhysX.PxQueryFlags.Dynamic) != 0;
            }

            // Create world transform matrix from pose (positions shape at starting location)
            var worldTransform = Matrix4x4.CreateFromQuaternion(pose.rotation) * Matrix4x4.CreateTranslation(pose.position);

            // Perform sweep query - direction is normalized, multiply by distance to get full sweep length
            // Use AllHit to get multiple results so we can filter by motion type
            var sweepResults = new List<ShapeCastResult>();
            _physicsSystem.NarrowPhaseQuery.CastShape(shape, worldTransform, unitDir * distance, pose.position, CollisionCollectorType.AllHitSorted, sweepResults);

            foreach (ShapeCastResult result in sweepResults)
            {
                var hitBodyID = result.BodyID2;
                if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                    continue;

                // Filter by motion type to match PhysX query flags behavior
                var motionType = _physicsSystem.BodyInterface.GetMotionType(hitBodyID);
                if (motionType == MotionType.Static && !includeStatic)
                    continue;
                if (motionType != MotionType.Static && !includeDynamic)
                    continue;

                var component = hitActor.GetOwningComponent();
                if (component is null)
                    continue;

                // Calculate actual hit distance using fraction
                float hitDistance = result.Fraction * distance;
                
                if (!items.TryGetValue(hitDistance, out var list))
                    items.Add(hitDistance, list = []);

                list.Add((component, new SweepHit
                {
                    Position = result.ContactPointOn2,
                    Normal = Vector3.Normalize(result.PenetrationAxis),
                    Distance = hitDistance,
                    FaceIndex = (uint)result.SubShapeID2
                }));

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
    }
}
