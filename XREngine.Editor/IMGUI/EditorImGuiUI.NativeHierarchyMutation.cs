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
            ReparentHierarchyNode(dragged, target.Transform, world, FindSceneForNode(dragged, world),
                validateCapturedWorld: true);
        });

    internal static bool CanApplyNativeHierarchyEdit(SceneNode? node, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => !world.IsDisposing && ownerIsCurrent?.Invoke() != false &&
            node is { IsDestroyed: false, IsDestroyQueued: false } &&
            ReferenceEquals(node.World, world);

    private static void RequireNativeHierarchyState(SceneNode node, RuntimeWorld world,
        TransformBase? expectedParent, bool allowDetached = false)
    {
        if (world.IsDisposing || node.IsDestroyed || node.IsDestroyQueued ||
            !ReferenceEquals(node.Transform.Parent, expectedParent) ||
            (allowDetached && expectedParent is null
                ? node.World is not null && !ReferenceEquals(node.World, world)
                : !ReferenceEquals(node.World, world)))
            throw new InvalidOperationException("The hierarchy target changed world or parent before Undo/Redo.");

        if (!allowDetached || expectedParent is not null)
            return;
        if (world.RootNodes.Contains(node))
            throw new InvalidOperationException("The detached hierarchy target became a root before Undo/Redo.");
        if (HierarchySceneOwnership.FindOwningScene(node, world) is not null)
            throw new InvalidOperationException("The detached hierarchy target entered a scene before Undo/Redo.");
    }

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
            XRScene? scene = FindSceneForNode(parent, world);
            SceneNode child = new(parent);
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("Create Child Node");
            Undo.TrackSceneNode(child);
            Undo.RecordStructuralChange("Create Child Node",
                undoAction: () =>
                {
                    RequireNativeHierarchyState(parent, world, parent.Transform.Parent);
                    RequireNativeHierarchyState(child, world, parent.Transform);
                    parent.Transform.RemoveChild(child.Transform, EParentAssignmentMode.Immediate);
                    child.IsActiveSelf = false;
                    MarkSceneHierarchyDirty(parent, scene, world);
                },
                redoAction: () =>
                {
                    RequireNativeHierarchyState(parent, world, parent.Transform.Parent);
                    RequireNativeHierarchyState(child, world, null, allowDetached: true);
                    child.Transform.SetParent(parent.Transform, false, EParentAssignmentMode.Immediate);
                    child.IsActiveSelf = true;
                    Undo.TrackSceneNode(child);
                    MarkSceneHierarchyDirty(child, scene, world);
                });
            MarkSceneHierarchyDirty(child, scene, world);
            onCreated?.Invoke(child);
        });

    internal static void QueueNativeHierarchyDelete(SceneNode node, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => EnqueueSceneEdit(() =>
        {
            if (!CanApplyNativeHierarchyEdit(node, world, ownerIsCurrent))
                return;
            XRScene? scene = FindSceneForNode(node, world);
            TransformBase? parent = node.Transform.Parent;
            if (parent?.SceneNode is { } initialParentNode &&
                !CanApplyNativeHierarchyEdit(initialParentNode, world))
                return;
            bool active = node.IsActiveSelf;
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("Delete Node");
            Undo.TrackSceneNode(node);
            if (parent is not null)
                parent.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
            else
            {
                scene?.RootNodes.Remove(node);
                world.RootNodes.Remove(node);
            }
            node.IsActiveSelf = false;
            Selection.SceneNodes = [.. Selection.SceneNodes.Where(selected =>
                !ReferenceEquals(selected, node) && !IsHierarchyDescendantOf(selected, node))];
            Undo.RecordStructuralChange("Delete Node",
                undoAction: () =>
                {
                    RequireNativeHierarchyState(node, world, null, allowDetached: true);
                    if (parent?.SceneNode is { } restoredParentNode)
                        RequireNativeHierarchyState(restoredParentNode, world, parent.Parent);
                    if (parent is not null)
                        node.Transform.SetParent(parent, false, EParentAssignmentMode.Immediate);
                    else
                    {
                        if (scene is not null && !scene.RootNodes.Contains(node))
                            scene.RootNodes.Add(node);
                        if ((scene is null || scene.IsVisible) && !world.RootNodes.Contains(node))
                            world.RootNodes.Add(node);
                    }
                    node.IsActiveSelf = active;
                    Undo.TrackSceneNode(node);
                    MarkSceneHierarchyDirty(node, scene, world);
                },
                redoAction: () =>
                {
                    RequireNativeHierarchyState(node, world, parent);
                    if (parent is not null)
                        parent.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
                    else
                    {
                        scene?.RootNodes.Remove(node);
                        world.RootNodes.Remove(node);
                    }
                    node.IsActiveSelf = false;
                    MarkSceneHierarchyDirty(node, scene, world);
                });
            MarkSceneHierarchyDirty(node, scene, world);
        });

    internal static void QueueNativeHierarchyMoveToRoot(SceneNode dragged, RuntimeWorld world,
        Func<bool>? ownerIsCurrent = null)
        => EnqueueSceneEdit(() =>
        {
            if (!CanApplyNativeHierarchyEdit(dragged, world, ownerIsCurrent))
                return;
            TransformBase? oldParent = dragged.Transform.Parent;
            if (oldParent is null)
                return;
            if (oldParent.SceneNode is { } initialParentNode &&
                !CanApplyNativeHierarchyEdit(initialParentNode, world))
                return;
            XRScene? scene = FindSceneForNode(dragged, world);
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("Move Node To Root");
            dragged.Transform.SetParent(null, true, EParentAssignmentMode.Immediate);
            if (scene is not null && !scene.RootNodes.Contains(dragged))
                scene.RootNodes.Add(dragged);
            if (scene is null || scene.IsVisible)
            {
                if (!world.RootNodes.Contains(dragged))
                    world.RootNodes.Add(dragged);
            }
            Undo.RecordStructuralChange("Move Node To Root",
                undoAction: () =>
                {
                    RequireNativeHierarchyState(dragged, world, null);
                    if (oldParent.SceneNode is { } restoredParentNode)
                        RequireNativeHierarchyState(restoredParentNode, world, oldParent.Parent);
                    scene?.RootNodes.Remove(dragged);
                    world.RootNodes.Remove(dragged);
                    dragged.Transform.SetParent(oldParent, true, EParentAssignmentMode.Immediate);
                    MarkSceneHierarchyDirty(dragged, scene, world);
                },
                redoAction: () =>
                {
                    RequireNativeHierarchyState(dragged, world, oldParent);
                    dragged.Transform.SetParent(null, true, EParentAssignmentMode.Immediate);
                    if (scene is not null && !scene.RootNodes.Contains(dragged))
                        scene.RootNodes.Add(dragged);
                    if ((scene is null || scene.IsVisible) && !world.RootNodes.Contains(dragged))
                        world.RootNodes.Add(dragged);
                    MarkSceneHierarchyDirty(dragged, scene, world);
                });
            MarkSceneHierarchyDirty(dragged, scene, world);
        });
}
