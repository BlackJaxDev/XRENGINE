using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Retains one guarded leaf after its exact parent adopts it.</summary>
internal sealed class AdoptedLeafRetirementReceipt
{
    private readonly ObjectConstructionIdentity[] _identities;
    private readonly XRObjectBase[] _objects;
    private readonly long[] _membershipRevisions;
    private readonly ObjectConstructionIdentity _parentIdentity;
    private readonly ObjectConstructionIdentity _parentListIdentity;
    private readonly ObjectConstructionIdentity? _parentNodeIdentity;
    private readonly Lock _sync = new();
    private EOwnedHierarchyLifetimeState _state;
    private Exception? _failure;
    private OwnedHierarchyRetirement? _retirement;
    private bool _detached;

    internal AdoptedLeafRetirementReceipt(SceneNode node, TransformBase parent,
        IReadOnlyList<RuntimeComponentCreationReceipt> attempts)
    {
        Node = node;
        Transform = node.OwnedRetirementTransform ?? throw new InvalidOperationException("An adopted node has no transform.");
        Parent = parent;
        ParentChildren = parent.OwnedRetirementChildren ?? throw new InvalidOperationException("The borrowed parent has no child storage.");
        SourceWorld = node.World;
        SourceStore = Transform.HierarchyStore;
        SourceHandle = Transform.HierarchyHandle;
        _parentIdentity = ObjectConstructionIdentity.Capture(parent);
        _parentListIdentity = ObjectConstructionIdentity.Capture(ParentChildren);
        ParentNode = parent.OwnedRetirementNode;
        _parentNodeIdentity = ParentNode is null ? null : ObjectConstructionIdentity.Capture(ParentNode);
        EventList<XRComponent> components = node.OwnedRetirementComponents ??
            throw new InvalidOperationException("An adopted node has no component storage.");
        EventList<TransformBase> children = Transform.OwnedRetirementChildren ??
            throw new InvalidOperationException("An adopted node has no child storage.");
        if (children.Count != 0 || components.Count != attempts.Count)
            throw new InvalidOperationException("Register an adopted leaf before adding children or foreign components.");
        List<XRObjectBase> objects = [node, Transform, components, children];
        List<ObjectConstructionIdentity> identities =
        [ObjectConstructionIdentity.Capture(node), ObjectConstructionIdentity.Capture(Transform),
         ObjectConstructionIdentity.Capture(components), ObjectConstructionIdentity.Capture(children)];
        HashSet<XRComponent> seen = new(ReferenceEqualityComparer.Instance);
        foreach (RuntimeComponentCreationReceipt attempt in attempts)
        {
            if (!attempt.IsInserted || attempt.ClaimedIdentity is not { IsCurrent: true } identity ||
                attempt.Component is not { } component || !seen.Add(component) ||
                !ReferenceEquals(identity.Target, component) || !components.Contains(component))
                throw new InvalidOperationException("An adopted component has no unique guarded claim.");
            objects.Add(component);
            identities.Add(identity);
        }
        _objects = [.. objects];
        _identities = [.. identities];
        _membershipRevisions = new long[_objects.Length];
        for (int index = 0; index < _objects.Length; index++)
            if (_objects[index] is XRWorldObjectBase structural)
            {
                if (!structural.HasUsableHierarchyMembershipRevision)
                    throw new InvalidOperationException("An adopted allocation exhausted its membership history.");
                _membershipRevisions[index] = structural.HierarchyMembershipRevision;
            }
        ValidateBoundary(attached: true);
    }

    internal SceneNode Node { get; }
    internal TransformBase Transform { get; }
    internal TransformBase Parent { get; }
    internal SceneNode? ParentNode { get; }
    internal EventList<TransformBase> ParentChildren { get; }
    internal IRuntimeWorldContext? SourceWorld { get; }
    internal TransformHierarchyStore? SourceStore { get; }
    internal TransformHandle SourceHandle { get; }
    internal EOwnedHierarchyLifetimeState State { get { lock (_sync) return _state; } }
    internal Exception? Failure { get { lock (_sync) return _failure; } }
    internal OwnedHierarchyRetirement? RetainedRetirement { get { lock (_sync) return _retirement; } }
    internal bool IsDetached => _detached;
    internal bool HasCurrentLifetime
    {
        get
        {
            foreach (ObjectConstructionIdentity identity in _identities)
                if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                    return false;
            return true;
        }
    }

    internal bool HasCurrentIdentity
    {
        get
        {
            if (!HasCurrentLifetime) return false;
            for (int index = 0; index < _objects.Length; index++)
                if (_objects[index] is XRWorldObjectBase structural &&
                    (!structural.HasUsableHierarchyMembershipRevision ||
                     structural.HierarchyMembershipRevision != _membershipRevisions[index]))
                    return false;
            return true;
        }
    }
    internal bool Contains(XRObjectBase value) => Array.IndexOf(_objects, value) >= 0;

    internal void ValidateBoundary(bool attached)
    {
        if (!_parentIdentity.IsCurrent || !_parentListIdentity.IsCurrent ||
            (_parentNodeIdentity is { } parentNodeIdentity && !parentNodeIdentity.IsCurrent) ||
            Parent.IsDestroyed || Parent.IsDestroyQueued ||
            ParentNode?.IsDestroyed == true || ParentNode?.IsDestroyQueued == true ||
            !ReferenceEquals(Parent.OwnedRetirementNode, ParentNode) ||
            ParentNode is not null && !ReferenceEquals(ParentNode.OwnedRetirementTransform, Parent) ||
            !ReferenceEquals(Parent.OwnedRetirementChildren, ParentChildren) ||
            !ReferenceEquals(Parent.World, SourceWorld) ||
            ParentChildren.GetMutationAuthority() is not HierarchyListAuthority<TransformBase> authority ||
            !authority.IsCurrent)
            throw new InvalidOperationException("The borrowed parent boundary changed.");
        int occurrences = 0;
        foreach (TransformBase child in ParentChildren)
            if (ReferenceEquals(child, Transform)) occurrences++;
        if (attached && (_detached || occurrences != 1) || !attached && (!_detached || occurrences != 0))
            throw new InvalidOperationException("The adopted leaf changed its exact parent occurrence.");
    }

    internal void MarkDetached() => _detached = true;

    internal bool TryRetire()
    {
        lock (_sync)
        {
            if (_state != EOwnedHierarchyLifetimeState.Retained)
                return _state == EOwnedHierarchyLifetimeState.Retired;
            _state = EOwnedHierarchyLifetimeState.Retiring;
        }
        try
        {
            ValidateBoundary(attached: true);
            if (!HasCurrentIdentity)
                throw new InvalidOperationException("An adopted allocation changed its structural membership.");
            _retirement = new(this, _objects, _identities);
            _retirement.Execute();
            lock (_sync)
            {
                _state = EOwnedHierarchyLifetimeState.Retired;
                _retirement = null;
            }
            return true;
        }
        catch (Exception failure)
        {
            lock (_sync)
            {
                _failure = failure;
                _state = EOwnedHierarchyLifetimeState.RecoveryRequired;
            }
            return false;
        }
    }
}
