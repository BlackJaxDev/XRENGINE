using System.Numerics;
using System.Runtime.ExceptionServices;
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
    private readonly NativeDockWindowRegistration _registration;
    private readonly NativeLayoutSaveDebouncer? _layoutSaves;
    private readonly List<IDisposable> _dropRegistrations = [];
    private readonly Dictionary<string, SceneNode> _panelSlots = [];
    private readonly NativeDockRenderGate _renderGate = new();
    private int _closedCursor;
    private ulong _dockMutationRevision;
    private bool _receivingPanel;
    private bool _disposed;

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
        _registration = _root.AddComponent<NativeDockWindowRegistration>()!;
        _registration.Configure(_windowKey, ReceivePanel, _world);
        _layoutSaves = _root.AddComponent<NativeLayoutSaveDebouncer>()!;
        _layoutSaves.Configure(Save);
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

    /// <summary>
    /// Rebuilds the dock controls. A rebuild requested by a callback during a rebuild runs
    /// after it, so callbacks never rebuild a half-built tree.
    /// </summary>
    private void Render()
    {
        if (!_disposed)
            _renderGate.Run(RenderPass);
    }

    private void RenderPass()
    {
        DisposeDropTargets();
        // Deactivation runs panel callbacks. They can reparent, replace, or destroy any slot,
        // so iterate a copy and detach only a slot that is still the registered one.
        KeyValuePair<string, SceneNode>[] slots = [.. _panelSlots];
        foreach ((string panel, SceneNode slot) in slots)
        {
            NativeDockSlotDetachment.DetachRegistered(panel, slot, _root, _panelSlots,
                () => !_disposed);
            if (_disposed)
                return;
        }
        foreach (var child in _root.Transform.Children.ToArray())
            if (child.SceneNode is { } structural)
            {
                structural.Destroy(true);
                if (!structural.IsDestroyed)
                    throw new InvalidOperationException("A native dock node refused teardown; the layout was not rebuilt.");
            }
        if (_disposed)
            return;
        // A callback can move a slot under a structural node; teardown then destroys it.
        // Forget destroyed slots so the stack creates a new panel.
        foreach ((string panel, SceneNode slot) in slots)
            if (slot.IsDestroyed && _panelSlots.TryGetValue(panel, out SceneNode? current) &&
                ReferenceEquals(current, slot))
                _panelSlots.Remove(panel);
        RenderNode(_root, _tree.Root);
    }

    /// <summary>Gets whether a control from the render pass <paramref name="revision"/> may change the dock.</summary>
    private bool IsCurrentControl(ulong revision)
        => !_disposed && !_receivingPanel && _renderGate.IsCurrent(revision);

    private bool IsCurrentStack(NativeDockNode stack, ulong revision)
        => IsCurrentControl(revision) && ContainsNode(_tree.Root, stack);

    private static bool ContainsNode(NativeDockNode root, NativeDockNode node)
        => ReferenceEquals(root, node) || root.IsSplit &&
            (ContainsNode(root.First!, node) || ContainsNode(root.Second!, node));

    private void DisposeDropTargets()
        => DisposeDropRegistrations(_dropRegistrations);

    internal static void DisposeDropRegistrations(List<IDisposable> registrations)
    {
        IDisposable[] oldRegistrations = [.. registrations];
        registrations.Clear();
        List<Exception>? failures = null;
        foreach (IDisposable registration in oldRegistrations)
        {
            try { registration.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Native dock drop-target cleanup failed.", failures);
    }

    private void Dispose()
    {
        _disposed = true;
        List<Exception>? failures = null;
        try { DisposeDropTargets(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        // Slot teardown runs panel callbacks that can change the slot map.
        foreach (SceneNode slot in _panelSlots.Values.ToArray())
            if (!slot.IsDestroyed && slot.Transform.Parent is null)
                try { slot.Destroy(true); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
        _panelSlots.Clear();
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Native dock owner cleanup failed.", failures);
    }

    private void RenderNode(SceneNode parent, NativeDockNode node)
    {
        if (!node.IsSplit)
        {
            RenderStack(parent, node);
            return;
        }
        ulong revision = _renderGate.Revision;
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
        handleNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
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
            if (change.PropertyName == nameof(UIDualSplitTransform.FixedSize) &&
                split.FixedSize is { } size && IsCurrentControl(revision) && ContainsNode(_tree.Root, node))
            {
                node.FixedSize = size;
                UpdateHandlePosition();
                // A drag changes the size on each pointer move. Write it once after the drag settles.
                _layoutSaves?.Request();
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
        ulong revision = _renderGate.Revision;
        SceneNode stackNode = parent.NewChildWithTransform(out UIDualSplitTransform outer, $"DockStack-{stack.Id}");
        outer.VerticalSplit = true;
        outer.FirstFixedSize = true;
        outer.FixedSize = 60;
        outer.SplitterSize = 0;
        SceneNode header = stackNode.NewChildWithTransform(out UIDualSplitTransform headerSplit, "DockHeader");
        header.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.StaticChrome;
        headerSplit.VerticalSplit = true;
        headerSplit.FirstFixedSize = true;
        headerSplit.FixedSize = 30;
        headerSplit.SplitterSize = 0;
        SceneNode tabsNode = header.NewChildWithTransform(out UIListTransform tabList, "DockTabs");
        tabsNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
        tabList.DisplayHorizontal = true;
        tabList.ItemAlignment = EListAlignment.TopOrLeft;
        tabList.ItemSize = 82;
        UITabStripComponent strip = tabsNode.AddComponent<UITabStripComponent>()!;
        strip.TabWidth = 82;
        _dropRegistrations.Add(EditorDragDropUtility.RegisterDropTarget(tabList,
            payload =>
            {
                if (!IsCurrentStack(stack, revision) ||
                    !EditorDragDropUtility.TryGetEditorPayload<NativeDockTabPayload>(payload, "dock-tab", out var tab) || tab is null ||
                    tab.WindowKey != _windowKey || !_tree.Move(tab.Panel, stack.Id, int.MaxValue))
                    return false;
                CommitStructure();
                return true;
            },
            payload => EditorDragDropUtility.TryGetEditorPayload<NativeDockTabPayload>(payload, "dock-tab", out var tab) && tab is not null &&
                tab.WindowKey == _windowKey && NativeDockPanel.All.Contains(tab.Panel)));
        SceneNode actionsNode = header.NewChildWithTransform(out UIListTransform actions, "DockActions");
        actionsNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
        actions.DisplayHorizontal = true;
        actions.ItemAlignment = EListAlignment.TopOrLeft;
        actions.ItemSpacing = 2;
        SceneNode content = stackNode.NewChildWithTransform<UIBoundableTransform>(out _, "DockContent");
        content.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Dynamic;
        SceneNode actionMenuNode = _root.NewChild<UIContextMenuComponent>(out var actionMenu, "DockActionsMenu");
        actionMenuNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
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
            if (!IsCurrentStack(stack, revision)) return;
            string panel = NativeDockPanel.All[(int)id - 1];
            if (!stack.Tabs.Contains(panel))
                return;
            stack.Selected = panel;
            Refresh();
            Save();
        };
        strip.CloseRequested += id =>
        {
            if (!IsCurrentStack(stack, revision)) return;
            string panel = NativeDockPanel.All[(int)id - 1];
            if (_tree.Close(panel))
                CommitStructure();
        };
        strip.MoveRequested += (id, direction) =>
        {
            if (!IsCurrentStack(stack, revision)) return;
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
            if (!IsCurrentStack(stack, revision))
                return;
            Vector2 position = _root.GetTransformAs<UIBoundableTransform>(true)!
                .CanvasToLocal(actionsButton.BoundableTransform.LocalToCanvas(Vector2.Zero));
            actionMenu.Show(position,
                new ContextMenuItem("Close", () => strip.RequestCloseSelected()),
                new ContextMenuItem("Move left", () => strip.RequestMoveSelected(-1)),
                new ContextMenuItem("Move right", () => strip.RequestMoveSelected(1)),
                new ContextMenuItem("Move to next stack", () => MoveToNextStack(stack, revision)),
                new ContextMenuItem("Split left", () => SplitSelected(stack, revision, vertical: false, before: true)),
                new ContextMenuItem("Split right", () => SplitSelected(stack, revision, vertical: false, before: false)),
                new ContextMenuItem("Split top", () => SplitSelected(stack, revision, vertical: true, before: true)),
                new ContextMenuItem("Split bottom", () => SplitSelected(stack, revision, vertical: true, before: false)),
                new ContextMenuItem("Reopen " + (_tree.ClosedPanels.Count == 0 ? "panel" :
                    Title(_tree.ClosedPanels[Math.Abs(_closedCursor % _tree.ClosedPanels.Count)])),
                    () => ReopenNext(stack, revision), _tree.ClosedPanels.Count > 0),
                new ContextMenuItem("Next closed panel", () =>
                {
                    if (!IsCurrentStack(stack, revision))
                        return;
                    _closedCursor++;
                    RefreshClosedLabel();
                }, _tree.ClosedPanels.Count > 1),
                new ContextMenuItem("Detach", () => DetachSelected(stack, revision)),
                new ContextMenuItem("Move to window", () => MoveToWindow(stack, revision)));
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
            if (IsCurrentStack(stack, revision) && stack.Selected is { } panel &&
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
                inspector.Window = _window;
                inspector.InspectedObjects = Selection.SceneNodes;
                break;
            case NativeDockPanel.Assets:
                slot.NewChild<AssetsPanel>(out var assets);
                BindToolState(assets, slot, panel, 0);
                break;
            case NativeDockPanel.Console:
                slot.NewChild<ConsolePanel>(out var console);
                BindToolState(console, slot, panel, 1);
                break;
            case NativeDockPanel.Profiler:
                slot.NewChild<ProfilerPanel>(out var profiler);
                BindToolState(profiler, slot, panel, 2);
                break;
            case NativeDockPanel.Animation:
                slot.NewChild<AnimationPanel>(out var animation);
                BindToolState(animation, slot, panel, 3);
                break;
        }
    }

    private void BindToolState(NativeVirtualTextPanel component, SceneNode slot, string panel, int index)
    {
        var rootIdentity = XREngine.Data.Core.ObjectConstructionIdentity.Capture(_root);
        NativeToolPanelState.Bind(component, slot, _layout, index, Save,
            () => _layoutSaves?.Request(),
            () => !_disposed && !_receivingPanel && !_renderGate.IsRendering &&
                rootIdentity.IsCurrent && !_root.IsDestroyed && !_root.IsDestroyQueued &&
                _panelSlots.TryGetValue(panel, out SceneNode? current) && ReferenceEquals(current, slot));
    }

    private void MoveToNextStack(NativeDockNode source, ulong revision)
    {
        if (!IsCurrentStack(source, revision)) return;
        if (source.Selected is not { } panel)
            return;
        NativeDockNode? target = _tree.Stacks().FirstOrDefault(candidate => candidate.Id != source.Id);
        if (target is not null && _tree.Move(panel, target.Id, int.MaxValue))
            CommitStructure();
    }

    private void SplitSelected(NativeDockNode source, ulong revision, bool vertical, bool before)
    {
        if (!IsCurrentStack(source, revision)) return;
        if (source.Selected is not { } panel)
            return;
        NativeDockNode? target = source.Tabs.Count > 1 ? source :
            _tree.Stacks().FirstOrDefault(candidate => candidate.Id != source.Id);
        if (target is not null && _tree.SplitPanel(panel, target.Id, vertical, before))
            CommitStructure();
    }

    private void ReopenNext(NativeDockNode target, ulong revision)
    {
        if (!IsCurrentStack(target, revision)) return;
        if (_tree.ClosedPanels.Count == 0)
            return;
        string panel = _tree.ClosedPanels[Math.Abs(_closedCursor % _tree.ClosedPanels.Count)];
        if (_tree.Reopen(panel, target.Id))
            CommitStructure();
    }

    private void DetachSelected(NativeDockNode source, ulong revision)
    {
        if (!IsCurrentStack(source, revision)) return;
        if (source.Selected is not { } panel || _window is null)
            return;
        // Once the detached window owns the panel, this window must not show it, even when
        // a callback changed this dock while the window opened.
        if (NativeDetachedDockWindows.Open(_window, panel) && !_disposed && _tree.Close(panel))
            CommitStructure();
    }

    private void MoveToWindow(NativeDockNode source, ulong revision)
    {
        if (!IsCurrentStack(source, revision) || _root.IsDestroyed || _root.IsDestroyQueued ||
            source.Selected is not { } panel)
            return;
        ulong sourceRevision = _dockMutationRevision;
        ulong registrationRevision = _registration.RegistrationRevision;
        NativeDockTransferResult result = NativeDockWindowRegistry.TransferToAnotherWindow(_registration, panel);
        if (result == NativeDockTransferResult.Rejected)
            Debug.UI("[NativeDock] Panel transfer could not be confirmed.");
        else if (result == NativeDockTransferResult.NoTarget)
            Debug.UI("[NativeDock] Open another native editor window in this world, then retry the panel transfer.");
        if (result == NativeDockTransferResult.Committed && !_disposed && !_root.IsDestroyed &&
            !_root.IsDestroyQueued && _root.IsActiveInHierarchy && ReferenceEquals(_root.World, _world) &&
            _registration.RegistrationRevision == registrationRevision &&
            _dockMutationRevision == sourceRevision && source.Selected == panel &&
            _tree.Stacks().Any(stack => ReferenceEquals(stack, source) && stack.Tabs.Contains(panel)) &&
            _tree.Close(panel))
        {
            CommitStructure();
            if (_panelSlots.Remove(panel, out SceneNode? oldSlot) && !oldSlot.IsDestroyed)
                try { oldSlot.Destroy(true); }
                catch (Exception exception)
                {
                    if (!oldSlot.IsDestroyed)
                        _panelSlots[panel] = oldSlot;
                    Debug.LogException(exception, "[NativeDock] Could not release the transferred panel slot.");
                }
        }
    }

    private bool ReceivePanel(string panel)
    {
        if (_receivingPanel || _disposed || _world is null || !_root.IsActiveInHierarchy ||
            _root.IsDestroyed || _root.IsDestroyQueued || !ReferenceEquals(_root.World, _world) ||
            !_registration.TryGetRegistration(out string registeredKey, out object? registeredWorld) ||
            registeredKey != _windowKey || !ReferenceEquals(registeredWorld, _world) ||
            !NativeDockPanel.All.Contains(panel))
            return false;
        NativeDockNode priorRoot = CopyDockNode(_tree.Root);
        List<string> priorClosed = [.. _tree.ClosedPanels];
        ulong priorRevision = _dockMutationRevision;
        ulong registrationRevision = _registration.RegistrationRevision;
        _receivingPanel = true;
        try
        {
            NativeDockNode target = _tree.Stacks().FirstOrDefault(stack =>
                stack.Tabs.Any(item => NativeDockPanel.LegacyTools.Contains(item))) ?? _tree.Stacks().First();
            if (!_tree.Move(panel, target.Id, int.MaxValue))
                return false;
            CommitStructure();
            if (!_disposed && _root.IsActiveInHierarchy && !_root.IsDestroyed &&
                !_root.IsDestroyQueued && ReferenceEquals(_root.World, _world) &&
                _registration.TryGetRegistration(out registeredKey, out registeredWorld) &&
                registeredKey == _windowKey && ReferenceEquals(registeredWorld, _world) &&
                _registration.RegistrationRevision == registrationRevision &&
                _dockMutationRevision == priorRevision + 1 &&
                _tree.Stacks().Any(stack => stack.Tabs.Contains(panel)))
                return true;
            RestoreReceiveSnapshot(priorRoot, priorClosed, priorRevision + 1, registrationRevision);
            return false;
        }
        catch (Exception exception)
        {
            ulong admittedRevision = _dockMutationRevision == priorRevision
                ? priorRevision : priorRevision + 1;
            RestoreReceiveSnapshot(priorRoot, priorClosed, admittedRevision, registrationRevision);
            Debug.LogException(exception, "[NativeDock] Destination panel commit failed.");
            return false;
        }
        finally { _receivingPanel = false; }
    }

    private void RestoreReceiveSnapshot(NativeDockNode root, List<string> closedPanels,
        ulong admittedRevision, ulong registrationRevision)
    {
        if (_disposed || _root.IsDestroyed || _root.IsDestroyQueued || !_root.IsActiveInHierarchy ||
            _world is null || !ReferenceEquals(_root.World, _world) ||
            _registration.RegistrationRevision != registrationRevision ||
            !_registration.TryGetRegistration(out string registeredKey, out object? registeredWorld) ||
            registeredKey != _windowKey || !ReferenceEquals(registeredWorld, _world) ||
            _dockMutationRevision != admittedRevision)
            return;
        _tree.Root = root;
        _tree.ClosedPanels = closedPanels;
        _dockMutationRevision++;
        try
        {
            Save();
            if (_root.IsActiveInHierarchy)
                Render();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, "[NativeDock] Could not restore the destination dock tree.");
        }
    }

    private void CommitStructure()
    {
        _dockMutationRevision++;
        _tree.Sanitize();
        Save();
        Render();
    }

    private void Save()
    {
        _layoutSaves?.MarkSaved();
        NativeDockLayoutStore.Save(_windowKey, _layout);
    }

    private static NativeDockNode CopyDockNode(NativeDockNode source)
        => new()
        {
            Id = source.Id,
            Vertical = source.Vertical,
            FirstFixedSize = source.FirstFixedSize,
            FixedSize = source.FixedSize,
            Tabs = [.. source.Tabs],
            Selected = source.Selected,
            First = source.First is null ? null : CopyDockNode(source.First),
            Second = source.Second is null ? null : CopyDockNode(source.Second),
        };

    private static string Title(string panel)
        => panel.Length == 0 ? panel : char.ToUpperInvariant(panel[0]) + panel[1..];
}
