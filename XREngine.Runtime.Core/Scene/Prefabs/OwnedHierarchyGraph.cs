using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Validates the exact detached graph before owned clone callbacks and identity changes.</summary>
internal sealed class OwnedHierarchyGraph
{
    private readonly ObjectConstructionContext _context;
    private readonly OwnedHierarchyGraphEntry[] _entries;

    internal OwnedHierarchyGraph(SceneNode root, ObjectConstructionContext context)
    {
        _context = context;
        context.RequireAllocation(root);
        if (root.OwnedRetirementTransform is not { } transform)
            throw new InvalidOperationException("An owned clone has no transform.");
        List<OwnedHierarchyGraphEntry> entries = [];
        HashSet<XRObjectBase> visited = new(ReferenceEqualityComparer.Instance);
        Capture(transform, null, entries, visited);
        _entries = [.. entries];
        if (!ReferenceEquals(_entries[0].Node, root))
            throw new InvalidOperationException("The cloned root does not own its transform.");
        Validate();
    }

    private void Capture(TransformBase transform, TransformBase? parent,
        List<OwnedHierarchyGraphEntry> entries, HashSet<XRObjectBase> visited)
    {
        RequireNew(transform, visited);
        SceneNode? node = transform.OwnedRetirementNode;
        if (node is not null)
            RequireNew(node, visited);
        EventList<XRComponent>? componentList = node?.OwnedRetirementComponents;
        XRComponent[] components = componentList is null ? [] : [.. componentList];
        foreach (XRComponent component in components)
            RequireNew(component, visited);
        EventList<TransformBase> childList = transform.OwnedRetirementChildren ??
            throw new InvalidOperationException("An owned clone has no child list.");
        TransformBase[] children = [.. childList];
        entries.Add(new(transform, parent, node, childList, children, componentList, components));
        foreach (TransformBase child in children)
            Capture(child, transform, entries, visited);
    }

    private void RequireNew(XRObjectBase value, HashSet<XRObjectBase> visited)
    {
        _context.RequireAllocation(value);
        if (value.IsDestroyed || value.IsDestroyQueued || !visited.Add(value))
            throw new InvalidOperationException("An owned clone contains a destroyed or repeated structural object.");
    }

    internal void Validate()
    {
        foreach (OwnedHierarchyGraphEntry entry in _entries)
        {
            TransformBase transform = entry.Transform;
            if (transform.IsDestroyed || transform.IsDestroyQueued || transform.World is not null ||
                !ReferenceEquals(transform.OwnedRetirementParent, entry.Parent) ||
                !ReferenceEquals(transform.OwnedRetirementNode, entry.Node) ||
                !ReferenceEquals(transform.OwnedRetirementChildren, entry.ChildList) ||
                entry.ChildList.IsDestroyed || entry.ChildList.IsDestroyQueued ||
                entry.ChildList.Count != entry.Children.Length)
                throw new InvalidOperationException("The owned clone's transform links changed during construction.");
            for (int index = 0; index < entry.Children.Length; index++)
                if (!ReferenceEquals(entry.ChildList[index], entry.Children[index]))
                    throw new InvalidOperationException("The owned clone's child links changed during construction.");
            if (entry.Node is not { } node)
                continue;
            if (node.IsDestroyed || node.IsDestroyQueued || node.World is not null ||
                !ReferenceEquals(node.OwnedRetirementTransform, transform) ||
                !ReferenceEquals(node.OwnedRetirementComponents, entry.ComponentList) ||
                entry.ComponentList is null || entry.ComponentList.IsDestroyed || entry.ComponentList.IsDestroyQueued ||
                entry.ComponentList?.Count != entry.Components.Length)
                throw new InvalidOperationException("The owned clone's node links changed during construction.");
            for (int index = 0; index < entry.Components.Length; index++)
            {
                XRComponent component = entry.Components[index];
                if (!ReferenceEquals(entry.ComponentList![index], component) || component.IsDestroyed || component.IsDestroyQueued ||
                    component.World is not null || !ReferenceEquals(component.OwnedRetirementNode, node))
                    throw new InvalidOperationException("The owned clone's component links changed during construction.");
            }
        }
    }

    internal void RegenerateIdentities()
    {
        foreach (OwnedHierarchyGraphEntry entry in _entries)
        {
            if (entry.Node is { } node)
            {
                node.Generate();
                Validate();
            }
            entry.Transform.Generate();
            Validate();
            foreach (XRComponent component in entry.Components)
            {
                component.Generate();
                Validate();
            }
        }
    }

    internal void Initialize()
    {
        foreach (OwnedHierarchyGraphEntry entry in _entries)
        {
            entry.Transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
            Validate();
        }
        foreach (OwnedHierarchyGraphEntry entry in _entries)
        {
            entry.Transform.SaveBindState();
            Validate();
        }
        foreach (OwnedHierarchyGraphEntry entry in _entries)
            foreach (XRComponent component in entry.Components)
            {
                component.NotifyOwningSceneNodePostDeserialize(Validate);
                Validate();
            }
    }
}
