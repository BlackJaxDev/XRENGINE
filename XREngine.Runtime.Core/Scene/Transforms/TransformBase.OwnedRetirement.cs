using MemoryPack;
using XREngine.Components;
using XREngine.Scene.Prefabs;
using YamlDotNet.Serialization;

namespace XREngine.Scene.Transforms;

public abstract partial class TransformBase
{
    [YamlIgnore, MemoryPackIgnore]
    internal TransformBase? OwnedRetirementParent => _parent;
    [YamlIgnore, MemoryPackIgnore]
    internal SceneNode? OwnedRetirementNode => _sceneNode;
    [YamlIgnore, MemoryPackIgnore]
    internal EventList<TransformBase>? OwnedRetirementChildren => _children;

    internal void StopOwnedLifecycle(OwnedHierarchyLifecyclePermit permit)
    {
        permit.Enter(this);
        if (permit.Kind == EOwnedHierarchyLifecycle.Deactivate) OnSceneNodeDeactivated();
        else OnSceneNodeEndPlay();
        permit.Complete();
    }

    private void DestroyOwnedHierarchy(OwnedHierarchyRetirement retirement)
    {
        retirement.Validate();
        using var mutation = PhysicsChainWorld.BeginTransformDestructionMutation(this);
        retirement.Validate();
        retirement.RetireChildren(this);
        DetachChildListHandlers();
        if (_parent is not null)
        {
            using OwnedHierarchyRemovalPermit permit = retirement.ParentRemoval(this);
            RemoveOwnedParent(permit);
        }
        if (_sceneNode is not null)
        {
            using OwnedHierarchyRemovalPermit permit = retirement.NodeRemoval(this);
            RemoveOwnedNode(permit);
        }
        retirement.Validate();
        _debugHandle?.Dispose();
        retirement.Validate();
        _localMatrixChanged = null;
        _inverseLocalMatrixChanged = null;
        _worldMatrixChanged = null;
        _inverseWorldMatrixChanged = null;
        _renderMatrixChanged = null;
        base.OnDestroying();
        retirement.RetireEmptyList(this, _children);
        retirement.CompleteCanonicalCleanup(this);
    }

    private void RemoveOwnedParent(OwnedHierarchyRemovalPermit permit)
    {
        TransformBase previous = _parent!;
        permit.Enter(this, EOwnedHierarchyRemoval.Parent, null, previous);
        using var mutation = PhysicsChainWorld.BeginHierarchyMutation(this, null);
        _parentAssignmentDepth++;
        try
        {
            bool notify = !ArePropertyNotificationsSuppressed;
            if (notify && !OnPropertyChanging<TransformBase?>(nameof(Parent), previous, null))
                throw new InvalidOperationException("The owned parent removal was vetoed.");
            permit.RequireCommit();
            permit.Retirement.RemoveChild(previous, this);
            lock (SceneRootCollection.MutationGate)
            {
                permit.RequireCommit();
                SetField<TransformBase?>(ref _parent, null, publishNotifications: false, propertyName: nameof(Parent));
                permit.Commit();
            }
            if (notify) OnPropertyChanged<TransformBase?>(nameof(Parent), previous, null);
            permit.Retirement.Validate();
        }
        finally { --_parentAssignmentDepth; }
    }

    private void RemoveOwnedNode(OwnedHierarchyRemovalPermit permit)
    {
        SceneNode previous = _sceneNode!;
        permit.Enter(this, EOwnedHierarchyRemoval.TransformNode, null, previous);
        bool notify = !ArePropertyNotificationsSuppressed;
        if (notify && !OnPropertyChanging<SceneNode?>(nameof(SceneNode), previous, null))
            throw new InvalidOperationException("The owned node reference removal was vetoed.");
        lock (SceneRootCollection.MutationGate)
        {
            permit.RequireCommit();
            SetField<SceneNode?>(ref _sceneNode, null, publishNotifications: false, propertyName: nameof(SceneNode));
            permit.Commit();
        }
        if (notify) OnPropertyChanged<SceneNode?>(nameof(SceneNode), previous, null);
        permit.Retirement.Validate();
    }
}
