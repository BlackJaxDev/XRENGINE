using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx;
using XREngine.Components.Animation;
using XREngine.Components.Physics;
using XREngine.Scene.Physics.Joints;

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

        /// <summary>
        /// Called when the engine enters play mode and the world has finished constructing/activating its scene graph.
        /// Physics implementations can use this to ensure all actors are registered and optionally wake actors so
        /// simulation begins with an up-to-date active list.
        /// </summary>
        public virtual void OnEnterPlayMode() { }

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
        public abstract bool RaycastSingleAsync(
            Segment worldSegment,
            LayerMask layerMask,
            IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> items,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback);
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

        #region Joint Factory

        /// <summary>
        /// Creates a fixed joint between two actors.
        /// Either actor may be null to attach to the world frame.
        /// </summary>
        public abstract IAbstractFixedJoint CreateFixedJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Creates a distance joint between two actors.
        /// </summary>
        public abstract IAbstractDistanceJoint CreateDistanceJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Creates a hinge (revolute) joint between two actors.
        /// </summary>
        public abstract IAbstractHingeJoint CreateHingeJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Creates a prismatic (slider) joint between two actors.
        /// </summary>
        public abstract IAbstractPrismaticJoint CreatePrismaticJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Creates a spherical (ball-and-socket) joint between two actors.
        /// </summary>
        public abstract IAbstractSphericalJoint CreateSphericalJoint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Creates a D6 (configurable) joint between two actors.
        /// </summary>
        public abstract IAbstractD6Joint CreateD6Joint(
            IAbstractPhysicsActor? actorA, JointAnchor localFrameA,
            IAbstractPhysicsActor? actorB, JointAnchor localFrameB);

        /// <summary>
        /// Removes and releases a joint from the scene's tracking.
        /// The native joint resources are freed.
        /// </summary>
        public abstract void RemoveJoint(IAbstractJoint joint);

        #endregion

        #region Joint Component Registration

        /// <summary>
        /// Maps native joint handles to the owning <see cref="PhysicsJointComponent"/>.
        /// Used by break-callback routing to find the component that owns a native joint.
        /// </summary>
        private readonly ConcurrentDictionary<IAbstractJoint, PhysicsJointComponent> _jointComponentMap = new();

        /// <summary>
        /// Registers a component as the owner of a native joint. Called by <see cref="PhysicsJointComponent"/> after joint creation.
        /// </summary>
        public void RegisterJointComponent(IAbstractJoint nativeJoint, PhysicsJointComponent component)
            => _jointComponentMap[nativeJoint] = component;

        /// <summary>
        /// Unregisters a component's joint from the map. Called before joint destruction.
        /// </summary>
        public void UnregisterJointComponent(IAbstractJoint nativeJoint)
            => _jointComponentMap.TryRemove(nativeJoint, out _);

        /// <summary>
        /// Looks up the owning component for a native joint. Returns null if not found.
        /// </summary>
        public PhysicsJointComponent? FindJointComponent(IAbstractJoint nativeJoint)
            => _jointComponentMap.TryGetValue(nativeJoint, out var comp) ? comp : null;

        /// <summary>
        /// Called by the physics backend when a constraint breaks.
        /// Routes the break notification to the owning component.
        /// </summary>
        public void NotifyConstraintBroken(IAbstractJoint nativeJoint)
        {
            if (_jointComponentMap.TryGetValue(nativeJoint, out var component))
                component.NotifyJointBroken();
        }

        #endregion
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