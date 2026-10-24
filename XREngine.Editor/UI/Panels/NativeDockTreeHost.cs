using System.Numerics;
using XREngine.Editor.UI;
using XREngine.Editor.UI.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Renders the persisted native dock tree in the primary editor canvas.</summary>
internal sealed class NativeDockTreeHost
{
    private readonly SceneNode _root;
    private readonly RuntimeWorld? _world;
    private readonly XRWindow? _window;
    private readonly string _windowKey;
    private readonly NativeDockLayout _layout;
    private readonly NativeDockTree _tree;
    private readonly List<IDisposable> _dropRegistrations = [];
    private readonly Dictionary<string, SceneNode> _panelSlots = [];
    private int _closedCursor;

    private NativeDockTreeHost(SceneNode root, RuntimeWorld? world, XRWindow? window = null,
        string? windowKey = null, string? initialPanel = null, bool restoreWindows = true)
    {
        _root = root;
        _world = world;
        _window = window ?? EditorPanelHost.ResolveWindow(root);
        _windowKey = windowKey ?? EditorPanelHost.GetWindowKey(_window);
        _layout = NativeDockLayoutStore.Load(_windowKey);
        if (initialPanel is not null)
        {
            _layout.DockTree = new NativeDockTree
            {
                Root = new NativeDockNode { Tabs = [initialPanel], Selected = initialPanel },
            }.Sanitize();
            NativeDockLayoutStore.Save(_windowKey, _layout);
        }
        _tree = _layout.DockTree!;
        _root.Destroyed += _ => Dispose();
        _root.AddComponent<NativeDockWindowRegistration>()!.Configure(_windowKey, ReceivePanel);
        Render();
        if (restoreWindows && _window is not null)
            NativeDetachedDockWindows.Restore(_window);
    }

    public static void Build(SceneNode root, RuntimeWorld? world)
        => _ = new NativeDockTreeHost(root, world);

    public static void BuildDetached(SceneNode root, RuntimeWorld world, XRWindow window,
        string windowKey, string panel, bool restoring)
        => _ = new NativeDockTreeHost(root, world, window, windowKey,
            restoring && NativeDockLayoutStore.HasLayout(windowKey) ? null : panel,
            restoreWindows: false);

    private void Render()
    {
        DisposeDropTargets();
        foreach (SceneNode slot in _panelSlots.Values)
            if (!slot.IsDestroyed && slot.Transform.Parent is not null)
            {
                slot.IsActiveSelf = false;
                slot.Transform.SetParent(null, false, EParentAssignmentMode.Immediate);
                // Clearing Parent alone retains Transform.World and can turn a detached
                // editor panel into a live world root until it is reattached.
                slot.Transform.SetWorldContext(null);
            }
        foreach (var child in _root.Transform.Children.ToArray())
            if (child.SceneNode is { } structural)
            {
                structural.Destroy(true);
                if (!structural.IsDestroyed)
                    throw new InvalidOperationException("A native dock node refused teardown; the layout was not rebuilt.");
            }
        RenderNode(_root, _tree.Root);
    }

    private void DisposeDropTargets()
    {
        foreach (IDisposable registration in _dropRegistrations)
            registration.Dispose();
        _dropRegistrations.Clear();
    }

    private void Dispose()
    {
        DisposeDropTargets();
        foreach (SceneNode slot in _panelSlots.Values)
            if (!slot.IsDestroyed && slot.Transform.Parent is null)
                slot.Destroy(true);
        _panelSlots.Clear();
    }

    private void RenderNode(SceneNode parent, NativeDockNode node)
    {
        if (!node.IsSplit)
        {
            RenderStack(parent, node);
            return;
        }
        SceneNode container = parent.NewChildWithTransform<UIBoundableTransform>(out _, $"DockSplit-{node.Id}");
        SceneNode root = container.NewChildWithTransform(out UIDualSplitTransform split, "SplitContent");
        split.MinAnchor = Vector2.Zero;
        split.MaxAnchor = Vector2.One;
        split.VerticalSplit = node.Vertical;
        split.FirstFixedSize = node.FirstFixedSize;
        split.FixedSize = node.FixedSize;
        split.SplitterSize = 5;
        split.CanUserResize = true;
        RenderNode(root, node.First!);
        RenderNode(root, node.Second!);

        // Keep the handle beside the split so it does not become a third pane.
        SceneNode handleNode = container.NewChild<UISplitterHandleComponent>(out var handle, "SplitHandle");
        handle.SplitTarget = split;
        handle.MinimumSize = 96;
        handle.MaximumSize = 2000;
        UIBoundableTransform handleBounds = handleNode.GetTransformAs<UIBoundableTransform>(true)!;
        handleBounds.BlocksInputBehind = true;
        handleBounds.NormalizedPivot = node.Vertical
            ? new Vector2(0, 0.5f) : new Vector2(0.5f, 0);
        if (node.Vertical)
        {
            float anchor = node.FirstFixedSize ? 1 : 0;
            handleBounds.MinAnchor = new Vector2(0, anchor);
            handleBounds.MaxAnchor = new Vector2(1, anchor);
            handleBounds.Height = 10;
        }
        else
        {
            float anchor = node.FirstFixedSize ? 0 : 1;
            handleBounds.MinAnchor = new Vector2(anchor, 0);
            handleBounds.MaxAnchor = new Vector2(anchor, 1);
            handleBounds.Width = 10;
        }
        UpdateHandlePosition();
        split.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(UIDualSplitTransform.FixedSize) && split.FixedSize is { } size)
            {
                node.FixedSize = size;
                UpdateHandlePosition();
                Save();
            }
        };

        void UpdateHandlePosition()
        {
            float offset = node.Vertical
                ? (node.FirstFixedSize ? -node.FixedSize : node.FixedSize)
                : (node.FirstFixedSize ? node.FixedSize : -node.FixedSize);
            handleBounds.Translation = node.Vertical ? new Vector2(0, offset) : new Vector2(offset, 0);
        }
    }

    private void RenderStack(SceneNode parent, NativeDockNode stack)
    {
        SceneNode stackNode = parent.NewChildWithTransform(out UIDualSplitTransform outer, $"DockStack-{stack.Id}");
        outer.VerticalSplit = true;
        outer.FirstFixedSize = true;
        outer.FixedSize = 60;
        outer.SplitterSize = 0;
        SceneNode header = stackNode.NewChildWithTransform(out UIDualSplitTransform headerSplit, "DockHeader");
        headerSplit.VerticalSplit = true;
        headerSplit.FirstFixedSize = true;
        headerSplit.FixedSize = 30;
        headerSplit.SplitterSize = 0;
        SceneNode tabsNode = header.NewChildWithTransform(out UIListTransform tabList, "DockTabs");
        tabList.DisplayHorizontal = true;
        tabList.ItemAlignment = EListAlignment.TopOrLeft;
        tabList.ItemSize = 82;
        UITabStripComponent strip = tabsNode.AddComponent<UITabStripComponent>()!;
        strip.TabWidth = 82;
        _dropRegistrations.Add(EditorDragDropUtility.RegisterDropTarget(tabList,
            payload =>
            {
                if (!EditorDragDropUtility.TryGetEditorPayload<NativeDockTabPayload>(payload, "dock-tab", out var tab) || tab is null ||
                    tab.WindowKey != _windowKey || !_tree.Move(tab.Panel, stack.Id, int.MaxValue))
                    return false;
                CommitStructure();
                return true;
            },
            payload => EditorDragDropUtility.TryGetEditorPayload<NativeDockTabPayload>(payload, "dock-tab", out var tab) && tab is not null &&
                tab.WindowKey == _windowKey && NativeDockPanel.All.Contains(tab.Panel)));
        SceneNode actionsNode = header.NewChildWithTransform(out UIListTransform actions, "DockActions");
        actions.DisplayHorizontal = true;
        actions.ItemAlignment = EListAlignment.TopOrLeft;
        actions.ItemSpacing = 2;
        SceneNode content = stackNode.NewChildWithTransform<UIBoundableTransform>(out _, "DockContent");
        _root.NewChild<UIContextMenuComponent>(out var actionMenu, "DockActionsMenu");
        Dictionary<string, SceneNode> panels = [];
        foreach (string panel in stack.Tabs)
        {
            if (!_panelSlots.TryGetValue(panel, out SceneNode? slot) || slot.IsDestroyed)
            {
                slot = content.NewChildWithTransform<UIBoundableTransform>(out _, panel);
                CreatePanel(slot, panel);
                _panelSlots[panel] = slot;
            }
            else
                slot.Transform.SetParent(content.Transform, false, EParentAssignmentMode.Immediate);
            panels.Add(panel, slot);
        }

        strip.TabActivated += id =>
        {
            string panel = NativeDockPanel.All[(int)id - 1];
            if (!stack.Tabs.Contains(panel))
                return;
            stack.Selected = panel;
            Refresh();
            Save();
        };
        strip.CloseRequested += id =>
        {
            string panel = NativeDockPanel.All[(int)id - 1];
            if (_tree.Close(panel))
                CommitStructure();
        };
        strip.MoveRequested += (id, direction) =>
        {
            string panel = NativeDockPanel.All[(int)id - 1];
            int index = stack.Tabs.IndexOf(panel);
            if (index < 0 || index + direction < 0 || index + direction >= stack.Tabs.Count)
                return;
            (stack.Tabs[index], stack.Tabs[index + direction]) =
                (stack.Tabs[index + direction], stack.Tabs[index]);
            CommitStructure();
        };

        SceneNode closedNode = actionsNode.NewChild<UITextComponent>(out var closedLabel);
        closedNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 100;
        closedLabel.VerticalAlignment = EVerticalAlignment.Center;

        var actionsButton = NativeUIElements.CreateButton(actionsNode, "Actions", width: 80, height: 28).Button;
        actionsButton.InteractAction += _ =>
        {
            Vector2 position = _root.GetTransformAs<UIBoundableTransform>(true)!
                .CanvasToLocal(actionsButton.BoundableTransform.LocalToCanvas(Vector2.Zero));
            actionMenu.Show(position,
                new ContextMenuItem("Close", () => strip.RequestCloseSelected()),
                new ContextMenuItem("Move left", () => strip.RequestMoveSelected(-1)),
                new ContextMenuItem("Move right", () => strip.RequestMoveSelected(1)),
                new ContextMenuItem("Move to next stack", () => MoveToNextStack(stack)),
                new ContextMenuItem("Split left", () => SplitSelected(stack, vertical: false, before: true)),
                new ContextMenuItem("Split right", () => SplitSelected(stack, vertical: false, before: false)),
                new ContextMenuItem("Split top", () => SplitSelected(stack, vertical: true, before: true)),
                new ContextMenuItem("Split bottom", () => SplitSelected(stack, vertical: true, before: false)),
                new ContextMenuItem("Reopen " + (_tree.ClosedPanels.Count == 0 ? "panel" :
                    Title(_tree.ClosedPanels[Math.Abs(_closedCursor % _tree.ClosedPanels.Count)])),
                    () => ReopenNext(stack), _tree.ClosedPanels.Count > 0),
                new ContextMenuItem("Next closed panel", () => { _closedCursor++; RefreshClosedLabel(); },
                    _tree.ClosedPanels.Count > 1),
                new ContextMenuItem("Detach", () => DetachSelected(stack)),
                new ContextMenuItem("Move to window", () => MoveToWindow(stack)));
        };
        var drag = NativeUIElements.CreateButton(actionsNode, "Drag", width: 60, height: 28).Button;
        drag.NeedsMouseMove = true;
        Vector2? dragStart = null;
        drag.MouseMove += (x, y, source) =>
        {
            if (!EditorDragDropUtility.IsLeftMouseButtonHeldFor(source))
            {
                dragStart = null;
                return;
            }
            Vector2 point = source.BoundableTransform.LocalToCanvas(new Vector2(x, y));
            if (dragStart is null)
            {
                dragStart = point;
                return;
            }
            if (stack.Selected is { } panel &&
                Vector2.Distance(dragStart.Value, point) >= EditorDragDropUtility.DefaultActivationDistance)
                EditorDragDropUtility.BeginDrag(source,
                    EditorDragDropUtility.CreateEditorPayload("dock-tab", new NativeDockTabPayload(_windowKey, panel)), point);
        };
        drag.MouseDirectOverlapLeave += _ => dragStart = null;
        RefreshClosedLabel();
        Refresh();

        void RefreshClosedLabel()
        {
            closedLabel.Text = _tree.ClosedPanels.Count == 0 ? string.Empty :
                Title(_tree.ClosedPanels[Math.Abs(_closedCursor % _tree.ClosedPanels.Count)]);
        }

        void Refresh()
        {
            UITabItem[] items = stack.Tabs.Select(panel =>
                new UITabItem((ulong)(Array.IndexOf(NativeDockPanel.All, panel) + 1), Title(panel))).ToArray();
            ulong? selectedId = stack.Selected is { } selected
                ? (ulong)(Array.IndexOf(NativeDockPanel.All, selected) + 1) : null;
            strip.SetTabs(items, selectedId);
            foreach (var (panel, slot) in panels)
            {
                bool active = panel == stack.Selected;
                slot.GetTransformAs<UIBoundableTransform>(true)!.Visibility = active
                    ? EVisibility.Visible : EVisibility.Hidden;
                slot.IsActiveSelf = active;
            }
        }
    }

    private void CreatePanel(SceneNode slot, string panel)
    {
        switch (panel)
        {
            case NativeDockPanel.Hierarchy:
                slot.NewChild<HierarchyPanel>(out var hierarchy);
                hierarchy.Window = _window;
                if (_world is not null)
                    hierarchy.RootNodes = [.. _world.RootNodes];
                break;
            case NativeDockPanel.Scene:
                slot.NewChild<SceneViewPanel>(out var scene);
                scene.Window = _window;
                break;
            case NativeDockPanel.Inspector:
                slot.NewChild<InspectorPanel>(out var inspector);
                inspector.InspectedObjects = Selection.SceneNodes;
                break;
            case NativeDockPanel.Assets:
                slot.NewChild<AssetsPanel>(out var assets);
                assets.UseEngineAssets = _layout.UseEngineAssets;
                assets.Folder = _layout.AssetFolder;
                assets.Search = _layout.AssetSearch;
                ApplyToolScroll(assets, 0);
                assets.ViewStateChanged += () =>
                {
                    _layout.UseEngineAssets = assets.UseEngineAssets;
                    _layout.AssetFolder = assets.Folder;
                    _layout.AssetSearch = assets.Search;
                    Save();
                };
                break;
            case NativeDockPanel.Console:
                slot.NewChild<ConsolePanel>(out var console);
                console.Filter = _layout.ConsoleFilter;
                ApplyToolScroll(console, 1);
                console.FilterChanged += filter => { _layout.ConsoleFilter = filter; Save(); };
                break;
            case NativeDockPanel.Profiler:
                slot.NewChild<ProfilerPanel>(out var profiler);
                ApplyToolScroll(profiler, 2);
                break;
            case NativeDockPanel.Animation:
                slot.NewChild<AnimationPanel>(out var animation);
                ApplyToolScroll(animation, 3);
                break;
        }
    }

    private void ApplyToolScroll(NativeVirtualTextPanel panel, int index)
    {
        panel.ScrollOffset = _layout.PanelScrollOffsets[index];
        panel.ScrollOffsetChanged += offset =>
        {
            _layout.PanelScrollOffsets[index] = offset;
            Save();
        };
    }

    private void MoveToNextStack(NativeDockNode source)
    {
        if (source.Selected is not { } panel)
            return;
        NativeDockNode? target = _tree.Stacks().FirstOrDefault(candidate => candidate.Id != source.Id);
        if (target is not null && _tree.Move(panel, target.Id, int.MaxValue))
            CommitStructure();
    }

    private void SplitSelected(NativeDockNode source, bool vertical, bool before)
    {
        if (source.Selected is not { } panel)
            return;
        NativeDockNode? target = source.Tabs.Count > 1 ? source :
            _tree.Stacks().FirstOrDefault(candidate => candidate.Id != source.Id);
        if (target is not null && _tree.SplitPanel(panel, target.Id, vertical, before))
            CommitStructure();
    }

    private void ReopenNext(NativeDockNode target)
    {
        if (_tree.ClosedPanels.Count == 0)
            return;
        string panel = _tree.ClosedPanels[Math.Abs(_closedCursor % _tree.ClosedPanels.Count)];
        if (_tree.Reopen(panel, target.Id))
            CommitStructure();
    }

    private void DetachSelected(NativeDockNode source)
    {
        if (source.Selected is not { } panel || _window is null)
            return;
        if (NativeDetachedDockWindows.Open(_window, panel) && _tree.Close(panel))
            CommitStructure();
    }

    private void MoveToWindow(NativeDockNode source)
    {
        if (source.Selected is not { } panel)
            return;
        if (NativeDockWindowRegistry.TryMoveToAnotherWindow(_windowKey, panel) &&
            _tree.Close(panel))
            CommitStructure();
    }

    private void ReceivePanel(string panel)
    {
        if (!NativeDockPanel.All.Contains(panel))
            return;
        NativeDockNode target = _tree.Stacks().FirstOrDefault(stack =>
            stack.Tabs.Any(item => NativeDockPanel.LegacyTools.Contains(item))) ?? _tree.Stacks().First();
        if (_tree.Move(panel, target.Id, int.MaxValue))
            CommitStructure();
    }

    private void CommitStructure()
    {
        _tree.Sanitize();
        Save();
        Render();
    }

    private void Save() => NativeDockLayoutStore.Save(_windowKey, _layout);

    private static string Title(string panel)
        => panel.Length == 0 ? panel : char.ToUpperInvariant(panel[0]) + panel[1..];
}
