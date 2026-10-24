using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Captures raw source links without invoking a transform override.</summary>
internal sealed class HierarchyDuplicateSourceEntry
{
    internal HierarchyDuplicateSourceEntry(TransformBase transform)
    {
        Transform = transform;
        Parent = transform.OwnedRetirementParent;
        Node = transform.OwnedRetirementNode;
        World = transform.World;
        Children = transform.OwnedRetirementChildren ?? throw new InvalidOperationException("The source has no child list.");
        ChildItems = [.. Children];
        Components = Node?.OwnedRetirementComponents;
        ComponentItems = Components is null ? [] : [.. Components];
    }

    internal TransformBase Transform { get; }
    internal TransformBase? Parent { get; }
    internal SceneNode? Node { get; }
    internal IRuntimeWorldContext? World { get; }
    internal EventList<TransformBase> Children { get; }
    internal TransformBase[] ChildItems { get; }
    internal EventList<XRComponent>? Components { get; }
    internal XRComponent[] ComponentItems { get; }

    internal void RequireCurrent()
        => RequireLinks(Parent, World);

    internal void RequireLinks(TransformBase? parent, IRuntimeWorldContext? world)
    {
        if (!ReferenceEquals(Transform.OwnedRetirementParent, parent) ||
            !ReferenceEquals(Transform.OwnedRetirementNode, Node) ||
            !ReferenceEquals(Transform.World, world) || Children.IsDestroyed || Children.IsDestroyQueued ||
            !ReferenceEquals(Transform.OwnedRetirementChildren, Children) || Children.Count != ChildItems.Length)
            throw new InvalidOperationException("The duplicate source hierarchy changed.");
        HierarchyListMembership[] transformLinks = Transform.CaptureHierarchyListMemberships();
        if (parent is null ? transformLinks.Length != 0 : transformLinks.Length != 1 ||
            !transformLinks[0].IsCurrent || transformLinks[0].Count != 1 ||
            !ReferenceEquals(transformLinks[0].Owner, parent) ||
            !ReferenceEquals(transformLinks[0].Collection, parent.OwnedRetirementChildren))
            throw new InvalidOperationException("The duplicate source has a nonreciprocal child link.");
        for (int index = 0; index < ChildItems.Length; index++)
            if (!ReferenceEquals(Children[index], ChildItems[index]))
                throw new InvalidOperationException("The duplicate source child order changed.");
        if (Node is null) return;
        if (!ReferenceEquals(Node.OwnedRetirementTransform, Transform) || !ReferenceEquals(Node.World, world) ||
            !ReferenceEquals(Node.OwnedRetirementComponents, Components) ||
            Components is null || Components.IsDestroyed || Components.IsDestroyQueued ||
            Components.Count != ComponentItems.Length || Node.CaptureHierarchyListMemberships().Length != 0)
            throw new InvalidOperationException("The duplicate source component owner changed.");
        for (int index = 0; index < ComponentItems.Length; index++)
        {
            if (!ReferenceEquals(Components![index], ComponentItems[index]) ||
                !ReferenceEquals(ComponentItems[index].OwnedRetirementNode, Node) ||
                !ReferenceEquals(ComponentItems[index].World, world))
                throw new InvalidOperationException("The duplicate source component order changed.");
            HierarchyListMembership[] links = ComponentItems[index].CaptureHierarchyListMemberships();
            if (links.Length != 1 || !links[0].IsCurrent || links[0].Count != 1 ||
                !ReferenceEquals(links[0].Owner, Node) || !ReferenceEquals(links[0].Collection, Components))
                throw new InvalidOperationException("The duplicate source has a nonreciprocal component link.");
        }
    }
}
