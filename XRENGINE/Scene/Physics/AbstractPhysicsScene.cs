using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Components.Physics;

namespace XREngine.Scene
{
    public struct RaycastHit
    {
        public Vector3 Position;
        public Vector3 Normal;
        public float Distance;
        public uint FaceIndex;
        public Vector2 UV;
    }
    public struct SweepHit
    {
        public Vector3 Position;
        public Vector3 Normal;
        public float Distance;
        public uint FaceIndex;
    }
    public struct OverlapHit
    {
        public uint FaceIndex;
    }

    public abstract class AbstractPhysicsScene : XRBase
    {
        public interface IAbstractQueryFilter
        {

        }
        public event Action? OnSimulationStep;

        protected virtual void NotifySimulationStepped()
            => OnSimulationStep?.Invoke();

        public abstract Vector3 Gravity { get; set; }

        public abstract void Initialize();
        public abstract void Destroy();
        public abstract void StepSimulation();

        public bool RaycastAny(Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter)
            => RaycastAny(worldSegment, layerMask, filter, out _);

        /// <summary>
        /// Raycasts the physics scene and returns true if anything was hit.
        /// </summary>
        /// <param name="worldSegment"></param>
        /// <param name="hitFaceIndex"></param>
        /// <returns></returns>
        public abstract bool RaycastAny(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex);
        /// <summary>
        /// Raycasts the physics scene and returns the first (nearest) hit item.
        /// </summary>
        /// <param name="worldSegment"></param>
        /// <param name="items"></param>
        public abstract bool RaycastSingle(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> items);
        /// <summary>
        /// Raycasts the physics scene and returns all hit items.
        /// </summary>
        /// <param name="worldSegment"></param>
        /// <param name="results"></param>
        public abstract bool RaycastMultiple(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results);

        public abstract bool SweepAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            out uint hitFaceIndex);
        public abstract bool SweepSingle(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> items);
        public abstract bool SweepMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results);

        public abstract bool OverlapAny(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results);
        public abstract bool OverlapMultiple(
            IPhysicsGeometry geometry,
            (Vector3 position, Quaternion rotation) pose,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> results);

        public virtual void DebugRender() { }
        public virtual void SwapDebugBuffers(){ }
        public virtual void DebugRenderCollect() { }

        public abstract void AddActor(IAbstractPhysicsActor actor);
        public abstract void RemoveActor(IAbstractPhysicsActor actor);

        public abstract void NotifyShapeChanged(IAbstractPhysicsActor actor);
    }
    public interface IAbstractPhysicsActor
    {
        void Destroy(bool wakeOnLostTouch = false);
    }
    public interface IAbstractStaticRigidBody : IAbstractRigidPhysicsActor
    {
        StaticRigidBodyComponent? OwningComponent { get; set; }
    }
    public interface IAbstractDynamicRigidBody : IAbstractRigidBody
    {
        DynamicRigidBodyComponent? OwningComponent { get; set; }
    }
    public interface IAbstractRigidPhysicsActor : IAbstractPhysicsActor
    {
        (Vector3 position, Quaternion rotation) Transform { get; }
        Vector3 LinearVelocity { get; }
        Vector3 AngularVelocity { get; }
        bool IsSleeping { get; }
    }
    public interface IAbstractRigidBody : IAbstractRigidPhysicsActor
    {

    }
}