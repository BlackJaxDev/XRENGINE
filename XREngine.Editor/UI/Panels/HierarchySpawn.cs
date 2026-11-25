using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Places a detached, newly spawned subtree through the canonical structural hierarchy edit.
/// Apply, Undo, and Redo then share the scene membership, routing, and dirty contracts of
/// other hierarchy edits.
/// </summary>
internal static class HierarchySpawn
{
    /// <summary>
    /// Attaches <paramref name="node"/> under <paramref name="parent"/>, or as a root of
    /// <paramref name="scene"/> when the parent is null. The node must be detached and must not
    /// be owned by another edit. Returns false and logs the failure when the edit is refused.
    /// </summary>
    /// <param name="preserveWorld">Keeps the node's current world matrix through attachment.</param>
    internal static bool Place(string description, RuntimeWorld world, SceneNode? parent, XRScene? scene,
        SceneNode node, bool preserveWorld, Func<bool>? ownerIsCurrent = null)
    {
        if (node.Transform.Parent is not null || world.RootNodes.Contains(node))
            throw new InvalidOperationException("A spawned node must be detached before placement.");
        bool placed = false;
        try
        {
            HierarchyMutation.Execute(description,
                () => new(HierarchyPlacement.Capture(node, world),
                    parent is not null
                        ? HierarchyPlacement.Attached(node, world, parent.Transform)
                        : HierarchyPlacement.SceneRoot(node, world, scene),
                    preserveWorld, activeAfter: true),
                () =>
                {
                    placed = true;
                    Selection.SceneNode = node;
                },
                ownerIsCurrent,
                releaseRestoredCreation: () => RetireDetached(node, world));
        }
        catch (Exception failure)
        {
            Debug.LogException(failure, $"{description} failed.");
            return false;
        }
        return placed;
    }

    /// <summary>Destroys a spawned subtree that a refused edit left detached.</summary>
    private static void RetireDetached(SceneNode node, RuntimeWorld world)
    {
        try
        {
            if (node.IsDestroyed || node.Transform.Parent is not null || world.RootNodes.Contains(node) ||
                HierarchySceneOwnership.CaptureRootScenes(node, world).Length != 0 ||
                Selection.SceneNodes.Contains(node))
                return;
            node.Destroy(true);
        }
        catch (Exception failure)
        {
            Debug.LogException(failure, "A refused spawn kept its detached subtree.");
        }
    }
}
