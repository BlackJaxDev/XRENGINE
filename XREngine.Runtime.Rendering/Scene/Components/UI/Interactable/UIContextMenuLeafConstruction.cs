using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

/// <summary>Builds one menu leaf before publishing it to its captured parent.</summary>
internal sealed class UIContextMenuLeafConstruction
{
    private readonly SceneNode _parent;
    private readonly TransformBase _parentTransform;
    private readonly TransformBase? _grandparent;
    private readonly ObjectConstructionIdentity _parentIdentity;
    private readonly ObjectConstructionIdentity _parentTransformIdentity;
    private readonly ObjectConstructionIdentity _nodeIdentity;
    private readonly ObjectConstructionIdentity _transformIdentity;
    private readonly ObjectConstructionIdentity _componentListIdentity;
    private readonly ObjectConstructionIdentity _childListIdentity;
    private readonly long _nodeMembershipRevision;
    private readonly long _transformMembershipRevision;
    private readonly List<long> _componentMembershipRevisions = [];
    private readonly Action _requireShow;
    private readonly List<RuntimeComponentCreationReceipt> _attempts = [];

    internal UIContextMenuLeafConstruction(SceneNode parent, Action requireShow,
        UIBoundableTransform? transform = null, string name = "ContextMenuItem")
    {
        _parent = parent;
        _parentTransform = parent.Transform;
        _grandparent = _parentTransform.Parent;
        _parentIdentity = ObjectConstructionIdentity.Capture(parent);
        _parentTransformIdentity = ObjectConstructionIdentity.Capture(_parentTransform);
        _requireShow = requireShow;
        Node = new(parent.World, name, transform ?? new UIBoundableTransform());
        _nodeIdentity = ObjectConstructionIdentity.Capture(Node);
        _transformIdentity = ObjectConstructionIdentity.Capture(Node.Transform);
        _nodeMembershipRevision = Node.HierarchyMembershipRevision;
        _transformMembershipRevision = Node.Transform.HierarchyMembershipRevision;
        Retirement = Node.World is { } world ? new(Node, world) : new(Node);
        _componentListIdentity = ObjectConstructionIdentity.Capture(Node.OwnedRetirementComponents!);
        _childListIdentity = ObjectConstructionIdentity.Capture(Node.Transform.OwnedRetirementChildren!);
    }

    internal SceneNode Node { get; }
    internal UIBoundableTransform Transform => (UIBoundableTransform)_transformIdentity.Target;
    internal DetachedLeafRetirementReceipt Retirement { get; }
    internal IReadOnlyList<RuntimeComponentCreationReceipt> Attempts => _attempts;

    internal T Add<T>(Func<T> factory) where T : XRComponent
    {
        RequireDetached();
        RuntimeComponentCreationReceipt attempt = new(Node, () => { RequireParent(); return true; });
        Retirement.Register(attempt);
        _attempts.Add(attempt);
        T component = Node.AddComponentGuarded(factory, attempt) ??
            throw new InvalidOperationException("A menu component factory returned no component.");
        _componentMembershipRevisions.Add(component.HierarchyMembershipRevision);
        RequireDetached();
        return component;
    }

    internal void Write(Action write)
    {
        RequireDetached();
        write();
        RequireDetached();
    }

    internal void Attach()
    {
        RequireDetached();
        Transform.SetParent(_parentTransform, false, EParentAssignmentMode.Immediate);
        RequireParent();
        if (!OwnsAttachedNode())
            throw new InvalidOperationException("A menu leaf changed ownership during attachment.");
    }

    private void RequireParent()
    {
        if (!_parentIdentity.IsCurrent || !_parentTransformIdentity.IsCurrent ||
            _parent.IsDestroyed || _parent.IsDestroyQueued ||
            _parentTransform.IsDestroyed || _parentTransform.IsDestroyQueued ||
            !ReferenceEquals(_parent.Transform, _parentTransform) ||
            !ReferenceEquals(_parentTransform.Parent, _grandparent) ||
            !ReferenceEquals(_parent.World, Retirement.SourceWorld))
            throw new InvalidOperationException("The menu leaf parent changed during construction.");
        _requireShow();
    }

    private void RequireDetached()
    {
        RequireParent();
        if (!IsLiveLeaf() || Transform.Parent is not null ||
            Node.HasEnteredRootCollection || Node.HasEnteredStructuralList || Transform.HasEnteredStructuralList ||
            Node.HierarchyMembershipRevision != _nodeMembershipRevision ||
            Transform.HierarchyMembershipRevision != _transformMembershipRevision ||
            !Transform.HasUsableHierarchyMembershipRevision)
            throw new InvalidOperationException("A menu leaf was adopted during construction.");
        for (int index = 0; index < _attempts.Count; index++)
        {
            _attempts[index].Validate();
            if (index < _componentMembershipRevisions.Count &&
                _attempts[index].Component?.HierarchyMembershipRevision != _componentMembershipRevisions[index])
                throw new InvalidOperationException("A menu component changed membership during construction.");
        }
    }

    private bool IsLiveLeaf()
        => _nodeIdentity.IsCurrent && _transformIdentity.IsCurrent &&
            _componentListIdentity.IsCurrent && _childListIdentity.IsCurrent &&
            !Node.IsDestroyed && !Node.IsDestroyQueued &&
            !Transform.IsDestroyed && !Transform.IsDestroyQueued &&
            ReferenceEquals(Node.Transform, Transform) &&
            ReferenceEquals(Node.OwnedRetirementComponents, _componentListIdentity.Target) &&
            ReferenceEquals(Transform.OwnedRetirementChildren, _childListIdentity.Target) &&
            ReferenceEquals(Node.World, Retirement.SourceWorld) &&
            ReferenceEquals(Transform.World, Retirement.SourceWorld) &&
            ReferenceEquals(Transform.HierarchyStore, Retirement.SourceStore) &&
            Transform.HierarchyHandle == Retirement.SourceHandle;

    internal bool OwnsAttachedNode()
    {
        if (!_parentIdentity.IsCurrent || !_parentTransformIdentity.IsCurrent ||
            !ReferenceEquals(_parent.Transform, _parentTransform) ||
            !ReferenceEquals(_parentTransform.Parent, _grandparent) ||
            !ReferenceEquals(_parent.World, Retirement.SourceWorld) || !IsLiveLeaf() ||
            !ReferenceEquals(Transform.Parent, _parentTransform) ||
            Node.HierarchyMembershipRevision != _nodeMembershipRevision ||
            !Transform.HasUsableHierarchyMembershipRevision ||
            Transform.HierarchyMembershipRevision != _transformMembershipRevision + 1)
            return false;
        for (int index = 0; index < _attempts.Count; index++)
        {
            RuntimeComponentCreationReceipt attempt = _attempts[index];
            if (index >= _componentMembershipRevisions.Count ||
                attempt.ClaimedIdentity is not { IsCurrent: true } || !attempt.IsInserted ||
                attempt.Component is not { IsDestroyed: false, IsDestroyQueued: false } component ||
                !ReferenceEquals(component.SceneNode, Node) || !Node.Components.Contains(component) ||
                !ReferenceEquals(component.World, Retirement.SourceWorld) ||
                component.HierarchyMembershipRevision != _componentMembershipRevisions[index])
                return false;
        }
        return true;
    }

    /// <summary>Leaves adopted content to its owner; retires only an unchanged detached leaf.</summary>
    internal bool RetireFailedLeaf()
    {
        // Attachment callbacks can cancel the show after adoption. The normal content
        // owner releases an attached leaf. Never detach it to force receipt admission.
        if (OwnsAttachedNode())
            return true;
        return Retirement.SourceWorld is { } world
            ? Retirement.TryRetireFromWorld(world) : Retirement.TryRetire();
    }
}
