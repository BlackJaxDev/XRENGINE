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
    private HierarchyMenuSession? _contextMenuSession;
    private Action? _contextMenuDismissed;
    private SceneNode? _contextMenuNode;
    private ulong _contextMenuRequest;
    private bool _showingHierarchyMenu;
    private bool _creatingHierarchyMenu;

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
        SceneNode? menuNode = _contextMenuNode;
        _contextMenuNode = null;
        _contextMenuRequest++;
        _contextMenuSession?.Release();
        _contextMenuSession = null;
        Action? dismissed = _contextMenuDismissed;
        _contextMenuDismissed = null;
        if (menu is not null && dismissed is not null)
            menu.Dismissed -= dismissed;
        Exception? failure = null;
        RetireHierarchyMenu(menu, menuNode, ref failure);
        ThrowHierarchyFailure(failure);
    }

    private static void RetireHierarchyMenu(UIContextMenuComponent? menu, SceneNode? node, ref Exception? failure)
    {
        if (menu is not null)
            AttemptHierarchyRelease(() => menu.Destroy(true), ref failure);
        if (node is null)
            return;
        if (node.Transform is UIBoundableTransform transform)
            AttemptHierarchyRelease(() => transform.Visibility = EVisibility.Collapsed, ref failure);
        // Retire input now. Remove the owned canvas child after scene transfer callbacks finish.
        AttemptHierarchyRelease(() => node.Destroy(false), ref failure);
    }

    private void OnRightClickInteractable(UIInteractableComponent target)
    {
        ulong request = ++_contextMenuRequest;
        if (!TryResolveHierarchyMenuRow(target, out HierarchyRow? row) || row is null ||
            TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
            _treeListTransform is not { } list || _virtualTree is not { } provider ||
            !list.TryGetVirtualRowLease(row.Transform, out UIVirtualRowLease lease) ||
            _rightClickInput is not { } input ||
            !input.TryGetPresentedPointerPosition(target, out Vector2 position) ||
            BoundableTransform.GetCanvasTransform() is not { } canvas)
        {
            DismissHierarchyMenu();
            return;
        }
        if (row.Node is { } targetNode && !CanApplyNativeHierarchyEdit(targetNode, world))
        {
            DismissHierarchyMenu();
            return;
        }
        if (row.Node is null && (row.Scene is null || world.TargetWorld?.Scenes.Contains(row.Scene) != true))
        {
            DismissHierarchyMenu();
            return;
        }
        uint revision = _hierarchyRevision;
        HierarchyCommandContext context = new(world, row.Node is { } node ? [node] : [],
            () => revision == _hierarchyRevision && IsCurrentHierarchyWorld(world) &&
                ReferenceEquals(BoundableTransform.GetCanvasTransform(), canvas) &&
                ReferenceEquals(GetCanvasInput(), input) && ReferenceEquals(_rightClickInput, input), row.Scene);
        HierarchyMenuSession session = new()
        {
            Owner = this, Context = context, Revision = revision, Node = row.Node, Scene = row.Scene,
            List = list, Provider = provider, Row = row.Transform, Lease = lease,
            ProviderIncarnation = list.VirtualProviderIncarnation, Input = input, Request = request,
            Position = position, Canvas = canvas, AwaitingShow = true
        };
        DismissHierarchyMenu();
        if (request != _contextMenuRequest || !context.CanStart())
        {
            session.Release();
            return;
        }
        _contextMenuSession = session;
        if (!IsHierarchyMenuSessionCurrent(session))
        {
            DismissHierarchyMenu();
            return;
        }
        // Finish the right-click dispatch before an old menu's dismissal callback can run.
        ScheduleHierarchyWork();
    }

    private void DismissHierarchyMenu()
    {
        HierarchyMenuSession? session = _contextMenuSession;
        _contextMenuSession = null;
        session?.Release();
        if (!_showingHierarchyMenu)
            _contextMenu?.Hide();
    }

    private void CompleteHierarchyMenuRequest()
    {
        if (_showingHierarchyMenu || _creatingHierarchyMenu)
        {
            ScheduleHierarchyWork();
            return;
        }
        if (_contextMenuSession is not { AwaitingShow: true } session)
            return;
        if (!IsHierarchyMenuSessionCurrent(session))
        {
            DismissHierarchyMenu();
            return;
        }
        UIContextMenuComponent? menu = EnsureHierarchyContextMenu(session);
        if (menu is null || !IsHierarchyMenuSessionCurrent(session))
        {
            if (ReferenceEquals(_contextMenuSession, session))
                DismissHierarchyMenu();
            return;
        }
        Exception? failure = null;
        _showingHierarchyMenu = true;
        try
        {
            if (session.Node is not null)
                menu.Show(session.Position,
                    new ContextMenuItem("Rename", session.Rename),
                    new ContextMenuItem("Add Child", session.AddChild),
                    new ContextMenuItem("Focus Camera", session.Focus),
                    ContextMenuItem.Separator(),
                    new ContextMenuItem("Delete", session.Delete));
            else if (session.Scene is { } scene)
                menu.Show(session.Position,
                    new ContextMenuItem(scene.Name ?? "Untitled Scene", null, enabled: false),
                    ContextMenuItem.Separator(),
                    new ContextMenuItem(scene.IsVisible ? "Hide Scene" : "Show Scene", session.ToggleScene),
                    new ContextMenuItem("Unload Scene", session.UnloadScene));
        }
        catch (Exception error) { failure = error; }
        finally { _showingHierarchyMenu = false; }
        if (ReferenceEquals(_contextMenuSession, session))
        {
            session.AwaitingShow = false;
            if (failure is not null || !menu.IsOpen || !IsHierarchyMenuSessionCurrent(session))
                AttemptHierarchyRelease(DismissHierarchyMenu, ref failure);
        }
        else if (ReferenceEquals(_contextMenu, menu))
            AttemptHierarchyRelease(menu.Hide, ref failure);
        ThrowHierarchyFailure(failure);
    }

    private bool TryResolveHierarchyMenuRow(UIInteractableComponent target, out HierarchyRow? row)
    {
        row = null;
        if (!CanUseHierarchy || target.IsDestroyed || target.IsDestroyQueued ||
            _treeListTransform is not { } list || _virtualTree is not { } provider ||
            !ReferenceEquals(list.VirtualProvider, provider))
            return false;
        for (TransformBase? current = target.Transform; current is not null && !ReferenceEquals(current, Transform); current = current.Parent)
        {
            if (current is not UIBoundableTransform candidate || !_hierarchyRows.TryGetValue(candidate, out HierarchyRow? found))
                continue;
            if (!IsHierarchyRowCurrent(found) || found.Transform.Visibility != EVisibility.Visible ||
                !list.TryGetVirtualRowLease(found.Transform, out UIVirtualRowLease lease) || lease.ItemId != found.ItemId ||
                !provider.TryGetNode(found.ItemId, out _) ||
                !ReferenceEquals(found.Node, _hierarchyNodes.GetValueOrDefault(found.ItemId)) ||
                !ReferenceEquals(found.Scene, _hierarchyScenes.GetValueOrDefault(found.ItemId)))
                return false;
            row = found;
            return true;
        }
        return false;
    }

    private UIContextMenuComponent? EnsureHierarchyContextMenu(HierarchyMenuSession session)
    {
        if (!IsHierarchyMenuSessionCurrent(session) || session.Canvas is not { } canvas)
            return null;
        if (_contextMenu is not null)
            return ReferenceEquals(_contextMenuNode?.Transform.Parent, canvas) ? _contextMenu : null;
        SceneNode? menuNode = null;
        UIContextMenuComponent? menu = null;
        Action? dismissed = null;
        bool completed = false;
        Exception? failure = null;
        _creatingHierarchyMenu = true;
        try
        {
            menuNode = new("HierarchyContextMenu", new UIBoundableTransform()) { IsActiveSelf = false };
            _contextMenuNode = menuNode;
            menu = menuNode.AddComponent<UIContextMenuComponent>()!;
            if (!IsHierarchyMenuSessionCurrent(session) || !ReferenceEquals(_contextMenuNode, menuNode))
                return null;
            _contextMenu = menu;
            dismissed = () =>
            {
                if (!ReferenceEquals(_contextMenu, menu))
                    return;
                if (_contextMenuSession is { AwaitingShow: false } dismissedSession)
                {
                    _contextMenuSession = null;
                    dismissedSession.Release();
                }
            };
            _contextMenuDismissed = dismissed;
            menu.Dismissed += dismissed;
            ((UIBoundableTransform)menuNode.Transform).Visibility = EVisibility.Collapsed;
            if (!IsHierarchyMenuSessionCurrent(session) || !ReferenceEquals(_contextMenu, menu))
                return null;
            menuNode.Transform.SetParent(canvas, false, EParentAssignmentMode.Immediate);
            if (!IsHierarchyMenuSessionCurrent(session) || !ReferenceEquals(_contextMenu, menu))
                return null;
            menuNode.IsActiveSelf = true;
            if (!IsHierarchyMenuSessionCurrent(session) || !ReferenceEquals(_contextMenu, menu) ||
                !ReferenceEquals(menuNode.Transform.Parent, canvas))
                return null;
            completed = true;
            return menu;
        }
        catch (Exception error)
        {
            failure = error;
            return null;
        }
        finally
        {
            try
            {
                if (!completed)
                {
                    if (ReferenceEquals(_contextMenuSession, session))
                    {
                        _contextMenuSession = null;
                        session.Release();
                    }
                    if (ReferenceEquals(_contextMenu, menu))
                        _contextMenu = null;
                    if (ReferenceEquals(_contextMenuNode, menuNode))
                        _contextMenuNode = null;
                    if (ReferenceEquals(_contextMenuDismissed, dismissed))
                        _contextMenuDismissed = null;
                    if (menu is not null && dismissed is not null)
                        menu.Dismissed -= dismissed;
                    RetireHierarchyMenu(menu, menuNode, ref failure);
                }
            }
            finally { _creatingHierarchyMenu = false; }
            ThrowHierarchyFailure(failure);
        }
    }

    private bool IsHierarchyMenuSessionCurrent(HierarchyMenuSession session)
        => ReferenceEquals(_contextMenuSession, session) && ReferenceEquals(session.Owner, this) &&
            session.Revision == _hierarchyRevision && session.Context?.CanStart() == true &&
            session.Request == _contextMenuRequest && ReferenceEquals(session.Input, _rightClickInput) &&
            ReferenceEquals(session.Input, GetCanvasInput()) &&
            session.Canvas is { IsDestroyed: false, IsDestroyQueued: false } &&
            ReferenceEquals(session.Canvas, BoundableTransform.GetCanvasTransform()) &&
            ReferenceEquals(session.List, _treeListTransform) && ReferenceEquals(session.Provider, _virtualTree) &&
            session.List is { } list && ReferenceEquals(list.VirtualProvider, session.Provider) &&
            list.VirtualProviderIncarnation == session.ProviderIncarnation && session.Row is { } row &&
            list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) && lease == session.Lease &&
            _hierarchyRows.TryGetValue(row, out HierarchyRow? state) && IsHierarchyRowCurrent(state) &&
            ReferenceEquals(state.Node, session.Node) && ReferenceEquals(state.Scene, session.Scene);

    internal void InvokeHierarchyMenuAction(HierarchyMenuSession session, EHierarchyMenuAction action)
    {
        if (session.AwaitingShow || _showingHierarchyMenu ||
            !IsHierarchyMenuSessionCurrent(session) || session.Context is not { } context)
            return;
        SceneNode? node = session.Node;
        switch (action)
        {
            case EHierarchyMenuAction.Rename: BeginRename(node); break;
            case EHierarchyMenuAction.Focus: TryFocusCameraOnNode(node); break;
            case EHierarchyMenuAction.AddChild when node is not null:
                QueueNativeHierarchyCreateChild(node, context.World, child =>
                {
                    _collapsedNodes.Remove(node.ID);
                    BeginRename(child);
                }, context.IsOwnerCurrent, context.CanStart);
                break;
            case EHierarchyMenuAction.Delete when node is not null:
                if (ReferenceEquals(node, _nodePendingRename))
                    CancelRename();
                QueueNativeHierarchyDelete(node, context.World, context.IsOwnerCurrent, context.CanStart);
                break;
            case EHierarchyMenuAction.ToggleScene when session.Scene is { } scene:
                bool visible = !scene.IsVisible;
                EnqueueSceneEdit(() =>
                {
                    if (!context.CanStart())
                        return;
                    ToggleNativeSceneVisibility(scene, context.World, visible);
                });
                break;
            case EHierarchyMenuAction.UnloadScene when session.Scene is { } unloadScene:
                EnqueueSceneEdit(() =>
                {
                    if (!context.CanStart())
                        return;
                    UnloadNativeScene(unloadScene, context.World);
                    if (context.IsOwnerCurrent())
                        RebuildVirtualTree();
                });
                break;
        }
    }
}
