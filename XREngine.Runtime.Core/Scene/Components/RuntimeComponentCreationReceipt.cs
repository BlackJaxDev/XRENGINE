using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Data.Core;

namespace XREngine.Components;

/// <summary>Retains the exact owner and component of a guarded creation attempt.</summary>
internal sealed class RuntimeComponentCreationReceipt
{
    private readonly SceneNode _node;
    private readonly ObjectConstructionIdentity _nodeIdentity;
    private readonly TransformBase _transform;
    private readonly ObjectConstructionIdentity _transformIdentity;
    private readonly TransformBase? _parent;
    private readonly ObjectConstructionIdentity? _parentIdentity;
    private readonly EventList<XRComponent> _componentList;
    private readonly ObjectConstructionIdentity _componentListIdentity;
    private readonly IRuntimeWorldContext? _world;
    private readonly Func<bool>? _validateOwner;
    private ObjectConstructionIdentity? _componentIdentity;
    private bool _worldAssigned;
    private bool _insertionStarted;
    private bool _insertionCommitted;
    private bool _aborting;
    private bool _cleanupStarted;
    private bool _cleanupCompleted;
    private bool _started;

    internal RuntimeComponentCreationReceipt(SceneNode node, Func<bool>? validateOwner = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
        _nodeIdentity = ObjectConstructionIdentity.Capture(node);
        _transform = node.Transform;
        _transformIdentity = ObjectConstructionIdentity.Capture(_transform);
        _parent = _transform.Parent;
        _parentIdentity = _parent is null ? null : ObjectConstructionIdentity.Capture(_parent);
        _componentList = node.OwnedRetirementComponents ??
            throw new InvalidOperationException("The component owner has no list.");
        _componentListIdentity = ObjectConstructionIdentity.Capture(_componentList);
        _world = node.World;
        _validateOwner = validateOwner;
        Validate();
    }

    internal XRComponent? Component { get; private set; }
    internal Exception? Failure { get; private set; }
    internal SceneNode Node => _node;

    internal void BeginAttempt()
    {
        if (_started)
            throw new InvalidOperationException("A component creation receipt is single-use.");
        _started = true;
    }

    internal void Claim(XRComponent component)
    {
        Component = component;
        _componentIdentity = ObjectConstructionIdentity.Capture(component);
    }

    internal void RecordFailure(Exception failure) => Failure = failure;
    internal Exception? CleanupFailure { get; private set; }
    internal void RecordCleanupFailure(Exception failure) => CleanupFailure = failure;
    internal void StartInsertion()
    {
        Validate();
        _insertionStarted = true;
    }

    internal void ValidateBeforeInsert(SceneNode owner)
    {
        if (!ReferenceEquals(owner, _node) || !_insertionStarted || Component is null ||
            _insertionCommitted || !HasExpectedMembership(inserted: false) ||
            !IsIntrinsicOwnerCurrentIgnoringMembership())
            throw new InvalidOperationException("Component owner changed before list insertion.");
    }

    internal void CommitInsertion()
    {
        _insertionCommitted = true;
        // The list lock is held here. Check ownership without calling the caller validator.
        ValidateIntrinsicOwner();
    }

    internal void RequireCommittedInsertion()
    {
        if (!_insertionCommitted || !HasExpectedMembership(inserted: true))
            throw new InvalidOperationException("Guarded component insertion did not commit.");
    }

    internal bool IsInserted => HasExpectedMembership(inserted: true);

    internal bool CanAbortClaimed => Failure is not null && !_cleanupStarted && !_insertionCommitted &&
        Component is { IsDestroyed: false, IsDestroyQueued: false, HasEnteredStructuralList: false } &&
        HasExpectedMembership(inserted: false) && IsIntrinsicOwnerCurrentIgnoringMembership();

    /// <summary>Aborts a failed component only while its original owner remains current.</summary>
    internal bool TryAbortUnchangedComponent()
    {
        if (!CanAbortClaimed || Component is not { } component)
            return false;
        component.AbortGuardedConstruction(this);
        return _cleanupCompleted;
    }

    internal void ValidateIntrinsicOwner()
    {
        if (!IsIntrinsicOwnerCurrent())
            throw new InvalidOperationException("Component owner changed during guarded cleanup.");
    }

    internal void BeginAbort() => _aborting = true;
    internal void EndAbort() => _aborting = false;

    internal void ValidateDestructionOwner()
    {
        if (_aborting)
        {
            if (_insertionCommitted || Component?.HasEnteredStructuralList == true ||
                !HasExpectedMembership(inserted: false) ||
                !IsIntrinsicOwnerCurrentIgnoringMembership())
                throw new InvalidOperationException("Component owner changed during guarded cleanup.");
            return;
        }
        ValidateIntrinsicOwner();
    }

    internal void AdmitAbortLifetime()
    {
        if (Component is not { } component || _componentIdentity is not { } previous ||
            !ReferenceEquals(previous.Target, component) || component.ID != previous.Id ||
            ObjectConstructionIdentity.Capture(component).LifetimeRevision != previous.LifetimeRevision + 1 ||
            !IsStructuralOwnerCurrent() || _insertionCommitted || component.HasEnteredStructuralList ||
            !HasExpectedMembership(inserted: false))
            throw new InvalidOperationException("Component lifetime changed before guarded cleanup.");
        _componentIdentity = ObjectConstructionIdentity.Capture(component);
        _cleanupStarted = true;
    }

    internal void CompleteAbort() => _cleanupCompleted = true;
    internal void MarkWorldAssigned()
    {
        _worldAssigned = true;
        Validate();
    }

    internal void ValidateWorldWrite(IRuntimeWorldContext? value)
    {
        if (!ReferenceEquals(value, _world))
            throw new InvalidOperationException("Component world changed during guarded creation.");
    }

    internal void Validate()
    {
        if (_aborting)
        {
            ValidateDestructionOwner();
            return;
        }
        if (!IsIntrinsicOwnerCurrent() ||
            (_validateOwner is not null && !_validateOwner()) ||
            !IsIntrinsicOwnerCurrent())
            throw new InvalidOperationException("Component creation owner changed during a callback.");
    }

    private bool IsIntrinsicOwnerCurrent()
    {
        if (!IsIntrinsicOwnerCurrentIgnoringMembership())
            return false;
        return HasExpectedMembership(_insertionCommitted);
    }

    private bool HasExpectedMembership(bool inserted)
    {
        if (Component is not { } component)
            return !inserted;
        HierarchyListMembership[] memberships = component.CaptureHierarchyListMemberships();
        if (!inserted)
            return memberships.Length == 0;
        return memberships.Length == 1 && memberships[0].Count == 1 && memberships[0].IsCurrent &&
            ReferenceEquals(memberships[0].Owner, _node) &&
            ReferenceEquals(memberships[0].Collection, _componentList);
    }

    private bool IsIntrinsicOwnerCurrentIgnoringMembership()
    {
        if (!IsStructuralOwnerCurrent() ||
            (Component is { } component &&
                (component.IsDestroyed || component.IsDestroyQueued || _componentIdentity?.IsCurrent != true ||
                 !ReferenceEquals(component.SceneNode, _node) ||
                 (_worldAssigned && !ReferenceEquals(component.World, _world)) ||
                 (component.World is not null && !ReferenceEquals(component.World, _world)))))
            return false;
        return true;
    }

    internal void ValidateStructuralOwner()
    {
        if (!IsStructuralOwnerCurrent())
            throw new InvalidOperationException("Component owner changed during guarded cleanup.");
    }

    private bool IsStructuralOwnerCurrent()
    {
        if (_node.IsDestroyed || _node.IsDestroyQueued || !_nodeIdentity.IsCurrent ||
            _transform.IsDestroyed || _transform.IsDestroyQueued || !_transformIdentity.IsCurrent ||
            !ReferenceEquals(_node.Transform, _transform) ||
            _componentList.IsDestroyed || _componentList.IsDestroyQueued || !_componentListIdentity.IsCurrent ||
            !ReferenceEquals(_node.OwnedRetirementComponents, _componentList) ||
            !ReferenceEquals(_transform.SceneNode, _node) ||
            !ReferenceEquals(_transform.Parent, _parent) ||
            !ReferenceEquals(_node.World, _world) ||
            (_parent is not null && (_parent.IsDestroyed || _parent.IsDestroyQueued || _parentIdentity?.IsCurrent != true)))
            return false;
        return true;
    }
}
