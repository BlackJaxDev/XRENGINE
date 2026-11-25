using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Pins the source forest and maps clone endpoints by canonical structural order.</summary>
internal sealed class HierarchyDuplicateSource
{
    private readonly HierarchyDuplicateSourceEntry[] _entries;
    private readonly ObjectConstructionIdentity[] _identities;
    internal XRWorldObjectBase[] Targets { get; }

    internal HierarchyDuplicateSource(SceneNode root)
    {
        List<HierarchyDuplicateSourceEntry> entries = [];
        List<XRWorldObjectBase> targets = [];
        HashSet<XRObjectBase> seen = new(ReferenceEqualityComparer.Instance);
        List<ObjectConstructionIdentity> identities = [];
        lock (SceneRootCollection.MutationGate)
        {
            TransformBase transform = root.OwnedRetirementTransform ?? throw new InvalidOperationException("The source has no transform.");
            Capture(transform, transform.OwnedRetirementParent, transform.World, true);
            _entries = [.. entries];
            Targets = [.. targets];
            _identities = [.. identities];
            RequireCurrent();
        }

        void Add(XRObjectBase value)
        {
            if (!seen.Add(value) || value.IsDestroyed || value.IsDestroyQueued)
                throw new InvalidOperationException("The duplicate source has a repeated or ended structural lifetime.");
            identities.Add(ObjectConstructionIdentity.Capture(value));
            if (value is XRWorldObjectBase target) targets.Add(target);
        }
        void Capture(TransformBase transform, TransformBase? parent, IRuntimeWorldContext? world, bool rootEntry)
        {
            HierarchyDuplicateSourceEntry entry = new(transform);
            entry.RequireLinks(parent, world);
            RequireRootLinks(entry, rootEntry);
            entries.Add(entry);
            if (entry.Node is not null) Add(entry.Node);
            Add(transform);
            Add(entry.Children);
            if (entry.Components is not null) Add(entry.Components);
            foreach (var component in entry.ComponentItems) Add(component);
            foreach (TransformBase child in entry.ChildItems) Capture(child, transform, world, false);
        }
    }

    internal void RequireCurrent()
    {
        foreach (ObjectConstructionIdentity identity in _identities)
            if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                throw new InvalidOperationException("The duplicate source lifetime changed.");
        for (int index = 0; index < _entries.Length; index++)
        {
            _entries[index].RequireCurrent();
            RequireRootLinks(_entries[index], index == 0);
        }
    }

    internal void RequireCloneShape()
    {
        IRuntimeWorldContext? world = _entries[0].Transform.World;
        for (int index = 0; index < _entries.Length; index++)
        {
            HierarchyDuplicateSourceEntry entry = _entries[index];
            entry.RequireLinks(index == 0 ? entry.Transform.OwnedRetirementParent : entry.Parent, world);
            RequireRootLinks(entry, index == 0);
        }
    }

    private static void RequireRootLinks(HierarchyDuplicateSourceEntry entry, bool rootEntry)
    {
        if (!rootEntry && entry.Node is { } node &&
            (node.CaptureSceneRootMemberships().Length != 0 || node.CaptureRuntimeRootMemberships().Length != 0))
            throw new InvalidOperationException("A duplicate child also belongs to a root collection.");
    }

    internal XRWorldObjectBase[] MatchClone(SceneNode clone, ObjectConstructionContext context)
    {
        HierarchyDuplicateSource cloneSource = new(clone);
        XRWorldObjectBase[] targets = cloneSource.Targets;
        if (targets.Length != Targets.Length || cloneSource._entries.Length != _entries.Length)
            throw new InvalidOperationException("The clone does not match its source structure.");
        for (int index = 0; index < targets.Length; index++)
        {
            context.RequireAllocation(targets[index]);
            if (targets[index].GetType() != Targets[index].GetType())
                throw new InvalidOperationException("The clone changed a source structural type.");
        }
        for (int index = 0; index < _entries.Length; index++)
            if (cloneSource._entries[index].ChildItems.Length != _entries[index].ChildItems.Length ||
                cloneSource._entries[index].ComponentItems.Length != _entries[index].ComponentItems.Length ||
                (cloneSource._entries[index].Node is null) != (_entries[index].Node is null))
                throw new InvalidOperationException("The clone changed a source structural shape.");
        return targets;
    }
}
