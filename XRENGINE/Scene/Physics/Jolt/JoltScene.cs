using JoltPhysicsSharp;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx;

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

        public override void AddActor(IAbstractPhysicsActor actor)
        {

        }

        public override void Destroy()
        {

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

        }

        public override bool OverlapAny(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override bool OverlapMultiple(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override bool RaycastAny(Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter, out uint hitFaceIndex)
        {
            throw new NotImplementedException();
        }

        public override bool RaycastMultiple(Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override bool RaycastSingleAsync(Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> items, Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback)
        {
            throw new NotImplementedException();
        }

        public override void RemoveActor(IAbstractPhysicsActor actor)
        {
            throw new NotImplementedException();
        }

        public override void StepSimulation()
        {
            _physicsSystem?.Update(Engine.FixedDelta, 3, _jobSystem!);
        }

        public override bool SweepAny(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, LayerMask layerMask, IAbstractQueryFilter? filter, out uint hitFaceIndex)
        {
            throw new NotImplementedException();
        }

        public override bool SweepMultiple(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override bool SweepSingle(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> items)
        {
            throw new NotImplementedException();
        }
    }
}
