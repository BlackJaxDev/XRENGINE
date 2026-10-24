using XREngine.Components;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Transforms;

public abstract partial class TransformBase
{
    internal bool OwnsChildList(object list) => ReferenceEquals(_children, list);

    private EventList<TransformBase> CreateChildList(IEnumerable<TransformBase>? values = null,
        bool allowDuplicates = true, bool allowNull = true)
        => new(new HierarchyListAuthority<TransformBase>(this), values, allowDuplicates, allowNull);

    private void ReplaceChildList(EventList<TransformBase> values)
    {
        if (values is null || ReferenceEquals(_children, values)) return;
        using HierarchyMutationLease owner = new(this);
        HierarchyConstructionScope.RequireLink(this, values);
        EventList<TransformBase> previous = _children;
        TransformBase[] previousItems = [.. previous];
        using HierarchyMutationRangeLease oldEndpoints = new(previousItems);
        TransformBase[] copied = [.. values];
        using HierarchyMutationRangeLease newEndpoints = new(copied);
        RequirePreviousChildList(previous, previousItems);
        foreach (TransformBase child in copied)
            HierarchyConstructionScope.RequireLink(this, child);

        EventList<TransformBase> replacement = CreateChildList(copied, values.AllowDuplicates, values.AllowNull);
        try
        {
            using var mutation = PhysicsChainWorld.BeginChildrenMutation(this, replacement);
            RequirePreviousChildList(previous, previousItems);
            bool notify = !ArePropertyNotificationsSuppressed;
            if (notify && !OnPropertyChanging(nameof(Children), previous, replacement))
                return;
            RequirePreviousChildList(previous, previousItems);
            IReadOnlyList<TransformBase> remaining = notify
                ? DetachReplacedChildren(previous, previousItems)
                : previousItems;
            lock (SceneRootCollection.MutationGate)
            {
                RequirePreviousChildList(previous, remaining);
                HierarchyConstructionScope.RequireAssignment(this, previous, _children, replacement);
                foreach (TransformBase child in copied)
                    HierarchyConstructionScope.RequireLink(this, child);
                SetField(ref _children, replacement, publishNotifications: false, propertyName: nameof(Children));
            }
            if (notify)
                OnPropertyChanged(nameof(Children), previous, replacement);
        }
        finally
        {
            // A nested replacement owns its own authority. Retire only these exact stale lists.
            if (!ReferenceEquals(_children, replacement))
                ((HierarchyListAuthority<TransformBase>)replacement.GetMutationAuthority()!).Retire();
            if (!ReferenceEquals(_children, previous))
                ((HierarchyListAuthority<TransformBase>)previous.GetMutationAuthority()!).Retire();
            else if (!ArePropertyNotificationsSuppressed)
                AttachChildListHandlers();
        }
    }

    private IReadOnlyList<TransformBase> DetachReplacedChildren(
        EventList<TransformBase> previous, TransformBase[] items)
    {
        List<TransformBase> remaining = [.. items];
        DetachChildListHandlers();
        foreach (TransformBase child in items)
        {
            RequirePreviousChildList(previous, remaining);
            if (child is null) continue;
            if (child.Parent is { } parent && !ReferenceEquals(parent, this))
                throw new InvalidOperationException("A child list entry belongs to another parent.");
            if (ReferenceEquals(child.Parent, this))
            {
                int index = remaining.FindIndex(item => ReferenceEquals(item, child));
                if (index >= 0) remaining.RemoveAt(index);
                SetReplacementParent(previous, child, null);
            }
            RequirePreviousChildList(previous, remaining);
            if (child.Parent is not null)
                throw new InvalidOperationException("A child changed parent during list replacement.");
            SetReplacementWorld(previous, child, null);
            RequirePreviousChildList(previous, remaining);
            if (child.Parent is not null)
                throw new InvalidOperationException("A child changed parent during list replacement.");
        }
        return remaining;
    }

    private void AttachReplacedChildren(EventList<TransformBase> expected, TransformBase[] items)
    {
        RequirePreviousChildList(expected, items);
        AttachChildListHandlers();
        foreach (TransformBase child in items)
        {
            RequirePreviousChildList(expected, items);
            if (child is null) continue;
            SetReplacementParent(expected, child, this);
            RequirePreviousChildList(expected, items);
            if (!ReferenceEquals(child.Parent, this))
                throw new InvalidOperationException("A child changed parent during list replacement.");
            SetReplacementWorld(expected, child, World);
            RequirePreviousChildList(expected, items);
            if (!ReferenceEquals(child.Parent, this))
                throw new InvalidOperationException("A child changed parent during list replacement.");
        }
    }

    private void SetReplacementParent(EventList<TransformBase> expected, TransformBase child, TransformBase? value)
    {
        TransformBase? parent = child._parent;
        IRuntimeWorldContext? world = child.World;
        child.SetParentValue(value, () => RequireReplacementChild(expected, child, parent, world));
    }

    private void SetReplacementWorld(EventList<TransformBase> expected, TransformBase child, IRuntimeWorldContext? value)
    {
        TransformBase? parent = child._parent;
        IRuntimeWorldContext? world = child.World;
        child.SetWorldValue(value, () => RequireReplacementChild(expected, child, parent, world));
    }

    private void RequireReplacementChild(EventList<TransformBase> expected, TransformBase child,
        TransformBase? parent, IRuntimeWorldContext? world)
    {
        if (!ReferenceEquals(_children, expected) || !ReferenceEquals(child._parent, parent) ||
            !ReferenceEquals(child.World, world))
            throw new InvalidOperationException("A child changed owner during list replacement.");
    }

    private void RequirePreviousChildList(EventList<TransformBase> expected, IReadOnlyList<TransformBase> items)
    {
        ((HierarchyListAuthority<TransformBase>)expected.GetMutationAuthority()!).RequireReplacement();
        if (!ReferenceEquals(_children, expected) || expected.Count != items.Count)
            throw new InvalidOperationException("The child list changed while replacement values were materialized.");
        for (int index = 0; index < items.Count; index++)
            if (!ReferenceEquals(expected[index], items[index]))
                throw new InvalidOperationException("The child list changed while replacement values were materialized.");
    }
}
