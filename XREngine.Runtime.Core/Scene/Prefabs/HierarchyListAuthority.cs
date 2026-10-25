using XREngine.Scene.Transforms;
using XREngine.Components;

namespace XREngine.Scene.Prefabs;

/// <summary>Maintains exact links for one private child or component list.</summary>
internal sealed class HierarchyListAuthority<T>(XRWorldObjectBase owner) : EventListMutationAuthority<T>, IHierarchyListAuthority
    where T : XRWorldObjectBase
{
    private EventList<T>? _list;
    private IReadOnlyList<T>? _storage;
    private bool _active;

    public XRWorldObjectBase Owner { get; } = owner;
    public object Collection => _list!;
    public bool IsCurrent => _active && _list is not null &&
        (Owner is SceneNode node && node.OwnsComponentList(_list) ||
         Owner is TransformBase transform && transform.OwnsChildList(_list));
    protected override Lock MutationGate => SceneRootCollection.MutationGate;

    protected override void Initialize(EventList<T> list, IReadOnlyList<T> values)
    {
        if (_list is not null)
            throw new InvalidOperationException("A structural list authority cannot be installed twice.");
        Owner.RequireStructuralMutation();
        HierarchyConstructionScope.RequireLink(Owner, list);
        foreach (T item in values)
        {
            if (item is null) continue;
            item.RequireStructuralMutation();
            HierarchyConstructionScope.RequireLink(Owner, item);
            item.PrepareHierarchyListOwner(this);
        }
        _list = list;
        _storage = values;
        foreach (T item in values)
            item?.AddHierarchyListOwner(this);
        _active = true;
    }

    protected override void RequireCurrent(EventList<T> list)
    {
        if (!ReferenceEquals(_list, list) || !IsCurrent || Owner.IsDestroyed || Owner.IsDestroyQueued ||
            list.IsDestroyed || list.IsDestroyQueued)
            throw new InvalidOperationException("The structural list no longer belongs to its exact owner.");
        Owner.RequireStructuralMutation();
    }

    protected override void EnterMutation(EventList<T> list)
    {
        RequireCurrent(list);
        Owner.EnterStructuralMutation();
    }

    protected override void RequireRemovalCurrent(EventList<T> list)
    {
        // A queued lifetime can be vetoed. Exact cleanup must settle before that decision.
        if (!ReferenceEquals(_list, list) || !IsCurrent || Owner.IsDestroyed || list.IsDestroyed)
            throw new InvalidOperationException("The structural list no longer belongs to its exact owner.");
        Owner.RequireStructuralMutation();
    }

    protected override void EnterRemoval(EventList<T> list)
    {
        RequireRemovalCurrent(list);
        Owner.EnterStructuralMutation();
    }

    internal void RequireReplacement()
    {
        lock (MutationGate) RequireCurrent(_list!);
    }

    protected override void ExitMutation() => Owner.ExitStructuralMutation();

    protected override void ReserveItem(T item, bool adding)
    {
        if (item is null) return;
        if (adding) HierarchyConstructionScope.RequireLink(Owner, item);
        item.EnterStructuralMutation();
    }

    protected override void ReleaseItem(T item) => item?.ExitStructuralMutation();
    protected override void PrepareAdd(T item)
    {
        if (item is XRComponent { CreationReceipt: { } receipt })
        {
            if (Owner is not SceneNode node)
                throw new InvalidOperationException("A guarded component requires its exact scene node.");
            receipt.ValidateBeforeInsert(node);
        }
        item?.PrepareHierarchyListOwner(this);
    }

    protected override void Added(T item)
    {
        item?.AddHierarchyListOwner(this);
        if (item is XRComponent { CreationReceipt: { } receipt })
            receipt.CommitInsertion();
    }
    protected override void Removed(T item) => item?.RemoveHierarchyListOwner(this);

    protected override void EnterLifetimeMutation(EventList<T> list)
    {
        if (!ReferenceEquals(_list, list))
            return; // A constructor that failed before authority installation owns no links.
        // The authority is immutable, even after replacement. Keep the exact owner's
        // counter through callbacks without granting the list its destruction permit.
        Owner.EnterLinkedListLifetimeMutation();
    }

    protected override void ExitLifetimeMutation(EventList<T> list)
    {
        if (ReferenceEquals(_list, list))
            Owner.ExitLinkedListLifetimeMutation();
    }

    internal void RemoveForRetirement(OwnedHierarchyRemovalPermit permit)
    {
        EOwnedHierarchyRemoval kind = Owner is SceneNode
            ? EOwnedHierarchyRemoval.ComponentOccurrence : EOwnedHierarchyRemoval.ChildOccurrence;
        if (_list is null || !IsCurrent || Owner.IsDestroyed || _list.IsDestroyed ||
            permit.Removed is not T item)
            throw new InvalidOperationException("The reserved removal has no live exact list owner.");
        permit.Enter(Owner, kind, _list, item);
        // These delegates are confined to rare lifetime cleanup. They advance the
        // reserved graph in the same callback-free transaction as the storage write.
        RemoveReservedOccurrence(_list, permit.Index, item, permit.RequireCommit, permit.Commit);
    }

    internal void Retire()
    {
        lock (MutationGate)
        {
            if (!_active) return;
            foreach (T item in _storage!)
                item?.RemoveHierarchyListOwner(this);
            _active = false;
        }
    }
}
