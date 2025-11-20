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
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Core.Files;
using XREngine.Scene;
using XREngine.Scene.Components.UI;
using XREngine.Scene.Transforms;
using XREngine.Editor.ComponentEditors;
using XREngine.Diagnostics;
using XREngine.Editor.TransformEditors;

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
        private static readonly Dictionary<Type, IXRTransformEditor?> _transformEditorCache = new();
        private static readonly Dictionary<int, ProfilerThreadCacheEntry> _profilerThreadCache = new();
        private static bool _showProfiler;
        private static bool _profilerSortByTime;
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
        private static AssetExplorerSearchScope _assetExplorerSearchScope = AssetExplorerSearchScope.Name;
        private static bool _assetExplorerSearchCaseSensitive;
        private static readonly AssetExplorerTabState _assetExplorerGameState = new("GameProject", "Game Assets");
        private static readonly AssetExplorerTabState _assetExplorerEngineState = new("EngineCommon", "Engine Assets");
        private static readonly List<AssetExplorerEntry> _assetExplorerScratchEntries = new();
        private static readonly byte[] _assetExplorerRenameBuffer = new byte[256];
        private static bool _assetExplorerRenameFocusRequested;
        private static bool _assetExplorerContextPopupRequested;
        private static AssetExplorerTabState? _assetExplorerContextState;
        private static string? _assetExplorerContextPath;
        private static bool _assetExplorerContextIsDirectory;
        private static bool _assetExplorerContextAllowCreate;
        private static readonly Dictionary<string, bool> _assetExplorerCategoryFilterSelections = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _assetExplorerCategoryFilterOrder = new();
        private static string _assetExplorerCategoryFilterLabel = "Categories: All";
        private static bool _assetExplorerCategoryFilterActive;
        private static bool _assetExplorerCategoryFiltersDirty = true;
        private static readonly Dictionary<string, string> _assetExplorerExtensionCategoryMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "Textures",
            [".jpg"] = "Textures",
            [".jpeg"] = "Textures",
            [".bmp"] = "Textures",
            [".tga"] = "Textures",
            [".dds"] = "Textures",
            [".hdr"] = "Textures",
            [".exr"] = "Textures",
            [".ktx"] = "Textures",
            [".ktx2"] = "Textures",
            [".tif"] = "Textures",
            [".tiff"] = "Textures",
            [".psd"] = "Textures",
            [".gif"] = "Textures",
            [".fbx"] = "Models",
            [".obj"] = "Models",
            [".gltf"] = "Models",
            [".glb"] = "Models",
            [".dae"] = "Models",
            [".stl"] = "Models",
            [".ply"] = "Models",
            [".wav"] = "Audio",
            [".mp3"] = "Audio",
            [".ogg"] = "Audio",
            [".flac"] = "Audio",
            [".aiff"] = "Audio",
            [".cs"] = "Scripts",
            [".lua"] = "Scripts",
            [".js"] = "Scripts",
            [".shader"] = "Shaders",
            [".hlsl"] = "Shaders",
            [".glsl"] = "Shaders",
            [".compute"] = "Shaders",
            [".fx"] = "Shaders",
            [".json"] = "Data",
            [".yaml"] = "Data",
            [".yml"] = "Data",
            [".csv"] = "Data",
            [".ini"] = "Data",
            [".txt"] = "Data",
            [".bin"] = "Data",
            [".zip"] = "Archives",
            [".pak"] = "Archives",
            [".rar"] = "Archives"
        };
        private static AssetExplorerTabState? _assetExplorerPendingDeleteState;
        private static string? _assetExplorerPendingDeletePath;
        private static bool _assetExplorerPendingDeleteIsDirectory;
        private static readonly Dictionary<string, List<AssetExplorerContextAction>> _assetExplorerContextActionsByExtension = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<AssetExplorerContextAction> _assetExplorerGlobalContextActions = new();
        private static readonly HashSet<string> _assetExplorerTextureExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tga",
            ".tif",
            ".tiff",
            ".dds",
            ".exr",
            ".hdr",
            ".ktx",
            ".ktx2"
        };
        private static readonly List<AssetTypeDescriptor> _assetTypeDescriptors = [];
        private static bool _assetTypeCacheDirty = true;
        private static readonly Dictionary<Type, List<CollectionTypeDescriptor>> _collectionTypeDescriptorCache = new();
        private static readonly Dictionary<string, string> _collectionTypePickerSearch = new(StringComparer.OrdinalIgnoreCase);
        private static readonly MethodInfo _drawAssetCollectionElementMethod = typeof(UserInterface).GetMethod(nameof(DrawAssetCollectionElementGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly List<OpenGLApiObjectRow> _openGlApiObjectScratch = new();
        private static GenericRenderObject? _selectedOpenGlRenderObject;
        private static AbstractRenderAPIObject? _selectedOpenGlApiObject;
        private static string _openGlApiSearch = string.Empty;
        private static string? _openGlWindowFilter;
        private static string? _openGlApiTypeFilter;
        private static string? _openGlXrTypeFilter;
        private static OpenGlApiGroupMode _openGlGroupMode = OpenGlApiGroupMode.ApiType;
        private static object? _inspectorStandaloneTarget;
        private static string? _inspectorStandaloneTitle;
        private static Action? _inspectorStandaloneClearAction;
        private static XRAsset? _inspectorAssetContext; // Root asset currently rendered in the inspector.
        private static string? _selectedMissingAssetKey;
        private static string _missingAssetReplacementPath = string.Empty;
        private const float MissingAssetEditorMinHeight = 140.0f;
        private const float MissingAssetListMinHeight = 110.0f;

        private static readonly ConcurrentQueue<Action> _queuedSceneEdits = new();

        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameDisplaySnapshot;
        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameWindowSnapshot;
        private static float _worstFrameDisplayMs;
        private static float _worstFrameWindowMaxMs;
        private static DateTime _worstFrameWindowStart = DateTime.MinValue;
        private static readonly TimeSpan WorstFrameWindowDuration = TimeSpan.FromSeconds(0.5);

        private readonly struct OpenGLApiObjectRow
        {
            public OpenGLApiObjectRow(
                string windowTitle,
                string apiType,
                string apiName,
                string xrType,
                string xrName,
                nint handle,
                GenericRenderObject renderObject,
                AbstractRenderAPIObject apiObject)
            {
                WindowTitle = windowTitle;
                ApiType = apiType;
                ApiName = apiName;
                XrType = xrType;
                XrName = xrName;
                Handle = handle;
                RenderObject = renderObject;
                ApiObject = apiObject;
            }

            public string WindowTitle { get; }
            public string ApiType { get; }
            public string ApiName { get; }
            public string XrType { get; }
            public string XrName { get; }
            public nint Handle { get; }
            public GenericRenderObject RenderObject { get; }
            public AbstractRenderAPIObject ApiObject { get; }
        }

        private enum OpenGlApiGroupMode
        {
            None,
            ApiType,
            Window,
        }

        [Flags]
        private enum AssetExplorerSearchScope
        {
            Name = 1 << 0,
            Path = 1 << 1,
            Metadata = 1 << 2,
        }

        static UserInterface()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, _) =>
            {
                _componentTypeCacheDirty = true;
                _assetTypeCacheDirty = true;
                ClearAssetExplorerTypeCaches();
                _collectionTypeDescriptorCache.Clear();
                _collectionTypePickerSearch.Clear();
                _transformEditorCache.Clear();
            };
            Engine.Time.Timer.UpdateFrame += ProcessQueuedSceneEdits;
            Selection.SelectionChanged += HandleSceneSelectionChanged;
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
        public static partial void DrawDefaultTransformInspector(TransformBase transform, HashSet<object> visited);
        private static partial IXRComponentEditor? ResolveComponentEditor(Type componentType);
        private static partial IXRTransformEditor? ResolveTransformEditor(Type transformType);
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

        internal static void EnqueueSceneEdit(Action edit)
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

        private static void HandleSceneSelectionChanged(SceneNode[] nodes)
        {
            if (nodes.Length == 0)
                return;

            ClearInspectorStandaloneTarget();
        }

        public static void RegisterAssetExplorerContextAction(string label, Action<string> handler, IEnumerable<string>? extensions = null, Func<string, bool>? predicate = null)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("Label must be provided.", nameof(label));
            if (handler is null)
                throw new ArgumentNullException(nameof(handler));

            var action = new AssetExplorerContextAction(label, handler, predicate);

            if (extensions is null)
            {
                RegisterGlobalAssetExplorerAction(action);
                return;
            }

            bool anyExtensionRegistered = false;
            foreach (var extension in extensions)
            {
                string normalized = NormalizeAssetExplorerExtension(extension);
                if (string.IsNullOrEmpty(normalized))
                    continue;

                anyExtensionRegistered = true;
                RegisterAssetExplorerActionForExtension(normalized, action);
            }

            if (!anyExtensionRegistered)
                RegisterGlobalAssetExplorerAction(action);
        }

        public static bool UnregisterAssetExplorerContextAction(string label, IEnumerable<string>? extensions = null)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            bool removed = false;

            if (extensions is null)
            {
                removed |= RemoveAssetExplorerAction(_assetExplorerGlobalContextActions, label);
                foreach (var kvp in _assetExplorerContextActionsByExtension)
                    removed |= RemoveAssetExplorerAction(kvp.Value, label);
                return removed;
            }

            foreach (var extension in extensions)
            {
                string normalized = NormalizeAssetExplorerExtension(extension);
                if (string.IsNullOrEmpty(normalized))
                    continue;

                if (_assetExplorerContextActionsByExtension.TryGetValue(normalized, out var actions))
                    removed |= RemoveAssetExplorerAction(actions, label);
            }

            return removed;
        }

        private static void RegisterGlobalAssetExplorerAction(AssetExplorerContextAction action)
        {
            RemoveAssetExplorerAction(_assetExplorerGlobalContextActions, action.Label);
            _assetExplorerGlobalContextActions.Add(action);
        }

        private static void RegisterAssetExplorerActionForExtension(string extension, AssetExplorerContextAction action)
        {
            if (!_assetExplorerContextActionsByExtension.TryGetValue(extension, out var actions))
            {
                actions = new List<AssetExplorerContextAction>();
                _assetExplorerContextActionsByExtension[extension] = actions;
            }

            RemoveAssetExplorerAction(actions, action.Label);
            actions.Add(action);
        }

        private static bool RemoveAssetExplorerAction(List<AssetExplorerContextAction> actions, string label)
            => actions.RemoveAll(a => string.Equals(a.Label, label, StringComparison.Ordinal)) > 0;

        private static string NormalizeAssetExplorerExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return string.Empty;

            extension = extension.Trim();
            if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;
            return extension.ToLowerInvariant();
        }

        private static void DrawDearImGuiTest()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawDearImGuiTest");
            var io = ImGui.GetIO();
            Engine.Input.SetUIInputCaptured(io.WantCaptureMouse || io.WantCaptureKeyboard || io.WantTextInput);
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

                if (includeProfiler && ImGui.BeginTabItem("OpenGL API Objects"))
                {
                    DrawOpenGLApiObjectsTabContent();
                    ImGui.EndTabItem();
                }

                if (includeProfiler && ImGui.BeginTabItem("OpenGL Errors"))
                {
                    DrawOpenGLDebugTabContent();
                    ImGui.EndTabItem();
                }

                if (includeProfiler && ImGui.BeginTabItem("Missing Assets"))
                {
                    DrawMissingAssetsTabContent();
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
            UpdateProfilerThreadCache(frameSnapshot.Threads);

            var snapshotForDisplay = GetSnapshotForHierarchy(frameSnapshot, out float hierarchyFrameMs, out bool showingWorstWindowSample);
            float worstFrameToDisplay = hierarchyFrameMs;

            ImGui.Text($"Captured at {frameSnapshot.FrameTime:F3}s");
            ImGui.Text($"Worst frame (0.5s window): {worstFrameToDisplay:F3} ms");
            if (showingWorstWindowSample)
                ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");

            ImGui.Checkbox("Sort by Time", ref _profilerSortByTime);

            ImGui.Separator();

            // Thread selection/history
            if (ImGui.CollapsingHeader("Thread History", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.BeginChild("ProfilerThreads", new Vector2(0, 150), true);
                foreach (var threadId in _profilerThreadCache.Keys.OrderBy(k => k))
                {
                    var entry = _profilerThreadCache[threadId];
                    string threadName = string.IsNullOrEmpty(entry.Name) ? $"Thread {threadId}" : $"{entry.Name} ({threadId})";
                    if (entry.IsStale) threadName += " (Stale)";

                    ImGui.Text(threadName);
                    
                    if (history.TryGetValue(threadId, out var samples) && samples.Length > 0)
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

                        ImGui.SameLine();
                        ImGui.PlotLines($"##Plot{threadId}", ref samples[0], samples.Length, 0, null, min, max, new Vector2(ImGui.GetContentRegionAvail().X, 20));
                    }
                }
                ImGui.EndChild();
            }

            ImGui.Separator();

            // Hierarchy with columns
            if (ImGui.BeginTable("ProfilerHierarchy", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Time (ms)", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();

                foreach (var thread in snapshotForDisplay.Threads.OrderBy(t => t.ThreadId))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    bool threadOpen = ImGui.TreeNodeEx($"Thread {thread.ThreadId} ({thread.TotalTimeMs:F3} ms)", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                    
                    if (threadOpen)
                    {
                        var rootNodes = thread.RootNodes;
                        if (_profilerSortByTime)
                            rootNodes = rootNodes.OrderByDescending(n => n.ElapsedMs).ToList();

                        foreach (var root in rootNodes)
                            DrawProfilerNode(root, $"T{thread.ThreadId}");
                        ImGui.TreePop();
                    }
                }
                ImGui.EndTable();
            }
        }

        private static void UpdateProfilerThreadCache(IReadOnlyList<Engine.CodeProfiler.ProfilerThreadSnapshot> threads)
        {
            var now = DateTime.UtcNow;
            // Mark existing as stale
            foreach (var entry in _profilerThreadCache.Values)
            {
                if (now - entry.LastSeen > ProfilerThreadStaleThreshold)
                    entry.IsStale = true;
            }

            // Update or add
            foreach (var thread in threads)
            {
                if (!_profilerThreadCache.TryGetValue(thread.ThreadId, out var entry))
                {
                    entry = new ProfilerThreadCacheEntry { ThreadId = thread.ThreadId };
                    _profilerThreadCache[thread.ThreadId] = entry;
                }
                entry.LastSeen = now;
                entry.IsStale = false;
                entry.Snapshot = thread;
            }

            // Remove old
            var toRemove = _profilerThreadCache.Where(kvp => now - kvp.Value.LastSeen > ProfilerThreadCacheTimeout).Select(kvp => kvp.Key).ToList();
            foreach (var key in toRemove)
                _profilerThreadCache.Remove(key);
        }

        private static void DrawProfilerNode(Engine.CodeProfiler.ProfilerNodeSnapshot node, string idSuffix)
        {
            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanFullWidth;
            if (node.Children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf;

            // Check cache for open state
            string nodeKey = $"{node.Name}_{idSuffix}";
            if (_profilerNodeOpenCache.TryGetValue(nodeKey, out bool isOpen) && isOpen)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            bool nodeOpen = ImGui.TreeNodeEx($"{node.Name}##{idSuffix}", flags);
            if (ImGui.IsItemToggledOpen())
                _profilerNodeOpenCache[nodeKey] = nodeOpen;

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{node.ElapsedMs:F3}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text("1"); // Calls not available in snapshot

            if (nodeOpen)
            {
                var children = node.Children;
                if (_profilerSortByTime)
                    children = children.OrderByDescending(c => c.ElapsedMs).ToList();

                foreach (var child in children)
                    DrawProfilerNode(child, idSuffix + "_" + node.Name);
                ImGui.TreePop();
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

            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                ImGuiSceneNodeDragDrop.SetPayload(node);
                ImGui.TextUnformatted(displayLabel);
                ImGui.EndDragDropSource();
            }

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

        private static void PopulateAssetExplorerRenameBuffer(string source)
        {
            Array.Clear(_assetExplorerRenameBuffer, 0, _assetExplorerRenameBuffer.Length);
            if (string.IsNullOrEmpty(source))
                return;

            int written = Encoding.UTF8.GetBytes(source, 0, source.Length, _assetExplorerRenameBuffer, 0);
            if (written < _assetExplorerRenameBuffer.Length)
                _assetExplorerRenameBuffer[written] = 0;
        }

        private static string ExtractAssetExplorerRenameBuffer()
        {
            int length = Array.IndexOf(_assetExplorerRenameBuffer, (byte)0);
            if (length < 0)
                length = _assetExplorerRenameBuffer.Length;

            return Encoding.UTF8.GetString(_assetExplorerRenameBuffer, 0, length).Trim();
        }

        private static void ClearAssetExplorerRenameBuffer()
            => Array.Clear(_assetExplorerRenameBuffer, 0, _assetExplorerRenameBuffer.Length);

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
                bool isStale = nowUtc - entry.LastSeen > ProfilerThreadStaleThreshold;
                float totalTimeMs = threadSnapshot?.TotalTimeMs ?? 0f;
                string headerLabel = $"Thread {threadId} ({totalTimeMs:F3} ms)";
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
                    if (threadSnapshot is not null)
                    {
                        foreach (var root in threadSnapshot.RootNodes)
                            DrawProfilerNode(root, $"T{threadId}");
                    }
                }
            }
                    foreach (var root in threadSnapshot.RootNodes)
                        DrawProfilerNode(root, $"T{threadId}");
                }
            }

            if (_profilerDockLeftEnabled)
                HandleProfilerDockResize(overlayViewport);

            ImGui.End();
        }

        private static void DrawMissingAssetsTabContent()
        {
            using var profilerScope = Engine.Profiler.Start("UI.DrawMissingAssetsTabContent");

            var missingAssets = AssetDiagnostics.GetTrackedMissingAssets();
            if (missingAssets.Count == 0)
            {
                ImGui.TextDisabled("No missing assets have been tracked.");
                if (ImGui.Button("Clear Missing Asset Log"))
                    AssetDiagnostics.ClearTrackedMissingAssets();
                ClearMissingAssetSelection();
                return;
            }

            int totalHits = 0;
            foreach (var info in missingAssets)
                totalHits += info.Count;

            ImGui.TextUnformatted($"Entries: {missingAssets.Count} | Hits: {totalHits}");

            if (ImGui.Button("Clear Missing Asset Log"))
            {
                AssetDiagnostics.ClearTrackedMissingAssets();
                ClearMissingAssetSelection();
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Sorted by most recent");

            var ordered = missingAssets.OrderByDescending(static i => i.LastSeenUtc).ToList();

            AssetDiagnostics.MissingAssetInfo selectedInfo = default;
            bool hasSelectedInfo = false;
            if (!string.IsNullOrEmpty(_selectedMissingAssetKey))
            {
                foreach (var info in ordered)
                {
                    if (!string.Equals(_selectedMissingAssetKey, BuildMissingAssetSelectionKey(info.AssetPath, info.Category), StringComparison.Ordinal))
                        continue;

                    selectedInfo = info;
                    hasSelectedInfo = true;
                    break;
                }

                if (!hasSelectedInfo)
                    ClearMissingAssetSelection();
            }

            float availableHeight = MathF.Max(ImGui.GetContentRegionAvail().Y, MissingAssetListMinHeight + MissingAssetEditorMinHeight);
            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float editorHeight = hasSelectedInfo ? MathF.Max(MissingAssetEditorMinHeight, availableHeight * 0.35f) : 0.0f;
            float listHeight = hasSelectedInfo
                ? MathF.Max(MissingAssetListMinHeight, availableHeight - editorHeight - spacing)
                : availableHeight;

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;

            bool selectionFoundThisFrame = false;

            if (ImGui.BeginChild("MissingAssetList", new Vector2(-1.0f, listHeight), ImGuiChildFlags.Border))
            {
                if (ImGui.BeginTable("ProfilerMissingAssetTable", 6, tableFlags, new Vector2(-1.0f, -1.0f)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 110.0f);
                    ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, 0.45f);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                    ImGui.TableSetupColumn("Last Context", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                    ImGui.TableSetupColumn("Last Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                    ImGui.TableSetupColumn("First Seen", ImGuiTableColumnFlags.WidthFixed, 140.0f);
                    ImGui.TableHeadersRow();

                    int rowIndex = 0;
                    foreach (var info in ordered)
                    {
                        string rowKey = BuildMissingAssetSelectionKey(info.AssetPath, info.Category);
                        bool isSelected = !string.IsNullOrEmpty(_selectedMissingAssetKey)
                            && string.Equals(_selectedMissingAssetKey, rowKey, StringComparison.Ordinal);

                        if (isSelected)
                        {
                            selectedInfo = info;
                            selectionFoundThisFrame = true;
                        }

                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        ImGui.PushID(rowIndex);
                        string label = $"{info.Category}##MissingAssetRow";
                        if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                        {
                            if (!string.Equals(_selectedMissingAssetKey, rowKey, StringComparison.Ordinal))
                            {
                                _missingAssetReplacementPath = File.Exists(info.AssetPath)
                                    ? info.AssetPath
                                    : string.Empty;
                            }

                            _selectedMissingAssetKey = rowKey;
                            selectedInfo = info;
                            selectionFoundThisFrame = true;
                            isSelected = true;
                        }
                        ImGui.PopID();

                        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            RevealMissingAssetLocation(info.AssetPath);

                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(info.AssetPath);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(info.AssetPath);

                        ImGui.TableSetColumnIndex(2);
                        ImGui.TextUnformatted(info.Count.ToString(CultureInfo.InvariantCulture));

                        ImGui.TableSetColumnIndex(3);
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

                        ImGui.TableSetColumnIndex(4);
                        ImGui.TextUnformatted(info.LastSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                        ImGui.TableSetColumnIndex(5);
                        ImGui.TextUnformatted(info.FirstSeenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                        rowIndex++;
                    }

                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }

            if (!selectionFoundThisFrame && !string.IsNullOrEmpty(_selectedMissingAssetKey))
            {
                ClearMissingAssetSelection();
                hasSelectedInfo = false;
            }
            else
            {
                hasSelectedInfo = selectionFoundThisFrame;
            }

            if (hasSelectedInfo && editorHeight > 0.0f)
            {
                ImGui.Dummy(new Vector2(0.0f, spacing));
                if (ImGui.BeginChild("MissingAssetEditor", new Vector2(-1.0f, editorHeight), ImGuiChildFlags.Border))
                {
                    DrawMissingAssetReplacementEditor(selectedInfo);
                    ImGui.EndChild();
                }
            }
        }

        private static void DrawOpenGLApiObjectsTabContent()
        {
            var rows = _openGlApiObjectScratch;
            rows.Clear();

            foreach (var window in Engine.Windows)
            {
                if (window?.Renderer is not OpenGLRenderer glRenderer)
                    continue;

                string windowTitle;
                try
                {
                    windowTitle = window.Window?.Title ?? string.Empty;
                }
                catch
                {
                    windowTitle = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(windowTitle))
                    windowTitle = $"Window 0x{window.GetHashCode():X}";

                foreach (var pair in glRenderer.RenderObjectCache)
                {
                    var renderObject = pair.Key;
                    var apiObject = pair.Value;

                    if (renderObject is null || apiObject is null)
                        continue;

                    if (!IsXrRenderObject(renderObject))
                        continue;

                    bool isGenerated;
                    try
                    {
                        isGenerated = apiObject.IsGenerated;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!isGenerated)
                        continue;

                    string apiName;
                    try
                    {
                        apiName = apiObject.GetDescribingName();
                    }
                    catch
                    {
                        apiName = apiObject.GetType().Name;
                    }

                    string xrName;
                    try
                    {
                        xrName = renderObject.GetDescribingName();
                    }
                    catch
                    {
                        xrName = renderObject.GetType().Name;
                    }

                    rows.Add(new OpenGLApiObjectRow(
                        windowTitle,
                        apiObject.GetType().Name,
                        apiName,
                        renderObject.GetType().Name,
                        xrName,
                        apiObject.GetHandle(),
                        renderObject,
                        apiObject));
                }
            }

            rows.Sort(static (a, b) =>
            {
                int cmp = string.Compare(a.WindowTitle, b.WindowTitle, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                    return cmp;

                cmp = string.Compare(a.ApiType, b.ApiType, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                    return cmp;

                return string.Compare(a.XrName, b.XrName, StringComparison.OrdinalIgnoreCase);
            });

            string[] windowOptions = rows.Count > 0
                ? rows.Select(static r => r.WindowTitle).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            string[] apiTypeOptions = rows.Count > 0
                ? rows.Select(static r => r.ApiType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            string[] xrTypeOptions = rows.Count > 0
                ? rows.Select(static r => r.XrType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static r => r, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            ImGui.TextUnformatted("Search:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220.0f);
            ImGui.InputTextWithHint("##OpenGlApiSearch", "Name, type, window, handle", ref _openGlApiSearch, 256);

            ImGui.SameLine();
            ImGui.TextUnformatted("Group:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140.0f);
            DrawOpenGlApiGroupCombo("##OpenGlApiGroupMode");

            ImGui.SameLine();
            if (ImGui.Button("Reset Filters"))
            {
                _openGlApiSearch = string.Empty;
                _openGlWindowFilter = null;
                _openGlApiTypeFilter = null;
                _openGlXrTypeFilter = null;
            }

            if (windowOptions.Length > 0 || apiTypeOptions.Length > 0 || xrTypeOptions.Length > 0)
            {
                ImGui.Spacing();
                bool anyFilterDrawn = false;

                if (windowOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(180.0f);
                    DrawOpenGlFilterCombo("Window##OpenGlWindowFilter", windowOptions, ref _openGlWindowFilter);
                    anyFilterDrawn = true;
                }

                if (apiTypeOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(180.0f);
                    DrawOpenGlFilterCombo("API Type##OpenGlApiTypeFilter", apiTypeOptions, ref _openGlApiTypeFilter);
                    anyFilterDrawn = true;
                }

                if (xrTypeOptions.Length > 0)
                {
                    if (anyFilterDrawn)
                        ImGui.SameLine();
                    ImGui.SetNextItemWidth(180.0f);
                    DrawOpenGlFilterCombo("XR Type##OpenGlXrTypeFilter", xrTypeOptions, ref _openGlXrTypeFilter);
                }
            }

            IEnumerable<OpenGLApiObjectRow> query = rows;

            if (!string.IsNullOrWhiteSpace(_openGlApiSearch))
            {
                string[] tokens = _openGlApiSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length > 0)
                    query = query.Where(row => OpenGlApiRowMatchesSearch(row, tokens));
            }

            if (!string.IsNullOrEmpty(_openGlWindowFilter))
            {
                string filter = _openGlWindowFilter!;
                query = query.Where(row => string.Equals(row.WindowTitle, filter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(_openGlApiTypeFilter))
            {
                string filter = _openGlApiTypeFilter!;
                query = query.Where(row => string.Equals(row.ApiType, filter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(_openGlXrTypeFilter))
            {
                string filter = _openGlXrTypeFilter!;
                query = query.Where(row => string.Equals(row.XrType, filter, StringComparison.OrdinalIgnoreCase));
            }

            List<OpenGLApiObjectRow> filteredRows = query as List<OpenGLApiObjectRow> ?? query.ToList();

            if (filteredRows.Count == rows.Count)
                ImGui.TextUnformatted($"Tracked Objects: {rows.Count}");
            else
                ImGui.TextUnformatted($"Matching Objects: {filteredRows.Count} / {rows.Count}");

            Vector2 contentHeight = ImGui.GetContentRegionAvail();
            if (contentHeight.Y <= 0.0f)
                contentHeight.Y = 200.0f;

            const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            bool selectionVisible = false;

            if (ImGui.BeginChild("OpenGLApiObjectsList", new Vector2(-1.0f, contentHeight.Y), ImGuiChildFlags.Border))
            {
                if (filteredRows.Count == 0)
                {
                    string message = rows.Count == 0
                        ? "No OpenGL API objects are currently generated."
                        : "No OpenGL API objects match the current filters.";
                    ImGui.TextDisabled(message);
                }
                else if (ImGui.BeginTable("ProfilerOpenGLApiObjectsTable", 4, tableFlags, new Vector2(-1.0f, -1.0f)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                    ImGui.TableSetupColumn("API Object", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                    ImGui.TableSetupColumn("XR Object", ImGuiTableColumnFlags.WidthStretch, 0.35f);
                    ImGui.TableSetupColumn("Handle", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                    ImGui.TableHeadersRow();

                    int rowIndex = 0;

                    foreach (var group in EnumerateOpenGlGroups(filteredRows))
                    {
                        if (group.Header is not null)
                        {
                            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextUnformatted($"{group.Header} ({group.Rows.Count})");
                            ImGui.TableSetColumnIndex(1);
                            ImGui.TableSetColumnIndex(2);
                            ImGui.TableSetColumnIndex(3);
                        }

                        foreach (var row in group.Rows)
                        {
                            bool isSelected = ReferenceEquals(_selectedOpenGlRenderObject, row.RenderObject);
                            if (isSelected)
                                selectionVisible = true;

                            ImGui.TableNextRow();

                            ImGui.TableSetColumnIndex(0);
                            ImGui.PushID(rowIndex);
                            string label = $"{row.WindowTitle}##OpenGLApiRow";
                            if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                            {
                                var capturedRow = row;
                                SetInspectorStandaloneTarget(capturedRow.RenderObject, $"{capturedRow.XrName} ({capturedRow.XrType})", () =>
                                {
                                    if (ReferenceEquals(_selectedOpenGlRenderObject, capturedRow.RenderObject))
                                    {
                                        _selectedOpenGlRenderObject = null;
                                        _selectedOpenGlApiObject = null;
                                    }
                                });
                                _selectedOpenGlRenderObject = capturedRow.RenderObject;
                                _selectedOpenGlApiObject = capturedRow.ApiObject;
                                selectionVisible = true;
                                isSelected = true;
                            }
                            ImGui.PopID();

                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.WindowTitle);

                            ImGui.TableSetColumnIndex(1);
                            ImGui.TextUnformatted(row.ApiName);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.ApiType);

                            ImGui.TableSetColumnIndex(2);
                            ImGui.TextUnformatted(row.XrName);
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(row.XrType);

                            ImGui.TableSetColumnIndex(3);
                            ulong handleValue = unchecked((ulong)row.Handle);
                            string handleLabel = handleValue == 0 ? "0x0" : $"0x{handleValue:X}";
                            ImGui.TextUnformatted(handleLabel);

                            rowIndex++;
                        }
                    }

                    ImGui.EndTable();
                }
                ImGui.EndChild();
            }

            if (!selectionVisible && _selectedOpenGlRenderObject is not null)
            {
                if (ReferenceEquals(_inspectorStandaloneTarget, _selectedOpenGlRenderObject))
                    ClearInspectorStandaloneTarget();
                _selectedOpenGlRenderObject = null;
                _selectedOpenGlApiObject = null;
            }

            rows.Clear();
        }

        private static IEnumerable<(string? Header, List<OpenGLApiObjectRow> Rows)> EnumerateOpenGlGroups(List<OpenGLApiObjectRow> rows)
        {
            if (_openGlGroupMode == OpenGlApiGroupMode.None)
            {
                yield return (null, rows);
                yield break;
            }

            var comparer = StringComparer.OrdinalIgnoreCase;
            var lookup = new Dictionary<string, List<OpenGLApiObjectRow>>(comparer);

            foreach (var row in rows)
            {
                string key = _openGlGroupMode == OpenGlApiGroupMode.ApiType ? row.ApiType : row.WindowTitle;
                if (!lookup.TryGetValue(key, out var list))
                {
                    list = new List<OpenGLApiObjectRow>();
                    lookup.Add(key, list);
                }

                list.Add(row);
            }

            foreach (var key in lookup.Keys.OrderBy(k => k, comparer))
                yield return (key, lookup[key]);
        }

        private static void DrawOpenGlFilterCombo(string label, IReadOnlyList<string> options, ref string? current)
        {
            string preview = current ?? "All";
            if (!ImGui.BeginCombo(label, preview))
                return;

            bool isAllSelected = current is null;
            if (ImGui.Selectable("All", isAllSelected))
                current = null;
            if (isAllSelected)
                ImGui.SetItemDefaultFocus();

            for (int i = 0; i < options.Count; i++)
            {
                string option = options[i];
                bool selected = current is not null && string.Equals(option, current, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(option, selected))
                    current = option;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        private static void DrawOpenGlApiGroupCombo(string label)
        {
            string preview = GetGroupModeLabel(_openGlGroupMode);
            if (!ImGui.BeginCombo(label, preview))
                return;

            foreach (OpenGlApiGroupMode mode in Enum.GetValues<OpenGlApiGroupMode>())
            {
                string optionLabel = GetGroupModeLabel(mode);
                bool selected = mode == _openGlGroupMode;
                if (ImGui.Selectable(optionLabel, selected) && !selected)
                    _openGlGroupMode = mode;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        private static string GetGroupModeLabel(OpenGlApiGroupMode mode)
            => mode switch
            {
                OpenGlApiGroupMode.ApiType => "API Type",
                OpenGlApiGroupMode.Window => "Window",
                _ => "None",
            };

        private static bool OpenGlApiRowMatchesSearch(OpenGLApiObjectRow row, IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0)
                return true;

            foreach (var token in tokens)
            {
                if (!OpenGlApiRowContainsToken(row, token))
                    return false;
            }

            return true;
        }

        private static bool OpenGlApiRowContainsToken(OpenGLApiObjectRow row, string token)
        {
            if (string.IsNullOrEmpty(token))
                return true;

            if (row.WindowTitle.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.ApiName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.ApiType.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.XrName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                row.XrType.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            ulong handleValue = unchecked((ulong)row.Handle);
            string handleHex = handleValue == 0 ? "0x0" : $"0x{handleValue:X}";
            if (handleHex.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;

            string handleDecimal = handleValue.ToString(CultureInfo.InvariantCulture);
            return handleDecimal.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        private static void SetInspectorStandaloneTarget(object target, string? title, Action? onClear = null)
        {
            if (target is null)
                throw new ArgumentNullException(nameof(target));

            if (!ReferenceEquals(_inspectorStandaloneTarget, target))
                ClearInspectorStandaloneTarget();

            _inspectorStandaloneTarget = target;
            _inspectorStandaloneTitle = string.IsNullOrWhiteSpace(title) ? null : title;
            _inspectorStandaloneClearAction = onClear;

            if (Selection.SceneNodes.Length > 0)
                Selection.SceneNodes = [];
        }

        private static void ClearInspectorStandaloneTarget()
        {
            if (_inspectorStandaloneTarget is null)
                return;

            try
            {
                _inspectorStandaloneClearAction?.Invoke();
            }
            finally
            {
                _inspectorStandaloneTarget = null;
                _inspectorStandaloneTitle = null;
                _inspectorStandaloneClearAction = null;
            }
        }

        private static string BuildMissingAssetSelectionKey(string assetPath, string category)
        {
            string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
            string normalizedPath = string.IsNullOrWhiteSpace(assetPath) ? string.Empty : assetPath;
            return string.Concat(normalizedCategory, "::", normalizedPath);
        }

        private static void ClearMissingAssetSelection()
        {
            _selectedMissingAssetKey = null;
            _missingAssetReplacementPath = string.Empty;
        }

        private static void DrawMissingAssetReplacementEditor(in AssetDiagnostics.MissingAssetInfo info)
        {
            ImGui.TextUnformatted("Selected Missing Asset");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reveal"))
                RevealMissingAssetLocation(info.AssetPath);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path"))
                ImGui.SetClipboardText(info.AssetPath);

            ImGui.Separator();

            ImGui.TextUnformatted($"Category: {info.Category}");
            ImGui.TextUnformatted($"Hits: {info.Count}");
            ImGui.TextUnformatted($"Last Seen: {info.LastSeenUtc.ToLocalTime():g}");
            ImGui.TextUnformatted($"First Seen: {info.FirstSeenUtc.ToLocalTime():g}");

            ImGui.Spacing();

            ImGui.TextUnformatted("Contexts:");
            if (info.Contexts.Count == 0)
            {
                ImGui.TextDisabled("<none>");
            }
            else
            {
                foreach (var ctx in info.Contexts.OrderBy(static c => c))
                    ImGui.BulletText(ctx);
            }

            ImGui.Spacing();

            string replacement = _missingAssetReplacementPath;
            if (ImGui.InputTextWithHint("##MissingAssetReplacement", "Replacement path...", ref replacement, 512u))
                _missingAssetReplacementPath = replacement.Trim();

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload(ImGuiAssetUtilities.AssetPayloadType);
                if (payload.Data != IntPtr.Zero && payload.DataSize > 0)
                {
                    string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
                    if (!string.IsNullOrEmpty(path))
                        _missingAssetReplacementPath = path;
                }
                ImGui.EndDragDropTarget();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Use Missing Path"))
                _missingAssetReplacementPath = info.AssetPath;

            ImGui.Spacing();

            bool hasReplacement = !string.IsNullOrWhiteSpace(_missingAssetReplacementPath);
            using (new ImGuiDisabledScope(!hasReplacement))
            {
                if (ImGui.Button("Copy Replacement File"))
                {
                    if (hasReplacement && TryCopyMissingAssetReplacement(_missingAssetReplacementPath, info.AssetPath))
                    {
                        if (AssetDiagnostics.RemoveTrackedMissingAsset(info.AssetPath, info.Category))
                            Debug.Out($"Replaced missing asset '{info.AssetPath}'.");
                        ClearMissingAssetSelection();
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Mark Resolved"))
            {
                if (AssetDiagnostics.RemoveTrackedMissingAsset(info.AssetPath, info.Category))
                {
                    Debug.Out($"Removed missing asset '{info.AssetPath}' from diagnostics.");
                    ClearMissingAssetSelection();
                }
                else
                {
                    Debug.LogWarning($"Failed to remove missing asset '{info.AssetPath}'.");
                }
            }
        }

        private static bool TryCopyMissingAssetReplacement(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                Debug.LogWarning("No replacement file selected.");
                return false;
            }

            if (!File.Exists(sourcePath))
            {
                Debug.LogWarning($"Replacement file '{sourcePath}' does not exist.");
                return false;
            }

            try
            {
                string? directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.Copy(sourcePath, destinationPath, true);
                Debug.Out($"Copied replacement file '{sourcePath}' to '{destinationPath}'.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to copy replacement file to '{destinationPath}'.");
                return false;
            }
        }

        private static void RevealMissingAssetLocation(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            try
            {
                if (File.Exists(assetPath))
                {
                    OpenPathInExplorer(assetPath, false);
                    return;
                }

                string? directory = Path.GetDirectoryName(assetPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    OpenPathInExplorer(directory, true);
                }
                else
                {
                    Debug.LogWarning($"Directory for missing asset '{assetPath}' could not be located.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to reveal location for '{assetPath}'.");
            }
        }

        private static bool IsXrRenderObject(GenericRenderObject renderObject)
        {
            Type? type = renderObject.GetType();
            while (type is not null)
            {
                if (type.Name.StartsWith("XR", StringComparison.OrdinalIgnoreCase))
                    return true;

                type = type.DeclaringType;
            }

            return false;
        }

        private static void DrawOpenGLDebugTabContent()
        {
            var errors = OpenGLRenderer.GetTrackedOpenGLErrors();
            if (errors.Count == 0)
            {
                ImGui.TextDisabled("No OpenGL debug errors are currently tracked.");
                if (ImGui.Button("Clear Tracked Errors"))
                    OpenGLRenderer.ClearTrackedOpenGLErrors();
                return;
            }

            int totalHits = 0;
            foreach (var info in errors)
                totalHits += info.Count;

            ImGui.TextUnformatted($"IDs: {errors.Count} | Hits: {totalHits}");

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

            Vector2 childSize = ImGui.GetContentRegionAvail();
            if (childSize.Y < 0.0f)
                childSize.Y = 0.0f;

            ImGui.BeginChild($"SettingsScroll##{headerLabel}", childSize, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
            try
            {
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                DrawSettingsObject(settingsRoot, headerLabel, null, visited, true);
            }
            finally
            {
                ImGui.EndChild();
            }
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

                    if (TryDrawCollectionProperty(obj, info.Property, info.DisplayName, info.Description, info.Value, visited))
                        continue;

                    DrawSettingsObject(info.Value, info.DisplayName, info.Description, visited, false, info.Property.Name);
                }
            }
        }

        private static bool TryDrawCollectionProperty(object? owner, PropertyInfo property, string label, string? description, object value, HashSet<object> visited)
        {
            using var profilerScope = Engine.Profiler.Start("UI.TryDrawCollectionProperty");
            if (value is not IList list)
                return false;

            Type declaredElementType = GetCollectionElementType(property, value.GetType()) ?? typeof(object);
            Type effectiveDeclaredType = Nullable.GetUnderlyingType(declaredElementType) ?? declaredElementType;

            ImGuiEditorUtilities.CollectionEditorAdapter adapter;

            if (value is Array arrayValue)
            {
                Func<Array, bool>? applyReplacement = null;
                if (property.CanWrite && property.SetMethod?.IsPublic == true)
                {
                    applyReplacement = replacement =>
                    {
                        try
                        {
                            property.SetValue(owner, replacement);
                            NotifyInspectorValueEdited(owner);
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    };
                }

                adapter = ImGuiEditorUtilities.CollectionEditorAdapter.ForArray(arrayValue, declaredElementType, applyReplacement);
            }
            else
            {
                adapter = ImGuiEditorUtilities.CollectionEditorAdapter.ForList(list, declaredElementType);
            }

            bool elementIsAsset = typeof(XRAsset).IsAssignableFrom(effectiveDeclaredType);
            bool elementUsesTypeSelector = ShouldUseCollectionTypeSelector(declaredElementType);
            IReadOnlyList<CollectionTypeDescriptor> availableTypeOptions = elementIsAsset || elementUsesTypeSelector
                ? GetCollectionTypeDescriptors(declaredElementType)
                : Array.Empty<CollectionTypeDescriptor>();
            string headerLabel = $"{label} [{adapter.Count}]";

            ImGui.PushID(property.Name);
            bool open = ImGui.TreeNodeEx(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (open)
            {
                if (adapter.Count == 0)
                {
                    ImGui.TextDisabled("<empty>");
                }
                else if (ImGui.BeginTable("CollectionItems", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                {
                    for (int i = 0; i < adapter.Count; i++)
                    {
                        IList currentList = adapter.Items;

                        object? item;
                        try
                        {
                            item = currentList[i];
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

                        Type? runtimeType = item?.GetType();
                        bool itemIsAsset = runtimeType is not null
                            ? typeof(XRAsset).IsAssignableFrom(runtimeType)
                            : elementIsAsset;
                        bool itemUsesTypeSelector = runtimeType is not null
                            ? ShouldUseCollectionTypeSelector(runtimeType)
                            : elementUsesTypeSelector;

                        ImGui.TableNextRow();
                        ImGui.PushID(i);

                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextUnformatted($"[{i}]");

                        if (adapter.CanAddRemove)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Remove"))
                            {
                                if (adapter.TryRemoveAt(i))
                                {
                                    NotifyInspectorValueEdited(owner);
                                    ImGui.PopID();
                                    i--;
                                    continue;
                                }
                            }
                        }

                        int availableTypeCount = availableTypeOptions.Count;

                        if (adapter.CanAddRemove && itemUsesTypeSelector && availableTypeCount > 0)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Replace"))
                            {
                                if (availableTypeCount == 1)
                                {
                                    TryReplaceCollectionInstance(adapter, i, availableTypeOptions[0].Type, owner);
                                }
                                else
                                {
                                    ImGui.OpenPopup("ReplaceElement");
                                }
                            }

                            if (availableTypeCount > 1)
                            {
                                DrawCollectionTypePickerPopup("ReplaceElement", declaredElementType, runtimeType, selectedType =>
                                {
                                    TryReplaceCollectionInstance(adapter, i, selectedType, owner);
                                });
                            }
                        }

                        ImGui.TableSetColumnIndex(1);

                        bool elementCanModify = currentList is Array || !currentList.IsReadOnly;

                        if (itemIsAsset)
                        {
                            DrawCollectionAssetElement(declaredElementType, runtimeType, item as XRAsset, adapter, i, owner);
                        }
                        else if (item is null)
                        {
                            if (runtimeType is not null && IsSimpleSettingType(runtimeType))
                            {
                                object? currentValue = item;
                                DrawCollectionSimpleElement(currentList, runtimeType, i, ref currentValue, elementCanModify);
                            }
                            else if (IsSimpleSettingType(effectiveDeclaredType))
                            {
                                object? currentValue = item;
                                DrawCollectionSimpleElement(currentList, effectiveDeclaredType, i, ref currentValue, elementCanModify);
                            }
                            else
                            {
                                ImGui.TextDisabled("<null>");
                            }
                        }
                        else if (runtimeType is not null && IsSimpleSettingType(runtimeType))
                        {
                            object? currentValue = item;
                            DrawCollectionSimpleElement(currentList, runtimeType, i, ref currentValue, elementCanModify);
                        }
                        else
                        {
                            DrawSettingsObject(item, $"{label}[{i}]", description, visited, false, property.Name + i.ToString(CultureInfo.InvariantCulture));
                        }

                        if (item is null && adapter.CanAddRemove && itemUsesTypeSelector && availableTypeCount > 0)
                        {
                            ImGui.SameLine(0f, 6f);
                            if (ImGui.SmallButton("Create"))
                            {
                                if (availableTypeCount == 1)
                                {
                                    TryReplaceCollectionInstance(adapter, i, availableTypeOptions[0].Type, owner);
                                }
                                else
                                {
                                    ImGui.OpenPopup("CreateElement");
                                }
                            }

                            if (availableTypeCount > 1)
                            {
                                DrawCollectionTypePickerPopup("CreateElement", declaredElementType, null, selectedType =>
                                {
                                    TryReplaceCollectionInstance(adapter, i, selectedType, owner);
                                });
                            }
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                if (adapter.CanAddRemove)
                {
                    if (elementIsAsset)
                    {
                        int assetTypeCount = availableTypeOptions.Count;

                        if (assetTypeCount == 0)
                        {
                            using (new ImGuiDisabledScope(true))
                                ImGui.Button($"Create Asset##{property.Name}");
                        }
                        else
                        {
                            string createLabel = assetTypeCount == 1
                                ? $"Create {availableTypeOptions[0].DisplayName}##{property.Name}"
                                : $"Create Asset##{property.Name}";

                            if (ImGui.Button(createLabel))
                            {
                                if (assetTypeCount == 1)
                                {
                                    TryAddCollectionInstance(adapter, availableTypeOptions[0].Type, owner);
                                }
                                else
                                {
                                    ImGui.OpenPopup("CreateAssetElement");
                                }
                            }

                            if (assetTypeCount > 1)
                            {
                                DrawCollectionTypePickerPopup("CreateAssetElement", declaredElementType, null, selectedType =>
                                {
                                    TryAddCollectionInstance(adapter, selectedType, owner);
                                });
                            }

                            ImGui.SameLine(0f, 6f);
                            if (ImGui.Button($"Pick Asset##{property.Name}"))
                                ImGui.OpenPopup("AddAssetElement");

                            DrawCollectionAssetAddPopup("AddAssetElement", adapter, owner, availableTypeOptions);
                        }
                    }
                    else if (elementUsesTypeSelector)
                    {
                        int typeCount = availableTypeOptions.Count;

                        if (typeCount == 0)
                        {
                            using (new ImGuiDisabledScope(true))
                                ImGui.Button($"Add Element##{property.Name}");
                        }
                        else if (typeCount == 1)
                        {
                            string typeLabel = $"Add {availableTypeOptions[0].DisplayName}##{property.Name}";
                            if (ImGui.Button(typeLabel))
                                TryAddCollectionInstance(adapter, availableTypeOptions[0].Type, owner);
                        }
                        else
                        {
                            if (ImGui.Button($"Add {GetFriendlyCollectionTypeName(effectiveDeclaredType)}##{property.Name}"))
                                ImGui.OpenPopup("AddCollectionElement");

                            DrawCollectionTypePickerPopup("AddCollectionElement", declaredElementType, null, selectedType =>
                            {
                                TryAddCollectionInstance(adapter, selectedType, owner);
                            });
                        }
                    }
                    else
                    {
                        if (ImGui.Button($"Add Element##{property.Name}"))
                        {
                            object? newElement = adapter.CreateDefaultElement();
                            if (adapter.TryAdd(newElement))
                                NotifyInspectorValueEdited(owner);
                        }
                    }
                }
                else
                {
                    using (new ImGuiDisabledScope(true))
                    {
                        ImGui.Button($"Add Element##{property.Name}");
                    }
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

        private static bool ShouldUseCollectionTypeSelector(Type type)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;
            if (typeof(XRAsset).IsAssignableFrom(effective))
                return false;
            if (IsSimpleSettingType(effective))
                return false;
            return !effective.IsValueType;
        }

        private static string GetFriendlyCollectionTypeName(Type type)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;
            string name = effective.Name;
            int backtick = name.IndexOf('`');
            if (backtick >= 0)
                name = name[..backtick];
            return string.IsNullOrWhiteSpace(name) ? effective.FullName ?? effective.Name : name;
        }

        private static void DrawCollectionTypePickerPopup(string popupId, Type baseType, Type? currentType, Action<Type> onSelected)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            string searchKey = popupId;
            string search = _collectionTypePickerSearch.TryGetValue(searchKey, out var existing) ? existing : string.Empty;
            if (ImGui.InputTextWithHint("##CollectionTypeSearch", "Search...", ref search, 256u))
                _collectionTypePickerSearch[searchKey] = search.Trim();

            ImGui.Separator();

            var descriptors = GetCollectionTypeDescriptors(baseType);
            IEnumerable<CollectionTypeDescriptor> filtered = descriptors;
            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = descriptors.Where(d =>
                    d.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(d.Namespace) && d.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase))
                    || d.FullName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var filteredList = filtered.ToList();

            if (ImGui.BeginChild("##CollectionTypeList", new Vector2(0f, 240f), ImGuiChildFlags.Border))
            {
                if (filteredList.Count == 0)
                {
                    ImGui.TextDisabled("No matching types.");
                }
                else
                {
                    foreach (var descriptor in filteredList)
                    {
                        bool selected = currentType == descriptor.Type;
                        string label = $"{descriptor.DisplayName}##{descriptor.FullName}";
                        if (ImGui.Selectable(label, selected))
                        {
                            onSelected(descriptor.Type);
                            ImGui.CloseCurrentPopup();
                            ImGui.EndChild();
                            ImGui.EndPopup();
                            return;
                        }

                        if (ImGui.IsItemHovered())
                        {
                            string tooltip = descriptor.FullName;
                            if (!string.IsNullOrEmpty(descriptor.AssemblyName))
                                tooltip += $" ({descriptor.AssemblyName})";
                            ImGui.SetTooltip(tooltip);
                        }

                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndChild();
            }

            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        private static IReadOnlyList<CollectionTypeDescriptor> GetCollectionTypeDescriptors(Type baseType)
        {
            baseType = Nullable.GetUnderlyingType(baseType) ?? baseType;

            if (_collectionTypeDescriptorCache.TryGetValue(baseType, out var cached))
                return cached;

            var descriptors = new List<CollectionTypeDescriptor>();
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
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    descriptors.Add(new CollectionTypeDescriptor(
                        type,
                        type.Name,
                        type.Namespace ?? string.Empty,
                        assembly.GetName().Name ?? assembly.FullName ?? "Unknown"));
                }
            }

            descriptors.Sort(static (a, b) =>
            {
                int nameCompare = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                    return nameCompare;
                int nsCompare = string.Compare(a.Namespace, b.Namespace, StringComparison.OrdinalIgnoreCase);
                if (nsCompare != 0)
                    return nsCompare;
                return string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase);
            });

            _collectionTypeDescriptorCache[baseType] = descriptors;
            return descriptors;
        }

        private static object? CreateInstanceForCollectionType(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to create instance of '{type.FullName}': {ex.Message}");
                return null;
            }
        }

        private static void DrawCollectionAssetElement(Type declaredElementType, Type? runtimeType, XRAsset? currentValue, ImGuiEditorUtilities.CollectionEditorAdapter adapter, int index, object? owner)
        {
            Type? assetType = ResolveAssetEditorType(declaredElementType, runtimeType);
            if (assetType is null)
            {
                ImGui.TextDisabled("Asset editor unavailable for this type.");
                return;
            }

            if (!DrawAssetFieldForCollection("AssetValue", assetType, currentValue, selected =>
            {
                if (adapter.TryReplace(index, selected))
                    NotifyInspectorValueEdited(owner);
            }))
            {
                ImGui.TextDisabled("Asset editor unavailable for this type.");
            }
        }

        private static void DrawCollectionAssetAddPopup(string popupId, ImGuiEditorUtilities.CollectionEditorAdapter adapter, object? owner, IReadOnlyList<CollectionTypeDescriptor> assetTypeOptions)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            if (assetTypeOptions.Count == 0)
            {
                ImGui.TextDisabled("No concrete asset types available.");
                if (ImGui.Button("Close"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                return;
            }

            bool closeRequested = false;
            for (int i = 0; i < assetTypeOptions.Count; i++)
            {
                var descriptor = assetTypeOptions[i];
                ImGui.PushID(descriptor.FullName);
                ImGui.TextUnformatted(descriptor.DisplayName);

                if (!DrawAssetFieldForCollection("NewAssetValue", descriptor.Type, null, selected =>
                {
                    if (selected is null)
                        return;
                    if (adapter.TryAdd(selected))
                    {
                        NotifyInspectorValueEdited(owner);
                        closeRequested = true;
                    }
                }))
                {
                    ImGui.TextDisabled("Unable to draw asset selector for this type.");
                }

                ImGui.PopID();

                if (i < assetTypeOptions.Count - 1)
                    ImGui.Separator();
            }

            if (closeRequested)
            {
                ImGui.CloseCurrentPopup();
            }
            else if (ImGui.Button("Close"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        private static bool TryAddCollectionInstance(ImGuiEditorUtilities.CollectionEditorAdapter adapter, Type type, object? owner)
        {
            object? instance = CreateInstanceForCollectionType(type);
            if (instance is null)
                return false;
            if (!adapter.TryAdd(instance))
                return false;

            NotifyInspectorValueEdited(owner);
            return true;
        }

        private static bool TryReplaceCollectionInstance(ImGuiEditorUtilities.CollectionEditorAdapter adapter, int index, Type type, object? owner)
        {
            object? instance = CreateInstanceForCollectionType(type);
            if (instance is null)
                return false;
            if (!adapter.TryReplace(index, instance))
                return false;

            NotifyInspectorValueEdited(owner);
            return true;
        }

        private static bool DrawAssetFieldForCollection(string id, Type assetType, XRAsset? current, Action<XRAsset?> assign)
        {
            if (!typeof(XRAsset).IsAssignableFrom(assetType))
                return false;
            if (assetType.IsAbstract || assetType.ContainsGenericParameters)
                return false;
            if (assetType.GetConstructor(Type.EmptyTypes) is null)
                return false;

            _drawAssetCollectionElementMethod.MakeGenericMethod(assetType).Invoke(null, new object?[] { id, current, assign });
            return true;
        }

        private static Type? ResolveAssetEditorType(Type declaredElementType, Type? runtimeType)
        {
            Type? candidate = runtimeType ?? (Nullable.GetUnderlyingType(declaredElementType) ?? declaredElementType);
            if (candidate is null || !typeof(XRAsset).IsAssignableFrom(candidate))
                return null;

            if (!candidate.IsAbstract && !candidate.ContainsGenericParameters && candidate.GetConstructor(Type.EmptyTypes) is not null)
                return candidate;

            return null;
        }

        private static void DrawAssetCollectionElementGeneric<TAsset>(string id, XRAsset? currentBase, Action<XRAsset?> assign)
            where TAsset : XRAsset, new()
        {
            TAsset? typedCurrent = currentBase as TAsset;
            ImGuiAssetUtilities.DrawAssetField<TAsset>(id, typedCurrent, asset => assign(asset));
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
                object? existing = list[index];
                if (Equals(existing, newValue))
                    return false;

                list[index] = newValue!;
                NotifyInspectorValueEdited(null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyInspectorValue(object owner, PropertyInfo property, object? previousValue, object? newValue)
        {
            if (Equals(previousValue, newValue))
                return false;

            property.SetValue(owner, newValue);
            NotifyInspectorValueEdited(owner);
            return true;
        }

        private static void NotifyInspectorValueEdited(object? valueOwner)
        {
            XRAsset? asset = null;

            if (valueOwner is XRAsset assetOwner)
                asset = assetOwner.SourceAsset;
            else if (_inspectorAssetContext is not null)
                asset = _inspectorAssetContext;

            if (asset is not null && !asset.IsDirty)
                asset.MarkDirty();
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
                        if (TryApplyInspectorValue(owner, property, currentValue, boolValue))
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
                using (new ImGuiDisabledScope(!canWrite || enumNames.Length == 0))
                {
                    if (enumNames.Length > 0 && ImGui.Combo("##Value", ref selectedIndex, enumNames, enumNames.Length) && canWrite && selectedIndex >= 0 && selectedIndex < enumNames.Length)
                    {
                        object newValue = Enum.Parse(effectiveType, enumNames[selectedIndex]);
                        if (TryApplyInspectorValue(owner, property, currentValue, newValue))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText("##Value", ref textValue, 512u, ImGuiInputTextFlags.None) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, textValue))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat2("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, vector))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat3("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, vector))
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
                using (new ImGuiDisabledScope(!canWrite))
                {
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.DragFloat4("##Value", ref vector, 0.05f) && canWrite)
                    {
                        if (TryApplyInspectorValue(owner, property, currentValue, vector))
                        {
                            currentValue = vector;
                            isCurrentlyNull = false;
                        }
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
                            if (TryApplyInspectorValue(owner, property, currentValue, defaultValue))
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
                        if (TryApplyInspectorValue(owner, property, currentValue, null))
                        {
                            currentValue = null;
                            isCurrentlyNull = true;
                        }
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
        {
            var previousValue = currentValue;

            bool Apply(object? newValue)
            {
                if (!TryApplyInspectorValue(owner, property, previousValue, newValue))
                    return false;

                previousValue = newValue;
                return true;
            }

            return TryDrawNumericEditor(effectiveType, canWrite, ref currentValue, ref isCurrentlyNull, Apply, "##Value");
        }

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
                        if (TryApplyInspectorValue(owner, property, currentValue, newColor))
                        {
                            currentValue = newColor;
                            isCurrentlyNull = false;
                        }
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
                        if (TryApplyInspectorValue(owner, property, currentValue, newColor))
                        {
                            currentValue = newColor;
                            isCurrentlyNull = false;
                        }
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

        private readonly struct InspectorAssetContextScope : IDisposable
        {
            private readonly XRAsset? _previous;

            public InspectorAssetContextScope(XRAsset? asset)
            {
                _previous = _inspectorAssetContext;
                _inspectorAssetContext = asset;
            }

            public void Dispose()
            {
                _inspectorAssetContext = _previous;
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
            public bool UseTileView { get; set; }
            public float TileViewScale { get; set; } = 1.0f;
            public string? RenamingPath { get; set; }
            public bool RenamingIsDirectory { get; set; }
            public bool RenameFocusRequested { get; set; }
            public Dictionary<string, AssetExplorerPreviewCacheEntry> PreviewCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record CollectionTypeDescriptor(Type Type, string DisplayName, string Namespace, string AssemblyName)
        {
            public string FullName => Type.FullName ?? Type.Name;
        }

        private sealed class AssetExplorerContextAction
        {
            public AssetExplorerContextAction(string label, Action<string> handler, Func<string, bool>? predicate)
            {
                Label = label;
                Handler = handler;
                Predicate = predicate;
            }

            public string Label { get; }
            public Action<string> Handler { get; }
            public Func<string, bool>? Predicate { get; }

            public bool ShouldDisplay(string path)
                => Predicate?.Invoke(path) ?? true;
        }

        private sealed class AssetExplorerPreviewCacheEntry
        {
            public AssetExplorerPreviewCacheEntry(string path)
            {
                Path = path;
            }

            public string Path { get; private set; }
            public XRTexture2D? Texture { get; set; }
            public bool RequestInFlight { get; set; }
            public uint RequestedSize { get; set; }

            public void UpdatePath(string path)
                => Path = path;
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
            public int ThreadId;
            public string Name = string.Empty;
            public DateTime LastSeen;
            public bool IsStale;
            public Engine.CodeProfiler.ProfilerThreadSnapshot? Snapshot;
            public float[] Samples = Array.Empty<float>();
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

        private class ProfilerRootNodeAggregate
        {
            public string Name { get; set; } = string.Empty;
            public double TotalTimeMs { get; set; }
            public int Calls { get; set; }
            public List<Engine.CodeProfiler.ProfilerNodeSnapshot> Nodes { get; } = new();
        }
    }
}
