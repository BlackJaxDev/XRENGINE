using System.Numerics;
using System.Runtime.ExceptionServices;
using XREngine.Components;
using XREngine.Data.Core;
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
    private readonly Dictionary<string, NativeToolPanelStateBinding> _toolStateBindings = [];
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
        _registration.Configure(_windowKey, ReceivePanel, _world, _window, ReceivePanelAtStack,
            CapturePanelState, ReceivePanelWithState, ReceivePanelAtStackWithState);
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

    internal bool IsCurrentDockDrag(NativeDockTabPayload tab)
        => ReferenceEquals(tab.Source, this) && !_disposed && !_receivingPanel &&
            _root is { IsDestroyed: false, IsDestroyQueued: false, IsActiveInHierarchy: true } &&
            ReferenceEquals(_root.World, _world) &&
            _dockMutationRevision == tab.DockRevision &&
            IsCurrentStack(tab.SourceStack, tab.RenderRevision) &&
            tab.SourceStack.Selected == tab.Panel &&
            tab.SourceStack.Tabs.Contains(tab.Panel) &&
            ReferenceEquals(tab.SourceWindow.Registration, _registration) &&
            ReferenceEquals(tab.SourceWindow.Window, _window);

    private bool IsCurrentLocalDockDrag(NativeDockLocalTabPayload tab)
        => ReferenceEquals(tab.Source, this) && tab.RootIdentity.IsCurrent &&
            ReferenceEquals(tab.RootIdentity.Target, _root) &&
            !_root.IsDestroyed && !_root.IsDestroyQueued && _root.IsActiveInHierarchy &&
            _dockMutationRevision == tab.DockRevision &&
            IsCurrentStack(tab.SourceStack, tab.RenderRevision) &&
            tab.SourceStack.Selected == tab.Panel && tab.SourceStack.Tabs.Contains(tab.Panel);

    private void BeginTabDrag(UIInteractableComponent source, NativeDockNode stack,
        ulong revision, string panel, Vector2 startPoint)
    {
        if (!IsCurrentStack(stack, revision) || stack.Selected != panel ||
            EditorDragDropUtility.HasDragFor(source))
            return;
        if (_window?.DesktopWindowBackend is IRuntimeDesktopDragPointerSource)
        {
            NativeDockDragWindowAdmission[] windows =
                NativeDockWindowRegistry.CaptureDragWindows(_registration);
            for (int index = 0; index < windows.Length; index++)
            {
                NativeDockDragWindowAdmission admission = windows[index];
                if (!ReferenceEquals(admission.Registration, _registration) || !admission.IsCurrent)
                    continue;
                NativeDockTabPayload tab = new(this, stack, revision, _dockMutationRevision,
                    panel, admission, windows);
                if (tab.IsSourceCurrent && EditorDragDropUtility.BeginNativeDockDrag(source,
                        new EditorDragDropUtility.DragPayload(NativeDockTabPayload.DragType, tab),
                        startPoint, admission.Backend, () => tab.IsSourceCurrent))
                    return;
                break;
            }
        }
        NativeDockLocalTabPayload local = new(this, stack, revision,
            _dockMutationRevision, panel, ObjectConstructionIdentity.Capture(_root));
        if (IsCurrentLocalDockDrag(local))
            EditorDragDropUtility.BeginDrag(source,
                new EditorDragDropUtility.DragPayload(NativeDockLocalTabPayload.DragType, local),
                startPoint);
    }

    private bool CanDropLocalTab(EditorDragDropUtility.DragPayload payload,
        NativeDockNode stack, ulong revision)
    {
        NativeDockLocalTabPayload? tab = NativeDockLocalTabPayload.Read(payload);
        return tab is not null && ReferenceEquals(tab.Source, this) &&
            IsCurrentStack(stack, revision) && IsCurrentLocalDockDrag(tab);
    }

    private bool TryDropLocalTab(EditorDragDropUtility.DragPayload payload,
        NativeDockNode stack, ulong revision)
    {
        NativeDockLocalTabPayload? tab = NativeDockLocalTabPayload.Read(payload);
        if (tab is null || !CanDropLocalTab(payload, stack, revision) ||
            !_tree.Move(tab.Panel, stack.Id, int.MaxValue))
            return false;
        CommitStructure();
        return true;
    }

    private bool CanDropTab(EditorDragDropUtility.DragPayload payload,
        NativeDockNode stack, ulong revision)
    {
        NativeDockTabPayload? tab = NativeDockTabPayload.Read(payload);
        if (!IsCurrentStack(stack, revision) || tab is null ||
            !tab.IsSourceCurrent || !NativeDockPanel.All.Contains(tab.Panel) ||
            _window?.DesktopWindowBackend is not { } backend ||
            !tab.TryGetWindow(backend.OperatingSystemWindowHandle,
                out NativeDockDragWindowAdmission admission) ||
            !ReferenceEquals(admission.Registration, _registration) ||
            !ReferenceEquals(admission.Window, _window))
            return false;
        return ReferenceEquals(tab.Source, this) ||
            !ReferenceEquals(tab.SourceWindow.Context, null) &&
            ReferenceEquals(tab.SourceWindow.Context, admission.Context) &&
            _registration.CanReceive(tab.Panel);
    }

    private bool TryHitDockTarget(EditorDragDropUtility.DragPayload payload,
        NativeDockNode stack, ulong revision, UIButtonComponent probe,
        UIListTransform probeTransform, ObjectConstructionIdentity probeIdentity,
        ObjectConstructionIdentity probeNodeIdentity,
        ObjectConstructionIdentity probeTransformIdentity,
        EditorDesktopDropPoint point)
    {
        NativeDockTabPayload? tab = NativeDockTabPayload.Read(payload);
        if (!CanDropTab(payload, stack, revision) || tab is null ||
            !probeIdentity.IsCurrent || !probeNodeIdentity.IsCurrent ||
            !probeTransformIdentity.IsCurrent ||
            probe.IsDestroyed || probe.IsDestroyQueued ||
            probe.SceneNode is not { IsDestroyed: false, IsDestroyQueued: false } probeNode ||
            !ReferenceEquals(probeNode, probeNodeIdentity.Target) ||
            !ReferenceEquals(probeNode.Transform, probeTransform) ||
            !tab.TryGetWindow(point.TargetWindowHandle,
                out NativeDockDragWindowAdmission admission) ||
            !ReferenceEquals(admission.Registration, _registration) ||
            admission.Backend is not IRuntimeDesktopDragPointerSource pointerSource ||
            probeTransform.GetCanvasComponent() is not { } canvas ||
            canvas.GetInputComponent() is not UICanvasInputComponent input ||
            input.OwningPawn?.Viewport is not XRViewport viewport ||
            !ReferenceEquals(viewport.Window, _window))
            return false;
        ObjectConstructionIdentity canvasIdentity = ObjectConstructionIdentity.Capture(canvas);
        ObjectConstructionIdentity inputIdentity = ObjectConstructionIdentity.Capture(input);
        RuntimeDesktopDragPointerSnapshot snapshot = pointerSource.ReadDesktopDragPointer();
        if (!admission.IsCurrent || snapshot.BackendLifetime != admission.BackendLifetime ||
            snapshot.SourceWindowHandle != admission.WindowHandle ||
            !snapshot.Surface.HasValidClientExtent)
            return false;
        Vector2 origin = point.IsRelease ? point.TargetClientScreenPosition :
            snapshot.ClientScreenPosition;
        Vector2 extent = point.IsRelease ? point.TargetClientSize :
            new Vector2(snapshot.Surface.ClientWidth, snapshot.Surface.ClientHeight);
        if (point.IsRelease &&
            (origin != snapshot.ClientScreenPosition ||
             extent.X != snapshot.Surface.ClientWidth ||
             extent.Y != snapshot.Surface.ClientHeight))
            return false;
        bool hit = input.TryGetPresentedDropRegion(probe,
            point.ScreenPosition - origin, extent, out _);
        RuntimeDesktopDragPointerSnapshot after = pointerSource.ReadDesktopDragPointer();
        return hit && probeIdentity.IsCurrent && probeNodeIdentity.IsCurrent &&
            probeTransformIdentity.IsCurrent &&
            canvasIdentity.IsCurrent && inputIdentity.IsCurrent &&
            !canvas.IsDestroyed && !canvas.IsDestroyQueued &&
            !input.IsDestroyed && !input.IsDestroyQueued &&
            ReferenceEquals(probe.SceneNode, probeNode) &&
            ReferenceEquals(probeNode.Transform, probeTransform) &&
            ReferenceEquals(probeTransform.GetCanvasComponent(), canvas) &&
            ReferenceEquals(canvas.GetInputComponent(), input) &&
            ReferenceEquals(input.OwningPawn?.Viewport, viewport) &&
            ReferenceEquals(viewport.Window, _window) &&
            admission.IsCurrent && tab.IsSourceCurrent &&
            after.BackendLifetime == admission.BackendLifetime &&
            after.SourceWindowHandle == admission.WindowHandle &&
            after.ClientScreenPosition == origin &&
            after.Surface.ClientWidth == extent.X &&
            after.Surface.ClientHeight == extent.Y &&
            IsCurrentStack(stack, revision);
    }

    private bool TryDropTab(EditorDragDropUtility.DragPayload payload,
        NativeDockNode stack, ulong revision)
    {
        NativeDockTabPayload? tab = NativeDockTabPayload.Read(payload);
        if (!CanDropTab(payload, stack, revision) || tab is null)
            return false;
        if (ReferenceEquals(tab.Source, this))
        {
            if (!_tree.Move(tab.Panel, stack.Id, int.MaxValue))
                return false;
            CommitStructure();
            return true;
        }
        if (_window?.DesktopWindowBackend is not { } backend ||
            !tab.TryGetWindow(backend.OperatingSystemWindowHandle,
                out NativeDockDragWindowAdmission destination) ||
            !ReferenceEquals(destination.Registration, _registration) ||
            NativeDockWindowRegistry.TransferToWindow(tab.SourceWindow,
                destination, tab.Panel, stack.Id) != NativeDockTransferResult.Committed)
            return false;
        return tab.Source.CloseTransferredTab(tab);
    }

    private bool CloseTransferredTab(NativeDockTabPayload tab)
    {
        if (!tab.IsSourceCurrent)
            return false;
        _panelSlots.TryGetValue(tab.Panel, out SceneNode? oldSlot);
        NativeDockClosedSlotReceipt receipt = NativeDockClosedSlotReceipt.Capture(
            tab.Panel, oldSlot, _root, _registration, unchecked(_dockMutationRevision + 1));
        if (!_tree.Close(tab.Panel))
            return false;
        CommitStructure();
        ReleaseClosedPanelSlot(receipt, tab.SourceWindow);
        return true;
    }

    private void ReleaseClosedPanelSlot(NativeDockClosedSlotReceipt receipt,
        NativeDockDragWindowAdmission? sourceWindow = null)
    {
        SceneNode? slot = receipt.Slot;
        TransformBase? transform = receipt.Transform;
        if (slot is null || transform is null)
            return;
        // The source check below stays separate from the slot check. A transfer callback
        // can change either owner before the old slot is released.
        bool Current() => !_disposed && !slot.IsDestroyed &&
            !slot.IsDestroyQueued && !transform.IsDestroyed && !transform.IsDestroyQueued &&
            receipt.RootIdentity.IsCurrent && receipt.RegistrationIdentity.IsCurrent &&
            receipt.SlotIdentity?.IsCurrent == true &&
            receipt.TransformIdentity?.IsCurrent == true &&
            _root.IsActiveInHierarchy && !_root.IsDestroyed && !_root.IsDestroyQueued &&
            _registration.RegistrationRevision == receipt.RegistrationRevision &&
            _dockMutationRevision == receipt.ExpectedDockRevision &&
            (sourceWindow is null || sourceWindow.Value.IsCurrent) &&
            ReferenceEquals(slot.Transform, transform) && transform.Parent is null &&
            transform.World is null && !slot.IsActiveSelf &&
            _tree.ClosedPanels.Contains(receipt.Panel) &&
            !_tree.Stacks().Any(stack => stack.Tabs.Contains(receipt.Panel)) &&
            _panelSlots.TryGetValue(receipt.Panel, out SceneNode? current) &&
            ReferenceEquals(current, slot);
        if (!Current())
            return;
        try { NativeDockClosedSlotRetirement.TryDestroy(_panelSlots, receipt.Panel, slot); }
        catch (Exception exception)
        {
            Debug.LogException(exception, "[NativeDock] Could not release the transferred panel slot.");
        }
    }

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
            if (!slot.IsDestroyed && !slot.HasDestructionAdmission &&
                slot.Transform.Parent is null)
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
        UIButtonComponent dropProbe = tabsNode.AddComponent<UIButtonComponent>()!;
        dropProbe.IsEnabled = false;
        dropProbe.IsTabStop = false;
        dropProbe.AccessibilityHidden = true;
        tabList.BlocksInputBehind = false;
        ObjectConstructionIdentity probeIdentity = ObjectConstructionIdentity.Capture(dropProbe);
        ObjectConstructionIdentity probeNodeIdentity = ObjectConstructionIdentity.Capture(tabsNode);
        ObjectConstructionIdentity probeTransformIdentity = ObjectConstructionIdentity.Capture(tabList);
        _dropRegistrations.Add(EditorDragDropUtility.RegisterNativeDockTarget(tabList,
            payload => TryDropTab(payload, stack, revision),
            payload => CanDropTab(payload, stack, revision),
            (payload, point) =>
                TryHitDockTarget(payload, stack, revision, dropProbe, tabList,
                    probeIdentity, probeNodeIdentity, probeTransformIdentity, point)));
        _dropRegistrations.Add(EditorDragDropUtility.RegisterDropTarget(tabList,
            payload => TryDropLocalTab(payload, stack, revision),
            payload => CanDropLocalTab(payload, stack, revision)));
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
                BeginTabDrag(source, stack, revision, panel, point);
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
        var rootTransformIdentity = ObjectConstructionIdentity.Capture(_root.Transform);
        var registrationIdentity = ObjectConstructionIdentity.Capture(_registration);
        _panelSlots.TryGetValue(panel, out SceneNode? predecessor);
        ObjectConstructionIdentity? predecessorIdentity = predecessor is null ? null :
            ObjectConstructionIdentity.Capture(predecessor);
        var slotParent = slot.Transform.Parent;
        ObjectConstructionIdentity? slotParentIdentity = slotParent is null ? null :
            ObjectConstructionIdentity.Capture(slotParent);
        ulong registrationRevision = _registration.RegistrationRevision;
        ulong dockRevision = _dockMutationRevision;
        ulong renderRevision = _renderGate.Revision;
        bool InitialCurrent()
        {
            if (_disposed || !rootIdentity.IsCurrent || !rootTransformIdentity.IsCurrent ||
                !registrationIdentity.IsCurrent || !_renderGate.IsRendering ||
                _renderGate.Revision != renderRevision ||
                _dockMutationRevision != dockRevision ||
                _registration.RegistrationRevision != registrationRevision ||
                !_root.IsActiveInHierarchy || _root.IsDestroyed || _root.IsDestroyQueued ||
                !ReferenceEquals(_root.World, _world) ||
                !ReferenceEquals(_registration.SceneNode, _root) ||
                !_registration.TryGetRegistration(out string key, out object? world) ||
                key != _windowKey || !ReferenceEquals(world, _world) ||
                slotParentIdentity?.IsCurrent != true ||
                !ReferenceEquals(slot.Transform.Parent, slotParent) ||
                !IsExpectedInitialSlotMapping(_panelSlots, panel, slot,
                    predecessor, predecessorIdentity))
                return false;
            for (TransformBase? ancestor = slotParent; ancestor is not null;
                 ancestor = ancestor.Parent)
                if (ReferenceEquals(ancestor, _root.Transform))
                    return true;
            return false;
        }
        NativeToolPanelStateBinding? binding = NativeToolPanelState.Bind(component, slot,
            _layout, panel, Save,
            () => _layoutSaves?.Request(),
            () => !_disposed &&
                rootIdentity.IsCurrent && !_root.IsDestroyed && !_root.IsDestroyQueued &&
                _panelSlots.TryGetValue(panel, out SceneNode? current) && ReferenceEquals(current, slot),
            InitialCurrent);
        if (binding is null)
            throw new InvalidOperationException("The tool panel lost its owner during binding.");
        _toolStateBindings[panel] = binding;
    }

    internal static bool IsExpectedInitialSlotMapping(
        IReadOnlyDictionary<string, SceneNode> slots, string panel, SceneNode slot,
        SceneNode? predecessor, ObjectConstructionIdentity? predecessorIdentity)
    {
        if (!slots.TryGetValue(panel, out SceneNode? mapped) ||
            ReferenceEquals(mapped, slot))
            return true;
        return ReferenceEquals(mapped, predecessor) &&
            predecessorIdentity?.IsCurrent == true && mapped.IsDestroyed;
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
        ObjectConstructionIdentity rootIdentity = ObjectConstructionIdentity.Capture(_root);
        TransformBase rootTransform = _root.Transform;
        ObjectConstructionIdentity rootTransformIdentity =
            ObjectConstructionIdentity.Capture(rootTransform);
        ObjectConstructionIdentity registrationIdentity = ObjectConstructionIdentity.Capture(_registration);
        NativeDockTransferResult result = NativeDockWindowRegistry.TransferToAnotherWindow(_registration, panel);
        if (result == NativeDockTransferResult.Rejected)
            Debug.UI("[NativeDock] Panel transfer could not be confirmed.");
        else if (result == NativeDockTransferResult.NoTarget)
            Debug.UI("[NativeDock] Open another native editor window in this world, then retry the panel transfer.");
        if (result == NativeDockTransferResult.Committed && rootIdentity.IsCurrent &&
            rootTransformIdentity.IsCurrent && ReferenceEquals(_root.Transform, rootTransform) &&
            registrationIdentity.IsCurrent && !_disposed && !_root.IsDestroyed &&
            !_root.IsDestroyQueued && _root.IsActiveInHierarchy && ReferenceEquals(_root.World, _world) &&
            _registration.RegistrationRevision == registrationRevision &&
            _dockMutationRevision == sourceRevision && source.Selected == panel &&
            _tree.Stacks().Any(stack => ReferenceEquals(stack, source) && stack.Tabs.Contains(panel)))
        {
            _panelSlots.TryGetValue(panel, out SceneNode? oldSlot);
            NativeDockClosedSlotReceipt receipt = NativeDockClosedSlotReceipt.Capture(
                panel, oldSlot, _root, _registration, unchecked(_dockMutationRevision + 1));
            if (!_tree.Close(panel))
                return;
            CommitStructure();
            ReleaseClosedPanelSlot(receipt);
        }
    }

    private bool ReceivePanel(string panel) => ReceivePanelCore(panel, null);

    private NativeDockPanelTransferState? CapturePanelState(string panel)
    {
        if (_disposed || !NativeDockPanel.All.Contains(panel) ||
            _root.IsDestroyed || _root.IsDestroyQueued || !_root.IsActiveInHierarchy ||
            !_tree.Stacks().Any(stack => stack.Tabs.Contains(panel)))
            return null;
        ObjectConstructionIdentity rootIdentity = ObjectConstructionIdentity.Capture(_root);
        TransformBase rootTransform = _root.Transform;
        ObjectConstructionIdentity rootTransformIdentity =
            ObjectConstructionIdentity.Capture(rootTransform);
        ObjectConstructionIdentity registrationIdentity =
            ObjectConstructionIdentity.Capture(_registration);
        ulong registrationRevision = _registration.RegistrationRevision;
        ulong dockRevision = _dockMutationRevision;
        bool HostCurrent() => !_disposed && rootIdentity.IsCurrent &&
            rootTransformIdentity.IsCurrent && ReferenceEquals(_root.Transform, rootTransform) &&
            registrationIdentity.IsCurrent && !_root.IsDestroyed &&
            !_root.IsDestroyQueued && _root.IsActiveInHierarchy &&
            ReferenceEquals(_root.World, _world) &&
            ReferenceEquals(_registration.SceneNode, _root) &&
            _registration.TryGetRegistration(out string currentKey, out object? currentWorld) &&
            currentKey == _windowKey && ReferenceEquals(currentWorld, _world) &&
            _registration.RegistrationRevision == registrationRevision &&
            _dockMutationRevision == dockRevision &&
            _tree.Stacks().Any(stack => stack.Tabs.Contains(panel));
        if (_panelSlots.TryGetValue(panel, out SceneNode? slot))
        {
            if (NativeDockPanelStateValues.ScrollIndex(panel) < 0)
            {
                ObjectConstructionIdentity slotIdentity = ObjectConstructionIdentity.Capture(slot);
                NativeDockPanelStateValues untracked =
                    NativeDockPanelStateValues.Capture(_layout, panel);
                return new(untracked, () => HostCurrent() && slotIdentity.IsCurrent &&
                    _panelSlots.TryGetValue(panel, out SceneNode? current) &&
                    ReferenceEquals(current, slot));
            }
            if (!_toolStateBindings.TryGetValue(panel, out NativeToolPanelStateBinding? binding) ||
                !ReferenceEquals(binding.Slot, slot) || !binding.IsCurrent)
                return null;
            NativeDockPanelStateReceipt receipt = binding.CaptureReceipt();
            if (panel == NativeDockPanel.Animation && receipt.AnimationView is null)
                return null;
            return new(receipt.Values, () => HostCurrent() && receipt.IsCurrent,
                receipt.AnimationView?.Values);
        }
        if (panel == NativeDockPanel.Animation)
            return null;
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(_layout, panel);
        return new(values, () => HostCurrent() && !_panelSlots.ContainsKey(panel) &&
            values.Matches(_layout));
    }

    private bool ReceivePanelWithState(string panel, NativeDockPanelTransferState state)
        => ReceivePanelCore(panel, null, state);

    private bool ReceivePanelAtStackWithState(string panel, string stackId,
        NativeDockPanelTransferState state)
        => ReceivePanelCore(panel, stackId, state);

    private bool ReceivePanelAtStack(string panel, string stackId)
        => ReceivePanelCore(panel, stackId);

    private bool ReceivePanelCore(string panel, string? targetStackId,
        NativeDockPanelTransferState? transferState = null)
    {
        ObjectConstructionIdentity rootIdentity = ObjectConstructionIdentity.Capture(_root);
        TransformBase rootTransform = _root.Transform;
        ObjectConstructionIdentity rootTransformIdentity =
            ObjectConstructionIdentity.Capture(rootTransform);
        ObjectConstructionIdentity registrationIdentity = ObjectConstructionIdentity.Capture(_registration);
        ulong registrationRevision = _registration.RegistrationRevision;
        ulong priorRevision = _dockMutationRevision;
        if (_receivingPanel || _disposed || _world is null || !_root.IsActiveInHierarchy ||
            _root.IsDestroyed || _root.IsDestroyQueued || !ReferenceEquals(_root.World, _world) ||
            !_registration.TryGetRegistration(out string registeredKey, out object? registeredWorld) ||
            registeredKey != _windowKey || !ReferenceEquals(registeredWorld, _world) ||
            !NativeDockPanel.All.Contains(panel))
            return false;
        if (transferState is { } incoming &&
            (incoming.Values.Panel != panel ||
             incoming.AnimationView is not null && panel != NativeDockPanel.Animation ||
             panel == NativeDockPanel.Animation && incoming.AnimationView is null ||
             !incoming.IsCurrent ||
             _disposed || !_root.IsActiveInHierarchy ||
             _root.IsDestroyed || _root.IsDestroyQueued ||
             !rootIdentity.IsCurrent || !rootTransformIdentity.IsCurrent ||
             !ReferenceEquals(_root.Transform, rootTransform) ||
             !registrationIdentity.IsCurrent ||
             _dockMutationRevision != priorRevision ||
             _registration.RegistrationRevision != registrationRevision ||
             !ReferenceEquals(_registration.SceneNode, _root) ||
             !ReferenceEquals(_root.World, _world) ||
             !_registration.TryGetRegistration(out registeredKey, out registeredWorld) ||
             registeredKey != _windowKey || !ReferenceEquals(registeredWorld, _world)))
            return false;
        NativeDockNode priorRoot = CopyDockNode(_tree.Root);
        List<string> priorClosed = [.. _tree.ClosedPanels];
        NativeDockPanelStateValues destinationBefore =
            NativeDockPanelStateValues.Capture(_layout, panel);
        NativeDockPanelStateReceipt? destinationReceipt = null;
        NativeAnimationPanelViewValues? destinationBeforeView = null;
        if (transferState is not null && _panelSlots.TryGetValue(panel, out SceneNode? priorSlot))
        {
            if (NativeDockPanelStateValues.ScrollIndex(panel) >= 0)
            {
                if (!_toolStateBindings.TryGetValue(panel, out NativeToolPanelStateBinding? priorBinding) ||
                    !ReferenceEquals(priorBinding.Slot, priorSlot) || !priorBinding.IsCurrent)
                    return false;
                destinationReceipt = priorBinding.CaptureReceipt();
                destinationBefore = destinationReceipt.Value.Values;
                if (priorBinding.Panel is AnimationPanel animation)
                    destinationBeforeView = animation.CapturePriorView();
            }
        }
        bool stateMutationStarted = false;
        NativeDockPanelStateReceipt? appliedState = null;
        NativeDockNode? target = null;
        SceneNode? receivedSlot = null;
        NativeToolPanelStateBinding? receivedBinding = null;
        bool DestinationCurrent() => !_disposed && rootIdentity.IsCurrent &&
            rootTransformIdentity.IsCurrent && ReferenceEquals(_root.Transform, rootTransform) &&
            registrationIdentity.IsCurrent && _root.IsActiveInHierarchy &&
            !_root.IsDestroyed && !_root.IsDestroyQueued &&
            ReferenceEquals(_registration.SceneNode, _root) &&
            ReferenceEquals(_root.World, _world) &&
            _registration.RegistrationRevision == registrationRevision &&
            _registration.TryGetRegistration(out string currentKey, out object? currentWorld) &&
            currentKey == _windowKey && ReferenceEquals(currentWorld, _world) &&
            _dockMutationRevision == priorRevision + 1 && target is not null &&
            _tree.Stacks().Any(stack => ReferenceEquals(stack, target) &&
                stack.Tabs.Contains(panel)) &&
            (receivedSlot is null || _panelSlots.TryGetValue(panel, out SceneNode? currentSlot) &&
                ReferenceEquals(currentSlot, receivedSlot)) &&
            (receivedBinding is null || _toolStateBindings.TryGetValue(panel,
                out NativeToolPanelStateBinding? currentBinding) &&
                ReferenceEquals(currentBinding, receivedBinding) && currentBinding.IsCurrent);
        _receivingPanel = true;
        try
        {
            target = targetStackId is null
                ? _tree.Stacks().FirstOrDefault(stack =>
                    stack.Tabs.Any(item => NativeDockPanel.LegacyTools.Contains(item))) ??
                    _tree.Stacks().FirstOrDefault()
                : _tree.Stacks().FirstOrDefault(stack => stack.Id == targetStackId);
            if (target is null)
                return false;
            if (!_tree.Move(panel, target.Id, int.MaxValue))
                return false;
            CommitStructure();
            if (transferState is { } state &&
                NativeDockPanelStateValues.ScrollIndex(panel) >= 0)
            {
                if (!DestinationCurrent() || !state.IsCurrent || !DestinationCurrent() ||
                    !_panelSlots.TryGetValue(panel, out SceneNode? slot) ||
                    !_toolStateBindings.TryGetValue(panel, out NativeToolPanelStateBinding? binding) ||
                    !ReferenceEquals(binding.Slot, slot) || !binding.IsCurrent)
                {
                    RestoreReceiveSnapshot(priorRoot, priorClosed, priorRevision + 1,
                        registrationRevision, rootIdentity, rootTransform,
                        rootTransformIdentity, registrationIdentity);
                    return false;
                }
                receivedSlot = slot;
                receivedBinding = binding;
                if (destinationReceipt is { } prior &&
                    (!ReferenceEquals(prior.Binding, binding) ||
                     binding.Panel.StateIntentRevision != prior.IntentRevision))
                {
                    RestoreReceiveSnapshot(priorRoot, priorClosed, priorRevision + 1,
                        registrationRevision, rootIdentity, rootTransform,
                        rootTransformIdentity, registrationIdentity);
                    return false;
                }
                bool ForwardCurrent() => DestinationCurrent() && state.IsCurrent &&
                    DestinationCurrent();
                if (!ForwardCurrent())
                {
                    if (DestinationCurrent())
                        RestoreReceiveSnapshot(priorRoot, priorClosed, priorRevision + 1,
                            registrationRevision, rootIdentity, rootTransform,
                            rootTransformIdentity, registrationIdentity);
                    return false;
                }
                stateMutationStarted = true;
                NativeDockPanelStateReceipt applied = binding.Restore(
                    state.Values, ForwardCurrent, state.AnimationView);
                appliedState = applied;
                if (!applied.IsCurrent || !ForwardCurrent())
                    return false;
                if (!DestinationCurrent() || !applied.IsCurrent)
                    return false;
                applied.Values.WriteTo(_layout);
                if (!DestinationCurrent() || !applied.IsCurrent)
                    return false;
                Save();
                if (!DestinationCurrent() || !applied.IsCurrent || !ForwardCurrent() ||
                    !applied.Values.Matches(_layout))
                    return false;
            }
            if (DestinationCurrent())
                return true;
            if (!stateMutationStarted)
                RestoreReceiveSnapshot(priorRoot, priorClosed, priorRevision + 1,
                    registrationRevision, rootIdentity, rootTransform,
                    rootTransformIdentity, registrationIdentity);
            return false;
        }
        catch (Exception exception)
        {
            if (appliedState is { IsCurrent: true } applied && DestinationCurrent() &&
                (panel != NativeDockPanel.Animation ||
                 destinationBeforeView is { } priorView &&
                 applied.Binding.Panel is AnimationPanel animation &&
                 animation.CanRestorePriorView(priorView)) &&
                applied.Binding.IsCurrent)
            {
                try
                {
                    NativeDockPanelStateReceipt restored =
                        applied.Binding.Restore(destinationBefore, DestinationCurrent,
                            destinationBeforeView);
                    if (restored.IsCurrent && DestinationCurrent())
                    {
                        restored.Values.WriteTo(_layout);
                        if (!DestinationCurrent() || !restored.IsCurrent)
                            throw new InvalidOperationException("The destination changed during compensation.");
                        Save();
                        if (DestinationCurrent() && restored.IsCurrent &&
                            restored.Values.Matches(_layout))
                            stateMutationStarted = false;
                    }
                }
                catch (Exception compensationFailure)
                {
                    Debug.LogException(compensationFailure,
                        "[NativeDock] Destination panel state could not be restored.");
                }
            }
            ulong admittedRevision = _dockMutationRevision == priorRevision
                ? priorRevision : priorRevision + 1;
            if (!stateMutationStarted)
                RestoreReceiveSnapshot(priorRoot, priorClosed, admittedRevision,
                    registrationRevision, rootIdentity, rootTransform,
                    rootTransformIdentity, registrationIdentity);
            Debug.LogException(exception, "[NativeDock] Destination panel commit failed.");
            return false;
        }
        finally { _receivingPanel = false; }
    }

    private void RestoreReceiveSnapshot(NativeDockNode root, List<string> closedPanels,
        ulong admittedRevision, ulong registrationRevision,
        ObjectConstructionIdentity rootIdentity,
        TransformBase rootTransform,
        ObjectConstructionIdentity rootTransformIdentity,
        ObjectConstructionIdentity registrationIdentity)
    {
        if (_disposed || !rootIdentity.IsCurrent || !rootTransformIdentity.IsCurrent ||
            !ReferenceEquals(_root.Transform, rootTransform) ||
            !registrationIdentity.IsCurrent ||
            _root.IsDestroyed || _root.IsDestroyQueued || !_root.IsActiveInHierarchy ||
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
