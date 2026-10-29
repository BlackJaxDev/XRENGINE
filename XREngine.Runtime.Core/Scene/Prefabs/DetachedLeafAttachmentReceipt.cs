using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Compensates only a failed parent pointer write before canonical attachment effects start.</summary>
internal sealed class DetachedLeafAttachmentReceipt
{
    private readonly DetachedLeafRetirementReceipt _leaf;
    private readonly IReadOnlyList<XRObjectBase> _objects;
    private readonly IReadOnlyList<ObjectConstructionIdentity> _identities;
    private readonly long[] _membershipRevisions;
    private readonly XRWorldObjectBase[] _endpoints;
    private readonly ObjectConstructionIdentity[] _parentIdentities;
    private readonly SceneNode _parentNode;
    private readonly TransformBase? _parentParent;
    private readonly EventList<TransformBase> _parentChildren;
    private readonly EventList<XRComponent> _parentComponents;
    private readonly IRuntimeWorldContext? _parentWorld;
    private readonly TransformHierarchyStore? _parentStore;
    private readonly TransformHandle _parentHandle;
    private readonly long _parentMembershipRevision;
    private readonly long _parentNodeMembershipRevision;
    private readonly long _originalAssignmentRevision;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private long _assignmentRevision;
    private bool _attempted;
    private bool _started;
    private bool _pointerCommitted;
    private bool _canonicalEffectsStarted;
    private bool _failed;
    private bool _consumed;
    private HierarchyStructuralReservation? _reservation;

    internal DetachedLeafAttachmentReceipt(DetachedLeafRetirementReceipt leaf, TransformBase parent)
    {
        _leaf = leaf;
        Parent = parent;
        Transform = leaf.Node.OwnedRetirementTransform ??
            throw new InvalidOperationException("The detached leaf has no transform.");
        (_objects, _identities) = leaf.CaptureAttachmentInput();
        lock (SceneRootCollection.MutationGate)
        {
            _originalAssignmentRevision = Transform.ParentAssignmentRevision;
            if (_originalAssignmentRevision >= long.MaxValue - 1)
                throw new InvalidOperationException("The parent assignment history is exhausted.");
            _membershipRevisions = new long[_objects.Count];
            List<XRWorldObjectBase> endpoints = [];
            for (int index = 0; index < _objects.Count; index++)
                if (_objects[index] is XRWorldObjectBase structural)
                {
                    if (!structural.HasUsableHierarchyMembershipRevision)
                        throw new InvalidOperationException("The detached leaf membership history is exhausted.");
                    _membershipRevisions[index] = structural.HierarchyMembershipRevision;
                    endpoints.Add(structural);
                }
            _parentNode = parent.OwnedRetirementNode ??
                throw new InvalidOperationException("The attachment parent has no node.");
            _parentChildren = parent.OwnedRetirementChildren ??
                throw new InvalidOperationException("The attachment parent has no child storage.");
            _parentComponents = _parentNode.OwnedRetirementComponents ??
                throw new InvalidOperationException("The attachment parent has no component storage.");
            _parentParent = parent.OwnedRetirementParent;
            _parentWorld = parent.World;
            _parentStore = parent.HierarchyStore;
            _parentHandle = parent.HierarchyHandle;
            _parentMembershipRevision = parent.HierarchyMembershipRevision;
            _parentNodeMembershipRevision = _parentNode.HierarchyMembershipRevision;
            List<ObjectConstructionIdentity> parentIdentities =
            [ObjectConstructionIdentity.Capture(parent), ObjectConstructionIdentity.Capture(_parentNode),
             ObjectConstructionIdentity.Capture(_parentChildren), ObjectConstructionIdentity.Capture(_parentComponents)];
            if (_parentParent is not null)
            {
                parentIdentities.Add(ObjectConstructionIdentity.Capture(_parentParent));
                endpoints.Add(_parentParent);
            }
            _parentIdentities = [.. parentIdentities];
            endpoints.Add(parent);
            endpoints.Add(_parentNode);
            _endpoints = [.. endpoints];
            ValidateOriginalBoundary(parentPointer: null);
        }
    }

    internal TransformBase Transform { get; }
    internal TransformBase Parent { get; }
    internal Exception? Failure { get; private set; }
    internal Exception? CompensationFailure { get; private set; }

    internal void Attach()
    {
        if (_thread != Environment.CurrentManagedThreadId || _attempted)
            throw new InvalidOperationException("The leaf attachment can run only once on its owning thread.");
        _attempted = true;
        try { Transform.AttachDetachedLeaf(this); }
        catch (Exception error)
        {
            _failed = true;
            Failure = error;
            TryCompensate();
            throw;
        }
    }

    internal void BeginAssignment(TransformBase transform, TransformBase? parent, long revision)
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_started || _thread != Environment.CurrentManagedThreadId ||
                !ReferenceEquals(transform, Transform) || !ReferenceEquals(parent, Parent) ||
                revision != _originalAssignmentRevision + 1 || transform.ParentAssignmentRevision != revision)
                throw new InvalidOperationException("The parent assignment does not own this attachment receipt.");
            _started = true;
            _assignmentRevision = revision;
            ValidateOriginalBoundary(parentPointer: null);
        }
    }

    internal void MarkPointerCommitted(TransformBase transform, long revision)
    {
        if (ReferenceEquals(transform, Transform) && revision == _assignmentRevision)
            _pointerCommitted = true;
    }

    internal void MarkCanonicalEffectsStarted(TransformBase transform, long revision)
    {
        if (ReferenceEquals(transform, Transform) && revision == _assignmentRevision)
            _canonicalEffectsStarted = true;
    }

    private void TryCompensate()
    {
        if (!_started || !_pointerCommitted || _canonicalEffectsStarted || _consumed)
            return;
        try
        {
            using HierarchyStructuralReservation reservation = HierarchyStructuralReservation.TryAcquire(_endpoints) ??
                throw new InvalidOperationException("The failed leaf attachment still has an active mutation.");
            lock (SceneRootCollection.MutationGate)
            {
                _reservation = reservation;
                try { Transform.CompensateDetachedLeafParent(this, reservation); }
                finally { _reservation = null; }
            }
        }
        catch (Exception error) { CompensationFailure = error; }
    }

    internal void RequireCompensation(TransformBase transform, HierarchyStructuralReservation reservation)
    {
        if (_thread != Environment.CurrentManagedThreadId || !_failed || !_started ||
            !_pointerCommitted || _canonicalEffectsStarted || _consumed ||
            !ReferenceEquals(transform, Transform) || !ReferenceEquals(_reservation, reservation) ||
            Transform.ParentAssignmentRevision != _assignmentRevision || _assignmentRevision >= long.MaxValue)
            throw new InvalidOperationException("The failed parent write no longer has exact compensation authority.");
        ValidateOriginalBoundary(Parent);
    }

    internal void ConsumeCompensation() => _consumed = true;

    private void ValidateOriginalBoundary(TransformBase? parentPointer)
    {
        if (PhysicsChainWorld.IsWorldMutationActive && _failed)
            throw new InvalidOperationException("The failed parent write must unwind its world mutation first.");
        foreach (ObjectConstructionIdentity identity in _identities)
            if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                throw new InvalidOperationException("The detached attachment allocation changed lifetime.");
        foreach (ObjectConstructionIdentity identity in _parentIdentities)
            if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                throw new InvalidOperationException("The attachment parent changed lifetime.");
        if (!ReferenceEquals(Parent.OwnedRetirementNode, _parentNode) ||
            !ReferenceEquals(_parentNode.OwnedRetirementTransform, Parent) ||
            !ReferenceEquals(Parent.OwnedRetirementChildren, _parentChildren) ||
            !ReferenceEquals(_parentNode.OwnedRetirementComponents, _parentComponents) ||
            !ReferenceEquals(Parent.OwnedRetirementParent, _parentParent) ||
            !Parent.HasUsableHierarchyMembershipRevision ||
            Parent.HierarchyMembershipRevision != _parentMembershipRevision ||
            !_parentNode.HasUsableHierarchyMembershipRevision ||
            _parentNode.HierarchyMembershipRevision != _parentNodeMembershipRevision ||
            !ReferenceEquals(Parent.World, _parentWorld) || !ReferenceEquals(_parentNode.World, _parentWorld) ||
            !ReferenceEquals(Parent.HierarchyStore, _parentStore) || Parent.HierarchyHandle != _parentHandle ||
            _parentChildren.GetMutationAuthority() is not HierarchyListAuthority<TransformBase> authority ||
            !authority.IsCurrent || _parentChildren.Contains(Transform))
            throw new InvalidOperationException("The exact attachment parent boundary changed.");
        SceneNode node = _leaf.Node;
        EventList<XRComponent> components = (EventList<XRComponent>)_objects[2];
        EventList<TransformBase> children = (EventList<TransformBase>)_objects[3];
        if (!ReferenceEquals(node.OwnedRetirementTransform, Transform) ||
            !ReferenceEquals(Transform.OwnedRetirementNode, node) ||
            !ReferenceEquals(node.OwnedRetirementComponents, components) ||
            !ReferenceEquals(Transform.OwnedRetirementChildren, children) || children.Count != 0 ||
            !ReferenceEquals(Transform.OwnedRetirementParent, parentPointer) ||
            node.HasEnteredRootCollection || node.HasEnteredStructuralList || Transform.HasEnteredStructuralList ||
            node.HasSceneRootOwner || node.CaptureRuntimeRootMemberships().Length != 0 ||
            node.CaptureHierarchyListMemberships().Length != 0 || Transform.CaptureHierarchyListMemberships().Length != 0 ||
            !ReferenceEquals(Transform.HierarchyStore, _leaf.SourceStore) || Transform.HierarchyHandle != _leaf.SourceHandle)
            throw new InvalidOperationException("The leaf no longer has its original detached shape.");
        int componentCount = 0;
        for (int index = 0; index < _objects.Count; index++)
        {
            if (_objects[index] is not XRWorldObjectBase structural) continue;
            if (!structural.HasUsableHierarchyMembershipRevision ||
                structural.HierarchyMembershipRevision != _membershipRevisions[index] ||
                !ReferenceEquals(structural.World, _leaf.SourceWorld))
                throw new InvalidOperationException("An attachment allocation changed world or membership history.");
            if (structural is not XRComponent component) continue;
            componentCount++;
            HierarchyListMembership[] memberships = component.CaptureHierarchyListMemberships();
            if (!ReferenceEquals(component.OwnedRetirementNode, node) || !components.Contains(component) ||
                memberships.Length != 1 || memberships[0].Count != 1 || !memberships[0].IsCurrent ||
                !ReferenceEquals(memberships[0].Owner, node) || !ReferenceEquals(memberships[0].Collection, components))
                throw new InvalidOperationException("An attachment component changed its exact owner.");
        }
        if (components.Count != componentCount)
            throw new InvalidOperationException("The detached attachment contains an unclaimed component.");
    }
}
