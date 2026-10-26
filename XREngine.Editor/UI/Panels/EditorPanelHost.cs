using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Editor.UI.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Mounts the native scene area and its tool panels.</summary>
public static class EditorPanelHost
{
    public static void Build(SceneNode middleNode)
    {
        XRWindow? window = ResolveWindow(middleNode);
        BuildCore(middleNode, window, null, false, null);
        if (window is not null)
            NativeDetachedDockWindows.Restore(window);
    }

    internal static void BuildDetached(SceneNode root, XRWindow window, string windowKey, int selectedPanel)
        => BuildCore(root, window, windowKey, true, selectedPanel);

    private static void BuildCore(SceneNode middleNode, XRWindow? window, string? keyOverride,
        bool toolsOnly, int? initialPanel)
    {
        string windowKey = keyOverride ?? GetWindowKey(window);
        NativeDockLayout layout = NativeDockLayoutStore.Load(windowKey);
        if (initialPanel is >= 0 and < 4 && layout.OpenMask == 15 && layout.SelectedTab == 0)
        {
            layout.OpenMask = 1 << initialPanel.Value;
            layout.SelectedTab = initialPanel.Value;
        }
        UIDualSplitTransform? split = null;
        NativeLayoutSaveDebouncer? saveDebouncer = null;
        SceneNode toolsParent = middleNode;
        if (!toolsOnly)
        {
            SceneNode splitNode = middleNode.NewChildWithTransform(out UIDualSplitTransform sceneSplit, "SceneAndTools");
            split = sceneSplit;
            split.VerticalSplit = true;
            split.FirstFixedSize = false;
            split.FixedSize = layout.BottomSize;
            split.SplitterSize = 5;
            split.CanUserResize = true;
            SceneNode sceneNode = splitNode.NewChildWithTransform<UIBoundableTransform>(out _, "SceneView");
            sceneNode.NewChild<SceneViewPanel>(out var sceneView);
            sceneView.Window = window;
            // The shared splitter handle owns drag capture, clamping, and keyboard steps.
            SceneNode handleNode = sceneNode.NewChild<UISplitterHandleComponent>(out var handle, "SceneSplitter");
            handleNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
            handle.SplitTarget = sceneSplit;
            handle.MinimumSize = 96;
            handle.MaximumSize = 2000;
            UIBoundableTransform handleTransform = handleNode.GetTransformAs<UIBoundableTransform>(true)!;
            handleTransform.MinAnchor = Vector2.Zero;
            handleTransform.MaxAnchor = new Vector2(1, 0);
            handleTransform.Height = 10;
            handleTransform.BlocksInputBehind = true;
            sceneSplit.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(UIDualSplitTransform.FixedSize) ||
                    sceneSplit.FixedSize is not { } size || size == layout.BottomSize)
                    return;
                layout.BottomSize = size;
                // A drag changes the size on each pointer move. Write it once after the drag settles.
                saveDebouncer?.Request();
            };
            toolsParent = splitNode;
        }

        SceneNode toolsNode = toolsParent.NewChildWithTransform(out UIDualSplitTransform toolsSplit, "ToolPanels");
        NativeLayoutSaveDebouncer layoutSaves = toolsNode.AddComponent<NativeLayoutSaveDebouncer>()!;
        saveDebouncer = layoutSaves;
        layoutSaves.Configure(Save);
        toolsSplit.VerticalSplit = true;
        toolsSplit.FirstFixedSize = true;
        toolsSplit.FixedSize = 60;
        toolsSplit.SplitterSize = 0;
        SceneNode commandRoot = toolsNode.NewChildWithTransform(out UIDualSplitTransform commandSplit, "ToolCommands");
        commandSplit.VerticalSplit = true;
        commandSplit.FirstFixedSize = true;
        commandSplit.FixedSize = 30;
        commandSplit.SplitterSize = 0;
        SceneNode tabsNode = commandRoot.NewChildWithTransform(out UIListTransform tabs, "Tabs");
        tabs.DisplayHorizontal = true;
        tabs.ItemAlignment = EListAlignment.TopOrLeft;
        tabs.ItemSize = 82;
        tabs.ItemSpacing = 2;
        UITabStripComponent tabStrip = tabsNode.AddComponent<UITabStripComponent>()!;
        tabStrip.TabWidth = 82;
        SceneNode actionsNode = commandRoot.NewChildWithTransform(out UIListTransform actions, "DockCommands");
        actions.DisplayHorizontal = true;
        actions.ItemAlignment = EListAlignment.TopOrLeft;
        actions.ItemSize = 64;
        actions.ItemSpacing = 2;
        SceneNode contentNode = toolsNode.NewChildWithTransform<UIBoundableTransform>(out _, "TabContent");

        SceneNode[] panels =
        [
            contentNode.NewChildWithTransform<UIBoundableTransform>(out _, "Assets"),
            contentNode.NewChildWithTransform<UIBoundableTransform>(out _, "Console"),
            contentNode.NewChildWithTransform<UIBoundableTransform>(out _, "Profiler"),
            contentNode.NewChildWithTransform<UIBoundableTransform>(out _, "Animation"),
        ];
        panels[0].NewChild<AssetsPanel>(out var assetsPanel);
        panels[1].NewChild<ConsolePanel>(out var consolePanel);
        panels[2].NewChild<ProfilerPanel>(out var profilerPanel);
        panels[3].NewChild<AnimationPanel>(out var animationPanel);
        NativeVirtualTextPanel[] panelComponents = [assetsPanel, consolePanel, profilerPanel, animationPanel];
        NativeToolPanelStateBinding?[] stateBindings = new NativeToolPanelStateBinding?[panelComponents.Length];
        for (int index = 0; index < panelComponents.Length; index++)
            stateBindings[index] = NativeToolPanelState.Bind(panelComponents[index], panels[index], layout,
                index, Save, layoutSaves.Request);

        string[] titles = ["Assets", "Console", "Profiler", "Animation"];
        NativeDockWindowRegistration windowRegistration =
            toolsNode.AddComponent<NativeDockWindowRegistration>()!;
        ulong panelSelectionRevision = 0;
        tabStrip.TabActivated += id => Select((int)id - 1);
        tabStrip.CloseRequested += _ => CloseSelected();
        tabStrip.MoveRequested += (_, direction) => MoveSelected(direction);
        NativeUIElements.CreateButton(actionsNode, "Close", _ => tabStrip.RequestCloseSelected(), width: 64, height: 28);
        NativeUIElements.CreateButton(actionsNode, "<", _ => tabStrip.RequestMoveSelected(-1), width: 64, height: 28);
        NativeUIElements.CreateButton(actionsNode, ">", _ => tabStrip.RequestMoveSelected(1), width: 64, height: 28);
        NativeUIElements.CreateButton(actionsNode, "To Window", _ => MoveToAnotherWindow(), width: 90, height: 28);
        NativeUIElements.CreateButton(actionsNode, "Detach", _ => DetachSelected(), width: 72, height: 28);
        if (!toolsOnly)
        {
            NativeUIElements.CreateButton(actionsNode, "-", _ => Resize(-40), width: 64, height: 28);
            NativeUIElements.CreateButton(actionsNode, "+", _ => Resize(40), width: 64, height: 28);
        }
        windowRegistration.Configure(windowKey, ReceivePanel, toolsNode.World as RuntimeWorld,
            capturePanelState: CapturePanelState, receivePanelState: ReceivePanelWithState);
        Refresh();

        NativeDockPanelTransferState? CapturePanelState(string panel)
        {
            int index = Array.IndexOf(NativeDockPanel.LegacyTools, panel);
            if ((uint)index >= (uint)stateBindings.Length ||
                layout.SelectedTab != index || (layout.OpenMask & (1 << index)) == 0 ||
                stateBindings[index] is not { IsCurrent: true } binding)
                return null;
            ObjectConstructionIdentity toolsIdentity = ObjectConstructionIdentity.Capture(toolsNode);
            ObjectConstructionIdentity registrationIdentity =
                ObjectConstructionIdentity.Capture(windowRegistration);
            ulong registrationRevision = windowRegistration.RegistrationRevision;
            ulong selectionRevision = panelSelectionRevision;
            object? ownerContext = toolsNode.World as RuntimeWorld;
            bool HostCurrent() => toolsIdentity.IsCurrent && registrationIdentity.IsCurrent &&
                !toolsNode.IsDestroyed && !toolsNode.IsDestroyQueued &&
                toolsNode.IsActiveInHierarchy &&
                ReferenceEquals(windowRegistration.SceneNode, toolsNode) &&
                windowRegistration.RegistrationRevision == registrationRevision &&
                windowRegistration.TryGetRegistration(out string currentKey,
                    out object? currentContext) && currentKey == windowKey &&
                ReferenceEquals(currentContext, ownerContext) &&
                ReferenceEquals(toolsNode.World, ownerContext) &&
                panelSelectionRevision == selectionRevision &&
                layout.SelectedTab == index && (layout.OpenMask & (1 << index)) != 0 &&
                ReferenceEquals(stateBindings[index], binding) && binding.IsCurrent;
            NativeDockPanelStateReceipt receipt = binding.CaptureReceipt();
            if (panel == NativeDockPanel.Animation && receipt.AnimationView is null)
                return null;
            if (!HostCurrent()) return null;
            return new(receipt.Values, () => HostCurrent() && receipt.IsCurrent,
                receipt.AnimationView?.Values);
        }

        bool ReceivePanelWithState(string panel, NativeDockPanelTransferState state)
        {
            ObjectConstructionIdentity toolsIdentity = ObjectConstructionIdentity.Capture(toolsNode);
            ObjectConstructionIdentity registrationIdentity =
                ObjectConstructionIdentity.Capture(windowRegistration);
            ulong registrationRevision = windowRegistration.RegistrationRevision;
            ulong startingSelectionRevision = panelSelectionRevision;
            object? ownerContext = toolsNode.World as RuntimeWorld;
            int index = Array.IndexOf(NativeDockPanel.LegacyTools, panel);
            if ((uint)index >= (uint)stateBindings.Length ||
                state.Values.Panel != panel ||
                state.AnimationView is not null && panel != NativeDockPanel.Animation ||
                panel == NativeDockPanel.Animation && state.AnimationView is null ||
                stateBindings[index] is not { IsCurrent: true } binding)
                return false;
            bool BeforeCurrent() => toolsIdentity.IsCurrent &&
                registrationIdentity.IsCurrent && !toolsNode.IsDestroyed &&
                !toolsNode.IsDestroyQueued && toolsNode.IsActiveInHierarchy &&
                ReferenceEquals(windowRegistration.SceneNode, toolsNode) &&
                ReferenceEquals(toolsNode.World, ownerContext) &&
                windowRegistration.RegistrationRevision == registrationRevision &&
                windowRegistration.TryGetRegistration(out string currentKey,
                    out object? currentContext) && currentKey == windowKey &&
                ReferenceEquals(currentContext, ownerContext) &&
                panelSelectionRevision == startingSelectionRevision &&
                ReferenceEquals(stateBindings[index], binding) && binding.IsCurrent;
            if (!BeforeCurrent() || !state.IsCurrent || !BeforeCurrent())
                return false;
            NativeDockPanelStateReceipt before = binding.CaptureReceipt();
            NativeAnimationPanelViewValues? priorAnimation =
                (binding.Panel as AnimationPanel)?.CapturePriorView();
            bool DestinationCurrent() => toolsIdentity.IsCurrent &&
                registrationIdentity.IsCurrent && !toolsNode.IsDestroyed &&
                !toolsNode.IsDestroyQueued && toolsNode.IsActiveInHierarchy &&
                ReferenceEquals(windowRegistration.SceneNode, toolsNode) &&
                ReferenceEquals(toolsNode.World, ownerContext) &&
                windowRegistration.RegistrationRevision == registrationRevision &&
                windowRegistration.TryGetRegistration(out string currentKey,
                    out object? currentContext) && currentKey == windowKey &&
                ReferenceEquals(currentContext, ownerContext) &&
                panelSelectionRevision == startingSelectionRevision + 1 &&
                layout.SelectedTab == index && (layout.OpenMask & (1 << index)) != 0 &&
                ReferenceEquals(stateBindings[index], binding) && binding.IsCurrent;
            using var searchReceive = (binding.Panel as AnimationPanel)?.BeginSearchReceive();
            if (!ReceivePanel(panel) || !DestinationCurrent() || !state.IsCurrent ||
                !DestinationCurrent() ||
                binding.Panel.StateIntentRevision != before.IntentRevision ||
                searchReceive is { IsCurrent: false })
                return false;
            bool ForwardCurrent() => DestinationCurrent() && state.IsCurrent &&
                DestinationCurrent();
            if (!ForwardCurrent()) return false;
            NativeDockPanelStateReceipt? retained = null;
            try
            {
                searchReceive?.Complete();
                NativeDockPanelStateReceipt applied = binding.Restore(
                    state.Values, ForwardCurrent, state.AnimationView, partial => retained = partial);
                retained = applied;
                if (!applied.IsCurrent || !ForwardCurrent() || !DestinationCurrent())
                    throw new InvalidOperationException("The transferred panel changed after restore.");
                applied.Values.WriteTo(layout);
                if (!DestinationCurrent() || !applied.IsCurrent)
                    throw new InvalidOperationException("The transferred panel changed during layout assignment.");
                Save();
                if (!DestinationCurrent() || !applied.IsCurrent || !ForwardCurrent() ||
                    !applied.Values.Matches(layout))
                    throw new InvalidOperationException("The transferred panel changed during save.");
                return true;
            }
            catch
            {
                if (retained is { IsCurrent: true } partial && DestinationCurrent() &&
                    priorAnimation is { } priorView && binding.Panel is AnimationPanel animation &&
                    animation.CanRestorePriorView(priorView))
                {
                    try
                    {
                        var restored = binding.Restore(before.Values, DestinationCurrent, priorView, restorePriorSearchFocus: true);
                        if (restored.IsCurrent && DestinationCurrent())
                        {
                            restored.Values.WriteTo(layout);
                            if (restored.IsCurrent && DestinationCurrent()) Save();
                        }
                    }
                    catch (Exception failure) { Debug.LogException(failure, "The prior Animation view could not be restored."); }
                }
                throw;
            }
        }

        bool ReceivePanel(string panel)
        {
            int index = Array.IndexOf(NativeDockPanel.LegacyTools, panel);
            if (index < 0 || toolsNode.IsDestroyed || !toolsNode.IsActiveInHierarchy)
                return false;
            Select(index);
            return !toolsNode.IsDestroyed && toolsNode.IsActiveInHierarchy &&
                layout.SelectedTab == index && (layout.OpenMask & (1 << index)) != 0;
        }

        void Select(int index)
        {
            panelSelectionRevision++;
            layout.OpenMask |= 1 << index;
            layout.SelectedTab = index;
            Refresh();
            Save();
        }

        void CloseSelected()
        {
            if (layout.SelectedTab < 0)
                return;
            ClosePanel(layout.SelectedTab);
        }

        void ClosePanel(int index)
        {
            panelSelectionRevision++;
            layout.OpenMask &= ~(1 << index);
            layout.Sanitize();
            Refresh();
            Save();
        }

        void MoveSelected(int direction)
        {
            int slot = Array.IndexOf(layout.TabOrder, layout.SelectedTab);
            int destination = slot + direction;
            if (slot < 0 || destination < 0 || destination >= layout.TabOrder.Length)
                return;
            panelSelectionRevision++;
            (layout.TabOrder[slot], layout.TabOrder[destination]) =
                (layout.TabOrder[destination], layout.TabOrder[slot]);
            Refresh();
            Save();
        }

        void MoveToAnotherWindow()
        {
            int index = layout.SelectedTab;
            if ((uint)index >= (uint)NativeDockPanel.LegacyTools.Length ||
                (layout.OpenMask & (1 << index)) == 0 ||
                !windowRegistration.TryGetRegistration(out string registeredKey, out object? ownerContext))
                return;
            ulong registrationRevision = windowRegistration.RegistrationRevision;
            NativeDockTransferResult result = NativeDockWindowRegistry.TransferToAnotherWindow(
                windowRegistration, NativeDockPanel.LegacyTools[index]);
            if (result == NativeDockTransferResult.Committed &&
                registrationRevision == windowRegistration.RegistrationRevision &&
                windowRegistration.TryGetRegistration(out string currentKey, out object? currentContext) &&
                currentKey == registeredKey && ReferenceEquals(currentContext, ownerContext) &&
                ReferenceEquals(toolsNode.World, ownerContext) && !toolsNode.IsDestroyed &&
                toolsNode.IsActiveInHierarchy && layout.SelectedTab == index &&
                (layout.OpenMask & (1 << index)) != 0)
                ClosePanel(index);
            else if (result == NativeDockTransferResult.Rejected)
                Debug.UI("[NativeDock] Panel transfer could not be confirmed.");
            else if (result == NativeDockTransferResult.NoTarget)
                Debug.UI("[NativeDock] Open another native editor window in this world, then retry the panel transfer.");
        }

        void DetachSelected()
        {
            if (layout.SelectedTab < 0 || window is null)
                return;
            if (NativeDetachedDockWindows.Open(window, layout.SelectedTab))
                CloseSelected();
        }

        void Resize(float delta)
        {
            if (split is null)
                return;
            layout.BottomSize = Math.Clamp(layout.BottomSize + delta, 96, 2000);
            split.FixedSize = layout.BottomSize;
            Save();
        }

        void Refresh()
        {
            UITabItem[] items = new UITabItem[panels.Length];
            for (int slot = 0; slot < items.Length; slot++)
            {
                int panel = layout.TabOrder[slot];
                items[slot] = new UITabItem((ulong)(panel + 1), titles[panel],
                    (layout.OpenMask & (1 << panel)) != 0);
            }
            tabStrip.SetTabs(items, layout.SelectedTab >= 0 ? (ulong)(layout.SelectedTab + 1) : null);
            for (int i = 0; i < panels.Length; i++)
            {
                bool visible = i == layout.SelectedTab;
                panels[i].GetTransformAs<UIBoundableTransform>(true)!.Visibility = visible
                    ? EVisibility.Visible : EVisibility.Hidden;
                panels[i].IsActiveSelf = visible;
            }
        }

        void Save()
        {
            saveDebouncer?.MarkSaved();
            NativeDockLayoutStore.Save(windowKey, layout);
        }
    }

    internal static XRWindow? ResolveWindow(SceneNode node)
    {
        UICanvasComponent? canvas = node.GetTransformAs<UIBoundableTransform>(true)?.GetCanvasComponent();
        foreach (XRWindow window in RuntimeEngine.Windows)
            foreach (XRViewport viewport in window.Viewports)
                if (ReferenceEquals(viewport.ScreenSpaceUserInterfaceOverride, canvas) ||
                    ReferenceEquals(viewport.CameraComponent?.GetUserInterfaceOverlay(), canvas))
                    return window;
        return RuntimeEngine.Windows.FirstOrDefault();
    }

    internal static string GetWindowKey(XRWindow? target)
    {
        int index = 0;
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            if (ReferenceEquals(window, target))
                return $"{index}:{window.WindowTitle}";
            index++;
        }
        return "primary";
    }
}
