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
using XREngine.Components;
using XREngine.Animation;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;
using XREngine.Scene.Components.UI;
using XREngine.Scene.Transforms;
using XREngine.Editor.ComponentEditors;
using XREngine.Diagnostics;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
    {
        private const string HierarchyAddComponentPopupId = "HierarchyAddComponent";
        private static readonly List<ComponentTypeDescriptor> _componentTypeDescriptors = [];
        private static readonly byte[] _renameBuffer = new byte[256];
        private static readonly List<ComponentTypeDescriptor> _filteredComponentTypes = [];
        private static readonly Dictionary<Type, IXRComponentEditor?> _componentEditorCache = new();
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

        private static bool _addComponentPopupOpen;
        private static bool _addComponentPopupRequested;

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

        private static partial void BeginAddComponentForHierarchyNode(SceneNode node);
        private static partial void DrawHierarchyAddComponentPopup();
        private static partial void DrawInspectorPanel();
        private static partial void DrawSceneNodeInspector(SceneNode node);
        private static partial void DrawSceneNodeBasics(SceneNode node);
        private static partial void DrawInspectorRow(string label, Action drawValue);
        private static partial void DrawTransformInspector(TransformBase transform, HashSet<object> visited);
        private static partial void DrawComponentInspectors(SceneNode node, HashSet<object> visited);
        private static partial void DrawInspectableObject(object target, string id, HashSet<object> visited);
        private static partial void DrawComponentInspector(XRComponent component, HashSet<object> visited);
        public static partial void DrawDefaultComponentInspector(XRComponent component, HashSet<object> visited);
        private static partial IXRComponentEditor? ResolveComponentEditor(Type componentType);
        private static partial void HandleInspectorDockResize(ImGuiViewportPtr viewport);
        private static partial void InvalidateComponentTypeCache();
        private static partial IReadOnlyList<ComponentTypeDescriptor> EnsureComponentTypeCache();
        private static partial IReadOnlyList<ComponentTypeDescriptor> GetFilteredComponentTypes(string? search);
        private static partial void CloseComponentPickerPopup();
        private static partial void ResetComponentPickerState();

        private static partial void DrawAssetExplorerPanel();
        private static partial void DrawAssetExplorerHeader(ImGuiViewportPtr viewport, bool headerAtBottom, bool dockedTop, bool dockedBottom, bool isDocked, float minHeight, float reservedVerticalMargin);
        private static partial void DrawAssetExplorerTab(AssetExplorerTabState state, string rootPath);
        private static partial void DrawAssetExplorerDirectoryTree(AssetExplorerTabState state, string rootPath);
        private static partial void DrawAssetExplorerDirectoryChildren(AssetExplorerTabState state, string directory);
        private static partial void DrawAssetExplorerFileList(AssetExplorerTabState state);
        private static partial void HandleAssetExplorerDockResize(ImGuiViewportPtr viewport, float reservedLeft, float reservedRight, bool dockedTop);
        private static partial void EnsureAssetExplorerState(AssetExplorerTabState state, string rootPath);
        private static partial string NormalizeAssetExplorerPath(string path);
        private static partial bool DirectoryHasChildren(string path);
        private static partial string FormatFileSize(long size);

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
            ImGuiUndoHelper.BeginFrame();
            bool showSettings = Toggles.DearImGuiUI;
            bool showProfiler = Toggles.DearImGuiProfiler;

            //Engine.Profiler.EnableFrameLogging = Toggles.EnableProfilerLogging || showProfiler;

            if (!showSettings && !showProfiler)
                return;

            EnsureProfessionalImGuiStyling();

            DrawMainMenuBar();

            // Unified left-docked window with tabs for Profiler, Settings, and Hierarchy
            DrawDebugDockWindow(showProfiler, showSettings);
            DrawInspectorPanel();
            DrawAssetExplorerPanel();
        }

        private static void DrawMainMenuBar()
        {
            if (!ImGui.BeginMainMenuBar())
                return;

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Save All", "Ctrl+Shift+S"))
                    SaveAll(null);
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Edit"))
            {
                bool canUndo = Undo.CanUndo;
                bool canRedo = Undo.CanRedo;

                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, canUndo))
                    Undo.TryUndo();

                if (ImGui.MenuItem("Redo", "Ctrl+Y", false, canRedo))
                    Undo.TryRedo();

                if (ImGui.BeginMenu("Undo History", canUndo))
                {
                    var undoHistory = Undo.PendingUndo;
                    if (undoHistory.Count == 0)
                        ImGui.MenuItem("No undo steps", null, false, false);
                    else
                    {
                        for (int i = 0; i < undoHistory.Count && i < 20; i++)
                        {
                            var entry = undoHistory[i];
                            if (ImGui.MenuItem($"{i + 1}. {entry.Description}"))
                                UndoMultiple(i);
                        }
                    }
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("Redo History", canRedo))
                {
                    var redoHistory = Undo.PendingRedo;
                    if (redoHistory.Count == 0)
                        ImGui.MenuItem("No redo steps", null, false, false);
                    else
                    {
                        for (int i = 0; i < redoHistory.Count && i < 20; i++)
                        {
                            var entry = redoHistory[i];
                            if (ImGui.MenuItem($"{i + 1}. {entry.Description}"))
                                RedoMultiple(i);
                        }
                    }
                    ImGui.EndMenu();
                }

                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
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
            ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4.0f, 2.0f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4.0f, 2.0f));
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4.0f, 2.0f));
            ImGui.SetWindowFontScale(0.95f);
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
            ImGui.SetWindowFontScale(1.0f);
            ImGui.PopStyleVar(4);
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
                : ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen;

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

            DrawMissingAssetSection();
            DrawOpenGLDebugSection();

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

        private static void DrawMissingAssetSection()
        {
            var missingAssets = AssetDiagnostics.GetTrackedMissingAssets();
            if (missingAssets.Count == 0)
                return;

            ImGui.Separator();

            int totalHits = 0;
            foreach (var info in missingAssets)
                totalHits += info.Count;

            string headerLabel = $"Missing Assets ({missingAssets.Count} entries / {totalHits} hits)";
            if (!ImGui.CollapsingHeader($"{headerLabel}##ProfilerMissingAssets", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (ImGui.Button("Clear Missing Asset Log"))
            {
                AssetDiagnostics.ClearTrackedMissingAssets();
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Sorted by most recent");

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            float estimatedHeight = MathF.Min(44.0f + missingAssets.Count * ImGui.GetTextLineHeightWithSpacing(), 320.0f);

            if (ImGui.BeginTable("ProfilerMissingAssetTable", 6, tableFlags, new Vector2(-1.0f, estimatedHeight)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Last Context", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                ImGui.TableSetupColumn("First Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                ImGui.TableHeadersRow();

                foreach (var info in missingAssets.OrderByDescending(static i => i.LastSeenUtc))
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(info.Category);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(info.AssetPath);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(info.AssetPath);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(info.Count.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableNextColumn();
                    string contextLabel = string.IsNullOrWhiteSpace(info.LastContext) ? "<none>" : info.LastContext;
                    ImGui.TextUnformatted(contextLabel);
                    if (info.Contexts.Count > 1 && ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted("Contexts:");
                        foreach (var ctx in info.Contexts.OrderBy(static c => c))
                            ImGui.TextUnformatted(ctx);
                        ImGui.EndTooltip();
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(info.LastSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(info.FirstSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                }

                ImGui.EndTable();
            }
        }

        private static void DrawOpenGLDebugSection()
        {
            var errors = OpenGLRenderer.GetTrackedOpenGLErrors();
            if (errors.Count == 0)
                return;

            ImGui.Separator();

            int totalHits = 0;
            foreach (var info in errors)
                totalHits += info.Count;

            string headerLabel = $"OpenGL Errors ({errors.Count} ids / {totalHits} hits)";
            if (!ImGui.CollapsingHeader($"{headerLabel}##ProfilerOpenGLErrors", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (ImGui.Button("Clear Tracked Errors"))
            {
                OpenGLRenderer.ClearTrackedOpenGLErrors();
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Sorted by most recent");

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            float estimatedHeight = MathF.Min(44.0f + errors.Count * ImGui.GetTextLineHeightWithSpacing(), 320.0f);

            if (ImGui.BeginTable("ProfilerOpenGLErrorTable", 7, tableFlags, new Vector2(-1.0f, estimatedHeight)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70.0f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Severity", ImGuiTableColumnFlags.WidthFixed, 90.0f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                ImGui.TableSetupColumn("Latest Message", ImGuiTableColumnFlags.None);
                ImGui.TableHeadersRow();

                foreach (var error in errors.OrderByDescending(static e => e.LastSeenUtc))
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Id.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Count.ToString(CultureInfo.InvariantCulture));

                    ImGui.TableNextColumn();
                    bool highlightSeverity = string.Equals(error.Severity, "High", StringComparison.OrdinalIgnoreCase);
                    if (highlightSeverity)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.45f, 1.0f));
                    ImGui.TextUnformatted(error.Severity);
                    if (highlightSeverity)
                        ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Type);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Source);

                    ImGui.TableNextColumn();
                    string lastSeenLocal = error.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    ImGui.TextUnformatted(lastSeenLocal);

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(error.Message);
                }

                ImGui.EndTable();
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
            DrawSettingsObject(settingsRoot, headerLabel, null, visited, true);
        }

        private static void DrawSettingsObject(object obj, string label, string? description, HashSet<object> visited, bool defaultOpen, string? idOverride = null)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSettingsObject");
            if (!visited.Add(obj))
            {
                ImGui.TextUnformatted($"{label}: <circular reference>");
                return;
            }

            string id = idOverride ?? label;
            ImGui.PushID(id);
            string treeLabel = $"{label} ({obj.GetType().Name})";
            bool open = ImGui.TreeNodeEx(treeLabel, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            if (open)
            {
                DrawSettingsProperties(obj, visited);
                ImGui.TreePop();
            }
            ImGui.PopID();

            visited.Remove(obj);
        }

        private sealed class PropertyRenderInfo
        {
            public required PropertyInfo Property { get; init; }
            public object? Value { get; init; }
            public bool ValueRetrievalFailed { get; init; }
            public bool IsSimple { get; init; }
            public string Category { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string? Description { get; init; }
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

            var propertyInfos = new List<PropertyRenderInfo>(properties.Length);

            foreach (var prop in properties)
            {
                string category = prop.GetCustomAttribute<CategoryAttribute>()?.Category ?? string.Empty;
                if (string.IsNullOrWhiteSpace(category))
                    category = string.Empty;

                string displayName = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? prop.Name;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = prop.Name;

                string? description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (string.IsNullOrWhiteSpace(description))
                    description = null;

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

                bool isSimple = !valueRetrieved || value is null || IsSimpleSettingType(prop.PropertyType);

                propertyInfos.Add(new PropertyRenderInfo
                {
                    Property = prop,
                    Value = value,
                    ValueRetrievalFailed = !valueRetrieved,
                    IsSimple = isSimple,
                    Category = category,
                    DisplayName = displayName,
                    Description = description
                });
            }

            var orderedPropertyInfos = propertyInfos
                .OrderBy(info => string.IsNullOrWhiteSpace(info.Category) ? 0 : 1)
                .ThenBy(info => info.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var grouped = orderedPropertyInfos
                .GroupBy(info => info.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool multipleCategories = grouped.Count > 1;
            bool renderedCategoryHeader = false;

            foreach (var group in grouped)
            {
                string categoryLabel = string.IsNullOrWhiteSpace(group.Key) ? "General" : group.Key;

                var simpleProps = group.Where(info => info.IsSimple).ToList();
                var complexProps = group.Where(info => !info.IsSimple).ToList();

                if (multipleCategories || !string.IsNullOrWhiteSpace(group.Key))
                {
                    if (renderedCategoryHeader)
                        ImGui.Separator();
                    ImGui.TextUnformatted(categoryLabel);
                    renderedCategoryHeader = true;
                }

                if (simpleProps.Count > 0)
                {
                    string tableId = $"Properties_{obj.GetHashCode():X8}_{group.Key?.GetHashCode() ?? 0:X8}";
                    if (ImGui.BeginTable(tableId, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                    {
                        foreach (var info in simpleProps)
                            DrawSimplePropertyRow(obj, info.Property, info.Value, info.DisplayName, info.Description, info.ValueRetrievalFailed);
                        ImGui.EndTable();
                    }
                }

                foreach (var info in complexProps)
                {
                    if (info.ValueRetrievalFailed)
                    {
                        ImGui.TextUnformatted($"{info.DisplayName}: <error>");
                        if (!string.IsNullOrEmpty(info.Description) && ImGui.IsItemHovered())
                            ImGui.SetTooltip(info.Description);
                        continue;
                    }

                    if (info.Value is null)
                    {
                        ImGui.TextUnformatted($"{info.DisplayName}: <null>");
                        if (!string.IsNullOrEmpty(info.Description) && ImGui.IsItemHovered())
                            ImGui.SetTooltip(info.Description);
                        continue;
                    }

                    if (TryDrawCollectionProperty(info.Property, info.DisplayName, info.Description, info.Value, visited))
                        continue;

                    DrawSettingsObject(info.Value, info.DisplayName, info.Description, visited, false, info.Property.Name);
                }
            }
        }

        private static bool TryDrawCollectionProperty(PropertyInfo property, string label, string? description, object value, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.TryDrawCollectionProperty");
            if (value is not IList list)
                return false;

            string headerLabel = $"{label} [{list.Count}]";
            bool canModifyElements = !list.IsReadOnly;
            Type? declaredElementType = GetCollectionElementType(property, value.GetType());

            ImGui.PushID(property.Name);
            bool open = ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            if (open)
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
                            DrawSettingsObject(item, $"{label}[{i}]", description, visited, false, property.Name + i.ToString(CultureInfo.InvariantCulture));
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

        private static void DrawSimplePropertyRow(object owner, PropertyInfo property, object? value, string displayName, string? description, bool valueRetrievalFailed)
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawSimplePropertyRow");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(displayName);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);
            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(property.Name);

            if (valueRetrievalFailed)
            {
                ImGui.TextDisabled("<error>");
                ImGui.PopID();
                return;
            }

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
            else if (TryDrawColorProperty(owner, property, effectiveType, canWrite, ref currentValue, ref isCurrentlyNull))
            {
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

        private static bool TryDrawColorProperty(object owner, PropertyInfo property, Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull)
        {
            const ImGuiColorEditFlags ColorPickerFlags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.NoOptions;

            if (effectiveType == typeof(ColorF3))
            {
                Vector3 colorVec = currentValue is ColorF3 color ? new(color.R, color.G, color.B) : Vector3.Zero;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit3("##ColorValue", ref colorVec, ColorPickerFlags) && canWrite)
                    {
                        var newColor = new ColorF3(colorVec.X, colorVec.Y, colorVec.Z);
                        property.SetValue(owner, newColor);
                        currentValue = newColor;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ColorF4))
            {
                Vector4 colorVec = currentValue is ColorF4 color ? new(color.R, color.G, color.B, color.A) : new Vector4(0f, 0f, 0f, 1f);
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit4("##ColorValue", ref colorVec, ColorPickerFlags) && canWrite)
                    {
                        var newColor = new ColorF4(colorVec.X, colorVec.Y, colorVec.Z, colorVec.W);
                        property.SetValue(owner, newColor);
                        currentValue = newColor;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            return false;
        }

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
