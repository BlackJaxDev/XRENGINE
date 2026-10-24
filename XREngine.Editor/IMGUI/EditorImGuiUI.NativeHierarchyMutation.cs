using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>Route native tree moves through the editor's structural undo path.</summary>
    internal static void QueueNativeHierarchyReparent(SceneNode dragged, SceneNode target, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => EnqueueSceneEdit(() =>
        {
            if (!CanApplyNativeHierarchyEdit(dragged, world, ownerIsCurrent) ||
                !CanApplyNativeHierarchyEdit(target, world, ownerIsCurrent) ||
                ReferenceEquals(dragged, target) || IsHierarchyDescendantOf(target, dragged))
                return;
            HierarchyMutation.Execute($"Reparent {dragged.Name ?? SceneNode.DefaultName}",
                () => new(HierarchyPlacement.Capture(dragged, world),
                    HierarchyPlacement.Attached(dragged, world, target.Transform), preserveWorld: true),
                ownerIsCurrent: ownerIsCurrent);
        });

    internal static bool CanApplyNativeHierarchyEdit(SceneNode? node, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => ownerIsCurrent?.Invoke() != false && HierarchySceneOwnership.CanAuthor(node, world);

    internal static void ToggleNativeSceneVisibility(XRScene scene, RuntimeWorld world, bool visible)
        => ToggleSceneVisibility(scene, world, visible);

    internal static void UnloadNativeScene(XRScene scene, RuntimeWorld world)
        => UnloadSceneFromWorld(scene, world);

    internal static void QueueNativeHierarchyCreateChild(SceneNode parent, RuntimeWorld world, Action<SceneNode>? onCreated,
        Func<bool>? ownerIsCurrent = null)
        => EnqueueSceneEdit(() =>
        {
            if (!CanApplyNativeHierarchyEdit(parent, world, ownerIsCurrent))
                return;
            HierarchyNodeConstruction? construction = null;
            SceneNode? child = null;
            HierarchyMutation.Execute("Create Child Node",
                () =>
                {
                    construction = new(world);
                    child = construction.Node;
                    return new(HierarchyPlacement.Capture(child, world),
                        HierarchyPlacement.Attached(child, world, parent.Transform), preserveWorld: false, activeAfter: true);
                }, ownerIsCurrent: ownerIsCurrent, releaseRestoredCreation: () => construction?.RetireRestoredCreation());
            if (CanApplyNativeHierarchyEdit(child, world, ownerIsCurrent))
                onCreated?.Invoke(child!);
        });

    internal static void QueueNativeHierarchyDelete(SceneNode node, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => EnqueueSceneEdit(() =>
        {
            if (!CanApplyNativeHierarchyEdit(node, world, ownerIsCurrent))
                return;
            HierarchyMutation.Execute("Delete Node",
                () => new(HierarchyPlacement.Capture(node, world), HierarchyPlacement.Detached(node, world),
                    preserveWorld: false, activeAfter: node.CanDeactivate ? false : node.IsActiveSelf),
                () => Selection.SceneNodes = [.. Selection.SceneNodes.Where(selected =>
                    !ReferenceEquals(selected, node) && !IsHierarchyDescendantOf(selected, node))], ownerIsCurrent);
        });

    internal static void QueueNativeHierarchyMoveToRoot(SceneNode dragged, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => EnqueueSceneEdit(() =>
        {
            if (!CanApplyNativeHierarchyEdit(dragged, world, ownerIsCurrent) || dragged.Transform.Parent is null ||
                HierarchySceneOwnership.GetRoot(dragged) is not { } root)
                return;
            HierarchyMutation.Execute("Move Node To Root", () =>
            {
                HierarchyPlacement sourceRoot = HierarchyPlacement.Capture(root, world);
                return new(HierarchyPlacement.Capture(dragged, world),
                    HierarchyPlacement.RootLike(dragged, sourceRoot), preserveWorld: true);
            }, ownerIsCurrent: ownerIsCurrent);
        });
}
