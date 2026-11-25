using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor.Mcp;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    // Context menu actions.

    private void DeleteNode(SceneNode? node)
    {
        if (node is null || TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
            !IsLiveHierarchyNode(node, world))
            return;

        if (node == _nodePendingRename)
            CancelRename();

        uint revision = _hierarchyRevision;
        QueueNativeHierarchyDelete(node, world, () => revision == _hierarchyRevision && IsCurrentHierarchyWorld(world));
    }

    private void CreateChildSceneNode(SceneNode? parent)
    {
        if (parent is null || TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
            !IsLiveHierarchyNode(parent, world))
            return;

        uint revision = _hierarchyRevision;
        QueueNativeHierarchyCreateChild(parent, world, child =>
        {
            BeginHierarchyViewEdit();
            _collapsedNodes.Remove(parent.ID);
            BeginRename(child);
        }, () => revision == _hierarchyRevision && IsCurrentHierarchyWorld(world));
    }

    // Dirty tracking helpers.

    private static void MarkSceneHierarchyDirty(SceneNode? node, RuntimeWorld world)
    {
        if (node is null || !HierarchySceneOwnership.CanAuthor(node, world))
            return;

        SceneNode root = HierarchySceneOwnership.GetRoot(node) ?? node;
        XRScene[] scenes = HierarchySceneOwnership.CaptureRootScenes(root, world);
        foreach (XRScene scene in scenes)
            scene.MarkDirty();
        if (scenes.Length == 0)
            world.TargetWorld?.MarkDirty();
    }
}
