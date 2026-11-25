using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Retains one detached leaf and only component attempts registered before construction.</summary>
internal sealed class DetachedLeafRetirementReceipt
{
    private readonly SceneNode _node;
    private readonly TransformBase _transform;
    private readonly EventList<XRComponent> _componentList;
    private readonly EventList<TransformBase> _childList;
    private readonly IRuntimeWorldContext? _sourceWorld;
    private readonly TransformHierarchyStore? _sourceStore;
    private readonly TransformHandle _sourceHandle;
    private readonly ObjectConstructionIdentity[] _baseIdentities;
    private readonly List<RuntimeComponentCreationReceipt> _attempts = [];
    private readonly Lock _sync = new();
    private EOwnedHierarchyLifetimeState _state;
    private Exception? _failure;
    private OwnedHierarchyRetirement? _retirement;
    private HashSet<XRObjectBase>? _retainedObjects;

    internal DetachedLeafRetirementReceipt(SceneNode node) : this(node, null, false) { }

    /// <summary>Captures an already world-bound leaf before its guarded component factories run.</summary>
    internal DetachedLeafRetirementReceipt(SceneNode node, IRuntimeWorldContext sourceWorld)
        : this(node, sourceWorld, true) { }

    private DetachedLeafRetirementReceipt(SceneNode node, IRuntimeWorldContext? sourceWorld, bool requireWorld)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (requireWorld) ArgumentNullException.ThrowIfNull(sourceWorld);
        _node = node;
        _transform = node.OwnedRetirementTransform ??
            throw new InvalidOperationException("A detached leaf requires a transform.");
        _componentList = node.OwnedRetirementComponents ??
            throw new InvalidOperationException("A detached leaf requires component storage.");
        _childList = _transform.OwnedRetirementChildren ??
            throw new InvalidOperationException("A detached leaf requires child storage.");
        _sourceWorld = sourceWorld;
        _sourceStore = _transform.HierarchyStore;
        _sourceHandle = _transform.HierarchyHandle;
        _baseIdentities =
        [
            ObjectConstructionIdentity.Capture(node),
            ObjectConstructionIdentity.Capture(_transform),
            ObjectConstructionIdentity.Capture(_componentList),
            ObjectConstructionIdentity.Capture(_childList)
        ];
        ValidateLeafShape();
        if (_componentList.Count != 0)
            throw new InvalidOperationException("Register the detached leaf before adding components.");
    }

    internal EOwnedHierarchyLifetimeState State { get { lock (_sync) return _state; } }
    internal Exception? Failure { get { lock (_sync) return _failure; } }
    internal OwnedHierarchyRetirement? RetainedRetirement { get { lock (_sync) return _retirement; } }
    internal IRuntimeWorldContext? SourceWorld => _sourceWorld;
    internal SceneNode Node => _node;
    internal TransformHierarchyStore? SourceStore => _sourceStore;
    internal TransformHandle SourceHandle => _sourceHandle;

    internal void Register(RuntimeComponentCreationReceipt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_sync)
        {
            if (_state != EOwnedHierarchyLifetimeState.Retained ||
                !ReferenceEquals(attempt.Node, _node) || attempt.HasStarted ||
                _attempts.Contains(attempt))
                throw new InvalidOperationException("Register a new component attempt before its factory starts.");
            ValidateLeafShape();
            _attempts.Add(attempt);
        }
    }

    internal bool TryRetire()
        => TryRetireCore(null);

    internal bool TryRetireFromWorld(IRuntimeWorldContext sourceWorld)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        return TryRetireCore(sourceWorld);
    }

    private bool TryRetireCore(IRuntimeWorldContext? sourceWorld)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_sourceWorld, sourceWorld))
                throw new InvalidOperationException("The retirement source world does not match its receipt.");
            if (_state != EOwnedHierarchyLifetimeState.Retained)
                return _state == EOwnedHierarchyLifetimeState.Retired;
            _state = EOwnedHierarchyLifetimeState.Retiring;
        }
        try
        {
            (IReadOnlyList<XRObjectBase> objects, IReadOnlyList<ObjectConstructionIdentity> identities) = BuildInput();
            _retainedObjects = new(objects, ReferenceEqualityComparer.Instance);
            _retirement = new OwnedHierarchyRetirement(this, objects, identities, sourceWorld);
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

    internal bool Contains(XRObjectBase value) => _retainedObjects?.Contains(value) == true;

    internal (IReadOnlyList<XRObjectBase> Objects, IReadOnlyList<ObjectConstructionIdentity> Identities)
        CaptureAttachmentInput()
    {
        lock (_sync)
        {
            if (_state != EOwnedHierarchyLifetimeState.Retained)
                throw new InvalidOperationException("The detached leaf is no longer available for attachment.");
            return BuildInput();
        }
    }

    private (IReadOnlyList<XRObjectBase>, IReadOnlyList<ObjectConstructionIdentity>) BuildInput()
    {
        ValidateLeafShape();
        List<XRObjectBase> objects = [_node, _transform, _componentList, _childList];
        List<ObjectConstructionIdentity> identities = [.. _baseIdentities];
        HashSet<XRComponent> registered = new(ReferenceEqualityComparer.Instance);
        foreach (RuntimeComponentCreationReceipt attempt in _attempts)
        {
            if (attempt.HasStarted && !attempt.HasFinished)
                throw new InvalidOperationException("A registered component factory is still active.");
            if (attempt.Component is not { } component)
                continue;
            if (!registered.Add(component) || attempt.ClaimedIdentity is not { } identity ||
                !ReferenceEquals(identity.Target, component))
                throw new InvalidOperationException("A component has no unique registered construction claim.");
            if (attempt.CleanupStarted)
            {
                if (!attempt.CleanupCompleted || !component.IsDestroyed)
                    throw new InvalidOperationException("A guarded component cleanup is incomplete.");
                continue;
            }
            if (!identity.IsCurrent || component.IsDestroyed || component.IsDestroyQueued ||
                !ReferenceEquals(component.OwnedRetirementNode, _node))
                throw new InvalidOperationException("A registered component changed owner or lifetime.");
            HierarchyListMembership[] memberships = component.CaptureHierarchyListMemberships();
            if (memberships.Length == 0)
            {
                if (attempt.Failure is null || component.HasEnteredStructuralList ||
                    component.World is not null && !ReferenceEquals(component.World, _sourceWorld))
                    throw new InvalidOperationException("A registered component left its original list.");
            }
            else if (memberships.Length != 1 || memberships[0].Count != 1 || !memberships[0].IsCurrent ||
                !ReferenceEquals(memberships[0].Owner, _node) ||
                !ReferenceEquals(memberships[0].Collection, _componentList) ||
                !ReferenceEquals(component.World, _sourceWorld))
                throw new InvalidOperationException("A registered component has a foreign or duplicate list owner.");
            objects.Add(component);
            identities.Add(identity);
        }
        foreach (XRComponent component in _componentList)
            if (!registered.Contains(component))
                throw new InvalidOperationException("The leaf contains an unregistered component.");
        return (objects, identities);
    }

    private void ValidateLeafShape()
    {
        foreach (ObjectConstructionIdentity identity in _baseIdentities)
            if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                throw new InvalidOperationException("The detached leaf changed lifetime.");
        if (_node.HasEnteredRootCollection || _node.HasEnteredStructuralList ||
            _transform.HasEnteredStructuralList ||
            !ReferenceEquals(_node.World, _sourceWorld) ||
            !ReferenceEquals(_transform.World, _sourceWorld) ||
            !ReferenceEquals(_transform.HierarchyStore, _sourceStore) ||
            _transform.HierarchyHandle != _sourceHandle ||
            !ReferenceEquals(_node.OwnedRetirementTransform, _transform) ||
            !ReferenceEquals(_transform.OwnedRetirementNode, _node) ||
            _transform.OwnedRetirementParent is not null ||
            !ReferenceEquals(_node.OwnedRetirementComponents, _componentList) ||
            !ReferenceEquals(_transform.OwnedRetirementChildren, _childList) ||
            _childList.Count != 0 || _node.HasSceneRootOwner ||
            _node.CaptureRuntimeRootMemberships().Length != 0 ||
            _node.CaptureHierarchyListMemberships().Length != 0 ||
            _transform.CaptureHierarchyListMemberships().Length != 0 ||
            (_sourceWorld is null && _transform.HierarchyStore is not null))
            throw new InvalidOperationException("The retirement receipt requires an exact detached leaf.");
    }
}
