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
    private IUICanvasInputSource? GetCanvasInput()
        => BoundableTransform.GetCanvasComponent()?.GetInputComponent();

    internal static bool IsDoubleClick(SceneNode? currentNode, SceneNode? lastClickedNode, long nowTicks, long lastClickTicks, float thresholdSeconds)
        => currentNode == lastClickedNode && Math.Max(0L, nowTicks - lastClickTicks) <= EngineTimer.SecondsToStopwatchTicks(thresholdSeconds);

    private void HandleNodeButtonInteraction(UIInteractableComponent obj)
    {
        if (!CanUseHierarchy)
            return;
        _contextMenu?.Hide();

        if (!CanUseHierarchy || obj.UserData is not SceneNode node)
            return;

        var input = GetCanvasInput();
        bool ctrl = input?.IsCtrlHeld ?? false;
        bool shift = input?.IsShiftHeld ?? false;
        bool alt = input?.IsAltHeld ?? false;

        if (ctrl)
        {
            // Ctrl+Click: toggle node in selection
            var current = Selection.SceneNodes;
            if (current.Contains(node))
                Selection.SceneNodes = current.Where(n => n != node).ToArray();
            else
                Selection.SceneNodes = [.. current, node];
        }
        else if (shift)
        {
            // Shift+Click: add to selection
            var current = Selection.SceneNodes;
            if (!current.Contains(node))
                Selection.SceneNodes = [.. current, node];
        }
        else if (alt)
        {
            // Alt+Click: remove from selection
            var current = Selection.SceneNodes;
            Selection.SceneNodes = current.Where(n => n != node).ToArray();
        }
        else
        {
            // Plain click: single select
            Selection.SceneNodes = [node];
        }

        if (!CanUseHierarchy || !FocusCameraOnDoubleClick)
            return;

        long nowTicks = Engine.ElapsedTicks;
        bool doubleClicked = IsDoubleClick(node, _lastClickedNode, nowTicks, _lastClickTicks, DoubleClickThreshold);

        if (doubleClicked)
        {
            if (TryFocusCameraOnNode(node))
            {
                _lastClickedNode = null;
                _lastClickTicks = long.MinValue;
            }
            else
            {
                _lastClickedNode = node;
                _lastClickTicks = nowTicks;
            }
        }
        else
        {
            _lastClickedNode = node;
            _lastClickTicks = nowTicks;
        }
    }

    private void UpdateNodeHighlights()
    {
        if (!CanUseHierarchy || _treeListTransform is null)
            return;

        var selected = Selection.SceneNodes;
        if (_virtualTree is not null)
        {
            SynchronizeVirtualSelection();
            uint revision = _hierarchyRevision;
            foreach (HierarchyRow row in _hierarchyRows.Values)
            {
                if (row.Node is not null)
                    row.Button.DefaultBackgroundColor = _virtualTree.IsSelected(row.ItemId)
                        ? SelectedNodeColor : EditorUI.Styles.ButtonBackgroundColor;
                if (!CanUseHierarchy || revision != _hierarchyRevision)
                    return;
            }
            return;
        }
        foreach (var child in _treeListTransform.SceneNode!.Transform.Children)
        {
            var btn = child.SceneNode?.GetComponent<UIButtonComponent>();
            if (btn is null)
                continue;

            if (btn.UserData is SceneNode node && selected.Contains(node))
                btn.DefaultBackgroundColor = SelectedNodeColor;
            else
                btn.DefaultBackgroundColor = EditorUI.Styles.ButtonBackgroundColor;
        }
    }

    private bool TryFocusCameraOnNode(SceneNode? node)
    {
        if (!CanUseHierarchy || node is null)
            return false;

        if (Engine.State.MainPlayer?.ControlledPawnComponent is not EditorFlyingCameraPawnComponent pawn)
            return false;

        pawn.FocusOnNode(node, DoubleClickFocusDuration);
        return true;
    }
}
