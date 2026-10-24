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

        QueueNativeHierarchyDelete(node, world, () => IsCurrentHierarchyWorld(world));
    }

    private void CreateChildSceneNode(SceneNode? parent)
    {
        if (parent is null || TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
            !IsLiveHierarchyNode(parent, world))
            return;

        QueueNativeHierarchyCreateChild(parent, world, child =>
        {
            _collapsedNodes.Remove(parent.ID);
            BeginRename(child);
        }, () => IsCurrentHierarchyWorld(world));
    }

    // Dirty tracking helpers.

    private static void MarkSceneHierarchyDirty(SceneNode? node, RuntimeWorld world)
    {
        if (node is null || !ReferenceEquals(node.World, world))
            return;

        var scene = FindSceneForNode(node, world);
        if (scene is not null)
            scene.MarkDirty();
        else
            world?.TargetWorld?.MarkDirty();
    }
}
