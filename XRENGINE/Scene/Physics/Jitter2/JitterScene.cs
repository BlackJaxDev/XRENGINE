using Jitter2;
using Jitter2.Collision;
using Jitter2.LinearMath;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Scene;
using static Jitter2.Collision.DynamicTree;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Rendering.Physics.Physx
{
    public class JitterScene : AbstractPhysicsScene
    {
        private readonly World _world = new();
        public World World => _world;

        public override Vector3 Gravity
        {
            get => _world.Gravity.ToVector3();
            set => _world.Gravity = value.ToJVector();
        }

        public override void Initialize()
        {
            Gravity = new Vector3(0, -9.81f, 0);
            World.SubstepCount = 2;
        }

        public override void Destroy()
        {
            World.Clear();
        }

        public override void StepSimulation()
        {
            World.Step(Engine.FixedDelta);
        }

        /// <summary>
        /// Post-filter delegate.
        /// </summary>
        /// <returns>False if the hit should be filtered out.</returns>
        public bool RayCastFilterPost(RayCastResult result)
        {
            return true;
        }

        /// <summary>
        /// Pre-filter delegate.
        /// </summary>
        /// <returns>False if the hit should be filtered out.</returns>
        public bool RayCastFilterPre(IDynamicTreeProxy result)
        {
            return true;
        }

        public override bool RaycastAny(Segment worldSegment, out uint hitFaceIndex)
        {
            bool hasHit = World.DynamicTree.RayCast(
                worldSegment.Start.ToJVector(),
                worldSegment.Direction.ToJVector(),
                RayCastFilterPre,
                RayCastFilterPost,
                out _,
                out _,
                out _);

            hitFaceIndex = 0;
            return hasHit;
        }

        public override void RaycastSingle(Segment worldSegment, SortedDictionary<float, List<(XRComponent? item, object? data)>> items)
        {
            bool hasHit = World.DynamicTree.RayCast(
                worldSegment.Start.ToJVector(),
                worldSegment.Direction.ToJVector(),
                RayCastFilterPre,
                RayCastFilterPost,
                out IDynamicTreeProxy? proxy,
                out JVector normal,
                out float distance);

            if (hasHit && proxy != null)
            {
                if (proxy is XRComponent comp)
                {
                    items.Add(distance, [(comp, normal.ToVector3())]);
                }
            }
        }

        public override void RaycastMultiple(Segment worldSegment, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {

        }

        public override bool SweepAny(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, out uint hitFaceIndex)
        {
            throw new NotImplementedException();
        }

        public override void SweepSingle(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, SortedDictionary<float, List<(XRComponent? item, object? data)>> items)
        {
            throw new NotImplementedException();
        }

        public override void SweepMultiple(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override bool OverlapAny(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override void OverlapMultiple(IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, SortedDictionary<float, List<(XRComponent? item, object? data)>> results)
        {
            throw new NotImplementedException();
        }

        public override void AddActor(IAbstractPhysicsActor actor)
        {
            throw new NotImplementedException();
        }

        public override void RemoveActor(IAbstractPhysicsActor actor)
        {
            throw new NotImplementedException();
        }

        public override void NotifyShapeChanged(IAbstractPhysicsActor actor)
        {
            throw new NotImplementedException();
        }
    }
}