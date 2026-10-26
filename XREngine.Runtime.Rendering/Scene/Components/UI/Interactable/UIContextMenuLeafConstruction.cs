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
        Retirement = Node.World is { } world ? new(Node, world) : new(Node);
    }

    internal SceneNode Node { get; }
    internal UIBoundableTransform Transform => (UIBoundableTransform)_transformIdentity.Target;
    internal DetachedLeafRetirementReceipt Retirement { get; }

    internal T Add<T>(Func<T> factory) where T : XRComponent
    {
        RequireDetached();
        RuntimeComponentCreationReceipt attempt = new(Node, () => { RequireParent(); return true; });
        Retirement.Register(attempt);
        _attempts.Add(attempt);
        T component = Node.AddComponentGuarded(factory, attempt) ??
            throw new InvalidOperationException("A menu component factory returned no component.");
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
        if (!IsOwnedAttachment())
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
            Node.HasEnteredRootCollection || Node.HasEnteredStructuralList || Transform.HasEnteredStructuralList)
            throw new InvalidOperationException("A menu leaf was adopted during construction.");
        foreach (RuntimeComponentCreationReceipt attempt in _attempts)
            attempt.Validate();
    }

    private bool IsLiveLeaf()
        => _nodeIdentity.IsCurrent && _transformIdentity.IsCurrent &&
            !Node.IsDestroyed && !Node.IsDestroyQueued &&
            !Transform.IsDestroyed && !Transform.IsDestroyQueued &&
            ReferenceEquals(Node.Transform, Transform) &&
            ReferenceEquals(Node.World, Retirement.SourceWorld) &&
            ReferenceEquals(Transform.World, Retirement.SourceWorld);

    private bool IsOwnedAttachment()
    {
        if (!IsLiveLeaf() || !ReferenceEquals(Transform.Parent, _parentTransform))
            return false;
        foreach (RuntimeComponentCreationReceipt attempt in _attempts)
            if (attempt.ClaimedIdentity is not { IsCurrent: true } || !attempt.IsInserted ||
                attempt.Component is not { IsDestroyed: false, IsDestroyQueued: false } component ||
                !ReferenceEquals(component.SceneNode, Node) ||
                !ReferenceEquals(component.World, Retirement.SourceWorld))
                return false;
        return true;
    }

    /// <summary>Leaves adopted content to its owner; retires only an unchanged detached leaf.</summary>
    internal bool RetireFailedLeaf()
    {
        // Attachment callbacks can cancel the show after adoption. The normal content
        // owner releases an attached leaf. Never detach it to force receipt admission.
        if (IsOwnedAttachment())
            return true;
        return Retirement.SourceWorld is { } world
            ? Retirement.TryRetireFromWorld(world) : Retirement.TryRetire();
    }
}
