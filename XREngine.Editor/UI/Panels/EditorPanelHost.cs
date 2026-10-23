using System.Numerics;
using XREngine.Components;
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
            SceneNode handleNode = sceneNode.NewChild<UIButtonComponent>(out var handle, "SceneSplitter");
            UIBoundableTransform handleTransform = handleNode.GetTransformAs<UIBoundableTransform>(true)!;
            handleTransform.MinAnchor = Vector2.Zero;
            handleTransform.MaxAnchor = new Vector2(1, 0);
            handleTransform.Height = 10;
            handleTransform.BlocksInputBehind = true;
            handle.NeedsMouseMove = true;
            float? dragStartY = null;
            float dragStartSize = layout.BottomSize;
            handle.MouseMove += (x, y, component) =>
            {
                if (!EditorDragDropUtility.IsLeftMouseButtonHeldFor(component))
                {
                    if (dragStartY.HasValue)
                        Save();
                    dragStartY = null;
                    return;
                }
                float canvasY = component.BoundableTransform.LocalToCanvas(new Vector2(x, y)).Y;
                if (!dragStartY.HasValue)
                {
                    dragStartY = canvasY;
                    dragStartSize = layout.BottomSize;
                }
                layout.BottomSize = Math.Clamp(dragStartSize + canvasY - dragStartY.Value, 96, 2000);
                sceneSplit.FixedSize = layout.BottomSize;
            };
            handle.MouseDirectOverlapLeave += _ =>
            {
                if (dragStartY.HasValue)
                    Save();
                dragStartY = null;
            };
            toolsParent = splitNode;
        }

        SceneNode toolsNode = toolsParent.NewChildWithTransform(out UIDualSplitTransform toolsSplit, "ToolPanels");
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
        assetsPanel.UseEngineAssets = layout.UseEngineAssets;
        assetsPanel.Folder = layout.AssetFolder;
        assetsPanel.Search = layout.AssetSearch;
        consolePanel.Filter = layout.ConsoleFilter;
        for (int i = 0; i < panelComponents.Length; i++)
        {
            int panelIndex = i;
            panelComponents[i].ScrollOffset = layout.PanelScrollOffsets[i];
            panelComponents[i].ScrollOffsetChanged += offset =>
            {
                layout.PanelScrollOffsets[panelIndex] = offset;
                Save();
            };
        }
        assetsPanel.ViewStateChanged += () =>
        {
            layout.UseEngineAssets = assetsPanel.UseEngineAssets;
            layout.AssetFolder = assetsPanel.Folder;
            layout.AssetSearch = assetsPanel.Search;
            Save();
        };
        consolePanel.FilterChanged += filter => { layout.ConsoleFilter = filter; Save(); };

        string[] titles = ["Assets", "Console", "Profiler", "Animation"];
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
        toolsNode.AddComponent<NativeDockWindowRegistration>()!.Configure(windowKey, Select);
        Refresh();

        void Select(int index)
        {
            layout.OpenMask |= 1 << index;
            layout.SelectedTab = index;
            Refresh();
            Save();
        }

        void CloseSelected()
        {
            if (layout.SelectedTab < 0)
                return;
            layout.OpenMask &= ~(1 << layout.SelectedTab);
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
            (layout.TabOrder[slot], layout.TabOrder[destination]) =
                (layout.TabOrder[destination], layout.TabOrder[slot]);
            Refresh();
            Save();
        }

        void MoveToAnotherWindow()
        {
            if (layout.SelectedTab >= 0 && NativeDockWindowRegistry.TryMoveToAnotherWindow(windowKey, layout.SelectedTab))
                CloseSelected();
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

        void Save() => NativeDockLayoutStore.Save(windowKey, layout);
    }

    private static XRWindow? ResolveWindow(SceneNode node)
    {
        UICanvasComponent? canvas = node.GetTransformAs<UIBoundableTransform>(true)?.GetCanvasComponent();
        foreach (XRWindow window in RuntimeEngine.Windows)
            foreach (XRViewport viewport in window.Viewports)
                if (ReferenceEquals(viewport.ScreenSpaceUserInterfaceOverride, canvas) ||
                    ReferenceEquals(viewport.CameraComponent?.GetUserInterfaceOverlay(), canvas))
                    return window;
        return RuntimeEngine.Windows.FirstOrDefault();
    }

    private static string GetWindowKey(XRWindow? target)
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
