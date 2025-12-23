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

            var shape = geometry.AsJoltShape();
            if (shape is null)
                return null;

            pose = ApplyShapeOffsetToPose(pose, shapeOffsetTranslation, shapeOffsetRotation);

            BodyCreationSettings bodySettings = new(
                shape,
                pose.position,
                pose.rotation,
                MotionType.Static,
                layerMask.AsJoltObjectLayer());

            BodyID bodyId = _physicsSystem.BodyInterface.CreateBody(bodySettings);
            var body = new JoltStaticRigidBody
            {
                BodyID = bodyId,
            };

            AddActor(body);
            return body;
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

            var shape = geometry.AsJoltShape();
            if (shape is null)
                return null;

            pose = ApplyShapeOffsetToPose(pose, shapeOffsetTranslation, shapeOffsetRotation);

            BodyCreationSettings bodySettings = new(
                shape,
                pose.position,
                pose.rotation,
                MotionType.Dynamic,
                layerMask.AsJoltObjectLayer());

            BodyID bodyId = _physicsSystem.BodyInterface.CreateBody(bodySettings);
            var body = new JoltDynamicRigidBody
            {
                BodyID = bodyId,
            };

            AddActor(body);
            return body;
        }

        public override void AddActor(IAbstractPhysicsActor actor)
        {
            if (actor is not JoltActor joltActor)
                return;
            
            if (_physicsSystem is null)
                return;

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

            _physicsSystem = null;
            _jobSystem = null;
        }

        public override void Initialize()
        {
            JobSystemThreadPoolConfig jobConfig = new()
            {
                maxBarriers = 256,
                maxJobs = 512,
                numThreads = 4,
            };
            _jobSystem = new JobSystemThreadPool(jobConfig);
            PhysicsSystemSettings settings = new()
            {
                MaxBodies = 1024,
                NumBodyMutexes = 0,
                MaxBodyPairs = 1024,
                MaxContactConstraints = 1024
            };
            PhysicsSystem system = new(settings)
            {
                Gravity = new Vector3(0, -9.81f, 0),
                Settings = new PhysicsSettings
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
                },
            };
            _physicsSystem = system;
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

            // Create a temporary body for the query
            var bodySettings = new BodyCreationSettings(shape, pose.position, pose.rotation, MotionType.Static, layerMask.AsJoltObjectLayer());
            var bodyID = _physicsSystem.BodyInterface.CreateAndAddBody(bodySettings, Activation.Activate);
            
            try
            {
                // Perform overlap query
                var overlapResults = new List<ShapeCastResult>();
                _physicsSystem.NarrowPhaseQuery.CastShape(shape, Matrix4x4.Identity, Vector3.Zero, Vector3.Zero, CollisionCollectorType.AnyHit, overlapResults);
                
                bool hasHit = false;
                foreach (ShapeCastResult result in overlapResults)
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
            finally
            {
                // Clean up temporary body
                _physicsSystem.BodyInterface.DestroyBody(bodyID);
            }
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

            // Create a temporary body for the query
            var bodySettings = new BodyCreationSettings(shape, pose.position, pose.rotation, MotionType.Static, layerMask.AsJoltObjectLayer());
            var bodyID = _physicsSystem.BodyInterface.CreateAndAddBody(bodySettings, Activation.Activate);
            
            try
            {
                // Perform overlap query
                var overlapResults = new List<ShapeCastResult>();
                _physicsSystem.NarrowPhaseQuery.CastShape(shape, Matrix4x4.Identity, Vector3.Zero, Vector3.Zero, CollisionCollectorType.AllHit, overlapResults);

                bool hasHit = false;
                foreach (ShapeCastResult result in overlapResults)
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
            finally
            {
                // Clean up temporary body
                _physicsSystem.BodyInterface.DestroyBody(bodyID);
            }
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

            // Create a temporary body for the query
            var bodySettings = new BodyCreationSettings(shape, pose.position, pose.rotation, MotionType.Static, layerMask.AsJoltObjectLayer());
            var bodyID = _physicsSystem.BodyInterface.CreateAndAddBody(bodySettings, Activation.Activate);
            
            try
            {
                // Perform sweep query
                var sweepResults = new List<ShapeCastResult>();
                _physicsSystem.NarrowPhaseQuery.CastShape(shape, Matrix4x4.Identity, unitDir * distance, Vector3.Zero, CollisionCollectorType.AnyHit, sweepResults);

                // For sweep, we need to check if any bodies are in the sweep path
                // This is a simplified implementation
                foreach (ShapeCastResult result in sweepResults)
                {
                    var hitBodyID = result.BodyID2;
                    if (_actors.TryGetValue(hitBodyID, out var hitActor))
                    {
                        hitFaceIndex = 0;
                        return true;
                    }
                }
                
                return false;
            }
            finally
            {
                // Clean up temporary body
                _physicsSystem.BodyInterface.DestroyBody(bodyID);
            }
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

            // Create a temporary body for the query
            var bodySettings = new BodyCreationSettings(shape, pose.position, pose.rotation, MotionType.Static, layerMask.AsJoltObjectLayer());
            var bodyID = _physicsSystem.BodyInterface.CreateAndAddBody(bodySettings, Activation.Activate);
            
            try
            {
                // Perform sweep query
                var sweepResults = new List<ShapeCastResult>();
                _physicsSystem.NarrowPhaseQuery.CastShape(shape, Matrix4x4.Identity, unitDir * distance, Vector3.Zero, CollisionCollectorType.AllHit, sweepResults);

                bool hasHit = false;
                foreach (ShapeCastResult result in sweepResults)
                {
                    var hitBodyID = result.BodyID2;
                    if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                        continue;
                    
                    var component = hitActor.GetOwningComponent();
                    if (component is null)
                        continue;
                    
                    if (!results.TryGetValue(0.0f, out var list))
                        results.Add(0.0f, list = []);

                    list.Add((component, new SweepHit
                    {
                        Position = pose.position,
                        Normal = Globals.Up,
                        Distance = 0.0f,
                        FaceIndex = 0
                    }));

                    hasHit = true;
                }

                return hasHit;
            }
            finally
            {
                // Clean up temporary body
                _physicsSystem.BodyInterface.DestroyBody(bodyID);
            }
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

            // Create a temporary body for the query
            var bodySettings = new BodyCreationSettings(shape, pose.position, pose.rotation, MotionType.Static, layerMask.AsJoltObjectLayer());
            var bodyID = _physicsSystem.BodyInterface.CreateAndAddBody(bodySettings, Activation.Activate);
            
            try
            {
                // Perform sweep query
                var sweepResults = new List<ShapeCastResult>();
                _physicsSystem.NarrowPhaseQuery.CastShape(shape, Matrix4x4.Identity, unitDir * distance, Vector3.Zero, CollisionCollectorType.ClosestHit, sweepResults);

                foreach (ShapeCastResult result in sweepResults)
                {
                    var hitBodyID = result.BodyID2;
                    if (!_actors.TryGetValue(hitBodyID, out var hitActor))
                        continue;
                    
                    var component = hitActor.GetOwningComponent();
                    if (component is null)
                        continue;
                    
                    if (!items.TryGetValue(0.0f, out var list))
                        items.Add(0.0f, list = []);

                    list.Add((component, new SweepHit
                    {
                        Position = pose.position,
                        Normal = Globals.Up,
                        Distance = 0.0f,
                        FaceIndex = 0
                    }));

                    return true;
                }

                return false;
            }
            finally
            {
                // Clean up temporary body
                _physicsSystem.BodyInterface.DestroyBody(bodyID);
            }
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
