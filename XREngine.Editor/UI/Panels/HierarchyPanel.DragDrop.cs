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
    private static bool HandleRootDrop(EditorDragDropUtility.DragPayload payload)
    {
        if (EditorDragDropUtility.TryGetAssetPath(payload, out var assetPath) && !string.IsNullOrWhiteSpace(assetPath))
            return TryGetActiveWorld() is { } world && TryHandleDroppedSpawnableAsset(world, null, assetPath);

        return false;
    }

    private static bool CanAcceptNodeDrop(SceneNode targetNode, EditorDragDropUtility.DragPayload payload)
    {
        if (!EditorDragDropUtility.TryGetSceneNode(payload, out var dragged) || dragged is null)
            return false;

        return CanAcceptNodeDrop(targetNode, dragged);
    }

    private static bool CanAcceptNodeDrop(SceneNode targetNode, SceneNode dragged)
    {
        if (targetNode == dragged)
            return false;

        return !IsDescendantOf(targetNode, dragged);
    }

    private static bool IsDescendantOf(SceneNode candidate, SceneNode potentialAncestor)
    {
        var parent = candidate.Transform.Parent;
        while (parent is not null)
        {
            if (parent.SceneNode == potentialAncestor)
                return true;
            parent = parent.Parent;
        }

        return false;
    }
}
