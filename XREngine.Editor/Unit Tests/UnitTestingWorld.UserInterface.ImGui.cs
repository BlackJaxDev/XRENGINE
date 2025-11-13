using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Components.UI;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private const string HierarchyAddComponentPopupId = "HierarchyAddComponent";
        private static readonly List<ComponentTypeDescriptor> _componentTypeDescriptors = [];
        private static readonly byte[] _renameBuffer = new byte[256];
        private static readonly List<ComponentTypeDescriptor> _filteredComponentTypes = [];
        private static readonly Dictionary<int, ProfilerThreadCacheEntry> _profilerThreadCache = new();
        private static readonly Dictionary<string, bool> _profilerNodeOpenCache = new();
        private static readonly TimeSpan ProfilerThreadCacheTimeout = TimeSpan.FromSeconds(15.0);
        private static readonly TimeSpan ProfilerThreadStaleThreshold = TimeSpan.FromSeconds(10.0);

        private static bool _renameInputFocusRequested;
        private static bool _imguiStyleInitialized;
        private static bool _componentTypeCacheDirty = true;
        private static float _lastProfilerCaptureTime;

        private static SceneNode? _nodePendingRename;
        private static SceneNode? _nodePendingAddComponent;
        private static string _componentPickerSearch = string.Empty;
        private static string? _componentPickerError;

    private static bool _profilerDockLeftEnabled = true;
        private static bool _profilerUndockNextFrame;
        private static bool _profilerDockDragging;
        private static float _profilerDockWidth = 360.0f;
        private static float _profilerDockDragStartWidth = 360.0f;
        private static float _profilerDockDragStartMouseX;

    private static bool _inspectorDockRightEnabled = true;
    private static bool _inspectorUndockNextFrame;
    private static bool _inspectorDockDragging;
    private static float _inspectorDockWidth = 380.0f;
    private static float _inspectorDockDragStartWidth = 380.0f;
    private static float _inspectorDockDragStartMouseX;

        private static bool _assetExplorerDockBottomEnabled = true;
        private static bool _assetExplorerDockTopEnabled;
        private static bool _assetExplorerUndockNextFrame;
        private static bool _assetExplorerDockDragging;
        private static float _assetExplorerDockHeight = 320.0f;
        private static float _assetExplorerDockDragStartHeight = 320.0f;
        private static float _assetExplorerDockDragStartMouseY;
        private static bool _assetExplorerCollapsed;
        private static string _assetExplorerSearchTerm = string.Empty;
        private static readonly AssetExplorerTabState _assetExplorerGameState = new("GameProject", "Game Assets");
        private static readonly AssetExplorerTabState _assetExplorerEngineState = new("EngineCommon", "Engine Assets");
        private static readonly List<AssetExplorerEntry> _assetExplorerScratchEntries = new();

    private static readonly ConcurrentQueue<Action> _queuedSceneEdits = new();

        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameDisplaySnapshot;
        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameWindowSnapshot;
        private static float _worstFrameDisplayMs;
        private static float _worstFrameWindowMaxMs;
        private static DateTime _worstFrameWindowStart = DateTime.MinValue;
        private static readonly TimeSpan WorstFrameWindowDuration = TimeSpan.FromSeconds(0.5);

        static UserInterface()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, _) => _componentTypeCacheDirty = true;
            Engine.Time.Timer.UpdateFrame += ProcessQueuedSceneEdits;
        }

        private static void EnqueueSceneEdit(Action edit)
        {
            if (edit is null)
                return;

            _queuedSceneEdits.Enqueue(edit);
        }

        private static void ProcessQueuedSceneEdits()
        {
            while (_queuedSceneEdits.TryDequeue(out var edit))
            {
                try
                {
                    edit();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, "Inspector scene edit failed.");
                }
            }
        }

        private static void DrawDearImGuiTest()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawDearImGuiTest");
            bool showSettings = Toggles.DearImGuiUI;
            bool showProfiler = Toggles.DearImGuiProfiler;

            Engine.Profiler.EnableFrameLogging = Toggles.EnableProfilerLogging || showProfiler;

            if (!showSettings && !showProfiler)
                return;

            EnsureProfessionalImGuiStyling();

            // Unified left-docked window with tabs for Profiler, Settings, and Hierarchy
            DrawDebugDockWindow(showProfiler, showSettings);
            DrawInspectorPanel();
            DrawAssetExplorerPanel();
        }

        private static void DrawDebugDockWindow(bool includeProfiler, bool includeSettings)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawDebugDockWindow");
            var overlayViewport = ImGui.GetMainViewport();
            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;
            if (_profilerDockLeftEnabled)
            {
                float maxWidth = MathF.Max(240.0f, overlayViewport.WorkSize.X - 50.0f);
                float dockWidth = Math.Clamp(_profilerDockWidth, 240.0f, maxWidth);
                _profilerDockWidth = dockWidth;
                ImGui.SetNextWindowPos(overlayViewport.WorkPos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(dockWidth, overlayViewport.WorkSize.Y), ImGuiCond.Always);
                ImGui.SetNextWindowViewport(overlayViewport.ID);
                windowFlags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove;
            }
            else if (_profilerUndockNextFrame)
            {
                var viewport = ImGui.GetMainViewport();
                var defaultSize = new Vector2(800.0f, 600.0f);
                var pos = viewport.WorkPos + (viewport.WorkSize - defaultSize) * 0.5f;
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(defaultSize, ImGuiCond.Always);
                _profilerUndockNextFrame = false;
            }

            if (!ImGui.Begin("Debug", windowFlags))
            {
                ImGui.End();
                return;
            }

            if (ImGui.Button(_profilerDockLeftEnabled ? "Undock" : "Dock Left"))
            {
                if (_profilerDockLeftEnabled)
                {
                    _profilerDockLeftEnabled = false;
                    _profilerUndockNextFrame = true;
                }
                else
                {
                    float maxWidth = MathF.Max(240.0f, overlayViewport.WorkSize.X - 50.0f);
                    _profilerDockWidth = Math.Clamp(_profilerDockWidth, 240.0f, maxWidth);
                    _profilerDockLeftEnabled = true;
                }
            }

            ImGui.Separator();

            if (ImGui.BeginTabBar("DebugTabs"))
            {
                if (includeProfiler && ImGui.BeginTabItem("Profiler"))
                {
                    DrawProfilerTabContent();
                    ImGui.EndTabItem();
                }

                if (includeSettings && ImGui.BeginTabItem("Engine Settings"))
                {
                    DrawSettingsTabContent(Engine.Rendering.Settings, "Engine Settings");
                    ImGui.EndTabItem();
                }

                if (includeSettings && ImGui.BeginTabItem("User Settings"))
                {
                    DrawSettingsTabContent(Engine.UserSettings, "User Settings");
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Hierarchy"))
                {
                    DrawWorldHierarchyTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            if (_profilerDockLeftEnabled)
                HandleProfilerDockResize(overlayViewport);

            ImGui.End();
        }

        private static void DrawProfilerTabContent()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawProfilerTabContent");
            var frameSnapshot = Engine.Profiler.GetLastFrameSnapshot();
            var history = Engine.Profiler.GetThreadHistorySnapshot();

            if (frameSnapshot is null || frameSnapshot.Threads.Count == 0)
            {
                ImGui.Text("No profiler samples captured yet.");
                return;
            }

            UpdateWorstFrameStatistics(frameSnapshot);
            var snapshotForDisplay = GetSnapshotForHierarchy(frameSnapshot, out float hierarchyFrameMs, out bool showingWorstWindowSample);
            float worstFrameToDisplay = hierarchyFrameMs;

            ImGui.Text($"Captured at {frameSnapshot.FrameTime:F3}s");
            ImGui.Text($"Worst frame (0.5s window): {worstFrameToDisplay:F3} ms");
            if (showingWorstWindowSample)
                ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");

            ImGui.Separator();

            foreach (var thread in snapshotForDisplay.Threads.OrderBy(t => t.ThreadId))
            {
                string headerLabel = $"Thread {thread.ThreadId} ({thread.TotalTimeMs:F3} ms)";
                if (ImGui.CollapsingHeader($"{headerLabel}##ProfilerThread{thread.ThreadId}", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    if (history.TryGetValue(thread.ThreadId, out var samples) && samples.Length > 0)
                    {
                        float min = samples.Min();
                        float max = samples.Max();
                        if (!float.IsFinite(min) || !float.IsFinite(max))
                        {
                            min = 0.0f;
                            max = 0.0f;
                        }
                        if (MathF.Abs(max - min) < 0.001f)
                            max = min + 0.001f;

                        ImGui.PlotLines($"Frame time (ms)##ProfilerThreadPlot{thread.ThreadId}", ref samples[0], samples.Length, 0, null, min, max, new Vector2(-1.0f, 70.0f));
                    }

                    ImGui.Separator();
                    ImGui.Text("Hierarchy");
                    foreach (var root in thread.RootNodes)
                        DrawProfilerNode(root, $"T{thread.ThreadId}");
                }
            }
        }

        private static void DrawWorldHierarchyTab()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawWorldHierarchyTab");
            var world = Engine.Rendering.State.RenderingWorld ?? Engine.WorldInstances.FirstOrDefault();
            if (world is null)
            {
                ImGui.Text("No world instance available.");
                return;
            }

            if (world.RootNodes.Count == 0)
            {
                ImGui.Text("World has no root nodes.");
                return;
            }

            ImGui.Text($"GameMode: {world.GameMode?.GetType().Name ?? "<none>"}");
            ImGui.Separator();

            ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg
                                        | ImGuiTableFlags.ScrollY
                                        | ImGuiTableFlags.Resizable
                                        | ImGuiTableFlags.SizingStretchProp
                                        | ImGuiTableFlags.BordersInnerV;

            Vector2 tableSize = ImGui.GetContentRegionAvail();
            if (ImGui.BeginTable("HierarchyTree", 2, tableFlags, tableSize))
            {
                ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 72.0f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                var rootSnapshot = world.RootNodes.ToArray();
                foreach (var root in rootSnapshot)
                    DrawSceneNodeTree(root, world);

                ImGui.EndTable();
            }

            DrawHierarchyAddComponentPopup();
        }

        private static void DrawSceneNodeTree(SceneNode node, XRWorldInstance world)
        {
            var transform = node.Transform;
            int childCount = transform.Children.Count;
            string nodeLabel = node.Name ?? "<unnamed>";
            if (childCount > 0)
                nodeLabel += $" ({childCount})";
            ImGuiTreeNodeFlags flags = childCount > 0
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet;

            bool nodeOpen = DrawSceneNodeEntry(node, world, nodeLabel, flags);

            if (childCount > 0 && nodeOpen)
            {
                var childSnapshot = transform.Children.ToArray();
                foreach (var child in childSnapshot)
                {
                    if (child?.SceneNode is SceneNode childNode)
                        DrawSceneNodeTree(childNode, world);
                }
                ImGui.TableSetColumnIndex(0);
                ImGui.TreePop();
            }
        }

        private static bool DrawSceneNodeEntry(SceneNode node, XRWorldInstance world, string displayLabel, ImGuiTreeNodeFlags flags)
        {
            bool isRenaming = ReferenceEquals(_nodePendingRename, node);
            bool isSelected = Selection.SceneNodes.Contains(node) || ReferenceEquals(Selection.LastSceneNode, node);

            ImGui.PushID(node.ID.ToString());

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            ImGuiTreeNodeFlags fullFlags = flags
                                         | ImGuiTreeNodeFlags.SpanFullWidth
                                         | ImGuiTreeNodeFlags.FramePadding
                                         | ImGuiTreeNodeFlags.OpenOnArrow
                                         | ImGuiTreeNodeFlags.OpenOnDoubleClick;
            if (isSelected)
                fullFlags |= ImGuiTreeNodeFlags.Selected;
            bool nodeOpen = ImGui.TreeNodeEx("##TreeNode", fullFlags);
            if (!isRenaming && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                Selection.SceneNode = node;
            ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

            ImGui.SameLine();

            if (isRenaming)
            {
                if (_renameInputFocusRequested)
                {
                    ImGui.SetKeyboardFocusHere();
                    _renameInputFocusRequested = false;
                }

                ImGui.SetNextItemWidth(-1f);
                bool submitted = ImGui.InputText("##Rename", _renameBuffer, (uint)_renameBuffer.Length,
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);
                bool cancel = ImGui.IsKeyPressed(ImGuiKey.Escape);
                bool lostFocus = ImGui.IsItemDeactivated();

                if (cancel)
                    CancelHierarchyNodeRename();
                else if (submitted || lostFocus)
                    ApplyHierarchyNodeRename();
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(displayLabel);
                ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);
            }

            ImGui.TableSetColumnIndex(1);

            bool activeSelf = node.IsActiveSelf;
            bool checkboxToggled = ImGui.Checkbox("##ActiveSelf", ref activeSelf);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Toggle node active state");
            if (checkboxToggled)
                node.IsActiveSelf = activeSelf;
            ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

            if (ImGui.BeginPopup("Context"))
            {
                if (ImGui.MenuItem("Rename"))
                    BeginHierarchyNodeRename(node);

                if (ImGui.MenuItem("Delete"))
                    DeleteHierarchyNode(node, world);

                if (ImGui.MenuItem("Add Child Scene Node"))
                    CreateChildSceneNode(node);

                if (ImGui.MenuItem("Add Component..."))
                    BeginAddComponentForHierarchyNode(node);

                ImGui.EndPopup();
            }

            ImGui.TableSetColumnIndex(0);
            ImGui.PopID();

            return nodeOpen;
        }

        private static void BeginHierarchyNodeRename(SceneNode node)
        {
            _nodePendingRename = node;
            _renameInputFocusRequested = true;
            PopulateRenameBuffer(node.Name ?? string.Empty);
        }

        private static void DeleteHierarchyNode(SceneNode node, XRWorldInstance world)
        {
            if (node == _nodePendingRename)
                _nodePendingRename = null;
            if (node == _nodePendingAddComponent)
                _nodePendingAddComponent = null;

            var tfm = node.Transform;
            var parentTransform = tfm.Parent;
            if (parentTransform is not null)
            {
                parentTransform.RemoveChild(tfm, true);
                FinalizeSceneNodeDeletion(node);
            }
            else
            {
                world.RootNodes.Remove(node);
                FinalizeSceneNodeDeletion(node);
            }
        }

        private static void FinalizeSceneNodeDeletion(SceneNode node)
        {
            node.IsActiveSelf = false;
        }

        private static void CreateChildSceneNode(SceneNode parent)
        {
            SceneNode child = new(parent);
            BeginHierarchyNodeRename(child);
        }

        private static void BeginAddComponentForHierarchyNode(SceneNode node)
        {
            _nodePendingAddComponent = node;
            _componentPickerSearch = string.Empty;
            _componentPickerError = null;
            ImGui.OpenPopup(HierarchyAddComponentPopupId);
        }

        private static void DrawHierarchyAddComponentPopup()
        {
            if (_nodePendingAddComponent is null)
                return;

            ImGui.SetNextWindowSize(new Vector2(640f, 520f), ImGuiCond.FirstUseEver);
            bool popupOpen = ImGui.BeginPopupModal(HierarchyAddComponentPopupId, ImGuiWindowFlags.NoCollapse);
            if (!popupOpen)
            {
                ResetComponentPickerState();
                return;
            }

            var targetNode = _nodePendingAddComponent;
            if (targetNode is null)
            {
                ResetComponentPickerState();
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                return;
            }

            ImGui.TextUnformatted($"Add Component to '{targetNode.Name ?? SceneNode.DefaultName}'");
            ImGui.Spacing();

            if (!string.IsNullOrEmpty(_componentPickerError))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                ImGui.TextWrapped(_componentPickerError);
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }

            ImGui.InputText("Search", ref _componentPickerSearch, 256, ImGuiInputTextFlags.AutoSelectAll);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Filter by component name, namespace, or assembly.");

            var filteredTypes = GetFilteredComponentTypes(_componentPickerSearch);

            ImGui.Separator();

            const float ComponentListHeight = 360.0f;
            Type? requestedComponent = null;

            if (ImGui.BeginChild("ComponentList", new Vector2(0, ComponentListHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar))
            {
                if (filteredTypes.Count == 0)
                {
                    ImGui.TextDisabled("No components match the current search.");
                }
                else
                {
                    const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.NoSavedSettings;
                    if (ImGui.BeginTable("ComponentTable", 3, tableFlags))
                    {
                        ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.NoHide, 0.4f);
                        ImGui.TableSetupColumn("Namespace", ImGuiTableColumnFlags.NoHide, 0.35f);
                        ImGui.TableSetupColumn("Assembly", ImGuiTableColumnFlags.NoHide, 0.25f);
                        ImGui.TableHeadersRow();

                        foreach (var descriptor in filteredTypes)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            string selectableLabel = $"{descriptor.DisplayName}##{descriptor.FullName}";
                            bool selected = ImGui.Selectable(selectableLabel, false, ImGuiSelectableFlags.SpanAllColumns);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(descriptor.FullName);
                            if (selected)
                                requestedComponent = descriptor.Type;

                            ImGui.TableSetColumnIndex(1);
                            ImGui.TextUnformatted(string.IsNullOrEmpty(descriptor.Namespace) ? "<global>" : descriptor.Namespace);

                            ImGui.TableSetColumnIndex(2);
                            ImGui.TextUnformatted(descriptor.AssemblyName);
                        }

                        ImGui.EndTable();
                    }
                }

                ImGui.EndChild();
            }

            bool closePopup = false;
            if (requestedComponent is not null)
            {
                if (targetNode.TryAddComponent(requestedComponent, out _))
                {
                    _componentPickerError = null;
                    closePopup = true;
                }
                else
                {
                    _componentPickerError = $"Unable to add component '{requestedComponent.Name}'.";
                }
            }

            if (closePopup)
                CloseComponentPickerPopup();

            ImGui.Separator();

            if (ImGui.Button("Close") || ImGui.IsKeyPressed(ImGuiKey.Escape))
                CloseComponentPickerPopup();

            ImGui.EndPopup();
        }

        private static void DrawInspectorPanel()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawInspectorPanel");
            var viewport = ImGui.GetMainViewport();
            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;

            if (_inspectorDockRightEnabled)
            {
                float maxWidth = MathF.Max(280.0f, viewport.WorkSize.X - 50.0f);
                float dockWidth = Math.Clamp(_inspectorDockWidth, 280.0f, maxWidth);
                _inspectorDockWidth = dockWidth;
                Vector2 pos = new(viewport.WorkPos.X + viewport.WorkSize.X - dockWidth, viewport.WorkPos.Y);
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(dockWidth, viewport.WorkSize.Y), ImGuiCond.Always);
                ImGui.SetNextWindowViewport(viewport.ID);
                windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar;
            }
            else if (_inspectorUndockNextFrame)
            {
                var defaultSize = new Vector2(420.0f, 680.0f);
                var pos = viewport.WorkPos + (viewport.WorkSize - defaultSize) * 0.5f;
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(defaultSize, ImGuiCond.Always);
                _inspectorUndockNextFrame = false;
            }

            if (!ImGui.Begin("Inspector", windowFlags))
            {
                ImGui.End();
                return;
            }

            if (ImGui.Button(_inspectorDockRightEnabled ? "Undock" : "Dock Right"))
            {
                if (_inspectorDockRightEnabled)
                {
                    _inspectorDockRightEnabled = false;
                    _inspectorUndockNextFrame = true;
                }
                else
                {
                    float maxWidth = MathF.Max(280.0f, viewport.WorkSize.X - 50.0f);
                    _inspectorDockWidth = Math.Clamp(_inspectorDockWidth, 280.0f, maxWidth);
                    _inspectorDockRightEnabled = true;
                }
            }

            ImGui.Separator();

            var selectedNode = Selection.SceneNode ?? Selection.LastSceneNode;
            if (selectedNode is null)
            {
                ImGui.TextUnformatted("Select a scene node in the hierarchy to inspect its properties.");
            }
            else
            {
                ImGui.BeginChild("InspectorContent", Vector2.Zero, ImGuiChildFlags.Border);
                DrawSceneNodeInspector(selectedNode);
                ImGui.EndChild();
            }

            if (_inspectorDockRightEnabled)
                HandleInspectorDockResize(viewport);

            ImGui.End();
        }

        private static void DrawAssetExplorerPanel()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawAssetExplorerPanel");
            var viewport = ImGui.GetMainViewport();
            float reservedLeft = _profilerDockLeftEnabled ? _profilerDockWidth : 0.0f;
            float reservedRight = _inspectorDockRightEnabled ? _inspectorDockWidth : 0.0f;
            bool dockedTop = _assetExplorerDockTopEnabled;
            bool dockedBottom = _assetExplorerDockBottomEnabled;
            bool isDocked = dockedTop || dockedBottom;

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;
            const float minHeight = 220.0f;
            const float reservedVerticalMargin = 110.0f;

            if (dockedTop)
            {
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                float dockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);
                float dockWidth = Math.Max(320.0f, viewport.WorkSize.X - reservedLeft - reservedRight);

                _assetExplorerDockHeight = dockHeight;

                Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y);
                Vector2 size = new(dockWidth, dockHeight);

                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                ImGui.SetNextWindowViewport(viewport.ID);

                windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
            }
            else if (dockedBottom)
            {
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                float dockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);
                float dockWidth = Math.Max(320.0f, viewport.WorkSize.X - reservedLeft - reservedRight);

                _assetExplorerDockHeight = dockHeight;

                Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y + viewport.WorkSize.Y - dockHeight);
                Vector2 size = new(dockWidth, dockHeight);

                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                ImGui.SetNextWindowViewport(viewport.ID);

                windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
            }
            else if (_assetExplorerUndockNextFrame)
            {
                var defaultSize = new Vector2(920.0f, 420.0f);
                var pos = viewport.WorkPos + (viewport.WorkSize - defaultSize) * 0.5f;
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(defaultSize, ImGuiCond.Always);
                _assetExplorerUndockNextFrame = false;
            }

            if (!ImGui.Begin("Assets", windowFlags))
            {
                ImGui.End();
                return;
            }

            bool headerAtBottom = dockedBottom && !dockedTop;

            if (!headerAtBottom)
                DrawAssetExplorerHeader(viewport, false, dockedTop, dockedBottom, isDocked, minHeight, reservedVerticalMargin);

            bool showContent = !_assetExplorerCollapsed;
            if (showContent)
            {
                if (!headerAtBottom)
                    ImGui.Separator();

                if (ImGui.BeginTabBar("AssetExplorerTabs"))
                {
                    if (ImGui.BeginTabItem("Game Project"))
                    {
                        DrawAssetExplorerTab(_assetExplorerGameState, Engine.Assets.GameAssetsPath);
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Engine Common"))
                    {
                        DrawAssetExplorerTab(_assetExplorerEngineState, Engine.Assets.EngineAssetsPath);
                        ImGui.EndTabItem();
                    }

                    ImGui.EndTabBar();
                }
            }

            if (headerAtBottom)
                DrawAssetExplorerHeader(viewport, true, dockedTop, dockedBottom, isDocked, minHeight, reservedVerticalMargin);

            if (_assetExplorerDockTopEnabled || _assetExplorerDockBottomEnabled)
                HandleAssetExplorerDockResize(viewport, reservedLeft, reservedRight, _assetExplorerDockTopEnabled);

            ImGui.End();
        }

        private static void DrawAssetExplorerHeader(ImGuiViewportPtr viewport, bool headerAtBottom, bool dockedTop, bool dockedBottom, bool isDocked, float minHeight, float reservedVerticalMargin)
        {
            ImGui.PushID(headerAtBottom ? "AssetExplorerHeaderBottom" : "AssetExplorerHeaderTop");

            if (headerAtBottom)
                ImGui.Separator();

            ImGuiDir arrowDir = _assetExplorerCollapsed ? ImGuiDir.Right : ImGuiDir.Down;
            if (ImGui.ArrowButton("##AssetExplorerCollapse", arrowDir))
                _assetExplorerCollapsed = !_assetExplorerCollapsed;

            ImGui.SameLine(0f, 6f);
            ImGui.TextUnformatted("Assets");

            ImGui.SameLine();
            if (ImGui.Button(isDocked ? "Undock" : "Dock Bottom"))
            {
                if (isDocked)
                {
                    _assetExplorerDockBottomEnabled = false;
                    _assetExplorerDockTopEnabled = false;
                    _assetExplorerUndockNextFrame = true;
                }
                else
                {
                    float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                    _assetExplorerDockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);
                    _assetExplorerDockBottomEnabled = true;
                    _assetExplorerDockTopEnabled = false;
                    _assetExplorerUndockNextFrame = false;
                }
            }

            ImGui.SameLine();
            string dockOrientationLabel = _assetExplorerDockTopEnabled ? "Dock Bottom" : "Dock Top";
            if (ImGui.Button(dockOrientationLabel))
            {
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                _assetExplorerDockHeight = Math.Clamp(_assetExplorerDockHeight, minHeight, maxHeight);

                if (_assetExplorerDockTopEnabled)
                {
                    _assetExplorerDockTopEnabled = false;
                    _assetExplorerDockBottomEnabled = true;
                }
                else
                {
                    _assetExplorerDockTopEnabled = true;
                    _assetExplorerDockBottomEnabled = false;
                }

                _assetExplorerUndockNextFrame = false;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(260.0f);
            if (ImGui.InputTextWithHint("##AssetExplorerSearch", "Filter files...", ref _assetExplorerSearchTerm, 256u))
                _assetExplorerSearchTerm = _assetExplorerSearchTerm.Trim();

            ImGui.SameLine();
            ImGui.TextDisabled("Filter applies to the current directory.");

            if (!headerAtBottom)
                ImGui.Separator();

            ImGui.PopID();
        }

        private static void DrawAssetExplorerTab(AssetExplorerTabState state, string rootPath)
        {
            using var profilerScope = Engine.Profiler.Start($"UI.DrawAssetExplorerTab.{state.Id}");
            EnsureAssetExplorerState(state, rootPath);

            if (string.IsNullOrEmpty(state.RootPath))
            {
                ImGui.TextDisabled($"Directory not found: {rootPath}");
                return;
            }

            Vector2 contentRegion = ImGui.GetContentRegionAvail();
            float directoryPaneWidth = Math.Clamp(contentRegion.X * 0.32f, 220.0f, 360.0f);

            if (ImGui.BeginChild($"{state.Id}DirectoryPane", new Vector2(directoryPaneWidth, 0f), ImGuiChildFlags.Border))
            {
                DrawAssetExplorerDirectoryTree(state, state.RootPath);
            }
            ImGui.EndChild();

            ImGui.SameLine();

            if (ImGui.BeginChild($"{state.Id}FilePane", Vector2.Zero, ImGuiChildFlags.Border))
            {
                DrawAssetExplorerFileList(state);
            }
            ImGui.EndChild();
        }

        private static void DrawAssetExplorerDirectoryTree(AssetExplorerTabState state, string rootPath)
        {
            using var profilerScope = Engine.Profiler.Start($"UI.AssetExplorer.DirectoryTree.{state.Id}");
            ImGui.PushID(state.Id);

            ImGuiTreeNodeFlags rootFlags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
            if (string.Equals(state.CurrentDirectory, rootPath, StringComparison.OrdinalIgnoreCase))
                rootFlags |= ImGuiTreeNodeFlags.Selected;

            bool rootOpen = ImGui.TreeNodeEx(state.DisplayName, rootFlags);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                state.CurrentDirectory = rootPath;
                state.SelectedPath = null;
            }

            if (rootOpen)
            {
                DrawAssetExplorerDirectoryChildren(state, rootPath);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        private static void DrawAssetExplorerDirectoryChildren(AssetExplorerTabState state, string directory)
        {
            using var profilerScope = Engine.Profiler.Start("UI.AssetExplorer.DirectoryChildren");
            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                return;
            }

            Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);

            foreach (var subdir in subdirectories)
            {
                string name = Path.GetFileName(subdir) ?? subdir;
                string normalized = NormalizeAssetExplorerPath(subdir);
                bool hasChildren = DirectoryHasChildren(normalized);

                ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
                if (!hasChildren)
                    flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.Bullet;

                if (string.Equals(state.CurrentDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                    flags |= ImGuiTreeNodeFlags.Selected;

                string childPrefix = normalized + Path.DirectorySeparatorChar;
                if (state.CurrentDirectory.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                    ImGui.SetNextItemOpen(true, ImGuiCond.Once);

                ImGui.PushID(normalized);
                bool open = ImGui.TreeNodeEx(name, flags);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    state.CurrentDirectory = normalized;
                    state.SelectedPath = null;
                }

                if (open && hasChildren)
                {
                    DrawAssetExplorerDirectoryChildren(state, normalized);
                    ImGui.TreePop();
                }

                ImGui.PopID();
            }
        }

        private static void DrawAssetExplorerFileList(AssetExplorerTabState state)
        {
            using var profilerScope = Engine.Profiler.Start($"UI.AssetExplorer.FileList.{state.Id}");
            string directory = Directory.Exists(state.CurrentDirectory) ? state.CurrentDirectory : state.RootPath;
            directory = NormalizeAssetExplorerPath(directory);
            state.CurrentDirectory = directory;

            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(state.RootPath, directory);
            }
            catch
            {
                relativePath = directory;
            }

            if (string.Equals(relativePath, ".", StringComparison.Ordinal))
                relativePath = state.DisplayName;

            ImGui.TextUnformatted($"Directory: {relativePath}");

            bool directoryChanged = false;
            if (!string.Equals(directory, state.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Up##{state.Id}"))
                {
                    string? parent = Path.GetDirectoryName(directory);
                    if (!string.IsNullOrEmpty(parent) && parent.StartsWith(state.RootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        state.CurrentDirectory = NormalizeAssetExplorerPath(parent);
                        state.SelectedPath = null;
                        directoryChanged = true;
                    }
                }
            }

            if (directoryChanged)
                return;

            ImGui.Separator();

            _assetExplorerScratchEntries.Clear();

            try
            {
                foreach (var subdir in Directory.GetDirectories(directory))
                {
                    string name = Path.GetFileName(subdir) ?? subdir;
                    DateTime modifiedUtc;
                    try
                    {
                        modifiedUtc = Directory.GetLastWriteTimeUtc(subdir);
                    }
                    catch
                    {
                        modifiedUtc = DateTime.MinValue;
                    }

                    _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, NormalizeAssetExplorerPath(subdir), true, 0L, modifiedUtc));
                }

                foreach (var file in Directory.GetFiles(directory))
                {
                    string name = Path.GetFileName(file) ?? file;
                    if (!string.IsNullOrWhiteSpace(_assetExplorerSearchTerm)
                        && !name.Contains(_assetExplorerSearchTerm, StringComparison.OrdinalIgnoreCase)
                        && !file.Contains(_assetExplorerSearchTerm, StringComparison.OrdinalIgnoreCase))
                        continue;

                    long size = 0L;
                    DateTime modifiedUtc;
                    try
                    {
                        var info = new FileInfo(file);
                        size = info.Length;
                        modifiedUtc = info.LastWriteTimeUtc;
                    }
                    catch
                    {
                        modifiedUtc = DateTime.MinValue;
                    }

                    _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, NormalizeAssetExplorerPath(file), false, size, modifiedUtc));
                }
            }
            catch (Exception ex)
            {
                ImGui.TextDisabled($"Unable to read '{directory}': {ex.Message}");
                return;
            }

            _assetExplorerScratchEntries.Sort(AssetExplorerEntryComparer.Instance);

            if (_assetExplorerScratchEntries.Count == 0)
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_assetExplorerSearchTerm) ? "Folder is empty." : "No entries match the current filter.");
                return;
            }

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.BordersOuter;

            Vector2 tableSize = ImGui.GetContentRegionAvail();
            if (ImGui.BeginTable($"{state.Id}FileTable", 4, tableFlags, tableSize))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100.0f);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthFixed, 170.0f);
                ImGui.TableHeadersRow();

                bool directoryChangedViaTable = false;

                foreach (var entry in _assetExplorerScratchEntries)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    bool selected = string.Equals(state.SelectedPath, entry.Path, StringComparison.OrdinalIgnoreCase);
                    ImGuiSelectableFlags selectableFlags = ImGuiSelectableFlags.SpanAllColumns;
                    string label = entry.IsDirectory ? $"[DIR] {entry.Name}" : entry.Name;
                    bool activated = ImGui.Selectable(label, selected, selectableFlags);
                    bool hovered = ImGui.IsItemHovered();

                    if (hovered)
                        ImGui.SetTooltip(entry.Path);

                    if (entry.IsDirectory)
                    {
                        if (activated || (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)))
                        {
                            state.CurrentDirectory = entry.Path;
                            state.SelectedPath = null;
                            directoryChangedViaTable = true;
                            break;
                        }
                    }
                    else if (activated)
                    {
                        state.SelectedPath = entry.Path;
                    }

                    ImGui.TableSetColumnIndex(1);
                    if (entry.IsDirectory)
                        ImGui.TextUnformatted("Directory");
                    else
                    {
                        string extension = Path.GetExtension(entry.Name);
                        ImGui.TextUnformatted(string.IsNullOrEmpty(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant());
                    }

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(entry.IsDirectory ? "—" : FormatFileSize(entry.Size));

                    ImGui.TableSetColumnIndex(3);
                    if (entry.ModifiedUtc == DateTime.MinValue)
                        ImGui.TextUnformatted("—");
                    else
                        ImGui.TextUnformatted(entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                }

                ImGui.EndTable();

                if (directoryChangedViaTable)
                    return;
            }

            if (!string.IsNullOrEmpty(state.SelectedPath))
            {
                ImGui.Separator();
                ImGui.TextUnformatted(Path.GetFileName(state.SelectedPath));
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(state.SelectedPath);
                ImGui.PopTextWrapPos();
            }
        }

        private static void HandleAssetExplorerDockResize(ImGuiViewportPtr viewport, float reservedLeft, float reservedRight, bool dockedTop)
        {
            const float minHeight = 220.0f;
            const float reservedVerticalMargin = 110.0f;
            const float handleHeight = 12.0f;

            Vector2 originalCursor = ImGui.GetCursorScreenPos();
            Vector2 windowPos = ImGui.GetWindowPos();
            Vector2 windowSize = ImGui.GetWindowSize();
            Vector2 handlePos = dockedTop
                ? new Vector2(windowPos.X, windowPos.Y + windowSize.Y - handleHeight)
                : windowPos;

            ImGui.SetCursorScreenPos(handlePos);
            ImGui.PushID("AssetExplorerDockResize");
            ImGui.InvisibleButton(string.Empty, new Vector2(windowSize.X, handleHeight), ImGuiButtonFlags.MouseButtonLeft);

            bool hovered = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();
            bool activated = ImGui.IsItemActivated();
            bool deactivated = ImGui.IsItemDeactivated();

            if (hovered || active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);

            if (activated)
            {
                _assetExplorerDockDragging = true;
                _assetExplorerDockDragStartHeight = _assetExplorerDockHeight;
                _assetExplorerDockDragStartMouseY = ImGui.GetIO().MousePos.Y;
            }

            if (active && _assetExplorerDockDragging)
            {
                float delta = ImGui.GetIO().MousePos.Y - _assetExplorerDockDragStartMouseY;
                float newHeight = dockedTop
                    ? _assetExplorerDockDragStartHeight + delta
                    : _assetExplorerDockDragStartHeight - delta;
                float maxHeight = MathF.Max(minHeight, viewport.WorkSize.Y - reservedVerticalMargin);
                newHeight = Math.Clamp(newHeight, minHeight, maxHeight);

                if (MathF.Abs(newHeight - _assetExplorerDockHeight) > float.Epsilon)
                {
                    _assetExplorerDockHeight = newHeight;
                    float dockWidth = Math.Max(320.0f, viewport.WorkSize.X - reservedLeft - reservedRight);
                    Vector2 size = new(dockWidth, _assetExplorerDockHeight);
                    if (dockedTop)
                    {
                        Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y);
                        ImGui.SetWindowPos(pos);
                        ImGui.SetWindowSize(size);
                    }
                    else
                    {
                        Vector2 pos = new(viewport.WorkPos.X + reservedLeft, viewport.WorkPos.Y + viewport.WorkSize.Y - _assetExplorerDockHeight);
                        ImGui.SetWindowPos(pos);
                        ImGui.SetWindowSize(size);
                    }
                }
            }

            if (deactivated)
                _assetExplorerDockDragging = false;

            var drawList = ImGui.GetWindowDrawList();
            uint color = ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
            Vector2 rectMin = dockedTop
                ? new Vector2(windowPos.X, windowPos.Y + windowSize.Y - handleHeight)
                : new Vector2(windowPos.X, windowPos.Y);
            Vector2 rectMax = dockedTop
                ? new Vector2(windowPos.X + windowSize.X, windowPos.Y + windowSize.Y)
                : new Vector2(windowPos.X + windowSize.X, windowPos.Y + handleHeight);
            drawList.AddRectFilled(rectMin, rectMax, color);

            ImGui.PopID();
            ImGui.SetCursorScreenPos(originalCursor);
        }

        private static void EnsureAssetExplorerState(AssetExplorerTabState state, string rootPath)
        {
            string normalizedRoot = NormalizeAssetExplorerPath(rootPath);
            if (string.IsNullOrEmpty(normalizedRoot) || !Directory.Exists(normalizedRoot))
            {
                state.RootPath = string.Empty;
                state.CurrentDirectory = string.Empty;
                state.SelectedPath = null;
                return;
            }

            if (!string.Equals(state.RootPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                state.RootPath = normalizedRoot;
                state.CurrentDirectory = normalizedRoot;
                state.SelectedPath = null;
            }

            if (string.IsNullOrEmpty(state.CurrentDirectory)
                || !Directory.Exists(state.CurrentDirectory)
                || !state.CurrentDirectory.StartsWith(state.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                state.CurrentDirectory = state.RootPath;
            }
            else
            {
                state.CurrentDirectory = NormalizeAssetExplorerPath(state.CurrentDirectory);
            }

            if (!string.IsNullOrEmpty(state.SelectedPath) && !File.Exists(state.SelectedPath))
                state.SelectedPath = null;
        }

        private static string NormalizeAssetExplorerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                string full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path;
            }
        }

        private static bool DirectoryHasChildren(string path)
        {
            try
            {
                using var enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
                return enumerator.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        private static string FormatFileSize(long size)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (size >= GB)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", size / (double)GB);
            if (size >= MB)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} MB", size / (double)MB);
            if (size >= KB)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} KB", size / (double)KB);
            return string.Format(CultureInfo.InvariantCulture, "{0} B", size);
        }

        private static void DrawSceneNodeInspector(SceneNode node)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeInspector");
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance)
            {
                node
            };

            ImGui.PushID(node.ID.ToString());

            DrawSceneNodeBasics(node);

            ImGui.Separator();

            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.PushID("TransformSection");
                DrawTransformInspector(node.Transform, visited);
                ImGui.PopID();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Components");
            ImGui.Spacing();
            DrawComponentInspectors(node, visited);

            ImGui.PopID();
        }

        private static void DrawSceneNodeBasics(SceneNode node)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSceneNodeBasics");
            const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV;
            if (!ImGui.BeginTable("SceneNodeBasics", 2, tableFlags))
                return;

            DrawInspectorRow("Name", () =>
            {
                string name = node.Name ?? string.Empty;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputText("##SceneNodeName", ref name, 256))
                {
                    string trimmed = name.Trim();
                    if (!string.Equals(trimmed, node.Name, StringComparison.Ordinal))
                        node.Name = string.IsNullOrWhiteSpace(trimmed) ? SceneNode.DefaultName : trimmed;
                }
            });

            DrawInspectorRow("Active Self", () =>
            {
                bool active = node.IsActiveSelf;
                if (ImGui.Checkbox("##SceneNodeActiveSelf", ref active))
                    node.IsActiveSelf = active;
            });

            DrawInspectorRow("Active In Hierarchy", () =>
            {
                bool active = node.IsActiveInHierarchy;
                if (ImGui.Checkbox("##SceneNodeActiveInHierarchy", ref active))
                    node.IsActiveInHierarchy = active;
            });

            DrawInspectorRow("ID", () => ImGui.TextUnformatted(node.ID.ToString()));

            DrawInspectorRow("Path", () =>
            {
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(node.GetPath());
                ImGui.PopTextWrapPos();
            });

            ImGui.EndTable();
        }

        private static void DrawInspectorRow(string label, Action drawValue)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(label);
            ImGui.TableSetColumnIndex(1);
            drawValue();
        }

        private static void DrawTransformInspector(TransformBase transform, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawTransformInspector");
            ImGui.PushID(transform.GetHashCode());

            if (transform is Transform standard)
            {
                Vector3 translation = standard.Translation;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.DragFloat3("Translation##TransformTranslation", ref translation, 0.05f))
                {
                    standard.Translation = translation;
                    var queuedTranslation = translation;
                    EnqueueSceneEdit(() => standard.Translation = queuedTranslation);
                }

                var rotator = standard.Rotator;
                Vector3 rotation = rotator.PitchYawRoll;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.DragFloat3("Rotation (Pitch/Yaw/Roll)##TransformRotation", ref rotation, 0.5f))
                {
                    rotator.Pitch = rotation.X;
                    rotator.Yaw = rotation.Y;
                    rotator.Roll = rotation.Z;
                    var queuedRotator = rotator;
                    standard.Rotator = queuedRotator;
                    EnqueueSceneEdit(() => standard.Rotator = queuedRotator);
                }

                Vector3 scale = standard.Scale;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.DragFloat3("Scale##TransformScale", ref scale, 0.05f))
                {
                    standard.Scale = scale;
                    var queuedScale = scale;
                    EnqueueSceneEdit(() => standard.Scale = queuedScale);
                }

                var order = standard.Order;
                ETransformOrder[] orderValues = (ETransformOrder[])Enum.GetValues(typeof(ETransformOrder));
                string[] orderNames = Enum.GetNames(typeof(ETransformOrder));
                int selectedIndex = Array.IndexOf(orderValues, order);
                if (selectedIndex < 0)
                    selectedIndex = 0;
                int orderIndex = selectedIndex;
                if (ImGui.Combo("Order##TransformOrder", ref orderIndex, orderNames, orderNames.Length))
                {
                    if (orderIndex >= 0 && orderIndex < orderValues.Length)
                    {
                        var newOrder = orderValues[orderIndex];
                        standard.Order = newOrder;
                        EnqueueSceneEdit(() => standard.Order = newOrder);
                    }
                }
            }
            else
            {
                // Fall back to the generic property grid for specialized transform types.
                DrawInspectableObject(transform, "TransformProperties", visited);
            }

            ImGui.PopID();
        }

        private static void DrawComponentInspectors(SceneNode node, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawComponentInspectors");
            bool anyComponentsDrawn = false;

            foreach (var component in node.Components)
            {
                if (component is null)
                    continue;

                anyComponentsDrawn = true;
                string headerLabel = $"{component.GetType().Name}##Component{component.GetHashCode()}";
                if (ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.PushID(component.GetHashCode());
                    DrawInspectableObject(component, "ComponentProperties", visited);
                    ImGui.PopID();
                }
            }

            if (!anyComponentsDrawn)
                ImGui.TextDisabled("No components attached to this scene node.");
        }

        private static void DrawInspectableObject(object target, string id, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawInspectableObject");
            if (!visited.Add(target))
            {
                ImGui.TextUnformatted("<circular reference>");
                return;
            }

            ImGui.PushID(id);
            DrawSettingsProperties(target, visited);
            ImGui.PopID();

            visited.Remove(target);
        }

        private static void HandleInspectorDockResize(ImGuiViewportPtr viewport)
        {
            const float minWidth = 280.0f;
            const float reservedMargin = 50.0f;
            const float handleWidth = 12.0f;

            Vector2 originalCursor = ImGui.GetCursorScreenPos();
            Vector2 windowPos = ImGui.GetWindowPos();
            Vector2 windowSize = ImGui.GetWindowSize();
            Vector2 handlePos = new(windowPos.X, windowPos.Y);

            ImGui.SetCursorScreenPos(handlePos);
            ImGui.PushID("InspectorDockResize");
            ImGui.InvisibleButton(string.Empty, new Vector2(handleWidth, windowSize.Y), ImGuiButtonFlags.MouseButtonLeft);
            bool hovered = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();
            bool activated = ImGui.IsItemActivated();
            bool deactivated = ImGui.IsItemDeactivated();

            if (hovered || active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

            if (activated)
            {
                _inspectorDockDragging = true;
                _inspectorDockDragStartWidth = _inspectorDockWidth;
                _inspectorDockDragStartMouseX = ImGui.GetIO().MousePos.X;
            }

            if (active && _inspectorDockDragging)
            {
                float delta = ImGui.GetIO().MousePos.X - _inspectorDockDragStartMouseX;
                float newWidth = _inspectorDockDragStartWidth - delta;
                float maxWidth = MathF.Max(minWidth, viewport.WorkSize.X - reservedMargin);
                newWidth = Math.Clamp(newWidth, minWidth, maxWidth);
                if (MathF.Abs(newWidth - _inspectorDockWidth) > float.Epsilon)
                {
                    _inspectorDockWidth = newWidth;
                    float newPosX = viewport.WorkPos.X + viewport.WorkSize.X - _inspectorDockWidth;
                    ImGui.SetWindowPos(new Vector2(newPosX, windowPos.Y));
                    ImGui.SetWindowSize(new Vector2(_inspectorDockWidth, windowSize.Y));
                }
            }

            if (deactivated)
                _inspectorDockDragging = false;

            var drawList = ImGui.GetWindowDrawList();
            uint color = ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
            Vector2 rectMin = new(windowPos.X, windowPos.Y);
            Vector2 rectMax = new(windowPos.X + handleWidth, windowPos.Y + windowSize.Y);
            drawList.AddRectFilled(rectMin, rectMax, color);

            ImGui.PopID();
            ImGui.SetCursorScreenPos(originalCursor);
        }

        private static void ApplyHierarchyNodeRename()
        {
            if (_nodePendingRename is null)
                return;

            string newName = ExtractStringFromRenameBuffer();
            if (string.IsNullOrWhiteSpace(newName))
                newName = SceneNode.DefaultName;

            _nodePendingRename.Name = newName;
            CancelHierarchyNodeRename();
        }

        private static void CancelHierarchyNodeRename()
        {
            _nodePendingRename = null;
            _renameInputFocusRequested = false;
            Array.Clear(_renameBuffer, 0, _renameBuffer.Length);
        }

        private static void PopulateRenameBuffer(string source)
        {
            Array.Clear(_renameBuffer, 0, _renameBuffer.Length);
            if (string.IsNullOrEmpty(source))
                return;

            int written = Encoding.UTF8.GetBytes(source, 0, source.Length, _renameBuffer, 0);
            if (written < _renameBuffer.Length)
                _renameBuffer[written] = 0;
        }

        private static string ExtractStringFromRenameBuffer()
        {
            int length = Array.IndexOf(_renameBuffer, (byte)0);
            if (length < 0)
                length = _renameBuffer.Length;

            return Encoding.UTF8.GetString(_renameBuffer, 0, length).Trim();
        }

        private static void EnsureProfessionalImGuiStyling()
        {
            if (_imguiStyleInitialized)
                return;

            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

            var style = ImGui.GetStyle();
            style.WindowRounding = 8.0f;
            style.FrameRounding = 6.0f;
            style.GrabRounding = 6.0f;
            style.TabRounding = 6.0f;
            style.ScrollbarRounding = 8.0f;
            style.WindowBorderSize = 1.0f;
            style.FrameBorderSize = 1.0f;
            style.TabBorderSize = 1.0f;
            style.WindowPadding = new Vector2(14.0f, 10.0f);
            style.FramePadding = new Vector2(10.0f, 6.0f);
            style.ItemSpacing = new Vector2(10.0f, 8.0f);
            style.ItemInnerSpacing = new Vector2(6.0f, 4.0f);
            style.ScrollbarSize = 16.0f;
            style.GrabMinSize = 12.0f;

            var colors = style.Colors;
            Vector4 darkBg = new(0.12f, 0.13f, 0.15f, 1.00f);
            Vector4 midBg = new(0.18f, 0.19f, 0.22f, 1.00f);
            Vector4 hoverBg = new(0.24f, 0.26f, 0.30f, 1.00f);
            Vector4 activeBg = new(0.32f, 0.35f, 0.40f, 1.00f);
            Vector4 accent = new(0.15f, 0.55f, 0.95f, 1.00f);
            Vector4 accentHover = new(0.25f, 0.65f, 1.00f, 1.00f);
            Vector4 accentActive = new(0.10f, 0.45f, 0.85f, 1.00f);
            Vector4 textPrimary = new(0.92f, 0.94f, 0.97f, 1.00f);
            Vector4 textMuted = new(0.62f, 0.66f, 0.72f, 1.00f);

            colors[(int)ImGuiCol.Text] = textPrimary;
            colors[(int)ImGuiCol.TextDisabled] = textMuted;
            colors[(int)ImGuiCol.WindowBg] = darkBg;
            colors[(int)ImGuiCol.ChildBg] = new Vector4(darkBg.X, darkBg.Y, darkBg.Z, 0.75f);
            colors[(int)ImGuiCol.PopupBg] = new Vector4(0.10f, 0.11f, 0.13f, 0.98f);
            colors[(int)ImGuiCol.Border] = new Vector4(0.20f, 0.22f, 0.27f, 1.00f);
            colors[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);
            colors[(int)ImGuiCol.FrameBg] = midBg;
            colors[(int)ImGuiCol.FrameBgHovered] = hoverBg;
            colors[(int)ImGuiCol.FrameBgActive] = activeBg;
            colors[(int)ImGuiCol.TitleBg] = midBg;
            colors[(int)ImGuiCol.TitleBgActive] = hoverBg;
            colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(midBg.X, midBg.Y, midBg.Z, 0.60f);
            colors[(int)ImGuiCol.MenuBarBg] = midBg;
            colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(darkBg.X, darkBg.Y, darkBg.Z, 1.00f);
            colors[(int)ImGuiCol.ScrollbarGrab] = hoverBg;
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = activeBg;
            colors[(int)ImGuiCol.ScrollbarGrabActive] = accentActive;
            colors[(int)ImGuiCol.CheckMark] = accent;
            colors[(int)ImGuiCol.SliderGrab] = accent;
            colors[(int)ImGuiCol.SliderGrabActive] = accentActive;
            colors[(int)ImGuiCol.Button] = midBg;
            colors[(int)ImGuiCol.ButtonHovered] = accentHover;
            colors[(int)ImGuiCol.ButtonActive] = accentActive;
            colors[(int)ImGuiCol.Header] = midBg;
            colors[(int)ImGuiCol.HeaderHovered] = hoverBg;
            colors[(int)ImGuiCol.HeaderActive] = activeBg;
            colors[(int)ImGuiCol.Separator] = new Vector4(0.28f, 0.30f, 0.34f, 1.00f);
            colors[(int)ImGuiCol.SeparatorHovered] = accentHover;
            colors[(int)ImGuiCol.SeparatorActive] = accent;
            colors[(int)ImGuiCol.ResizeGrip] = hoverBg;
            colors[(int)ImGuiCol.ResizeGripHovered] = accentHover;
            colors[(int)ImGuiCol.ResizeGripActive] = accentActive;
            colors[(int)ImGuiCol.Tab] = midBg;
            colors[(int)ImGuiCol.TabHovered] = hoverBg;
            colors[(int)ImGuiCol.TabActive] = activeBg;
            colors[(int)ImGuiCol.TabUnfocused] = new Vector4(midBg.X, midBg.Y, midBg.Z, 0.90f);
            colors[(int)ImGuiCol.TabUnfocusedActive] = hoverBg;
            colors[(int)ImGuiCol.DockingPreview] = new Vector4(accent.X, accent.Y, accent.Z, 0.35f);
            colors[(int)ImGuiCol.DockingEmptyBg] = new Vector4(0.08f, 0.08f, 0.09f, 1.00f);
            colors[(int)ImGuiCol.PlotLines] = accent;
            colors[(int)ImGuiCol.PlotLinesHovered] = accentHover;
            colors[(int)ImGuiCol.PlotHistogram] = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
            colors[(int)ImGuiCol.PlotHistogramHovered] = accentHover;
            colors[(int)ImGuiCol.TableHeaderBg] = midBg;
            colors[(int)ImGuiCol.TableBorderStrong] = new Vector4(0.20f, 0.22f, 0.27f, 1.00f);
            colors[(int)ImGuiCol.TableBorderLight] = new Vector4(0.16f, 0.18f, 0.21f, 1.00f);
            colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(accent.X, accent.Y, accent.Z, 0.35f);
            colors[(int)ImGuiCol.DragDropTarget] = new Vector4(accent.X, accent.Y, accent.Z, 0.90f);
            colors[(int)ImGuiCol.NavHighlight] = accent;
            colors[(int)ImGuiCol.NavWindowingHighlight] = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
            colors[(int)ImGuiCol.NavWindowingDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.60f);
            colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.45f);

            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                style.WindowRounding = 0.0f;
                colors[(int)ImGuiCol.WindowBg].W = 1.0f;
            }

            _imguiStyleInitialized = true;
        }

        private static void InvalidateComponentTypeCache()
            => _componentTypeCacheDirty = true;

        private static IReadOnlyList<ComponentTypeDescriptor> EnsureComponentTypeCache()
        {
            if (!_componentTypeCacheDirty && _componentTypeDescriptors.Count > 0)
                return _componentTypeDescriptors;

            _componentTypeDescriptors.Clear();

            var baseType = typeof(XRComponent);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (type is null)
                        continue;
                    if (!baseType.IsAssignableFrom(type))
                        continue;
                    if (type.IsAbstract || type.IsInterface)
                        continue;
                    if (type.ContainsGenericParameters)
                        continue;

                    string displayName = type.Name;
                    string ns = type.Namespace ?? string.Empty;
                    string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? "Unknown";

                    _componentTypeDescriptors.Add(new ComponentTypeDescriptor(type, displayName, ns, assemblyName));
                }
            }

            _componentTypeDescriptors.Sort(static (a, b) =>
            {
                int nameCompare = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                    return nameCompare;
                int nsCompare = string.Compare(a.Namespace, b.Namespace, StringComparison.Ordinal);
                if (nsCompare != 0)
                    return nsCompare;
                return string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase);
            });

            _componentTypeCacheDirty = false;
            return _componentTypeDescriptors;
        }

        private static IReadOnlyList<ComponentTypeDescriptor> GetFilteredComponentTypes(string? search)
        {
            var all = EnsureComponentTypeCache();
            _filteredComponentTypes.Clear();

            if (string.IsNullOrWhiteSpace(search))
            {
                _filteredComponentTypes.AddRange(all);
                return _filteredComponentTypes;
            }

            string term = search.Trim();
            foreach (var descriptor in all)
            {
                if (descriptor.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(descriptor.Namespace) && descriptor.Namespace.Contains(term, StringComparison.OrdinalIgnoreCase))
                    || descriptor.AssemblyName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || descriptor.FullName.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    _filteredComponentTypes.Add(descriptor);
                }
            }

            return _filteredComponentTypes;
        }

        private static void CloseComponentPickerPopup()
        {
            ResetComponentPickerState();
            ImGui.CloseCurrentPopup();
        }

        private static void ResetComponentPickerState()
        {
            _nodePendingAddComponent = null;
            _componentPickerError = null;
            _componentPickerSearch = string.Empty;
        }

        private static void DrawProfilerOverlay()
        {
            var overlayViewport = ImGui.GetMainViewport();
            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;
            if (_profilerDockLeftEnabled)
            {
                float maxWidth = MathF.Max(240.0f, overlayViewport.WorkSize.X - 50.0f);
                float dockWidth = Math.Clamp(_profilerDockWidth, 240.0f, maxWidth);
                _profilerDockWidth = dockWidth;
                ImGui.SetNextWindowPos(overlayViewport.WorkPos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(dockWidth, overlayViewport.WorkSize.Y), ImGuiCond.Always);
                ImGui.SetNextWindowViewport(overlayViewport.ID);
                windowFlags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove;
            }
            else if (_profilerUndockNextFrame)
            {
                var viewport = ImGui.GetMainViewport();
                var defaultSize = new Vector2(640.0f, 480.0f);
                var pos = viewport.WorkPos + (viewport.WorkSize - defaultSize) * 0.5f;
                ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
                ImGui.SetNextWindowSize(defaultSize, ImGuiCond.Always);
                _profilerUndockNextFrame = false;
            }

            var frameSnapshot = Engine.Profiler.GetLastFrameSnapshot();
            var history = Engine.Profiler.GetThreadHistorySnapshot();

            if (!ImGui.Begin("Engine Profiler", windowFlags))
            {
                ImGui.End();
                return;
            }

            Engine.CodeProfiler.ProfilerFrameSnapshot? snapshotForDisplay = null;
            float hierarchyFrameMs = 0.0f;
            bool showingWorstWindowSample = false;

            if (frameSnapshot is not null && frameSnapshot.Threads.Count > 0)
            {
                UpdateWorstFrameStatistics(frameSnapshot);
                snapshotForDisplay = GetSnapshotForHierarchy(frameSnapshot, out hierarchyFrameMs, out showingWorstWindowSample);
                UpdateProfilerThreadCache(frameSnapshot.Threads);
                _lastProfilerCaptureTime = frameSnapshot.FrameTime;
            }
            else
            {
                UpdateProfilerThreadCache(Array.Empty<Engine.CodeProfiler.ProfilerThreadSnapshot>());
            }

            if (_profilerThreadCache.Count == 0)
            {
                ImGui.Text("No profiler samples captured yet.");
                ImGui.End();
                return;
            }

            if (snapshotForDisplay is not null)
            {
                ImGui.Text($"Captured at {_lastProfilerCaptureTime:F3}s");
                ImGui.Text($"Worst frame (0.5s window): {hierarchyFrameMs:F3} ms");
                if (showingWorstWindowSample)
                    ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");
            }
            else
            {
                ImGui.Text($"Awaiting fresh profiler samples… (last capture at {_lastProfilerCaptureTime:F3}s)");
                ImGui.Text($"Worst frame (0.5s window): {_worstFrameDisplayMs:F3} ms");
            }
            if (ImGui.Button(_profilerDockLeftEnabled ? "Undock" : "Dock Left"))
            {
                if (_profilerDockLeftEnabled)
                {
                    _profilerDockLeftEnabled = false;
                    _profilerUndockNextFrame = true;
                }
                else
                {
                    float maxWidth = MathF.Max(240.0f, overlayViewport.WorkSize.X - 50.0f);
                    _profilerDockWidth = Math.Clamp(_profilerDockWidth, 240.0f, maxWidth);
                    _profilerDockLeftEnabled = true;
                }
            }

            ImGui.Separator();

            var nowUtc = DateTime.UtcNow;
            foreach (var (threadId, entry) in _profilerThreadCache.OrderBy(static kvp => kvp.Key))
            {
                var threadSnapshot = entry.Snapshot;
                bool isStale = nowUtc - entry.LastUpdatedUtc > ProfilerThreadStaleThreshold;
                string headerLabel = $"Thread {threadId} ({threadSnapshot.TotalTimeMs:F3} ms)";
                if (isStale)
                    headerLabel += " (inactive)";

                string threadKey = $"Thread{threadId}";
                bool defaultOpen = _profilerNodeOpenCache.TryGetValue(threadKey, out bool cached) ? cached : true;
                ImGui.SetNextItemOpen(defaultOpen, ImGuiCond.Once);

                ImGuiTreeNodeFlags headerFlags = ImGuiTreeNodeFlags.None;
                if (isStale)
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));

                bool open = ImGui.CollapsingHeader($"{headerLabel}##ProfilerThread{threadId}", headerFlags);

                if (ImGui.IsItemToggledOpen())
                    _profilerNodeOpenCache[threadKey] = open;

                if (isStale)
                    ImGui.PopStyleColor();

                if (open)
                {
                    float[] samplesToPlot = Array.Empty<float>();
                    if (history.TryGetValue(threadId, out var samples) && samples.Length > 0)
                    {
                        if (entry.Samples.Length != samples.Length)
                            entry.Samples = new float[samples.Length];
                        Array.Copy(samples, entry.Samples, samples.Length);
                        samplesToPlot = entry.Samples;
                    }
                    else if (entry.Samples.Length > 0)
                    {
                        samplesToPlot = entry.Samples;
                    }

                    if (samplesToPlot.Length > 0)
                    {
                        float min = samplesToPlot.Min();
                        float max = samplesToPlot.Max();
                        if (!float.IsFinite(min) || !float.IsFinite(max))
                        {
                            min = 0.0f;
                            max = 0.0f;
                        }
                        if (MathF.Abs(max - min) < 0.001f)
                            max = min + 0.001f;

                        ImGui.PlotLines($"Frame time (ms)##ProfilerThreadPlot{threadId}", ref samplesToPlot[0], samplesToPlot.Length, 0, null, min, max, new Vector2(-1.0f, 70.0f));
                    }

                    ImGui.Separator();
                    ImGui.Text("Hierarchy");
                    foreach (var root in threadSnapshot.RootNodes)
                        DrawProfilerNode(root, $"T{threadId}");
                }
            }

            if (_profilerDockLeftEnabled)
                HandleProfilerDockResize(overlayViewport);

            ImGui.End();
        }

        private static void UpdateProfilerThreadCache(IReadOnlyList<Engine.CodeProfiler.ProfilerThreadSnapshot> threads)
        {
            var now = DateTime.UtcNow;

            foreach (var thread in threads)
            {
                if (_profilerThreadCache.TryGetValue(thread.ThreadId, out var existing))
                {
                    existing.Snapshot = thread;
                    existing.LastUpdatedUtc = now;
                }
                else
                {
                    _profilerThreadCache[thread.ThreadId] = new ProfilerThreadCacheEntry
                    {
                        Snapshot = thread,
                        LastUpdatedUtc = now
                    };
                }
            }
        }

        private static void DrawProfilerNode(Engine.CodeProfiler.ProfilerNodeSnapshot node, string path)
        {
            string id = $"{path}/{node.Name}";
            bool hasChildren = node.Children.Count > 0;
            string label = $"{node.Name} ({node.ElapsedMs:F3} ms)##{id}";

            bool defaultOpen = _profilerNodeOpenCache.TryGetValue(id, out bool cached) ? cached : true;
            ImGui.SetNextItemOpen(defaultOpen, ImGuiCond.Once);

            if (hasChildren)
            {
                bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen);
                if (ImGui.IsItemToggledOpen())
                    _profilerNodeOpenCache[id] = open;
                if (open)
                {
                    foreach (var child in node.Children)
                        DrawProfilerNode(child, id);
                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen);
            }
        }

        private static Engine.CodeProfiler.ProfilerFrameSnapshot GetSnapshotForHierarchy(Engine.CodeProfiler.ProfilerFrameSnapshot currentSnapshot, out float frameMs, out bool usingWorstSnapshot)
        {
            if (_worstFrameDisplaySnapshot is not null)
            {
                usingWorstSnapshot = true;
                frameMs = _worstFrameDisplayMs;
                return _worstFrameDisplaySnapshot;
            }

            usingWorstSnapshot = false;
            frameMs = currentSnapshot.Threads.Max(t => t.TotalTimeMs);
            return currentSnapshot;
        }

        private static void UpdateWorstFrameStatistics(Engine.CodeProfiler.ProfilerFrameSnapshot snapshot)
        {
            var now = DateTime.UtcNow;
            if (_worstFrameWindowStart == DateTime.MinValue)
                _worstFrameWindowStart = now;

            float currentFrameMs = snapshot.Threads.Max(t => t.TotalTimeMs);
            if (_worstFrameWindowSnapshot is null || currentFrameMs > _worstFrameWindowMaxMs)
            {
                _worstFrameWindowMaxMs = currentFrameMs;
                _worstFrameWindowSnapshot = snapshot;
            }

            if (now - _worstFrameWindowStart >= WorstFrameWindowDuration)
            {
                _worstFrameDisplayMs = _worstFrameWindowMaxMs;
                _worstFrameDisplaySnapshot = _worstFrameWindowSnapshot;

                _worstFrameWindowMaxMs = currentFrameMs;
                _worstFrameWindowSnapshot = snapshot;
                _worstFrameWindowStart = now;
            }
        }

        private static void HandleProfilerDockResize(ImGuiViewportPtr viewport)
        {
            const float minWidth = 240.0f;
            const float reservedMargin = 50.0f;
            const float handleWidth = 12.0f;

            Vector2 originalCursor = ImGui.GetCursorScreenPos();
            Vector2 windowPos = ImGui.GetWindowPos();
            Vector2 windowSize = ImGui.GetWindowSize();
            Vector2 handlePos = new(windowPos.X + windowSize.X - handleWidth, windowPos.Y);

            ImGui.SetCursorScreenPos(handlePos);
            ImGui.PushID("ProfilerDockResize");
            ImGui.InvisibleButton(string.Empty, new Vector2(handleWidth, windowSize.Y), ImGuiButtonFlags.MouseButtonLeft);
            bool hovered = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();
            bool activated = ImGui.IsItemActivated();
            bool deactivated = ImGui.IsItemDeactivated();

            if (hovered || active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

            if (activated)
            {
                _profilerDockDragging = true;
                _profilerDockDragStartWidth = _profilerDockWidth;
                _profilerDockDragStartMouseX = ImGui.GetIO().MousePos.X;
            }

            if (active && _profilerDockDragging)
            {
                var io = ImGui.GetIO();
                float delta = io.MousePos.X - _profilerDockDragStartMouseX;
                float newWidth = _profilerDockDragStartWidth + delta;
                float maxWidth = MathF.Max(minWidth, viewport.WorkSize.X - reservedMargin);
                newWidth = Math.Clamp(newWidth, minWidth, maxWidth);
                if (MathF.Abs(newWidth - _profilerDockWidth) > float.Epsilon)
                {
                    _profilerDockWidth = newWidth;
                    ImGui.SetWindowSize(new Vector2(_profilerDockWidth, windowSize.Y));
                    windowSize = ImGui.GetWindowSize();
                }
            }

            if (deactivated)
                _profilerDockDragging = false;

            var drawList = ImGui.GetWindowDrawList();
            uint color = ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
            Vector2 rectMin = new(windowPos.X + windowSize.X - handleWidth, windowPos.Y);
            Vector2 rectMax = new(windowPos.X + windowSize.X, windowPos.Y + windowSize.Y);
            drawList.AddRectFilled(rectMin, rectMax, color);
            ImGui.PopID();
            ImGui.SetCursorScreenPos(originalCursor);
        }

        private static void DrawSettingsTabContent(object? settingsRoot, string headerLabel)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsTabContent");
            if (settingsRoot is null)
            {
                ImGui.TextDisabled($"{headerLabel} unavailable.");
                return;
            }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            DrawSettingsObject(settingsRoot, headerLabel, visited, true);
        }

        private static void DrawSettingsObject(object obj, string label, HashSet<object> visited, bool defaultOpen)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsObject");
            if (!visited.Add(obj))
            {
                ImGui.TextUnformatted($"{label}: <circular reference>");
                return;
            }

            ImGui.PushID(label);
            string treeLabel = $"{label} ({obj.GetType().Name})";
            if (ImGui.TreeNodeEx(treeLabel, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                DrawSettingsProperties(obj, visited);
                ImGui.TreePop();
            }
            ImGui.PopID();

            visited.Remove(obj);
        }

        private static void DrawSettingsProperties(object obj, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsProperties");
            var properties = obj.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p =>
                {
                    var browsable = p.GetCustomAttribute<BrowsableAttribute>();
                    if (browsable is not null && !browsable.Browsable)
                        return false;
                    var editorBrowsable = p.GetCustomAttribute<EditorBrowsableAttribute>();
                    if (editorBrowsable?.State == EditorBrowsableState.Never)
                        return false;
                    if (p.PropertyType == typeof(SceneNode))
                        return false;
                    return true;
                })
                .OrderBy(p => p.Name)
                .ToArray();

            var simpleProps = new List<(PropertyInfo Property, object? Value)>();
            var complexProps = new List<(PropertyInfo Property, object? Value)>();

            foreach (var prop in properties)
            {
                object? value = null;
                bool valueRetrieved = false;
                try
                {
                    value = prop.GetValue(obj);
                    valueRetrieved = true;
                }
                catch
                {
                }

                if (!valueRetrieved)
                {
                    simpleProps.Add((prop, null));
                    continue;
                }

                if (value is null || IsSimpleSettingType(prop.PropertyType))
                    simpleProps.Add((prop, value));
                else
                    complexProps.Add((prop, value));
            }

            if (simpleProps.Count > 0 && ImGui.BeginTable("Properties", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            {
                foreach (var (property, value) in simpleProps)
                    DrawSimplePropertyRow(obj, property, value);
                ImGui.EndTable();
            }

            foreach (var (property, value) in complexProps)
            {
                if (value is null)
                {
                    ImGui.TextUnformatted($"{property.Name}: <null>");
                    continue;
                }

                if (TryDrawCollectionProperty(property, value, visited))
                    continue;

                DrawSettingsObject(value, property.Name, visited, false);
            }
        }

        private static bool TryDrawCollectionProperty(PropertyInfo property, object value, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.TryDrawCollectionProperty");
            if (value is not IList list)
                return false;

            string label = $"{property.Name} [{list.Count}]";
            bool canModifyElements = !list.IsReadOnly;
            Type? declaredElementType = GetCollectionElementType(property, value.GetType());

            ImGui.PushID(property.Name);
            if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (list.Count == 0)
                {
                    ImGui.TextDisabled("<empty>");
                }
                else if (ImGui.BeginTable("CollectionItems", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        object? item;
                        try
                        {
                            item = list[i];
                        }
                        catch (Exception ex)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextUnformatted($"[{i}]");
                            ImGui.TableSetColumnIndex(1);
                            ImGui.TextUnformatted($"<error: {ex.Message}>");
                            continue;
                        }

                        Type? itemType = item?.GetType() ?? declaredElementType;

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted($"[{i}]");
                        ImGui.TableSetColumnIndex(1);
                        ImGui.PushID(i);

                        if (item is null)
                        {
                            if (itemType is not null && IsSimpleSettingType(itemType))
                            {
                                object? currentValue = item;
                                DrawCollectionSimpleElement(list, itemType, i, ref currentValue, canModifyElements);
                            }
                            else
                            {
                                ImGui.TextDisabled("<null>");
                            }
                        }
                        else if (itemType is not null && IsSimpleSettingType(itemType))
                        {
                            object? currentValue = item;
                            DrawCollectionSimpleElement(list, itemType, i, ref currentValue, canModifyElements);
                        }
                        else
                        {
                            DrawSettingsObject(item, $"{property.Name}[{i}]", visited, false);
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                ImGui.TreePop();
            }
            ImGui.PopID();
            return true;
        }

        private static Type? GetCollectionElementType(PropertyInfo property, Type runtimeType)
        {
            static Type? Resolve(Type type)
            {
                if (type.IsArray)
                    return type.GetElementType();

                if (type.IsGenericType)
                {
                    Type[] genericArguments = type.GetGenericArguments();
                    if (genericArguments.Length == 1)
                        return genericArguments[0];
                }

                foreach (var iface in type.GetInterfaces())
                {
                    if (!iface.IsGenericType)
                        continue;

                    Type genericDef = iface.GetGenericTypeDefinition();
                    if (genericDef == typeof(IList<>) || genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>))
                        return iface.GetGenericArguments()[0];
                }

                return null;
            }

            return Resolve(property.PropertyType) ?? Resolve(runtimeType);
        }

        private static void DrawCollectionSimpleElement(IList list, Type elementType, int index, ref object? currentValue, bool canModifyElements)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawCollectionSimpleElement");
            Type effectiveType = Nullable.GetUnderlyingType(elementType) ?? elementType;
            bool isNullable = !elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null;
            bool isCurrentlyNull = currentValue is null;
            bool handled = false;

            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    if (ImGui.Checkbox("##Value", ref boolValue) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, boolValue))
                        {
                            currentValue = boolValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType.IsEnum)
            {
                string[] enumNames = Enum.GetNames(effectiveType);
                int currentIndex = currentValue is null ? -1 : Array.IndexOf(enumNames, Enum.GetName(effectiveType, currentValue));
                if (currentIndex < 0)
                    currentIndex = 0;

                int selectedIndex = currentIndex;
                using (new ImGuiDisabledScope(!canModifyElements || enumNames.Length == 0))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (enumNames.Length > 0 && ImGui.Combo("##Value", ref selectedIndex, enumNames, enumNames.Length) && canModifyElements && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                    {
                        object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                        if (TryAssignCollectionValue(list, index, newValue))
                        {
                            currentValue = newValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(string))
            {
                string textValue = currentValue as string ?? string.Empty;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##Value", ref textValue, 512u, ImGuiInputTextFlags.None) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, textValue))
                        {
                            currentValue = textValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector2))
            {
                Vector2 vector = currentValue is Vector2 v ? v : Vector2.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector3))
            {
                Vector3 vector = currentValue is Vector3 v ? v : Vector3.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector4))
            {
                Vector4 vector = currentValue is Vector4 v ? v : Vector4.Zero;
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canModifyElements)
                    {
                        if (TryAssignCollectionValue(list, index, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
                    }
                }
                handled = true;
            }
            else if (TryDrawNumericCollectionElement(effectiveType, canModifyElements, ref currentValue, ref isCurrentlyNull, list, index))
            {
                handled = true;
            }

            if (!handled)
            {
                if (currentValue is null)
                    ImGui.TextDisabled("<null>");
                else
                    ImGui.TextUnformatted(FormatSettingValue(currentValue));
            }

            if (isNullable)
            {
                using (new ImGuiDisabledScope(!canModifyElements))
                {
                    if (isCurrentlyNull)
                    {
                        if (TryGetDefaultValue(effectiveType, out var defaultValue) && defaultValue is not null)
                        {
                            ImGui.SameLine();
                            if (ImGui.SmallButton("Set"))
                            {
                                if (TryAssignCollectionValue(list, index, defaultValue))
                                {
                                    currentValue = defaultValue;
                                    isCurrentlyNull = false;
                                }
                            }
                        }
                    }
                    else
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Clear"))
                        {
                            if (TryAssignCollectionValue(list, index, null))
                            {
                                currentValue = null;
                                isCurrentlyNull = true;
                            }
                        }
                    }
                }
            }
        }

        private static bool TryDrawNumericCollectionElement(Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull, IList list, int index)
            => TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, newValue => TryAssignCollectionValue(list, index, newValue), "##Value");

        private static bool TryAssignCollectionValue(IList list, int index, object? newValue)
        {
            try
            {
                list[index] = newValue!;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DrawSimplePropertyRow(object owner, PropertyInfo property, object? value)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSimplePropertyRow");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(property.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(property.Name);

            Type propertyType = property.PropertyType;
            Type? underlyingType = Nullable.GetUnderlyingType(propertyType);
            bool isNullable = underlyingType is not null;
            Type effectiveType = underlyingType ?? propertyType;
            bool canWrite = property.CanWrite && property.SetMethod?.IsPublic == true;

            object? currentValue = value;
            bool isCurrentlyNull = currentValue is null;
            bool handled = false;

            if (effectiveType == typeof(bool))
            {
                bool boolValue = currentValue is bool b && b;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (ImGui.Checkbox("##Value", ref boolValue) && canWrite)
                    {
                        property.SetValue(owner, boolValue);
                        currentValue = boolValue;
                        isCurrentlyNull = false;
                    }
                }
                handled = true;
            }
            else if (effectiveType.IsEnum)
            {
                string[] enumNames = Enum.GetNames(effectiveType);
                int currentIndex = currentValue is null ? -1 : Array.IndexOf(enumNames, Enum.GetName(effectiveType, currentValue));
                if (currentIndex < 0)
                    currentIndex = 0;

                int selectedIndex = currentIndex;
                using (new ImGuiDisabledScope(!canWrite || enumNames.Length == 0))
                {
                    if (enumNames.Length > 0 && ImGui.Combo("##Value", ref selectedIndex, enumNames, enumNames.Length) && canWrite && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                    {
                        object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                        property.SetValue(owner, newValue);
                        currentValue = newValue;
                        isCurrentlyNull = false;
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(string))
            {
                string textValue = currentValue as string ?? string.Empty;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##Value", ref textValue, 512u, ImGuiInputTextFlags.None) && canWrite)
                    {
                        property.SetValue(owner, textValue);
                        currentValue = textValue;
                        isCurrentlyNull = false;
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector2))
            {
                Vector2 vector = currentValue is Vector2 v ? v : Vector2.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canWrite)
                    {
                        property.SetValue(owner, vector);
                        currentValue = vector;
                        isCurrentlyNull = false;
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector3))
            {
                Vector3 vector = currentValue is Vector3 v ? v : Vector3.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canWrite)
                    {
                        property.SetValue(owner, vector);
                        currentValue = vector;
                        isCurrentlyNull = false;
                    }
                }
                handled = true;
            }
            else if (effectiveType == typeof(Vector4))
            {
                Vector4 vector = currentValue is Vector4 v ? v : Vector4.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canWrite)
                    {
                        property.SetValue(owner, vector);
                        currentValue = vector;
                        isCurrentlyNull = false;
                    }
                }
                handled = true;
            }
            else if (TryDrawNumericProperty(owner, property, effectiveType, canWrite, ref currentValue, ref isCurrentlyNull))
            {
                handled = true;
            }

            if (!handled)
            {
                if (currentValue is null)
                    ImGui.TextDisabled("<null>");
                else
                    ImGui.TextUnformatted(FormatSettingValue(currentValue));
            }

            if (isNullable && canWrite)
            {
                if (isCurrentlyNull)
                {
                    if (TryGetDefaultValue(effectiveType, out var defaultValue))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Set"))
                        {
                            property.SetValue(owner, defaultValue);
                            currentValue = defaultValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                else
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear"))
                    {
                        property.SetValue(owner, null);
                        currentValue = null;
                        isCurrentlyNull = true;
                    }
                }
            }

            ImGui.PopID();
        }

        private static unsafe bool TryDrawNumericEditor(Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull, Func<object?, bool> applyValue, string label)
        {
            if (effectiveType == typeof(float))
            {
                float floatValue = currentValue is float f ? f : 0f;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputFloat(label, ref floatValue) && canWrite && float.IsFinite(floatValue))
                    {
                        if (applyValue(floatValue))
                        {
                            currentValue = floatValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(double))
            {
                double doubleValue = currentValue is double d ? d : 0.0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputDouble(label, ref doubleValue) && canWrite && double.IsFinite(doubleValue))
                    {
                        if (applyValue(doubleValue))
                        {
                            currentValue = doubleValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(decimal))
            {
                double doubleValue = currentValue is decimal dec ? (double)dec : 0.0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputDouble(label, ref doubleValue) && canWrite && double.IsFinite(doubleValue))
                    {
                        try
                        {
                            decimal newValue = Convert.ToDecimal(doubleValue);
                            if (applyValue(newValue))
                            {
                                currentValue = newValue;
                                isCurrentlyNull = false;
                            }
                        }
                        catch (OverflowException)
                        {
                            decimal clamped = doubleValue > 0 ? decimal.MaxValue : decimal.MinValue;
                            if (applyValue(clamped))
                            {
                                currentValue = clamped;
                                isCurrentlyNull = false;
                            }
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(int))
            {
                int intValue = currentValue is int i ? i : 0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S32, ref intValue) && canWrite)
                    {
                        if (applyValue(intValue))
                        {
                            currentValue = intValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(uint))
            {
                uint uintValue = currentValue is uint u ? u : 0u;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U32, ref uintValue) && canWrite)
                    {
                        if (applyValue(uintValue))
                        {
                            currentValue = uintValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(long))
            {
                long longValue = currentValue is long l ? l : 0L;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S64, ref longValue) && canWrite)
                    {
                        if (applyValue(longValue))
                        {
                            currentValue = longValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ulong))
            {
                ulong ulongValue = currentValue is ulong ul ? ul : 0UL;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U64, ref ulongValue) && canWrite)
                    {
                        if (applyValue(ulongValue))
                        {
                            currentValue = ulongValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(short))
            {
                short shortValue = currentValue is short s ? s : (short)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S16, ref shortValue) && canWrite)
                    {
                        if (applyValue(shortValue))
                        {
                            currentValue = shortValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ushort))
            {
                ushort ushortValue = currentValue is ushort us ? us : (ushort)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U16, ref ushortValue) && canWrite)
                    {
                        if (applyValue(ushortValue))
                        {
                            currentValue = ushortValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(byte))
            {
                byte byteValue = currentValue is byte by ? by : (byte)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.U8, ref byteValue) && canWrite)
                    {
                        if (applyValue(byteValue))
                        {
                            currentValue = byteValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            if (effectiveType == typeof(sbyte))
            {
                sbyte sbyteValue = currentValue is sbyte sb ? sb : (sbyte)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (InputScalar(label, ImGuiDataType.S8, ref sbyteValue) && canWrite)
                    {
                        if (applyValue(sbyteValue))
                        {
                            currentValue = sbyteValue;
                            isCurrentlyNull = false;
                        }
                    }
                }
                return true;
            }

            return false;
        }

        private static unsafe bool TryDrawNumericProperty(object owner, PropertyInfo property, Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull)
            => TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, newValue =>
            {
                property.SetValue(owner, newValue);
                return true;
            }, "##Value");

        private static unsafe bool InputScalar<T>(string label, ImGuiDataType dataType, ref T value)
            where T : unmanaged
        {
            T localValue = value;
            void* ptr = Unsafe.AsPointer(ref localValue);
            bool changed = ImGui.InputScalar(label, dataType, new IntPtr(ptr));

            if (changed)
                value = localValue;

            return changed;
        }

        private readonly struct ImGuiDisabledScope : IDisposable
        {
            private readonly bool _disabled;

            public ImGuiDisabledScope(bool disabled)
            {
                _disabled = disabled;
                if (disabled)
                    ImGui.BeginDisabled();
            }

            public void Dispose()
            {
                if (_disabled)
                    ImGui.EndDisabled();
            }
        }

        private static bool TryGetDefaultValue(Type type, out object? value)
        {
            if (type == typeof(bool))
            {
                value = false;
                return true;
            }

            if (type == typeof(float))
            {
                value = 0f;
                return true;
            }

            if (type == typeof(double))
            {
                value = 0.0;
                return true;
            }

            if (type == typeof(decimal))
            {
                value = 0m;
                return true;
            }

            if (type == typeof(int))
            {
                value = 0;
                return true;
            }

            if (type == typeof(uint))
            {
                value = 0u;
                return true;
            }

            if (type == typeof(long))
            {
                value = 0L;
                return true;
            }

            if (type == typeof(ulong))
            {
                value = 0UL;
                return true;
            }

            if (type == typeof(short))
            {
                value = (short)0;
                return true;
            }

            if (type == typeof(ushort))
            {
                value = (ushort)0;
                return true;
            }

            if (type == typeof(byte))
            {
                value = (byte)0;
                return true;
            }

            if (type == typeof(sbyte))
            {
                value = (sbyte)0;
                return true;
            }

            if (type.IsEnum)
            {
                string[] names = Enum.GetNames(type);
                if (names.Length > 0)
                {
                    value = Enum.Parse(type, names[0]);
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool IsSimpleSettingType(Type type)
        {
            Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;
            if (effectiveType == typeof(string))
                return true;
            if (effectiveType.IsPrimitive || effectiveType.IsEnum)
                return true;
            if (effectiveType == typeof(decimal))
                return true;
            if (effectiveType == typeof(Vector2) || effectiveType == typeof(Vector3) || effectiveType == typeof(Vector4))
                return true;
            if (effectiveType.IsValueType)
                return true;
            return false;
        }

        private static string FormatSettingValue(object? value)
        {
            if (value is null)
                return "<null>";

            return value switch
            {
                bool b => b ? "True" : "False",
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                uint ui => ui.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                ulong ul => ul.ToString(CultureInfo.InvariantCulture),
                short s => s.ToString(CultureInfo.InvariantCulture),
                ushort us => us.ToString(CultureInfo.InvariantCulture),
                byte by => by.ToString(CultureInfo.InvariantCulture),
                sbyte sb => sb.ToString(CultureInfo.InvariantCulture),
                Vector2 v2 => $"({v2.X:0.###}, {v2.Y:0.###})",
                Vector3 v3 => $"({v3.X:0.###}, {v3.Y:0.###}, {v3.Z:0.###})",
                Vector4 v4 => $"({v4.X:0.###}, {v4.Y:0.###}, {v4.Z:0.###}, {v4.W:0.###})",
                _ => value.ToString() ?? string.Empty
            };
        }

        private sealed class AssetExplorerTabState
        {
            public AssetExplorerTabState(string id, string displayName)
            {
                Id = id;
                DisplayName = displayName;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public string RootPath { get; set; } = string.Empty;
            public string CurrentDirectory { get; set; } = string.Empty;
            public string? SelectedPath { get; set; }
        }

        private readonly record struct AssetExplorerEntry(string Name, string Path, bool IsDirectory, long Size, DateTime ModifiedUtc);

        private sealed class AssetExplorerEntryComparer : IComparer<AssetExplorerEntry>
        {
            public static readonly AssetExplorerEntryComparer Instance = new();

            public int Compare(AssetExplorerEntry x, AssetExplorerEntry y)
            {
                if (x.IsDirectory != y.IsDirectory)
                    return x.IsDirectory ? -1 : 1;

                return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class ProfilerThreadCacheEntry
        {
            public Engine.CodeProfiler.ProfilerThreadSnapshot Snapshot { get; set; } = null!;
            public DateTime LastUpdatedUtc { get; set; }
            public float[] Samples { get; set; } = Array.Empty<float>();
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            bool IEqualityComparer<object>.Equals(object? x, object? y)
                => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj)
                => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ComponentTypeDescriptor
        {
            public ComponentTypeDescriptor(Type type, string displayName, string ns, string assemblyName)
            {
                Type = type;
                DisplayName = displayName;
                Namespace = ns;
                AssemblyName = assemblyName;
                FullName = type.FullName ?? type.Name;
            }

            public Type Type { get; }
            public string DisplayName { get; }
            public string Namespace { get; }
            public string AssemblyName { get; }
            public string FullName { get; }
        }
    }
}
