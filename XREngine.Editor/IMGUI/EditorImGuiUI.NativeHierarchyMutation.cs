using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>Route native tree moves through the editor's structural undo path.</summary>
    internal static void QueueNativeHierarchyReparent(SceneNode dragged, SceneNode target, RuntimeWorld world)
        => QueueHierarchyReparent(dragged, target, world, FindSceneForNode(dragged, world));

    internal static void ToggleNativeSceneVisibility(XRScene scene, RuntimeWorld world, bool visible)
        => ToggleSceneVisibility(scene, world, visible);

    internal static void UnloadNativeScene(XRScene scene, RuntimeWorld world)
        => UnloadSceneFromWorld(scene, world);

    internal static void QueueNativeHierarchyCreateChild(SceneNode parent, RuntimeWorld world, Action<SceneNode>? onCreated)
        => EnqueueSceneEdit(() =>
        {
            XRScene? scene = FindSceneForNode(parent, world);
            SceneNode child = new(parent);
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("Create Child Node");
            Undo.TrackSceneNode(child);
            Undo.RecordStructuralChange("Create Child Node",
                undoAction: () =>
                {
                    parent.Transform.RemoveChild(child.Transform, EParentAssignmentMode.Immediate);
                    child.IsActiveSelf = false;
                    MarkSceneHierarchyDirty(parent, scene, world);
                },
                redoAction: () =>
                {
                    child.Transform.SetParent(parent.Transform, false, EParentAssignmentMode.Immediate);
                    child.IsActiveSelf = true;
                    Undo.TrackSceneNode(child);
                    MarkSceneHierarchyDirty(child, scene, world);
                });
            MarkSceneHierarchyDirty(child, scene, world);
            onCreated?.Invoke(child);
        });

    internal static void QueueNativeHierarchyDelete(SceneNode node, RuntimeWorld world)
        => EnqueueSceneEdit(() =>
        {
            XRScene? scene = FindSceneForNode(node, world);
            TransformBase? parent = node.Transform.Parent;
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

    internal static void QueueNativeHierarchyMoveToRoot(SceneNode dragged, RuntimeWorld world)
        => EnqueueSceneEdit(() =>
        {
            TransformBase? oldParent = dragged.Transform.Parent;
            if (oldParent is null)
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
                    scene?.RootNodes.Remove(dragged);
                    world.RootNodes.Remove(dragged);
                    dragged.Transform.SetParent(oldParent, true, EParentAssignmentMode.Immediate);
                },
                redoAction: () =>
                {
                    dragged.Transform.SetParent(null, true, EParentAssignmentMode.Immediate);
                    if (scene is not null && !scene.RootNodes.Contains(dragged))
                        scene.RootNodes.Add(dragged);
                    if ((scene is null || scene.IsVisible) && !world.RootNodes.Contains(dragged))
                        world.RootNodes.Add(dragged);
                });
            MarkSceneHierarchyDirty(dragged, scene, world);
        });
}
