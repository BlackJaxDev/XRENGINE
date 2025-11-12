using ImGuiNET;
using Silk.NET.Input;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Actors.Types;
using XREngine.Components;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Components.Scripting;
using XREngine.Scene.Transforms;
using XREngine.Scene.Components.UI;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static class UserInterface
    {
        private static readonly bool DockFPSTopLeft = false;

        private static readonly Queue<float> _fpsAvg = new();
        private static int _imguiClickCount = 0;
        private static DateTime _worstFrameWindowStart = DateTime.MinValue;
        private static float _worstFrameWindowMaxMs = 0.0f;
        private static float _worstFrameDisplayMs = 0.0f;
        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameWindowSnapshot;
        private static Engine.CodeProfiler.ProfilerFrameSnapshot? _worstFrameDisplaySnapshot;
        private static readonly TimeSpan WorstFrameWindowDuration = TimeSpan.FromSeconds(0.5);
        private static bool _profilerDockLeftEnabled = false;
        private static bool _profilerUndockNextFrame = false;
        private static float _profilerDockWidth = 480.0f;
        private static bool _profilerDockDragging = false;
        private static float _profilerDockDragStartWidth = 0.0f;
        private static float _profilerDockDragStartMouseX = 0.0f;

        private static void TickFPS(UITextComponent t)
        {
            _fpsAvg.Enqueue(1.0f / Engine.Time.Timer.Render.Delta);
            if (_fpsAvg.Count > 60)
                _fpsAvg.Dequeue();
            string str = $"{MathF.Round(_fpsAvg.Sum() / _fpsAvg.Count)}hz";
            var net = Engine.Networking;
            if (net is not null)
            {
                str += $"\n{net.AverageRoundTripTimeMs}ms";
                str += $"\n{net.DataPerSecondString}";
                str += $"\n{net.PacketsPerSecond}p/s";
            }
            t.Text = str;
        }

        //Simple FPS counter in the bottom right for debugging.
        public static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
        {
            SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
            UITextComponent text = textNode.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 22;
            text.WrapMode = FontGlyphSet.EWrapMode.None;
            text.RegisterAnimationTick<UITextComponent>(TickFPS);
            var textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
            if (DockFPSTopLeft)
            {
                textTransform.MinAnchor = new Vector2(0.0f, 1.0f);
                textTransform.MaxAnchor = new Vector2(0.0f, 1.0f);
                textTransform.NormalizedPivot = new Vector2(0.0f, 1.0f);
            }
            else
            {
                textTransform.MinAnchor = new Vector2(1.0f, 0.0f);
                textTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
                textTransform.NormalizedPivot = new Vector2(1.0f, 0.0f);
            }
            textTransform.Width = null;
            textTransform.Height = null;
            textTransform.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
            textTransform.Scale = new Vector3(1.0f);
            return text;
        }

        public static void ShowMenu(UICanvasComponent canvas, bool screenSpace, TransformBase? parent)
        {
            var canvasTfm = canvas.CanvasTransform;
            canvasTfm.Parent = parent;
            canvasTfm.DrawSpace = screenSpace ? ECanvasDrawSpace.Screen : ECanvasDrawSpace.World;
            canvas.IsActive = !canvas.IsActive;
        }

        //The full editor UI - includes a toolbar, inspector, viewport and scene hierarchy.
        public static UICanvasComponent CreateEditorUI(SceneNode parent, CameraComponent? screenSpaceCamera, PawnComponent? pawnForInput = null)
        {
            var rootCanvasNode = new SceneNode(parent) { Name = "TestUINode" };
            var canvas = rootCanvasNode.AddComponent<UICanvasComponent>()!;
            var canvasTfm = rootCanvasNode.GetTransformAs<UICanvasTransform>(true)!;
            canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
            canvasTfm.Width = 1920.0f;
            canvasTfm.Height = 1080.0f;
            canvasTfm.CameraDrawSpaceDistance = 10.0f;
            canvasTfm.Padding = new Vector4(0.0f);

            if (Toggles.RiveUI || Toggles.DearImGuiUI || Toggles.DearImGuiProfiler || Toggles.AddEditorUI)
                rootCanvasNode.AddComponent<UICanvasInputComponent>()!.OwningPawn = pawnForInput;

            if (Toggles.VisualizeQuadtree)
                rootCanvasNode.AddComponent<DebugVisualizeQuadtreeComponent>();

            if (screenSpaceCamera is not null)
                screenSpaceCamera.UserInterface = canvas;

            if (UnitTestingWorld.Toggles.RiveUI)
            {
                bool disableRiveUi = false;
                SceneNode riveNode = new(rootCanvasNode) { Name = "RIVE Node" };
                var tfm = riveNode.SetTransform<UIBoundableTransform>();
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);

                try
                {
                    var riveComponent = riveNode.AddComponent<RiveUIComponent>();
                    if (riveComponent is null)
                        disableRiveUi = true;
                    else
                        riveComponent.SetSource("RiveAssets/switcher.riv");
                }
                catch (DllNotFoundException ex)
                {
                    Debug.LogWarning($"Rive native library missing: {ex.Message}. Disabling Rive UI.");
                    disableRiveUi = true;
                }
                catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException dllEx)
                {
                    Debug.LogWarning($"Rive native library failed to load: {dllEx.Message}. Disabling Rive UI.");
                    disableRiveUi = true;
                }

                if (disableRiveUi)
                {
                    Toggles.RiveUI = false;
                    riveNode.Parent = null;
                }
            }

            bool addDearImGui = UnitTestingWorld.Toggles.DearImGuiUI || UnitTestingWorld.Toggles.DearImGuiProfiler;
            if (addDearImGui)
            {
                SceneNode dearImGuiNode = new(rootCanvasNode) { Name = "Dear ImGui Node" };
                var tfm = dearImGuiNode.SetTransform<UIBoundableTransform>();
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
                tfm.Width = null;
                tfm.Height = null;

                var dearImGuiComponent = dearImGuiNode.AddComponent<DearImGuiComponent>();
                if (dearImGuiComponent is null)
                {
                    Toggles.DearImGuiUI = false;
                    Toggles.DearImGuiProfiler = false;
                    dearImGuiNode.Parent = null;
                }
                else
                {
                    dearImGuiComponent.Draw += DrawDearImGuiTest;
                }
            }
            
            if (Toggles.AddEditorUI)
            {
                //This will take care of editor UI arrangement operations for us
                var mainUINode = rootCanvasNode.NewChild<UIEditorComponent>(out UIEditorComponent? editorComp);
                if (editorComp.UITransform is UIBoundableTransform tfm)
                {
                    tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                    tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                    tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
                    tfm.Translation = new Vector2(0.0f, 0.0f);
                    tfm.Width = null;
                    tfm.Height = null;
                }
                _editorComponent = editorComp;
                RemakeMenu();

                GameCSProjLoader.OnAssemblyLoaded += GameCSProjLoader_OnAssemblyLoaded;
                GameCSProjLoader.OnAssemblyUnloaded += GameCSProjLoader_OnAssemblyUnloaded;
            }

            AddFPSText(null, rootCanvasNode);

            return canvas;
        }

        private static UIEditorComponent? _editorComponent = null;

        public static void GameCSProjLoader_OnAssemblyUnloaded(string obj) => RemakeMenu();
        public static void GameCSProjLoader_OnAssemblyLoaded(string arg1, GameCSProjLoader.AssemblyData arg2) => RemakeMenu();

        public static void RemakeMenu()
        {
            if (_editorComponent is not null)
                _editorComponent.RootMenuOptions = GenerateRootMenu();
        }

        //Signals the camera to take a picture of the current view.
        public static void TakeScreenshot(UIInteractableComponent comp)
        {
            //Debug.Out("Take Screenshot clicked");

            var camera = Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).ControlledPawn as EditorFlyingCameraPawnComponent;
            camera?.TakeScreenshot();
        }
        //Loads a project from the file system.
        public static void LoadProject(UIInteractableComponent comp)
        {
            //Debug.Out("Load Project clicked");
        }
        //Saves all modified assets in the project.
        public static async void SaveAll(UIInteractableComponent comp)
        {
            await Engine.Assets.SaveAllAsync();
        }

        //Generates the root menu for the editor UI.
        //TODO: allow scripts to add menu options with attributes
        public static List<ToolbarItemBase> GenerateRootMenu()
        {
            List<ToolbarItemBase> buttons = [
                new ToolbarButton("File", [Key.ControlLeft, Key.F],
            [
                new ToolbarButton("Save All", SaveAll),
                new ToolbarButton("Open", [
                    new ToolbarButton("Project", LoadProject),
                    ])
            ]),
            new ToolbarButton("Edit"),
            new ToolbarButton("Assets"),
            new ToolbarButton("Tools", [Key.ControlLeft, Key.T],
            [
                new ToolbarButton("Take Screenshot", TakeScreenshot),
            ]),
            new ToolbarButton("View"),
            new ToolbarButton("Window"),
            new ToolbarButton("Help"),
        ];

            //Add dynamically loaded menu options
            foreach (GameCSProjLoader.AssemblyData assembly in GameCSProjLoader.LoadedAssemblies.Values)
            {
                foreach (Type menuItem in assembly.MenuItems)
                {
                    if (!menuItem.IsSubclassOf(typeof(ToolbarItemBase)))
                        continue;

                    buttons.Add((ToolbarItemBase)Activator.CreateInstance(menuItem)!);
                }
            }

            return buttons;
        }

        public static void EnableTransformToolForNode(SceneNode? node)
        {
            if (node is null)
                return;

            //we have to wait for the scene node to be activated in the instance of the world before we can attach the transform tool
            void Edit(SceneNode x)
            {
                TransformTool3D.GetInstance(x.Transform);
                x.Activated -= Edit;
            }

            if (node.IsActiveInHierarchy && node.World is not null)
                TransformTool3D.GetInstance(node.Transform);
            else
                node.Activated += Edit;
        }

        private static void DrawDearImGuiTest()
        {
            bool showSettings = Toggles.DearImGuiUI;
            bool showProfiler = Toggles.DearImGuiProfiler;

            Engine.Profiler.EnableFrameLogging = Toggles.EnableProfilerLogging || showProfiler;

            if (!showSettings && !showProfiler)
                return;

            // Unified left-docked window with tabs for Profiler, Settings, and Hierarchy
            DrawDebugDockWindow(showProfiler, showSettings);
        }

        private static void DrawDebugDockWindow(bool includeProfiler, bool includeSettings)
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

            // Toolbar row
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

                if (includeSettings && ImGui.BeginTabItem("Settings"))
                {
                    DrawSettingsDebugPanel();
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

            // Optional info header
            ImGui.Text($"GameMode: {world.GameMode?.GetType().Name ?? "<none>"}");
            ImGui.Separator();

            int idx = 0;
            foreach (var root in world.RootNodes)
            {
                DrawSceneNodeTree(root, $"Root{idx++}");
            }
        }

        private static void DrawSceneNodeTree(SceneNode node, string path)
        {
            string id = $"{path}/{node.ID}";
            var transform = node.Transform;
            int childCount = transform.Children.Count;
            string label = $"{node.Name ?? "<unnamed>"} ({childCount})##{id}";

            if (childCount > 0)
            {
                if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var child in transform.Children)
                    {
                        if (child?.SceneNode is SceneNode childNode)
                            DrawSceneNodeTree(childNode, id);
                    }
                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen);
            }
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

            if (frameSnapshot is null || frameSnapshot.Threads.Count == 0)
            {
                ImGui.Text("No profiler samples captured yet.");
                ImGui.End();
                return;
            }

            UpdateWorstFrameStatistics(frameSnapshot);
            var snapshotForDisplay = GetSnapshotForHierarchy(frameSnapshot, out float hierarchyFrameMs, out bool showingWorstWindowSample);
            float worstFrameToDisplay = hierarchyFrameMs;

            ImGui.Text($"Captured at {frameSnapshot.FrameTime:F3}s");
            ImGui.Text($"Worst frame (0.5s window): {worstFrameToDisplay:F3} ms");
            if (showingWorstWindowSample)
                ImGui.Text("Hierarchy shows worst frame snapshot from the rolling window.");
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

            if (_profilerDockLeftEnabled)
                HandleProfilerDockResize(overlayViewport);

            ImGui.End();
        }

        private static void DrawProfilerNode(Engine.CodeProfiler.ProfilerNodeSnapshot node, string path)
        {
            string id = $"{path}/{node.Name}";
            bool hasChildren = node.Children.Count > 0;
            string label = $"{node.Name} ({node.ElapsedMs:F3} ms)##{id}";

            if (hasChildren)
            {
                if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
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

        private static void DrawSettingsDebugPanel()
        {
            if (!ImGui.CollapsingHeader("Engine & User Settings", ImGuiTreeNodeFlags.DefaultOpen))
                return;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            var engineSettings = Engine.Rendering.Settings;
            if (engineSettings is not null)
                DrawSettingsObject(engineSettings, "Engine Settings", visited, true);

            var userSettings = Engine.UserSettings;
            if (userSettings is not null)
                DrawSettingsObject(userSettings, "User Settings", visited, true);
        }

        private static void DrawSettingsObject(object obj, string label, HashSet<object> visited, bool defaultOpen)
        {
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
            var properties = obj.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0)
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

                DrawSettingsObject(value, property.Name, visited, false);
            }
        }

        private static void DrawSimplePropertyRow(object owner, PropertyInfo property, object? value)
        {
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

        private static unsafe bool TryDrawNumericProperty(object owner, PropertyInfo property, Type effectiveType, bool canWrite, ref object? currentValue, ref bool isCurrentlyNull)
        {
            if (effectiveType == typeof(float))
            {
                float floatValue = currentValue is float f ? f : 0f;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (ImGui.InputFloat("##Value", ref floatValue) && canWrite && float.IsFinite(floatValue))
                    {
                        property.SetValue(owner, floatValue);
                        currentValue = floatValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(double))
            {
                double doubleValue = currentValue is double d ? d : 0.0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (ImGui.InputDouble("##Value", ref doubleValue) && canWrite && double.IsFinite(doubleValue))
                    {
                        property.SetValue(owner, doubleValue);
                        currentValue = doubleValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(decimal))
            {
                double doubleValue = currentValue is decimal dec ? (double)dec : 0.0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (ImGui.InputDouble("##Value", ref doubleValue) && canWrite && double.IsFinite(doubleValue))
                    {
                        try
                        {
                            decimal newValue = Convert.ToDecimal(doubleValue);
                            property.SetValue(owner, newValue);
                            currentValue = newValue;
                            isCurrentlyNull = false;
                        }
                        catch (OverflowException)
                        {
                            decimal clamped = doubleValue > 0 ? decimal.MaxValue : decimal.MinValue;
                            property.SetValue(owner, clamped);
                            currentValue = clamped;
                            isCurrentlyNull = false;
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
                    if (InputScalar("##Value", ImGuiDataType.S32, ref intValue) && canWrite)
                    {
                        property.SetValue(owner, intValue);
                        currentValue = intValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(uint))
            {
                uint uintValue = currentValue is uint u ? u : 0u;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.U32, ref uintValue) && canWrite)
                    {
                        property.SetValue(owner, uintValue);
                        currentValue = uintValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(long))
            {
                long longValue = currentValue is long l ? l : 0L;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.S64, ref longValue) && canWrite)
                    {
                        property.SetValue(owner, longValue);
                        currentValue = longValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ulong))
            {
                ulong ulongValue = currentValue is ulong ul ? ul : 0UL;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.U64, ref ulongValue) && canWrite)
                    {
                        property.SetValue(owner, ulongValue);
                        currentValue = ulongValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(short))
            {
                short shortValue = currentValue is short s ? s : (short)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.S16, ref shortValue) && canWrite)
                    {
                        property.SetValue(owner, shortValue);
                        currentValue = shortValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(ushort))
            {
                ushort ushortValue = currentValue is ushort us ? us : (ushort)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.U16, ref ushortValue) && canWrite)
                    {
                        property.SetValue(owner, ushortValue);
                        currentValue = ushortValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(byte))
            {
                byte byteValue = currentValue is byte by ? by : (byte)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.U8, ref byteValue) && canWrite)
                    {
                        property.SetValue(owner, byteValue);
                        currentValue = byteValue;
                        isCurrentlyNull = false;
                    }
                }
                return true;
            }

            if (effectiveType == typeof(sbyte))
            {
                sbyte sbyteValue = currentValue is sbyte sb ? sb : (sbyte)0;
                using (new ImGuiDisabledScope(!canWrite))
                {
                    if (InputScalar("##Value", ImGuiDataType.S8, ref sbyteValue) && canWrite)
                    {
                        property.SetValue(owner, sbyteValue);
                        currentValue = sbyteValue;
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

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            bool IEqualityComparer<object>.Equals(object? x, object? y)
                => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj)
                => RuntimeHelpers.GetHashCode(obj);
        }
    }
}