using XREngine.Components;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Transforms;

public abstract partial class TransformBase
{
    internal void ClearWorldForCanonicalUnlink(Action? validate)
        => SetWorldValue(null, validate);

    private void RequireChangedParent(TransformBase? expected)
    {
        if (!ReferenceEquals(_parent, expected))
            throw new InvalidOperationException("The parent changed during hierarchy callbacks.");
    }

    private void SetInheritedWorld(TransformBase parent, IRuntimeWorldContext world)
    {
        IRuntimeWorldContext? previousWorld = World;
        SetWorldValue(world, () =>
        {
            RequireChangedParent(parent);
            if (!ReferenceEquals(World, previousWorld))
                throw new InvalidOperationException("The world changed during hierarchy callbacks.");
        });
    }

    private void SetParentValue(TransformBase? value, Action? validate = null)
    {
        using HierarchyMutationLease structural = new(this, _parent, value);
        validate?.Invoke();
        HierarchyConstructionScope.RequireLink(this, value);
        if (ReferenceEquals(_parent, value)) return;
        TransformBase? original = _parent;
        using var mutation = PhysicsChainWorld.BeginHierarchyMutation(this, value?.World ?? World, value);
        _parentAssignmentDepth++;
        try
        {
            validate?.Invoke();
            bool notify = !ArePropertyNotificationsSuppressed;
            if (notify && !OnPropertyChanging(nameof(Parent), original, value)) return;
            validate?.Invoke();
            HierarchyConstructionScope.RequireAssignment(this, original, _parent, value);
            if (notify)
            {
                for (TransformBase? ancestor = value; ancestor is not null; ancestor = ancestor.Parent)
                    if (ReferenceEquals(ancestor, this))
                        throw new InvalidOperationException("A transform cannot be parented beneath itself.");
                _parent?._children.Remove(this);
            }
            lock (SceneRootCollection.MutationGate)
            {
                validate?.Invoke();
                HierarchyConstructionScope.RequireAssignment(this, original, _parent, value);
                SetField(ref _parent, value, publishNotifications: false, propertyName: nameof(Parent));
            }
            if (notify) OnPropertyChanged(nameof(Parent), original, value);
        }
        finally { --_parentAssignmentDepth; }
    }
}
