using XREngine.Components;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Executes one reserved structural forest cleanup and retains exact progress on failure.</summary>
internal sealed class OwnedHierarchyRetirement(ObjectConstructionOwnership ownership)
{
    private readonly Dictionary<XRWorldObjectBase, OwnedHierarchyRetirementEntry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    internal HierarchyStructuralReservation Reservation { get; private set; } = null!;

    internal void Execute()
    {
        List<XRWorldObjectBase> objects = [];
        foreach (XRObjectBase value in ownership.Objects)
        {
            Type type = value.GetType();
            if (value is SceneNode or TransformBase or XRComponent)
            {
                if (!((XRWorldObjectBase)value).UsesCanonicalOwnedDestruction())
                    throw new InvalidOperationException($"Owned cleanup has no synchronous destruction adapter for '{type.FullName}' ({value.ID}).");
                objects.Add((XRWorldObjectBase)value);
            }
            else if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(EventList<>))
                throw new InvalidOperationException($"Owned cleanup retains unsupported allocation '{type.FullName}' ({value.ID}).");
        }

        using HierarchyStructuralReservation reservation = HierarchyStructuralReservation.TryAcquire(objects) ??
            throw new InvalidOperationException("An owned structural mutation is still in progress.");
        Reservation = reservation;
        lock (SceneRootCollection.MutationGate)
        {
            foreach (XRWorldObjectBase value in objects)
                _entries.Add(value, new(value));
            ValidateInitialGraph();
            Validate();
        }

        // Detached components can retain their old node reference. Retire them while
        // that node's transform is still available to canonical component cleanup.
        foreach (OwnedHierarchyRetirementEntry entry in _entries.Values)
            if (entry.Value is XRComponent && entry.ListSlot is null && !entry.Destroyed)
                DestroyTarget(entry.Value);
        foreach (OwnedHierarchyRetirementEntry entry in _entries.Values)
            if (entry.Value is TransformBase transform && entry.Parent is null && !entry.Destroyed)
                RetireBranch(transform);
        foreach (OwnedHierarchyRetirementEntry entry in _entries.Values)
            if (!entry.Destroyed || !entry.CleanupComplete)
                throw new InvalidOperationException("An owned structural object did not complete canonical cleanup.");
        Validate();
        // Exact built-in EventLists are non-cache containers. Releasing this ledger
        // releases only those containers; it does not destroy their elements.
    }

    private void ValidateInitialGraph()
    {
        foreach (OwnedHierarchyRetirementEntry entry in _entries.Values)
        {
            switch (entry.Value)
            {
                case SceneNode:
                    RequireOwnedList(entry.ComponentList!);
                    if (!entry.Destroyed && entry.ComponentList!.IsDestroyed)
                        throw new InvalidOperationException("A live owned node has destroyed component storage.");
                    if (entry.Transform is null || !TryEntry(entry.Transform, out OwnedHierarchyRetirementEntry? transform) ||
                        !entry.Destroyed && (transform.Destroyed || !ReferenceEquals(transform.Node, entry.Value)))
                        throw new InvalidOperationException("An owned node has no exclusive owned transform.");
                    foreach (XRComponent component in entry.Components!)
                    {
                        OwnedHierarchyRetirementEntry child = RequireLive(component);
                        if (!ReferenceEquals(child.Node, entry.Value))
                            throw new InvalidOperationException("An owned component occurrence has a different node.");
                        SetInitialListOwner(child, entry.Value, entry.ComponentList!);
                    }
                    break;
                case TransformBase:
                    RequireOwnedList(entry.ChildList!);
                    if (!entry.Destroyed && entry.ChildList!.IsDestroyed)
                        throw new InvalidOperationException("A live owned transform has destroyed child storage.");
                    if (entry.Node is not null && (!TryEntry(entry.Node, out OwnedHierarchyRetirementEntry? node) ||
                        !ReferenceEquals(node.Transform, entry.Value)))
                        throw new InvalidOperationException("An owned transform points to a foreign node.");
                    if (entry.Parent is not null) RequireLive(entry.Parent);
                    foreach (TransformBase childTransform in entry.Children!)
                    {
                        OwnedHierarchyRetirementEntry child = RequireLive(childTransform);
                        if (!ReferenceEquals(child.Parent, entry.Value))
                            throw new InvalidOperationException("An owned child occurrence has a different parent.");
                        SetInitialListOwner(child, entry.Value, entry.ChildList!);
                    }
                    break;
                case XRComponent:
                    if (entry.Node is not null && !TryEntry(entry.Node, out _))
                        throw new InvalidOperationException("An owned component points to a foreign node.");
                    break;
            }
        }
        foreach (OwnedHierarchyRetirementEntry entry in _entries.Values)
        {
            if (entry.Value is XRComponent component && entry.ListSlot is null && component.IsComponentActivated)
                throw new InvalidOperationException("An orphan owned component still has an active lifecycle.");
            if (entry.Destroyed && entry.ListSlot is not null)
                throw new InvalidOperationException("A destroyed allocation retains a live structural occurrence.");
            if (entry.Value is not TransformBase) continue;
            if (entry.Parent is not null && !ReferenceEquals(entry.ListOwner, entry.Parent))
                throw new InvalidOperationException("An owned transform has no matching parent occurrence.");
            int remaining = _entries.Count;
            for (TransformBase? parent = entry.Parent; parent is not null; parent = _entries[parent].Parent)
                if (--remaining < 0)
                    throw new InvalidOperationException("An owned hierarchy contains a parent cycle.");
        }
    }

    private void RequireOwnedList<T>(EventList<T> list) where T : XRWorldObjectBase
    {
        if (!ownership.Contains(list) || list.GetType() != typeof(EventList<T>) ||
            list.GetMutationAuthority() is not HierarchyListAuthority<T>)
            throw new InvalidOperationException("An owned hierarchy has borrowed or unsupported structural storage.");
    }

    private bool TryEntry(XRWorldObjectBase? value, [NotNullWhen(true)] out OwnedHierarchyRetirementEntry? entry)
    {
        entry = null;
        return value is not null && _entries.TryGetValue(value, out entry);
    }

    private OwnedHierarchyRetirementEntry RequireLive(XRWorldObjectBase? value)
        => TryEntry(value, out OwnedHierarchyRetirementEntry? entry) && !entry!.Destroyed ? entry :
            throw new InvalidOperationException("An owned hierarchy contains a foreign or destroyed structural edge.");

    private static void SetInitialListOwner(OwnedHierarchyRetirementEntry entry, XRWorldObjectBase owner, object list)
    {
        if (entry.ListSlot is not null)
            throw new InvalidOperationException("Owned cleanup requires one exact occurrence per structural object.");
        entry.ListOwner = owner;
        entry.ListSlot = list;
    }

    internal void Validate()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Owned cleanup must remain on its synchronous owner thread.");
        lock (SceneRootCollection.MutationGate)
            foreach (OwnedHierarchyRetirementEntry entry in _entries.Values)
                entry.Validate();
    }

    internal void RetireBranch(TransformBase transform)
    {
        Validate();
        OwnedHierarchyRetirementEntry entry = _entries[transform];
        if (entry.Destroyed) return;
        DestroyTarget(entry.Node is { } node && !_entries[node].Destroyed ? node : transform);
    }

    internal void RetireChildren(TransformBase transform)
    {
        List<TransformBase> children = _entries[transform].Children!;
        while (children.Count != 0)
            RetireBranch(children[^1]);
    }

    internal void DestroyTarget(XRWorldObjectBase value)
    {
        Validate();
        OwnedHierarchyRetirementEntry entry = _entries[value];
        if (entry.Destroyed) return;
        OwnedHierarchyDestructionPermit permit = new(this, value);
        value.SetOwnedDestructionPermit(permit);
        try
        {
            value.Destroy(true);
            if (!permit.Returned || !value.IsDestroyed || !entry.CleanupComplete)
                throw new InvalidOperationException("Owned destruction was vetoed or omitted canonical cleanup.");
            entry.Destroyed = true;
            Validate();
        }
        finally { value.ClearOwnedDestructionPermit(permit); }
    }

    internal void CompleteCanonicalCleanup(XRWorldObjectBase value)
    {
        if (!ReferenceEquals(value.GetOwnedRetirement(), this))
            throw new InvalidOperationException("Canonical cleanup requires the exact active destruction permit.");
        Validate();
        if (value is XRComponent component && component.IsComponentActivated ||
            value is SceneNode node && node.HasBegunPlay)
            throw new InvalidOperationException("Canonical cleanup left an owned runtime lifecycle active.");
        _entries[value].CleanupComplete = true;
    }

    internal void StopLifecycle(XRWorldObjectBase value, EOwnedHierarchyLifecycle kind)
    {
        using OwnedHierarchyLifecyclePermit permit = new(this, value, kind);
        switch (value)
        {
            case SceneNode node: node.StopOwnedLifecycle(permit); break;
            case TransformBase transform: transform.StopOwnedLifecycle(permit); break;
            case XRComponent component: component.StopOwnedLifecycle(permit); break;
            default: throw new InvalidOperationException("The owned lifecycle target is unsupported.");
        }
    }

    internal OwnedHierarchyRemovalPermit ParentRemoval(TransformBase transform)
        => new(this, transform, EOwnedHierarchyRemoval.Parent, null, _entries[transform].Parent!);

    internal OwnedHierarchyRemovalPermit NodeRemoval(TransformBase transform)
        => new(this, transform, EOwnedHierarchyRemoval.TransformNode, null, _entries[transform].Node!);

    internal void RemoveChild(TransformBase parent, TransformBase child)
        => RemoveOccurrence(parent, child, EOwnedHierarchyRemoval.ChildOccurrence,
            _entries[parent].ChildList!, _entries[parent].Children!);

    internal void RemoveComponent(SceneNode node, XRComponent component)
        => RemoveOccurrence(node, component, EOwnedHierarchyRemoval.ComponentOccurrence,
            _entries[node].ComponentList!, _entries[node].Components!);

    private void RemoveOccurrence<T>(XRWorldObjectBase owner, T item, EOwnedHierarchyRemoval kind,
        EventList<T> list, IReadOnlyList<T> items) where T : XRWorldObjectBase
    {
        int index = 0;
        while (index < items.Count && !ReferenceEquals(items[index], item)) index++;
        if (index == items.Count) throw new InvalidOperationException("The owned occurrence is absent.");
        using OwnedHierarchyRemovalPermit permit = new(this, owner, kind, list, item, index);
        ((HierarchyListAuthority<T>)list.GetMutationAuthority()!).RemoveForRetirement(permit);
        Validate();
    }

    internal void Advance(OwnedHierarchyRemovalPermit permit)
    {
        OwnedHierarchyRetirementEntry entry = _entries[permit.Owner];
        switch (permit.Kind)
        {
            case EOwnedHierarchyRemoval.Parent: entry.Parent = null; break;
            case EOwnedHierarchyRemoval.TransformNode: entry.Node = null; break;
            case EOwnedHierarchyRemoval.ChildOccurrence:
                entry.Children!.RemoveAt(permit.Index);
                _entries[permit.Removed].ListOwner = null;
                _entries[permit.Removed].ListSlot = null;
                break;
            case EOwnedHierarchyRemoval.ComponentOccurrence:
                entry.Components!.RemoveAt(permit.Index);
                _entries[permit.Removed].ListOwner = null;
                _entries[permit.Removed].ListSlot = null;
                break;
        }
    }

    internal void RetireEmptyList<T>(XRWorldObjectBase owner, EventList<T> list) where T : XRWorldObjectBase
    {
        lock (SceneRootCollection.MutationGate)
        {
            Validate();
            if (!ReferenceEquals(owner.GetOwnedRetirement(), this) || list.Count != 0)
                throw new InvalidOperationException("Owned storage must be empty before its exact authority retires.");
            ((HierarchyListAuthority<T>)list.GetMutationAuthority()!).Retire();
            if (owner is SceneNode) _entries[owner].ComponentAuthorityRetired = true;
            else _entries[owner].ChildAuthorityRetired = true;
        }
    }
}
