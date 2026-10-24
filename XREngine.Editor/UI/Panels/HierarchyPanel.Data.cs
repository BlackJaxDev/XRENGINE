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
    // Scene management helpers.

    private static List<SceneNode> CollectUnassignedRoots(RuntimeWorld world, IReadOnlyList<XRScene> scenes)
    {
        var assigned = new HashSet<SceneNode>();
        foreach (var scene in scenes)
        {
            foreach (var root in scene.RootNodes)
            {
                if (root is not null)
                    assigned.Add(root);
            }
        }

        // Also mark nodes from editor-only scenes as assigned
        var targetWorld = world.TargetWorld;
        if (targetWorld is not null)
        {
            foreach (var scene in targetWorld.Scenes.Where(s => s.IsEditorOnly))
            {
                foreach (var root in scene.RootNodes)
                {
                    if (root is not null)
                        assigned.Add(root);
                }
            }
        }

        var unassigned = new List<SceneNode>();
        foreach (var root in world.RootNodes)
        {
            if (root is null)
                continue;
            if (!assigned.Contains(root) && !IsInEditorScene(world, root))
                unassigned.Add(root);
        }

        return unassigned;
    }

    private static bool HasSceneNodeChildren(SceneNode node)
    {
        if (node.Transform?.Children is not { } children)
            return false;
        foreach (var child in children)
        {
            if (child.SceneNode is { } sn && sn != node)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gets only the direct SceneNode children of a node, filtering out
    /// internal transform children that don't represent scene nodes.
    /// </summary>
    private static IEnumerable<SceneNode> GetSceneNodeChildren(SceneNode node)
    {
        if (node.Transform?.Children is not { } children)
            yield break;
        foreach (var child in children)
        {
            if (child.SceneNode is { } sn && sn != node)
                yield return sn;
        }
    }

    private static XRScene? FindSceneForNode(SceneNode? node, RuntimeWorld? world)
        => HierarchySceneOwnership.FindOwningScene(node, world);

    private RuntimeWorld? TryGetActiveWorld()
    {
        if (SceneNode?.World is not RuntimeWorld world || world.IsDisposing)
            return null;
        // A cached panel can briefly remain attached to its old scene after a
        // window rebind. Never redirect its pending edits into the new window world.
        return Window is null || ReferenceEquals(Window.TargetWorldInstance?.WorldContext, world)
            ? world : null;
    }

    private bool IsCurrentHierarchyWorld(RuntimeWorld world)
        => CanUseHierarchy && IsActiveInHierarchy &&
            ReferenceEquals(TryGetActiveWorld(), world);

    private static bool IsLiveHierarchyNode(SceneNode? node, RuntimeWorld world)
        => node is { IsDestroyed: false, IsDestroyQueued: false } &&
            ReferenceEquals(node.World, world);

    private static bool IsInEditorScene(RuntimeWorld world, SceneNode node)
        => EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration)
            && integration?.IsInEditorScene(node) == true;

    private static SceneNode? GetHierarchyRoot(SceneNode node)
    {
        TransformBase? transform = node.Transform;
        while (transform?.Parent is TransformBase parent)
            transform = parent;
        return transform?.SceneNode;
    }
}
