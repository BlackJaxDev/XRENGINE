using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.UnitTests.Scene;

/// <summary>Supplies an unused physics dependency for runtime ownership tests.</summary>
internal sealed class HierarchyTestPhysicsScene : AbstractPhysicsScene
{
    private Vector3 _gravity;
    public override Vector3 Gravity { get => _gravity; set => SetField(ref _gravity, value); }

    public override void Initialize() { }
    public override void Destroy() { }
    public override void StepSimulation() { }
    public override bool RaycastAny( Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter, out uint hitFaceIndex) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool RaycastSingleAsync( Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> items, Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>> finishedCallback) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool RaycastMultiple( Segment worldSegment, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool SweepAny( IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, LayerMask layerMask, IAbstractQueryFilter? filter, out uint hitFaceIndex) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool SweepSingle( IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> items) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool SweepMultiple( IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, Vector3 unitDir, float distance, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool OverlapAny( IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results) => throw new NotSupportedException("The ownership test does not run physics.");
    public override bool OverlapMultiple( IPhysicsGeometry geometry, (Vector3 position, Quaternion rotation) pose, LayerMask layerMask, IAbstractQueryFilter? filter, SortedDictionary<float, List<(XRComponent? item, object? data)>> results) => throw new NotSupportedException("The ownership test does not run physics.");
    public override void AddActor(IAbstractPhysicsActor actor) => throw new NotSupportedException("The ownership test does not run physics.");
    public override void RemoveActor(IAbstractPhysicsActor actor) => throw new NotSupportedException("The ownership test does not run physics.");
    public override void NotifyShapeChanged(IAbstractPhysicsActor actor) => throw new NotSupportedException("The ownership test does not run physics.");
    public override IAbstractFixedJoint CreateFixedJoint( IAbstractPhysicsActor? actorA, JointAnchor localFrameA, IAbstractPhysicsActor? actorB, JointAnchor localFrameB) => throw new NotSupportedException("The ownership test does not run physics.");
    public override IAbstractDistanceJoint CreateDistanceJoint( IAbstractPhysicsActor? actorA, JointAnchor localFrameA, IAbstractPhysicsActor? actorB, JointAnchor localFrameB) => throw new NotSupportedException("The ownership test does not run physics.");
    public override IAbstractHingeJoint CreateHingeJoint( IAbstractPhysicsActor? actorA, JointAnchor localFrameA, IAbstractPhysicsActor? actorB, JointAnchor localFrameB) => throw new NotSupportedException("The ownership test does not run physics.");
    public override IAbstractPrismaticJoint CreatePrismaticJoint( IAbstractPhysicsActor? actorA, JointAnchor localFrameA, IAbstractPhysicsActor? actorB, JointAnchor localFrameB) => throw new NotSupportedException("The ownership test does not run physics.");
    public override IAbstractSphericalJoint CreateSphericalJoint( IAbstractPhysicsActor? actorA, JointAnchor localFrameA, IAbstractPhysicsActor? actorB, JointAnchor localFrameB) => throw new NotSupportedException("The ownership test does not run physics.");
    public override IAbstractD6Joint CreateD6Joint( IAbstractPhysicsActor? actorA, JointAnchor localFrameA, IAbstractPhysicsActor? actorB, JointAnchor localFrameB) => throw new NotSupportedException("The ownership test does not run physics.");
    public override void RemoveJoint(IAbstractJoint joint) => throw new NotSupportedException("The ownership test does not run physics.");
}
