using System;
using Silk.NET.Input;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Text;
using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Components.Scripting;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Editor.UI.Tools;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components.UI;
using XREngine.Scene.Transforms;
using XREngine.Scene.Components.Editing;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static partial class UserInterface
    {
        private static readonly bool DockFPSTopLeft = false;
        private static readonly Queue<float> _fpsAvg = new();
        private static readonly StringBuilder _fpsTextBuilder = new(512);
        private static long _lastSampledRenderTimestampTicks = -1L;

        private static UIEditorComponent? _editorComponent = null;
        private static SceneNode? _vrStereoPreviewRoot;
        private static UIMaterialComponent? _vrStereoPreviewLeft;
        private static UIMaterialComponent? _vrStereoPreviewRight;
        private static bool _vrStereoPreviewLeftWasArray;
        private static bool _vrStereoPreviewRightWasArray;
        private static XRTexture? _vrStereoPreviewLastLeft;
        private static XRTexture? _vrStereoPreviewLastRight;
        private static bool _vrStereoPreviewRefreshHooked;

        private static int _tickFpsDiagCount = 0;
        private static void TickFPS(UITextComponent t)
        {
            if (_tickFpsDiagCount < 5)
            {
                _tickFpsDiagCount++;
                XREngine.Debug.Out($"[FpsTextDiag] TickFPS fired #{_tickFpsDiagCount} on '{t.SceneNode?.Name}' textLen={t.Text?.Length ?? -1} instances2D={t.RenderCommand2D.Instances} mesh={(t.Mesh is not null)} disableBatching={t.DisableBatching} parentCanvasActive={(t.BoundableTransform.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false)}");
            }
            // Only sample once per actual render frame to avoid duplicate stale samples
            long renderTimestampTicks = Engine.Time.Timer.Render.LastTimestampTicks;
            if (renderTimestampTicks != _lastSampledRenderTimestampTicks)
            {
                _lastSampledRenderTimestampTicks = renderTimestampTicks;
                _fpsAvg.Enqueue(1.0f / Engine.Time.Timer.Render.Delta);
                if (_fpsAvg.Count > 60)
                    _fpsAvg.Dequeue();
            }

            float averageHz = _fpsAvg.Count > 0 ? MathF.Round(_fpsAvg.Sum() / _fpsAvg.Count) : 0.0f;
            double renderMs = Engine.Time.Timer.Render.Delta * 1000.0;
            double updateMs = Engine.Time.Timer.Update.Delta * 1000.0;
            double fixedMs = Engine.Time.Timer.FixedUpdateDelta * 1000.0;
            int drawCalls = Engine.Rendering.Stats.DrawCalls;
            int multiDrawCalls = Engine.Rendering.Stats.MultiDrawCalls;
            int trianglesRendered = Engine.Rendering.Stats.TrianglesRendered;
            double cpuFrameMs = Engine.Rendering.Stats.VulkanFrameTotalMs;
            double gpuCmdMs = Engine.Rendering.Stats.VulkanFrameGpuCommandBufferMs;
            int fallbackEvents = Engine.Rendering.Stats.GpuCpuFallbackEvents;

            var builder = _fpsTextBuilder;
            builder.Clear();
            builder.Append(averageHz.ToString("F0"));
            builder.Append("hz");
            builder.Append(" (");
            builder.Append(renderMs.ToString("F2"));
            builder.Append("ms)");
            builder.Append("\nu ");
            builder.Append(updateMs.ToString("F2"));
            builder.Append("ms");
            builder.Append("\nf ");
            builder.Append(fixedMs.ToString("F2"));
            builder.Append("ms");

            float networkingRttMs = 0.0f;
            var net = Engine.Networking;
            if (net is not null
                && (net.PacketsPerSecond > 0.05f || net.BytesSentLastSecond > 0 || net.AverageRoundTripTimeMs > 0.05f))
            {
                networkingRttMs = net.AverageRoundTripTimeMs;
                builder.Append("\nn ");
                builder.Append(net.AverageRoundTripTimeMs.ToString("F1"));
                builder.Append("ms");
                builder.Append(" ");
                builder.Append(net.PacketsPerSecond.ToString("F1"));
                builder.Append("p/s");
                builder.Append("\n");
                builder.Append(net.DataPerSecondString);
            }

            builder.Append("\ndc ");
            builder.Append(drawCalls.ToString("N0"));
            builder.Append(" | md ");
            builder.Append(multiDrawCalls.ToString("N0"));
            builder.Append("\ntri ");
            builder.Append(FormatCompactCount(trianglesRendered));

            if (cpuFrameMs > 0.0 || gpuCmdMs > 0.0)
            {
                builder.Append("\nc ");
                builder.Append(cpuFrameMs.ToString("F2"));
                builder.Append("ms");
                builder.Append(" | g ");
                builder.Append(gpuCmdMs.ToString("F2"));
                builder.Append("ms");
            }

            if (fallbackEvents > 0)
            {
                builder.Append("\nfb ");
                builder.Append(fallbackEvents.ToString("N0"));
            }

            var videoComp = _editorComponent?.SceneNode.FindFirstDescendantComponent<UIVideoComponent>();
            if (videoComp is not null)
            {
                string syncState = videoComp.DebugAudioSyncActive ? "on" : "off";
                builder.Append("\nA/V: drift ");
                builder.Append(videoComp.DebugPresentDriftMs.ToString("F1"));
                builder.Append("ms");
                builder.Append("\ndebt ");
                builder.Append(videoComp.DebugVideoDebtMs.ToString("F1"));
                builder.Append("ms");
                builder.Append("\nunderruns ");
                builder.Append(videoComp.DebugAudioUnderruns.ToString("N0"));
                builder.Append(" (");
                builder.Append(syncState);
                builder.Append(')');
            }

            t.Text = builder.ToString();
            t.Color = ResolveFpsOverlayColor(renderMs, cpuFrameMs, gpuCmdMs, networkingRttMs, fallbackEvents);
        }

        private static ColorF4 ResolveFpsOverlayColor(double renderMs, double cpuFrameMs, double gpuCmdMs, float rttMs, int fallbackEvents)
        {
            double primaryMs = Math.Max(renderMs, Math.Max(cpuFrameMs, gpuCmdMs));
            if (fallbackEvents > 0 || primaryMs >= 33.0 || rttMs >= 120.0f)
                return new ColorF4(1.0f, 0.45f, 0.40f, 1.0f);
            if (primaryMs >= 20.0 || rttMs >= 60.0f)
                return new ColorF4(1.0f, 0.78f, 0.30f, 1.0f);
            if (primaryMs >= 12.0 || rttMs >= 20.0f)
                return new ColorF4(0.95f, 0.90f, 0.35f, 1.0f);
            return new ColorF4(0.80f, 1.0f, 0.82f, 1.0f);
        }

        private static string FormatCompactCount(int value)
        {
            if (value >= 1_000_000)
                return $"{value / 1_000_000.0:F2}M";
            if (value >= 1_000)
                return $"{value / 1_000.0:F1}K";
            return value.ToString("N0");
        }

        //Simple FPS counter in the bottom right for debugging.
        public static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
        {
            SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
            UITextComponent text = textNode.AddComponent<UITextComponent>()!;
            text.DisableBatching = true;
            text.Font = font;
            text.FontSize = 22;
            text.HorizontalAlignment = EHorizontalAlignment.Center;
            text.WrapMode = FontGlyphSet.EWrapMode.None;
            text.HideOverflow = false;
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
                textTransform.MinAnchor = new Vector2(0.5f, 0.0f);
                textTransform.MaxAnchor = new Vector2(0.5f, 0.0f);
                textTransform.NormalizedPivot = new Vector2(0.5f, 0.0f);
            }
            textTransform.Margins = new Vector4(0.0f, 10.0f, 0.0f, 10.0f);
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
            using var profilerScope = Engine.Profiler.Start("UnitTestingWorld.UserInterface.CreateEditorUI");
            var createUiStopwatch = System.Diagnostics.Stopwatch.StartNew();
            Debug.Out(
                "[StartupUI] CreateEditorUI begin: DrawSpace={0}, EditorType={1}, Rive={2}",
                Toggles.CameraUIDrawSpaceOnInit,
                Toggles.EditorType,
                Toggles.RiveUI);

            // Create as a root/editor node (not parented under gameplay nodes).
            // Editor-only migration in the world instance only applies to root nodes.
            var rootCanvasNode = new SceneNode(parent.World, "TestUINode") { IsEditorOnly = true };
            var canvas = rootCanvasNode.AddComponent<UICanvasComponent>()!;
            var canvasTfm = canvas.CanvasTransform;
            bool useOffscreenForNonScreenSpaces;
            bool bindToCameraSpace;
            switch (Toggles.CameraUIDrawSpaceOnInit)
            {
                case CameraUIDrawMode.Screen:
                    canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
                    useOffscreenForNonScreenSpaces = false;
                    bindToCameraSpace = false;
                    break;
                case CameraUIDrawMode.World:
                    canvasTfm.DrawSpace = ECanvasDrawSpace.World;
                    useOffscreenForNonScreenSpaces = false;
                    bindToCameraSpace = false;
                    break;
                case CameraUIDrawMode.Camera:
                    canvasTfm.DrawSpace = ECanvasDrawSpace.Camera;
                    useOffscreenForNonScreenSpaces = false;
                    bindToCameraSpace = true;
                    break;
                case CameraUIDrawMode.WorldOffscreen:
                    canvasTfm.DrawSpace = ECanvasDrawSpace.World;
                    useOffscreenForNonScreenSpaces = true;
                    bindToCameraSpace = false;
                    break;
                case CameraUIDrawMode.CameraOffscreen:
                    canvasTfm.DrawSpace = ECanvasDrawSpace.Camera;
                    useOffscreenForNonScreenSpaces = true;
                    bindToCameraSpace = true;
                    break;
                default:
                    canvasTfm.DrawSpace = ECanvasDrawSpace.Screen;
                    useOffscreenForNonScreenSpaces = false;
                    bindToCameraSpace = false;
                    break;
            }

            canvas.PreferOffscreenRenderingForNonScreenSpaces = useOffscreenForNonScreenSpaces;
            canvas.AutoDisableOffscreenForBackdropBlur = !useOffscreenForNonScreenSpaces;
            canvasTfm.CameraSpaceCamera = bindToCameraSpace ? screenSpaceCamera?.Camera : null;
            canvasTfm.SetSize(new Vector2(1920.0f, 1080.0f));
            canvasTfm.Padding = new Vector4(0.0f);

            // Ensure it's attached to the hidden editor scene (so it doesn't show in the hierarchy panel)
            // while still being part of the world instance for rendering/ticking.
            if (parent.World is not null)
            {
                (parent.World as XRWorldInstance)?.AddToEditorScene(rootCanvasNode);
            }
            else
            {
                void OnParentWorldAssigned(object? _, IXRPropertyChangedEventArgs e)
                {
                    if (e.PropertyName != nameof(SceneNode.World))
                        return;

                    if (parent.World is not null)
                    {
                        (parent.World as XRWorldInstance)?.AddToEditorScene(rootCanvasNode);

                        // In unit-test world construction, UI components are often created before the
                        // root node is attached to a world. When the world arrives later, component
                        // activation callbacks (OnComponentActivated) are not guaranteed to run
                        // automatically, so explicitly activate the tree once after world attach.
                        if (rootCanvasNode.IsActiveSelf)
                            rootCanvasNode.OnActivated();

                        parent.PropertyChanged -= OnParentWorldAssigned;
                    }
                }

                parent.PropertyChanged += OnParentWorldAssigned;
            }

            if (Toggles.RiveUI || Toggles.EditorType != UnitTestEditorType.None)
            {
                var inputComponent = rootCanvasNode.AddComponent<UICanvasInputComponent>();
                if (inputComponent is not null)
                {
                    inputComponent.OwningPawn = pawnForInput;
                    EditorDragDropUtility.Initialize(inputComponent);
                }
            }

            if (Toggles.VisualizeQuadtree)
                rootCanvasNode.AddComponent<DebugVisualizeQuadtreeComponent>();

            screenSpaceCamera?.UserInterface = Toggles.CameraUIDrawSpaceOnInit == CameraUIDrawMode.Screen ? canvas : null;

            if (EditorUnitTests.Toggles.RiveUI)
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

            bool addDearImGui = EditorUnitTests.Toggles.EditorType == UnitTestEditorType.IMGUI;
            if (addDearImGui)
            {
                using var imGuiScope = Engine.Profiler.Start("UnitTestingWorld.UserInterface.CreateEditorUI.DearImGui");
                var imGuiStopwatch = System.Diagnostics.Stopwatch.StartNew();
                SceneNode dearImGuiNode = new(rootCanvasNode) { Name = "Dear ImGui Node" };
                var tfm = dearImGuiNode.SetTransform<UIBoundableTransform>();
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
                tfm.Width = null;
                tfm.Height = null;

                var dearImGuiComponent = dearImGuiNode.AddComponent<DearImGuiComponent>();
                dearImGuiComponent?.Draw += EditorImGuiUI.RenderEditor;
                imGuiStopwatch.Stop();
                Debug.Out("[StartupUI] DearImGui node ready in {0:F1} ms.", imGuiStopwatch.Elapsed.TotalMilliseconds);
            }
            
            if (Toggles.EditorType == UnitTestEditorType.Native)
            {
                using var editorUiScope = Engine.Profiler.Start("UnitTestingWorld.UserInterface.CreateEditorUI.Native");
                var nativeUiStopwatch = System.Diagnostics.Stopwatch.StartNew();
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
                using (Engine.Profiler.Start("UnitTestingWorld.UserInterface.RemakeMenu"))
                    RemakeMenu();

                GameCSProjLoader.OnAssemblyLoaded += GameCSProjLoader_OnAssemblyLoaded;
                GameCSProjLoader.OnAssemblyUnloaded += GameCSProjLoader_OnAssemblyUnloaded;
                nativeUiStopwatch.Stop();
                Debug.Out("[StartupUI] Native editor UI ready in {0:F1} ms.", nativeUiStopwatch.Elapsed.TotalMilliseconds);
            }

            AddFPSText(null, rootCanvasNode);

            if (Toggles.VRPawn && Toggles.PreviewVRStereoViews)
                CreateVRStereoPreviewOverlay(rootCanvasNode);

            createUiStopwatch.Stop();
            Debug.Out("[StartupUI] CreateEditorUI complete in {0:F1} ms.", createUiStopwatch.Elapsed.TotalMilliseconds);

            return canvas;
        }

        private static void CreateVRStereoPreviewOverlay(SceneNode rootCanvasNode)
        {
            // This is a screenspace UI overlay; it will only actually render if a screenspace UI canvas is attached
            // to a camera (handled elsewhere via screenSpaceCamera?.UserInterface = canvas).

            SceneNode previewRoot = new(rootCanvasNode) { Name = "VR Stereo Preview" };
            _vrStereoPreviewRoot = previewRoot;
            var previewTfm = previewRoot.SetTransform<UIBoundableTransform>();
            previewTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            previewTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            previewTfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
            previewTfm.Width = null;
            previewTfm.Height = null;

            SceneNode leftNode = new(previewRoot) { Name = "Left Eye" };
            var leftTfm = leftNode.SetTransform<UIBoundableTransform>();
            leftTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            leftTfm.MaxAnchor = new Vector2(0.5f, 1.0f);
            leftTfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
            leftTfm.Width = null;
            leftTfm.Height = null;

            SceneNode rightNode = new(previewRoot) { Name = "Right Eye" };
            var rightTfm = rightNode.SetTransform<UIBoundableTransform>();
            rightTfm.MinAnchor = new Vector2(0.5f, 0.0f);
            rightTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
            rightTfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
            rightTfm.Width = null;
            rightTfm.Height = null;

            var left = leftNode.AddComponent<UIMaterialComponent>()!;
            var right = rightNode.AddComponent<UIMaterialComponent>()!;
            _vrStereoPreviewLeft = left;
            _vrStereoPreviewRight = right;
            _vrStereoPreviewLeftWasArray = false;
            _vrStereoPreviewRightWasArray = false;
            _vrStereoPreviewLastLeft = null;
            _vrStereoPreviewLastRight = null;

            // Hard gate: do not show unless VR pawn is enabled.
            if (!Toggles.VRPawn || !Toggles.PreviewVRStereoViews)
            {
                previewRoot.IsActiveSelf = false;
                return;
            }

            EnsureVRStereoPreviewRefreshHooked();
            RefreshVRStereoPreviewOverlay();
        }

        private static void EnsureVRStereoPreviewRefreshHooked()
        {
            if (_vrStereoPreviewRefreshHooked)
                return;

            _vrStereoPreviewRefreshHooked = true;
            Engine.Time.Timer.RenderFrame += RefreshVRStereoPreviewOverlay;
        }

        private static void RefreshVRStereoPreviewOverlay()
        {
            if (_vrStereoPreviewRoot is null || _vrStereoPreviewLeft is null || _vrStereoPreviewRight is null)
                return;

            if (!Toggles.VRPawn || !Toggles.PreviewVRStereoViews)
            {
                _vrStereoPreviewRoot.IsActiveSelf = false;
                return;
            }

            _vrStereoPreviewRoot.IsActiveSelf = true;

            XRTexture? leftTex;
            XRTexture? rightTex;
            bool isArray;

            if (Engine.VRState.StereoLeftViewTexture is not null && Engine.VRState.StereoRightViewTexture is not null)
            {
                leftTex = Engine.VRState.StereoLeftViewTexture;
                rightTex = Engine.VRState.StereoRightViewTexture;
                isArray = true;
            }
            else
            {
                leftTex = Engine.VRState.VRLeftEyeViewTexture;
                rightTex = Engine.VRState.VRRightEyeViewTexture;
                isArray = false;
            }

            ApplyPreviewTexture(_vrStereoPreviewLeft, leftTex, isArray, ref _vrStereoPreviewLeftWasArray, ref _vrStereoPreviewLastLeft);
            ApplyPreviewTexture(_vrStereoPreviewRight, rightTex, isArray, ref _vrStereoPreviewRightWasArray, ref _vrStereoPreviewLastRight);
        }

        private static void ApplyPreviewTexture(
            UIMaterialComponent target,
            XRTexture? texture,
            bool isArray,
            ref bool wasArray,
            ref XRTexture? lastTexture)
        {
            if (texture is null)
                return;

            // Only rebuild the material if the texture type (2D vs 2DArray) changed.
            if (target.Material is null || wasArray != isArray || target.Material.Textures.Count == 0)
            {
                XRShader frag = isArray
                    ? XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedArraySliceForward.fs"), EShaderType.Fragment)
                    : XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment);

                var mat = new XRMaterial([texture], frag);
                target.Material = mat;
                wasArray = isArray;
                lastTexture = texture;
                return;
            }

            if (!ReferenceEquals(lastTexture, texture))
            {
                target.Material.Textures[0] = texture;
                lastTexture = texture;
            }
        }

        public static void GameCSProjLoader_OnAssemblyUnloaded(string obj)
        {
            RemakeMenu();
            InvalidateTypeDescriptorCache();
        }

        public static void GameCSProjLoader_OnAssemblyLoaded(string arg1, GameCSProjLoader.AssemblyData arg2)
        {
            RemakeMenu();
            InvalidateTypeDescriptorCache();
        }

        public static void RemakeMenu()
        {
            if (_editorComponent != null)
                _editorComponent.RootMenuOptions = GenerateRootMenu();
        }

        //Signals the camera to take a picture of the current view.
        public static void TakeScreenshot(UIInteractableComponent comp)
        {
            //Debug.Out("Take Screenshot clicked");

            var camera = Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One)?.ControlledPawnComponent as EditorFlyingCameraPawnComponent;
            camera?.TakeScreenshot();
        }

        //Opens a dialog to select and load a project file.
        public static void OpenProjectDialog(UIInteractableComponent comp)
        {
            XREngine.Editor.UI.ImGuiFileBrowser.OpenFile(
                "OpenProjectDialog",
                "Open Project",
                result =>
                {
                    if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                    {
                        Engine.LoadProject(result.SelectedPath);
                    }
                },
                $"XREngine Projects (*.{XRProject.ProjectExtension})|*.{XRProject.ProjectExtension}|All Files (*.*)|*.*"
            );
        }

        private static bool _showNewProjectDialog = false;
        private static byte[] _newProjectNameBuffer = new byte[256];
        private static byte[] _newProjectPathBuffer = new byte[512];

        //Shows the new project dialog.
        public static void ShowNewProjectDialog()
        {
            _showNewProjectDialog = true;
            Array.Clear(_newProjectNameBuffer);
            Array.Clear(_newProjectPathBuffer);
            
            // Set default path to user's Documents folder
            string defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            System.Text.Encoding.UTF8.GetBytes(defaultPath, 0, Math.Min(defaultPath.Length, _newProjectPathBuffer.Length - 1), _newProjectPathBuffer, 0);
        }

        //Opens a folder browser to select project location
        private static void BrowseForProjectLocation()
        {
            ImGuiFileBrowser.SelectFolder(
                "SelectProjectLocation",
                "Select Project Location",
                result =>
                {
                    if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                    {
                        Array.Clear(_newProjectPathBuffer);
                        System.Text.Encoding.UTF8.GetBytes(result.SelectedPath, 0, Math.Min(result.SelectedPath.Length, _newProjectPathBuffer.Length - 1), _newProjectPathBuffer, 0);
                    }
                }
            );
        }

        //Draws the new project dialog if visible.
        public static void DrawNewProjectDialog()
        {
            // Draw any active file browser dialogs
            ImGuiFileBrowser.DrawDialogs();

            if (!_showNewProjectDialog)
                return;

            ImGuiNET.ImGui.OpenPopup("New Project");

            var viewport = ImGuiNET.ImGui.GetMainViewport();
            ImGuiNET.ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiNET.ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
            ImGuiNET.ImGui.SetNextWindowSize(new System.Numerics.Vector2(500, 180));

            if (ImGuiNET.ImGui.BeginPopupModal("New Project", ref _showNewProjectDialog, ImGuiNET.ImGuiWindowFlags.NoResize))
            {
                ImGuiNET.ImGui.Text("Project Name:");
                ImGuiNET.ImGui.InputText("##ProjectName", _newProjectNameBuffer, (uint)_newProjectNameBuffer.Length);

                ImGuiNET.ImGui.Text("Project Location:");
                ImGuiNET.ImGui.InputText("##ProjectPath", _newProjectPathBuffer, (uint)_newProjectPathBuffer.Length);
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.Button("Browse..."))
                {
                    BrowseForProjectLocation();
                }

                ImGuiNET.ImGui.Separator();

                if (ImGuiNET.ImGui.Button("Create", new System.Numerics.Vector2(120, 0)))
                {
                    string projectName = ExtractString(_newProjectNameBuffer);
                    string projectPath = ExtractString(_newProjectPathBuffer);

                    if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(projectPath))
                    {
                        string fullPath = System.IO.Path.Combine(projectPath, projectName);
                        if (Engine.CreateAndLoadProject(fullPath, projectName))
                        {
                            _showNewProjectDialog = false;
                        }
                        else
                        {
                        Debug.LogWarning($"Failed to create project: {fullPath}");
                        }
                    }
                }
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.Button("Cancel", new System.Numerics.Vector2(120, 0)))
                {
                    _showNewProjectDialog = false;
                }

                ImGuiNET.ImGui.EndPopup();
            }
        }

        private static string ExtractString(byte[] buffer)
        {
            int nullIndex = Array.IndexOf(buffer, (byte)0);
            int length = nullIndex >= 0 ? nullIndex : buffer.Length;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        }

        //Saves all modified assets in the project.
        public static async void SaveAll(UIInteractableComponent? comp)
        {
            await Engine.Assets.SaveAllAsync();
            RefreshSaveMenu();
        }

        private static ToolbarButton? _saveMenu;
        private static bool _saveMenuHooksInitialized;

        private static void EnsureSaveMenuHooks()
        {
            if (_saveMenuHooksInitialized)
                return;

            if (Engine.Assets is not null)
            {
                Engine.Assets.AssetMarkedDirty += OnAssetMarkedDirty;
                Engine.Assets.AssetSaved += OnAssetSaved;
            }
            _saveMenuHooksInitialized = true;
            RefreshSaveMenu();
        }

        private static void OnAssetMarkedDirty(XRAsset asset)
        {
            RefreshSaveMenu();
        }

        private static void OnAssetSaved(XRAsset asset)
        {
            RefreshSaveMenu();
        }

        private static void RefreshSaveMenu()
        {
            if (_saveMenu is null)
                return;

            _saveMenu.ChildOptions.Clear();

            var assets = Engine.Assets;
            if (assets is null)
            {
                _saveMenu.ChildOptions.Add(new ToolbarButton("No asset manager available"));
                return;
            }

            var dirtyAssets = assets.DirtyAssets.ToArray();
            if (dirtyAssets.Length == 0)
            {
                _saveMenu.ChildOptions.Add(new ToolbarButton("No modified assets"));
                return;
            }

            foreach (var asset in dirtyAssets)
            {
                string displayName = GetAssetDisplayName(asset.Value);
                var capturedAsset = asset;
                _saveMenu.ChildOptions.Add(new ToolbarButton(displayName, _ => SaveSingleAsset(capturedAsset.Value)));
            }
        }

        public static string GetAssetDisplayName(XRAsset asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Name))
                return asset.Name;
            if (!string.IsNullOrWhiteSpace(asset.FilePath))
                return Path.GetFileNameWithoutExtension(asset.FilePath);
            return $"{asset.GetType().Name} ({asset.ID.ToString()[..8]})";
        }

        public static async void SaveSingleAsset(XRAsset asset)
        {
            var assets = Engine.Assets;
            if (assets is null)
                return;

            await assets.SaveAsync(asset);
            RefreshSaveMenu();
        }

        //Generates the root menu for the editor UI.
        //TODO: allow scripts to add menu options with attributes
        public static List<ToolbarItemBase> GenerateRootMenu()
        {
            EnsureUndoMenuHooks();
            EnsureSaveMenuHooks();
            _saveMenu ??= new ToolbarButton("Save");
            RefreshSaveMenu();

            List<ToolbarItemBase> buttons = [
                new ToolbarButton("File", [Key.ControlLeft, Key.F],
            [
                _saveMenu,
                new ToolbarButton("Save All", SaveAll, [Key.ControlLeft, Key.ShiftLeft, Key.S]),
                new ToolbarButton("Open", [
                    new ToolbarButton("Project", OpenProjectDialog),
                ]),
                new ToolbarButton("New Project", _ => ShowNewProjectDialog()),
            ]),
            CreateEditMenu(),
            new ToolbarButton("Assets"),
            new ToolbarButton("Tools", [Key.ControlLeft, Key.T],
            [
                new ToolbarButton("Take Screenshot", TakeScreenshot),
                new ToolbarButton("Shader Editor", _ => ShaderEditorWindow.Instance.Open()),
                new ToolbarButton("MCP Assistant", _ => McpAssistantWindow.Instance.Open()),
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
                var tool = TransformTool3D.GetInstance(x.Transform);
                TransformToolUndoAdapter.Attach(tool);
                x.Activated -= Edit;
            }

            if (node.IsActiveInHierarchy && node.World is not null)
            {
                var tool = TransformTool3D.GetInstance(node.Transform);
                TransformToolUndoAdapter.Attach(tool);
            }
            else
                node.Activated += Edit;
        }

        private static ToolbarButton? _undoHistoryMenu;
        private static bool _undoHooksInitialized;

        private static void EnsureUndoMenuHooks()
        {
            if (_undoHooksInitialized)
                return;

            Undo.HistoryChanged += RefreshUndoHistoryMenu;
            _undoHooksInitialized = true;
            RefreshUndoHistoryMenu();
        }

        private static ToolbarButton CreateEditMenu()
        {
            var undoButton = new ToolbarButton("Undo", OnToolbarUndo, [Key.ControlLeft, Key.Z]);
            var redoButton = new ToolbarButton("Redo", OnToolbarRedo, [Key.ControlLeft, Key.Y]);
            _undoHistoryMenu ??= new ToolbarButton("Undo History");
            RefreshUndoHistoryMenu();

            return new ToolbarButton("Edit", undoButton, redoButton, _undoHistoryMenu);
        }

        private static void OnToolbarUndo(UIInteractableComponent _)
        {
            Undo.TryUndo();
        }

        private static void OnToolbarRedo(UIInteractableComponent _)
        {
            Undo.TryRedo();
        }

        private static void RefreshUndoHistoryMenu()
        {
            if (_undoHistoryMenu is null)
                return;

            _undoHistoryMenu.ChildOptions.Clear();

            var history = Undo.PendingUndo;
            if (history.Count == 0)
            {
                _undoHistoryMenu.ChildOptions.Add(new ToolbarButton("No undo steps available"));
                return;
            }

            int index = 0;
            foreach (var entry in history)
            {
                int targetIndex = index;
                string label = $"{targetIndex + 1}. {entry.Description}";
                _undoHistoryMenu.ChildOptions.Add(new ToolbarButton(label, _ => UndoMultiple(targetIndex)));
                index++;
                if (index >= 15)
                    break;
            }
        }

        public static void UndoMultiple(int targetIndex)
        {
            for (int i = 0; i <= targetIndex; i++)
            {
                if (!Undo.TryUndo())
                    break;
            }
        }

        public static void RedoMultiple(int targetIndex)
        {
            for (int i = 0; i <= targetIndex; i++)
            {
                if (!Undo.TryRedo())
                    break;
            }
        }

        private static void InvalidateTypeDescriptorCache()
        {
            // Placeholder until the editor exposes type-descriptor caching again.
        }
    }
}
