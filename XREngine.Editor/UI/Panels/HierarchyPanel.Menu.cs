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
    private void SubscribeRightClick()
    {
        var input = GetCanvasInput();
        if (input is not null)
            input.RightClick += OnRightClickInteractable;
    }

    private void UnsubscribeRightClick()
    {
        var input = GetCanvasInput();
        if (input is not null)
            input.RightClick -= OnRightClickInteractable;
    }

    private void OnRightClickInteractable(UIInteractableComponent target)
    {
        // Only handle right-clicks on our node buttons
        if (target.UserData is not SceneNode node)
            return;

        // Verify the cursor is actually within this panel's bounds.
        // The canvas-wide RightClick event can fire with a stale TopMostInteractable
        // from a previous frame if the cursor moved off the panel between SwapBuffers
        // and the input event. Reject those to avoid opening the context menu when
        // the user is right-clicking in the 3D viewport.
        var input = GetCanvasInput();
        if (input is not null)
        {
            var cursor = input.CursorPositionWorld2D;
            if (!BoundableTransform.Contains(cursor))
                return;
        }

        // Also verify the target interactable actually belongs to this panel's subtree.
        // This guards against other UI elements that happen to store SceneNode UserData.
        if (target.SceneNode is null || !IsDescendantOf(target.SceneNode, SceneNode))
            return;

        _contextMenuTargetNode = node;

        // Ensure context menu component exists
        if (_contextMenu is null)
        {
            var menuNode = SceneNode.NewChild<UIContextMenuComponent>(out var menu);
            menuNode.Name = "HierarchyContextMenu";
            _contextMenu = menu;
        }

        Vector2 pos = input is not null
            ? BoundableTransform.CanvasToLocal(input.CursorPositionWorld2D)
            : Vector2.Zero;

        _contextMenu.Show(pos,
            new ContextMenuItem("Rename", () => BeginRename(_contextMenuTargetNode)),
            new ContextMenuItem("Add Child", () => CreateChildSceneNode(_contextMenuTargetNode)),
            new ContextMenuItem("Focus Camera", () => TryFocusCameraOnNode(_contextMenuTargetNode)),
            ContextMenuItem.Separator(),
            new ContextMenuItem("Delete", () => DeleteNode(_contextMenuTargetNode)));
    }
}
