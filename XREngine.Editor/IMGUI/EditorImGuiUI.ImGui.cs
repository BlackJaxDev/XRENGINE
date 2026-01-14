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
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Core.Files;
using XREngine.Scene;
using XREngine.Scene.Components.UI;
using XREngine.Scene.Transforms;
using XREngine.Editor.AssetEditors;
using XREngine.Editor.ComponentEditors;
using XREngine.Diagnostics;
using XREngine.Editor.TransformEditors;
using XREngine.Editor.UI.Tools;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
        private const string HierarchyAddComponentPopupId = "HierarchyAddComponent";
        private static readonly List<ComponentTypeDescriptor> _componentTypeDescriptors = [];
        private static readonly byte[] _renameBuffer = new byte[256];
        private static readonly List<ComponentTypeDescriptor> _filteredComponentTypes = [];
        private static readonly Dictionary<Type, IXRComponentEditor?> _componentEditorCache = new();
        private static readonly Dictionary<Type, IXRAssetInspector?> _assetInspectorCache = new();
        private static readonly Dictionary<string, MethodInfo?> _assetContextMenuHandlerCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<Type, IXRTransformEditor?> _transformEditorCache = new();
        private static readonly SceneNode.ETransformSetFlags TransformTypeChangeFlags =
            SceneNode.ETransformSetFlags.RetainCurrentParent |
            SceneNode.ETransformSetFlags.RetainWorldTransform |
            SceneNode.ETransformSetFlags.ClearNewChildren |
            SceneNode.ETransformSetFlags.RetainCurrentChildren |
            SceneNode.ETransformSetFlags.RetainedChildrenMaintainWorldTransform;
        private static string _transformTypeSearch = string.Empty;
        private static IReadOnlyList<TransformTypeEntry>? _transformTypeEntries;
        private static readonly Dictionary<int, ProfilerThreadCacheEntry> _profilerThreadCache = new();
        private static bool _showProfiler;
        private static bool _showOpenGLApiObjects;
        private static bool _showOpenGLErrors;
        private static bool _showMissingAssets;
        private static bool _showEngineSettings;
        private static bool _showUserSettings;
        private static bool _showBuildSettings;
        private static bool _showStatePanel;
        private static bool _showRenderPipelineGraph;
        private static bool _showShaderGraphPanel;
        private static int _probePreviewLayer;
        private static bool _showHierarchy = true;
        private static bool _showEditorSceneHierarchy;
        private static bool _showInspector = true;
        private static bool _showAssetExplorer = true;
        private static bool _showNetworking;
        private static bool _profilerSortByTime;
        private static readonly Dictionary<string, bool> _profilerNodeOpenCache = new();
        private static readonly TimeSpan ProfilerThreadCacheTimeout = TimeSpan.FromSeconds(15.0);
        private static readonly TimeSpan ProfilerThreadStaleThreshold = TimeSpan.FromSeconds(10.0);

        private static bool _renameInputFocusRequested;
        private static bool _imguiStyleInitialized;
        private static Vector4? _imguiBaseWindowBg;
        private static Vector4? _imguiBaseChildBg;
        private static Vector4? _imguiBaseDockingEmptyBg;
        private static bool _componentTypeCacheDirty = true;
        private static float _lastProfilerCaptureTime;

        private static SceneNode? _nodePendingRename;
        private static SceneNode? _nodePendingAddComponent;
        private static string _componentPickerSearch = string.Empty;
        private static string? _componentPickerError;

        private static bool _addComponentPopupOpen;
        private static bool _addComponentPopupRequested;

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
        private static readonly MethodInfo _drawAssetCollectionElementMethod = typeof(EditorImGuiUI).GetMethod(nameof(DrawAssetCollectionElementGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static object? _inspectorStandaloneTarget;
        private static string? _inspectorStandaloneTitle;
        private static Action? _inspectorStandaloneClearAction;
        private static XRAsset? _inspectorAssetContext; // Root asset currently rendered in the inspector.


        private static readonly ConcurrentQueue<Action> _queuedSceneEdits = new();

        // Manual asset drag tracking used for FullViewportBehindImGuiUI drops.
        // We intentionally do not rely on ImGui drag/drop targets for the background region
        // because dockspace passthru + docking drags can make target detection unreliable.
        private static bool _assetExplorerWindowHovered;
        private static bool _assetDragInProgress;
        private static string? _assetDragPath;

        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameDisplaySnapshot;
        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameWindowSnapshot;
        private static float _worstFrameDisplayMs;
        private static float _worstFrameWindowMaxMs;
        private static DateTime _worstFrameWindowStart = DateTime.MinValue;
        private static readonly TimeSpan WorstFrameWindowDuration = TimeSpan.FromSeconds(0.5);



        [Flags]
        private enum AssetExplorerSearchScope
        {
            Name = 1 << 0,
            Path = 1 << 1,
            Metadata = 1 << 2,
        }

        static EditorImGuiUI()
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
            Engine.Time.Timer.UpdateFrame += EnsureScenePanelWindowHooked;
            Selection.SelectionChanged += HandleSceneSelectionChanged;
        }

        private static XRWindow? _scenePanelHookedWindow;
        private static XRViewport? _scenePanelImGuiViewport;

        private static void EnsureScenePanelWindowHooked()
        {
            if (_scenePanelHookedWindow is not null)
                return;

            if (!Engine.IsEditor)
                return;

            if (Engine.Windows.Count <= 0)
                return;

            var window = Engine.Windows[0];
            if (window is null)
                return;

            _scenePanelHookedWindow = window;
            window.RenderViewportsCallback += RenderEditorScenePanelMode;
        }

        private static void RenderEditorScenePanelMode()
        {
            if (Engine.Rendering.Settings.ViewportPresentationMode != Engine.Rendering.EngineSettings.EViewportPresentationMode.UseViewportPanel)
                return;

            if (AbstractRenderer.Current is not { } renderer)
                return;

            XRWindow? window = _scenePanelHookedWindow;
            XRViewport? viewport = window?.Viewports.FirstOrDefault();
            if (viewport is null)
                return;

            // IMPORTANT: In viewport-panel mode the real window viewports are resized/offset to the panel bounds.
            // If we render ImGui using those viewports, ImGui's coordinate space inherits that offset and the
            // computed panel bounds get offset again ("double padding" / drifting render origin).
            // Render ImGui using a dedicated full-window viewport that is NOT part of the window's viewport list.
            var fbSize = window!.Window.FramebufferSize;
            if (fbSize.X <= 0 || fbSize.Y <= 0)
                return;

            _scenePanelImGuiViewport ??= new XRViewport(window);
            _scenePanelImGuiViewport.Window = window;
            _scenePanelImGuiViewport.Resize((uint)fbSize.X, (uint)fbSize.Y, setInternalResolution: false);

            // Clear the backbuffer since we won't be rendering the world to it in this mode.
            try
            {
                var fbSize2 = window!.Window.FramebufferSize;
                renderer.BindFrameBuffer(EFramebufferTarget.Framebuffer, null);
                renderer.SetRenderArea(new XREngine.Data.Geometry.BoundingRectangle(0, 0, fbSize2.X, fbSize2.Y));
                renderer.ClearColor(new XREngine.Data.Colors.ColorF4(0f, 0f, 0f, 1f));
                renderer.Clear(color: true, depth: true, stencil: false);
            }
            catch
            {
                // Best-effort clear; don't fail the frame.
            }

            renderer.TryRenderImGui(_scenePanelImGuiViewport, canvas: null, camera: viewport.ActiveCamera, draw: RenderEditor);
        }

        private static partial void BeginAddComponentForHierarchyNode(SceneNode node);
        private static partial void DrawHierarchyAddComponentPopup();
        private static partial void DrawInspectorPanel();
        private static partial void DrawSceneNodeInspector(SceneNode node);
        private static partial void DrawSceneNodeBasics(SceneNode node);
        private static partial void DrawInspectorRow(string label, Action drawValue);
        private static partial void DrawTransformInspector(TransformBase transform, HashSet<object> visited);
        private static partial void DrawComponentInspectors(SceneNode node, HashSet<object> visited);
        private static partial void DrawInspectableObject(InspectorTargetSet targets, string id, HashSet<object> visited);
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
                actions = [];
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

        private static bool _dockingLayoutInitialized;
        private const string MainDockSpaceId = "MainDockSpace";
        private const string HierarchyWindowId = "Hierarchy";
        private const string ViewportWindowId = "Viewport";
        private const string InspectorWindowId = "Inspector";
        private const string AssetsWindowId = "Assets";
        private const string ConsoleWindowId = "Console";

        public static void RenderEditor()
        {
            using var profilerScope = Engine.Profiler.Start("EditorImGuiUI.RenderEditor");

            // In play mode, don't let ImGui capture keyboard input - let the game pawn receive it
            bool inPlayMode =
                Engine.PlayMode.State == EPlayModeState.Play || 
                Engine.PlayMode.State == EPlayModeState.EnteringPlay;

            var io = ImGui.GetIO();
            bool captureKeyboard = !inPlayMode && (io.WantCaptureKeyboard || io.WantTextInput);
            ImGuiUndoHelper.BeginFrame();
            
            bool showSettings = UnitTestingWorld.Toggles.DearImGuiUI;
            if (!showSettings)
            {
                Engine.Input.SetUIInputCaptured(false);
                return;
            }

            // Reset per-frame UI state; other panels set these during their draw.
            _assetExplorerWindowHovered = false;
            _assetDragInProgress = false;

            EnsureProfessionalImGuiStyling();
            ApplyViewportModeImGuiBackgroundAlpha();

            SuppressUnexpectedImGuiDebugWindows();

            // Draw menu bar and toolbar first
            DrawMainMenuBar();
            DrawToolbar();

            // Calculate dock space position accounting for menu bar and toolbar
            var viewport = ImGui.GetMainViewport();
            float menuBarHeight = ImGui.GetFrameHeight();
            float toolbarHeight = GetToolbarReservedHeight();
            float totalReservedHeight = menuBarHeight + toolbarHeight;

            // Create the dock space below the toolbar
            ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X, viewport.Pos.Y + totalReservedHeight));
            ImGui.SetNextWindowSize(new Vector2(viewport.Size.X, viewport.Size.Y - totalReservedHeight));
            ImGui.SetNextWindowViewport(viewport.ID);
            
            ImGuiWindowFlags dockSpaceWindowFlags = 
                ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.NoBackground;
            
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            
            ImGui.Begin("DockSpaceWindow", dockSpaceWindowFlags);
            ImGui.PopStyleVar(3);

            uint dockSpaceId = ImGui.GetID(MainDockSpaceId);

            ImGui.DockSpace(dockSpaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
            
            // Initialize default docking layout if not yet done
            if (!_dockingLayoutInitialized)
            {
                InitializeDefaultDockingLayout(dockSpaceId, viewport);
                _dockingLayoutInitialized = true;
            }
            
            ImGui.End();

            UnitTestingWorld.UserInterface.DrawNewProjectDialog();
            DrawArchiveImportDialog();

            DrawProfilerPanel();
            DrawConsolePanel();
            DrawStatePanel();
            DrawOpenGLApiObjectsPanel();
            DrawOpenGLErrorsPanel();
            DrawMissingAssetsPanel();
            DrawEngineSettingsPanel();
            DrawUserSettingsPanel();
            DrawBuildSettingsPanel();
            DrawNetworkingPanel();
            DrawRenderPipelineGraphPanel();
            DrawShaderGraphPanel();
            DrawHierarchyPanel();
            DrawScenePanel();
            DrawInspectorPanel();
            DrawAssetExplorerPanel();

            // Tool windows
            UI.Tools.ShaderLockingWindow.Instance.Render();
            UI.Tools.ShaderLockingWindow.Instance.RenderDialogs();
            UI.Tools.ShaderAnalyzerWindow.Instance.Render();

            // Background-mode model spawning on drop.
            // We track the dragged asset path from the Asset Explorer and spawn when the mouse is
            // released over the dockspace region (but not while the Assets window is hovered).
            if (Engine.Rendering.Settings.ViewportPresentationMode == Engine.Rendering.EngineSettings.EViewportPresentationMode.FullViewportBehindImGuiUI)
            {
                if (!string.IsNullOrWhiteSpace(_assetDragPath) && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    // Avoid spawning when releasing over the Assets panel.
                    if (!_assetExplorerWindowHovered)
                    {
                        Vector2 dockMin = new(viewport.Pos.X, viewport.Pos.Y + totalReservedHeight);
                        Vector2 dockMax = dockMin + new Vector2(viewport.Size.X, viewport.Size.Y - totalReservedHeight);

                        // Avoid ImGui.IsMouseHoveringRect(..., clip: true) here: the clip rect is tied
                        // to whatever window happened to render last, which can make this always-false.
                        var mousePos = ImGui.GetMousePos();
                        bool mouseInDockRect =
                            mousePos.X >= dockMin.X && mousePos.Y >= dockMin.Y &&
                            mousePos.X <= dockMax.X && mousePos.Y <= dockMax.Y;

                        if (mouseInDockRect)
                        {
                            var world = TryGetActiveWorldInstance();
                            if (world is not null)
                            {
                                string path = _assetDragPath;
                                if (TryLoadPrefabAsset(path, out var prefab))
                                    EnqueueSceneEdit(() => SpawnPrefabNode(world, parent: null, prefab!));
                                else if (TryLoadModelAsset(path, out var model))
                                    EnqueueSceneEdit(() => SpawnModelNode(world, parent: null, model!, path));
                            }
                        }
                    }

                    // Consume the drag regardless to avoid repeats.
                    _assetDragPath = null;
                }

                // Cleanup: if we're not actively dragging anymore, clear lingering state.
                if (!_assetDragInProgress && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    _assetDragPath = null;
            }

            bool uiWantsCapture = io.WantCaptureMouse || captureKeyboard;
            bool allowEngineInputThroughScenePanel =
                Engine.Rendering.Settings.ViewportPresentationMode == Engine.Rendering.EngineSettings.EViewportPresentationMode.UseViewportPanel &&
                _scenePanelInteracting;

            Engine.Input.SetUIInputCaptured(uiWantsCapture && !allowEngineInputThroughScenePanel && Engine.PlayMode.State != EPlayModeState.EnteringPlay && !Engine.PlayMode.IsPlaying);
        }

        private static void SuppressUnexpectedImGuiDebugWindows()
        {
            // Some ImGui backends/sample layers can create an always-on "Debug" window.
            // We don't want that in the editor UI.
            //
            // We use reflection so this remains tolerant of ImGui.NET version differences.
            TryHideImGuiWindowByName("Debug");
            TryHideImGuiWindowByName("Debug##Default");
        }

        private static void TryHideImGuiWindowByName(string windowName)
        {
            try
            {
                var imguiType = typeof(ImGui);

                // Prefer collapsing; if the window is undocked this effectively removes it.
                var setCollapsed = imguiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (!string.Equals(m.Name, "SetWindowCollapsed", StringComparison.Ordinal))
                            return false;
                        var p = m.GetParameters();
                        return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(bool);
                    });

                if (setCollapsed is not null)
                {
                    var parameters = setCollapsed.GetParameters();
                    object?[]? args = parameters.Length switch
                    {
                        2 => [windowName, true],
                        3 => [windowName, true, ImGuiCond.Always],
                        _ => null
                    };

                    if (args is not null)
                        setCollapsed.Invoke(null, args);
                }

                // Fallback: move it far off-screen (helps if collapsing isn't supported).
                var setPos = imguiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (!string.Equals(m.Name, "SetWindowPos", StringComparison.Ordinal))
                            return false;
                        var p = m.GetParameters();
                        return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(Vector2);
                    });

                if (setPos is not null)
                {
                    var parameters = setPos.GetParameters();
                    object?[]? args = parameters.Length switch
                    {
                        2 => [windowName, new Vector2(-100000f, -100000f)],
                        3 => [windowName, new Vector2(-100000f, -100000f), ImGuiCond.Always],
                        _ => null
                    };

                    if (args is not null)
                        setPos.Invoke(null, args);
                }
            }
            catch
            {
                // If the window isn't present or API differs, ignore.
            }
        }

        /// <summary>
        /// Initializes the default docking layout:
        /// - Hierarchy on the left
        /// - Inspector on the right  
        /// - Assets and Console tabbed at the bottom
        /// - Central viewport in the middle
        /// </summary>
        private static void InitializeDefaultDockingLayout(uint dockSpaceId, ImGuiViewportPtr viewport)
        {
            // Check if we have a saved layout by seeing if the dock node already exists and has children
            // If ImGui loaded a layout from ini, the dock space will already be populated
            if (ImGuiDockBuilderNative.NodeExists(dockSpaceId))
            {
                // Node exists - layout was loaded from ini file, don't override
                // But we still want to ensure the first-time setup happens if it's empty
                IntPtr nodePtr = ImGuiDockBuilderNative.GetNode(dockSpaceId);
                // If the node has already been set up (has splits), skip initialization
                // We use a simple heuristic: if the node exists and we've already set up once, skip
                return;
            }

            // Clear and rebuild the dock layout
            ImGuiDockBuilderNative.RemoveNode(dockSpaceId);
            ImGuiDockBuilderNative.AddNode(dockSpaceId, ImGuiDockNodeFlags.PassthruCentralNode);
            
            float availableWidth = viewport.Size.X;
            float availableHeight = viewport.Size.Y - ImGui.GetFrameHeight() - GetToolbarReservedHeight();
            ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));
            ImGuiDockBuilderNative.SetNodePos(dockSpaceId, new Vector2(viewport.Pos.X, viewport.Pos.Y + ImGui.GetFrameHeight() + GetToolbarReservedHeight()));

            // Split: Left (Hierarchy) | Center+Right
            ImGuiDockBuilderNative.SplitNode(dockSpaceId, ImGuiDir.Left, 0.18f, out uint leftDockId, out uint remainingId);
            
            // Split remaining: Center+Bottom | Right (Inspector)
            ImGuiDockBuilderNative.SplitNode(remainingId, ImGuiDir.Right, 0.22f, out uint rightDockId, out uint centerBottomId);
            
            // Split center+bottom: Center (viewport) | Bottom (Assets/Console)
            ImGuiDockBuilderNative.SplitNode(centerBottomId, ImGuiDir.Down, 0.25f, out uint bottomDockId, out uint centerDockId);

            // Dock windows
            ImGuiDockBuilderNative.DockWindow(HierarchyWindowId, leftDockId);
            ImGuiDockBuilderNative.DockWindow(InspectorWindowId, rightDockId);
            ImGuiDockBuilderNative.DockWindow(ViewportWindowId, centerDockId);
            ImGuiDockBuilderNative.DockWindow(AssetsWindowId, bottomDockId);
            ImGuiDockBuilderNative.DockWindow(ConsoleWindowId, bottomDockId); // Same dock as Assets (tabbed)

            ImGuiDockBuilderNative.Finish(dockSpaceId);
        }

        /// <summary>
        /// Resets the docking layout to the default configuration.
        /// </summary>
        private static void ResetDockingLayout()
        {
            uint dockSpaceId = ImGui.GetID(MainDockSpaceId);
            var viewport = ImGui.GetMainViewport();
            
            // Force re-initialization by clearing the node and rebuilding
            ImGuiDockBuilderNative.RemoveNode(dockSpaceId);
            ImGuiDockBuilderNative.AddNode(dockSpaceId, ImGuiDockNodeFlags.PassthruCentralNode);
            
            float availableWidth = viewport.Size.X;
            float availableHeight = viewport.Size.Y - ImGui.GetFrameHeight() - GetToolbarReservedHeight();
            ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));
            ImGuiDockBuilderNative.SetNodePos(dockSpaceId, new Vector2(viewport.Pos.X, viewport.Pos.Y + ImGui.GetFrameHeight() + GetToolbarReservedHeight()));

            // Split: Left (Hierarchy) | Center+Right
            ImGuiDockBuilderNative.SplitNode(dockSpaceId, ImGuiDir.Left, 0.18f, out uint leftDockId, out uint remainingId);
            
            // Split remaining: Center+Bottom | Right (Inspector)
            ImGuiDockBuilderNative.SplitNode(remainingId, ImGuiDir.Right, 0.22f, out uint rightDockId, out uint centerBottomId);
            
            // Split center+bottom: Center (viewport) | Bottom (Assets/Console)
            ImGuiDockBuilderNative.SplitNode(centerBottomId, ImGuiDir.Down, 0.25f, out uint bottomDockId, out uint centerDockId);

            // Dock windows
            ImGuiDockBuilderNative.DockWindow(HierarchyWindowId, leftDockId);
            ImGuiDockBuilderNative.DockWindow(InspectorWindowId, rightDockId);
            ImGuiDockBuilderNative.DockWindow(ViewportWindowId, centerDockId);
            ImGuiDockBuilderNative.DockWindow(AssetsWindowId, bottomDockId);
            ImGuiDockBuilderNative.DockWindow(ConsoleWindowId, bottomDockId);

            ImGuiDockBuilderNative.Finish(dockSpaceId);
        }

        private static void DrawMainMenuBar()
        {
            if (!ImGui.BeginMainMenuBar())
                return;

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("New Project..."))
                    UnitTestingWorld.UserInterface.ShowNewProjectDialog();
                
                if (ImGui.MenuItem("Open Project..."))
                    UnitTestingWorld.UserInterface.OpenProjectDialog(null!);

                ImGui.Separator();

                if (ImGui.MenuItem("Save All", "Ctrl+Shift+S"))
                    UnitTestingWorld.UserInterface.SaveAll(null);

                if (ImGui.BeginMenu("Save..."))
                {
                    DrawDirtyAssetSaveMenuItems();
                    ImGui.EndMenu();
                }

                ImGui.Separator();

                bool canBuildProject = Engine.CurrentProject is not null;
                if (ImGui.MenuItem("Build Project", null, false, canBuildProject))
                    ProjectBuilder.RequestBuild();

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
                                UnitTestingWorld.UserInterface.UndoMultiple(i);
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
                                UnitTestingWorld.UserInterface.RedoMultiple(i);
                        }
                    }
                    ImGui.EndMenu();
                }

                ImGui.Separator();
                if (ImGui.MenuItem("Engine Settings"))
                    OpenSettingsInInspector(Engine.Rendering.Settings, "Engine Settings");

                if (ImGui.MenuItem("User Settings"))
                    OpenSettingsInInspector(Engine.UserSettings, "User Settings");

                if (ImGui.MenuItem("Game Settings"))
                    OpenSettingsInInspector(Engine.GameSettings, "Game Settings");

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Hierarchy", null, ref _showHierarchy);
                ImGui.MenuItem("Inspector", null, ref _showInspector);
                ImGui.MenuItem("Asset Explorer", null, ref _showAssetExplorer);
                ImGui.Separator();
                ImGui.MenuItem("Console", null, ref _showConsole);
                ImGui.MenuItem("Render Pipeline Graph", null, ref _showRenderPipelineGraph);
                ImGui.MenuItem("Engine State", null, ref _showStatePanel);
                ImGui.MenuItem("Profiler", null, ref _showProfiler);
                ImGui.MenuItem("OpenGL API Objects", null, ref _showOpenGLApiObjects);
                ImGui.MenuItem("OpenGL Errors", null, ref _showOpenGLErrors);
                ImGui.MenuItem("Missing Assets", null, ref _showMissingAssets);
                ImGui.MenuItem("Networking", null, ref _showNetworking);
                ImGui.Separator();
                if (ImGui.MenuItem("Reset Layout"))
                    ResetDockingLayout();
                    
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Tools"))
            {
                if (ImGui.MenuItem("Import Archive..."))
                    OpenArchiveImportDialog();

                if (ImGui.MenuItem("Shader Locking Tool"))
                    ShaderLockingWindow.Instance.Open();

                if (ImGui.MenuItem("Shader Analyzer Tool"))
                    ShaderAnalyzerWindow.Instance.Open();

                if (ImGui.MenuItem("Shader Graph Builder"))
                    _showShaderGraphPanel = true;

                ImGui.EndMenu();
            }

            DrawProjectStatusIndicator();
            DrawJobProgressIndicator();
            ImGui.EndMainMenuBar();
        }


        private static void DrawProjectStatusIndicator()
        {
            string label;
            bool sandboxMode = Engine.CurrentProject is null;
            if (sandboxMode)
                label = "Sandbox mode";
            else
                label = $"Project: {Engine.CurrentProject!.ProjectName}";

            // Draw centered on the menu/header bar without affecting layout for other indicators.
            // We treat this as an overlay: move cursor, draw, then restore cursor position.
            var originalCursor = ImGui.GetCursorPos();

            ImGui.AlignTextToFramePadding();

            var style = ImGui.GetStyle();
            Vector2 textSize = ImGui.CalcTextSize(label);
            Vector2 regionMin = ImGui.GetWindowContentRegionMin();
            Vector2 regionMax = ImGui.GetWindowContentRegionMax();
            float regionWidth = regionMax.X - regionMin.X;

            float centeredX = regionMin.X + (regionWidth - textSize.X) * 0.5f;
            float minX = regionMin.X + style.WindowPadding.X;
            float maxX = regionMax.X - style.WindowPadding.X - textSize.X;
            if (centeredX < minX)
                centeredX = minX;
            else if (centeredX > maxX)
                centeredX = maxX;

            ImGui.SetCursorPos(new Vector2(centeredX, originalCursor.Y));

            if (sandboxMode)
            {
                var warningColor = new Vector4(0.96f, 0.78f, 0.32f, 1f);
                ImGui.TextColored(warningColor, label);
            }
            else
            {
                ImGui.Text(label);
            }

            ImGui.SetCursorPos(originalCursor);
        }


        private static void DrawJobProgressIndicator()
        {
            var snapshots = EditorJobTracker.GetSnapshots();
            if (snapshots.Count == 0)
                return;

            var snapshot = snapshots[0];
            var style = ImGui.GetStyle();
            float windowWidth = ImGui.GetWindowWidth();
            float indicatorWidth = Math.Max(160f, Math.Min(320f, windowWidth * 0.35f));
            float cursorY = ImGui.GetCursorPosY();

            float desiredX = windowWidth - indicatorWidth - style.WindowPadding.X - style.ItemInnerSpacing.X;
            float cursorX = ImGui.GetCursorPosX();
            if (desiredX < cursorX)
                desiredX = cursorX;

            ImGui.SameLine(0f, 0f);
            ImGui.SetCursorPos(new Vector2(desiredX, cursorY));

            string overlay = BuildJobOverlay(snapshot, snapshots.Count);
            float barHeight = ImGui.GetFrameHeight() - style.FramePadding.Y * 2f;
            if (barHeight < 0f)
                barHeight = ImGui.GetFrameHeight();

            var barSize = new Vector2(indicatorWidth, barHeight);
            var colorOverride = ResolveJobProgressColor(snapshot.State);
            if (colorOverride.HasValue)
            {
                var color = colorOverride.Value;
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
                ImGui.PushStyleColor(ImGuiCol.PlotHistogramHovered, color);
            }

            ImGui.ProgressBar(snapshot.Progress, barSize, overlay);

            if (colorOverride.HasValue)
                ImGui.PopStyleColor(2);
        }

        private static string BuildJobOverlay(EditorJobTracker.TrackedJobSnapshot snapshot, int jobCount)
        {
            string status = snapshot.Status ?? snapshot.Label;
            if (string.IsNullOrWhiteSpace(status))
                status = snapshot.Label;

            if (jobCount > 1)
                status = $"{status} (+{jobCount - 1})";

            return status;
        }

        private static void DrawDirtyAssetSaveMenuItems()
        {
            var assets = Engine.Assets;
            if (assets is null)
            {
                ImGui.MenuItem("Asset system unavailable", null, false, false);
                return;
            }

            var dirtySnapshot = assets.DirtyAssets.ToArray();
            if (dirtySnapshot.Length == 0)
            {
                ImGui.MenuItem("No modified assets", null, false, false);
                return;
            }

            var uniqueRoots = new Dictionary<Guid, XRAsset>(dirtySnapshot.Length);
            foreach (var asset in dirtySnapshot)
            {
                XRAsset root = asset.Value.SourceAsset;
                uniqueRoots[root.ID] = root;
            }

            foreach (var entry in uniqueRoots.Values.OrderBy(static a => UnitTestingWorld.UserInterface.GetAssetDisplayName(a), StringComparer.OrdinalIgnoreCase))
            {
                string label = UnitTestingWorld.UserInterface.GetAssetDisplayName(entry);
                string menuLabel = $"{label}##DirtyAsset{entry.ID}";
                if (ImGui.MenuItem(menuLabel))
                    UnitTestingWorld.UserInterface.SaveSingleAsset(entry);
            }
        }

        private static Vector4? ResolveJobProgressColor(EditorJobTracker.TrackedJobState state)
            => state switch
            {
                EditorJobTracker.TrackedJobState.Faulted => new Vector4(0.9f, 0.25f, 0.25f, 1f),
                EditorJobTracker.TrackedJobState.Canceled => new Vector4(0.6f, 0.6f, 0.6f, 1f),
                _ => null
            };

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

            _imguiBaseWindowBg = colors[(int)ImGuiCol.WindowBg];
            _imguiBaseChildBg = colors[(int)ImGuiCol.ChildBg];
            _imguiBaseDockingEmptyBg = colors[(int)ImGuiCol.DockingEmptyBg];

            _imguiStyleInitialized = true;
        }

        private static void ApplyViewportModeImGuiBackgroundAlpha()
        {
            if (!_imguiStyleInitialized || !_imguiBaseWindowBg.HasValue || !_imguiBaseChildBg.HasValue || !_imguiBaseDockingEmptyBg.HasValue)
                return;

            var colors = ImGui.GetStyle().Colors;
            var mode = Engine.Rendering.Settings.ViewportPresentationMode;

            if (mode == Engine.Rendering.EngineSettings.EViewportPresentationMode.FullViewportBehindImGuiUI)
            {
                var windowBg = _imguiBaseWindowBg.Value;
                windowBg.W = MathF.Min(windowBg.W, 0.70f);
                colors[(int)ImGuiCol.WindowBg] = windowBg;

                var childBg = _imguiBaseChildBg.Value;
                childBg.W = MathF.Min(childBg.W, 0.55f);
                colors[(int)ImGuiCol.ChildBg] = childBg;

                ImGui.MenuItem("Render Pipeline Graph", null, ref _showRenderPipelineGraph);
                var dockingBg = _imguiBaseDockingEmptyBg.Value;
                dockingBg.W = MathF.Min(dockingBg.W, 0.35f);
                colors[(int)ImGuiCol.DockingEmptyBg] = dockingBg;
            }
            else
            {
                colors[(int)ImGuiCol.WindowBg] = _imguiBaseWindowBg.Value;
                colors[(int)ImGuiCol.ChildBg] = _imguiBaseChildBg.Value;
                colors[(int)ImGuiCol.DockingEmptyBg] = _imguiBaseDockingEmptyBg.Value;
            }
        }

        private static XRWorldInstance? TryGetActiveWorldInstance()
        {
            foreach (var window in Engine.Windows)
            {
                var instance = window?.TargetWorldInstance;
                if (instance is not null)
                    return instance;
            }

            foreach (var instance in XRWorldInstance.WorldInstances.Values)
            {
                if (instance is not null)
                    return instance;
            }

            return null;
        }

        private static void OpenSettingsInInspector(object? settingsRoot, string title)
        {
            if (settingsRoot is null)
                return;

            _showInspector = true;
            SetInspectorStandaloneTarget(settingsRoot, title);
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

        private static partial void HandleInspectorDockResize(ImGuiViewportPtr viewport)
        {
        }

        private static partial void HandleAssetExplorerDockResize(ImGuiViewportPtr viewport, float reservedLeft, float reservedRight, bool dockedTop)
    {
    }
}
