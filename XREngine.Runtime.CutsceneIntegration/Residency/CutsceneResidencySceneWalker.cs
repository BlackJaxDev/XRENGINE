using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>Visits the nodes of a scene hierarchy and collects their residency assets.</summary>
public static class CutsceneResidencySceneWalker
{
    /// <summary>
    /// Collects the assets of a node and its descendants. With <paramref name="shownOnly"/>, a node that is hidden,
    /// or that has a hidden ancestor, adds nothing, and neither do its descendants.
    /// </summary>
    /// <param name="root">The first node.</param>
    /// <param name="collector">The collector that finds the assets of each node.</param>
    /// <param name="destination">The list that receives the assets. The method does not clear it.</param>
    /// <param name="shownOnly">True to skip hidden nodes.</param>
    public static void Collect(SceneNode root, ICutsceneResidencyAssetCollector collector, List<CutsceneResidencyAsset> destination, bool shownOnly)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(destination);

        if (shownOnly && !IsShown(root))
            return;

        Visit(root, collector, destination, shownOnly);
    }

    /// <summary>
    /// True if a node and each of its ancestors are active. This does not need a world, so it also works for a node
    /// that is not in a world yet.
    /// </summary>
    /// <param name="node">The node.</param>
    public static bool IsShown(SceneNode node)
    {
        for (SceneNode? current = node; current is not null; current = current.Parent)
            if (!current.IsActiveSelf)
                return false;
        return true;
    }

    /// <summary>Gets the top ancestor of a node, or the node itself if it has no parent.</summary>
    /// <param name="node">The node.</param>
    public static SceneNode GetRoot(SceneNode node)
    {
        SceneNode current = node;
        while (current.Parent is { } parent)
            current = parent;
        return current;
    }

    private static void Visit(SceneNode node, ICutsceneResidencyAssetCollector collector, List<CutsceneResidencyAsset> destination, bool shownOnly)
    {
        if (shownOnly && !node.IsActiveSelf)
            return;

        collector.Collect(node, destination);

        var children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i]?.SceneNode is { } child)
                Visit(child, collector, destination, shownOnly);
    }
}
