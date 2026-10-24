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
    private sealed class HierarchyMenuActions(HierarchyPanel owner)
    {
        public HierarchyPanel? Owner = owner;
        public void Rename() { if (Owner is { } current) current.BeginRename(current._contextMenuTargetNode); }
        public void AddChild() { if (Owner is { } current) current.CreateChildSceneNode(current._contextMenuTargetNode); }
        public void Focus() { if (Owner is { } current) current.TryFocusCameraOnNode(current._contextMenuTargetNode); }
        public void Delete() { if (Owner is { } current) current.DeleteNode(current._contextMenuTargetNode); }
    }

    private HierarchyMenuActions? _contextMenuActions;

    private void SubscribeRightClick()
    {
        UnsubscribeRightClick();
        if (!CanUseHierarchy)
            return;
        var input = GetCanvasInput();
        if (input is not null)
        {
            uint revision = _hierarchyRevision;
            Action<UIInteractableComponent> callback = target =>
            {
                if (revision == _hierarchyRevision && ReferenceEquals(_rightClickInput, input) &&
                    ReferenceEquals(GetCanvasInput(), input))
                    OnRightClickInteractable(target);
            };
            _rightClickInput = input;
            _rightClickCallback = callback;
            input.RightClick += callback;
            if (!CanUseHierarchy || revision != _hierarchyRevision)
            {
                if (ReferenceEquals(_rightClickCallback, callback))
                {
                    _rightClickInput = null;
                    _rightClickCallback = null;
                }
                input.RightClick -= callback;
            }
        }
    }

    private void UnsubscribeRightClick()
    {
        var input = _rightClickInput;
        var callback = _rightClickCallback;
        _rightClickInput = null;
        _rightClickCallback = null;
        if (input is not null && callback is not null)
            input.RightClick -= callback;
    }

    private void ReleaseHierarchyMenu()
    {
        UIContextMenuComponent? menu = _contextMenu;
        _contextMenu = null;
        if (_contextMenuActions is { } actions)
            actions.Owner = null;
        _contextMenuActions = null;
        _contextMenuTargetNode = null;
        if (menu is null)
            return;
        UIBoundableTransform? transform = menu.Transform as UIBoundableTransform;
        Exception? failure = null;
        // Component destruction keeps the child transforms in place during scene transfer.
        AttemptHierarchyRelease(() => menu.Destroy(true), ref failure);
        if (transform is not null)
            AttemptHierarchyRelease(() => transform.Visibility = EVisibility.Collapsed, ref failure);
        ThrowHierarchyFailure(failure);
    }

    private void OnRightClickInteractable(UIInteractableComponent target)
    {
        // Only handle right-clicks on our node buttons
        if (!CanUseHierarchy || target.UserData is not SceneNode node)
            return;

        // Verify the cursor is actually within this panel's bounds.
        // The canvas-wide RightClick event can fire with a stale TopMostInteractable
        // from a previous frame if the cursor moved off the panel between SwapBuffers
        // and the input event. Reject those to avoid opening the context menu when
        // the user is right-clicking in the 3D viewport.
        var input = _rightClickInput;
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
            uint revision = _hierarchyRevision;
            var menuNode = SceneNode.NewChild<UIContextMenuComponent>(out var menu);
            if (!CanUseHierarchy || revision != _hierarchyRevision)
            {
                menu.Destroy(true);
                return;
            }
            _contextMenu = menu;
            menuNode.Name = "HierarchyContextMenu";
            if (!CanUseHierarchy || revision != _hierarchyRevision)
                return;
        }
        if (_contextMenuActions is { } previousActions)
            previousActions.Owner = null;
        HierarchyMenuActions actions = new(this);
        _contextMenuActions = actions;

        Vector2 pos = input is not null
            ? BoundableTransform.CanvasToLocal(input.CursorPositionWorld2D)
            : Vector2.Zero;

        _contextMenu.Show(pos,
            new ContextMenuItem("Rename", actions.Rename),
            new ContextMenuItem("Add Child", actions.AddChild),
            new ContextMenuItem("Focus Camera", actions.Focus),
            ContextMenuItem.Separator(),
            new ContextMenuItem("Delete", actions.Delete));
    }
}
