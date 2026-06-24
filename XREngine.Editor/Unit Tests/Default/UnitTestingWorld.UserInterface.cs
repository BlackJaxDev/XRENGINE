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
using XREngine.Data.Rendering;
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
        private const float FpsOverlayWidth = 1000.0f;
        private const float FpsOverlayHeight = 128.0f;
        private const float FpsOverlayBottomMargin = 26.0f;
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
        private const float VRStereoPreviewEyeWidth = 300.0f;
        private const float VRStereoPreviewEyeHeight = 170.0f;
        private const float VRStereoPreviewGap = 8.0f;
        private const float VRStereoPreviewTopMargin = 12.0f;
        private const int VRStereoPreviewZIndex = -100_000;

        private static int _tickFpsDiagCount = 0;
        private static void TickFPS(UITextComponent t)
        {
            if (_tickFpsDiagCount < 5)
            {
                _tickFpsDiagCount++;
                XREngine.Debug.Log(ELogCategory.UI, $"[FpsTextDiag] TickFPS fired #{_tickFpsDiagCount} on '{t.SceneNode?.Name}' textLen={t.Text?.Length ?? -1} instances2D={t.RenderCommand2D.Instances} mesh={(t.Mesh is not null)} disableBatching={t.DisableBatching} parentCanvasActive={(t.BoundableTransform.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false)}");
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
            int drawCalls = Engine.Rendering.Stats.Frame.DrawCalls;
            int multiDrawCalls = Engine.Rendering.Stats.Frame.MultiDrawCalls;
            int trianglesRendered = Engine.Rendering.Stats.Frame.TrianglesRendered;
            double cpuFrameMs = Engine.Rendering.Stats.Vulkan.VulkanFrameTotalMs;
            double gpuCmdMs = Engine.Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs;
            int fallbackEvents = Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents;
            float networkingRttMs = 0.0f;
            float packetsPerSecond = 0.0f;
            int bytesPerSecond = 0;

            var net = Engine.Networking;
            if (net is not null)
            {
                networkingRttMs = net.AverageRoundTripTimeMs;
                packetsPerSecond = net.PacketsPerSecond;
                bytesPerSecond = net.BytesSentLastSecond;
            }

            var builder = _fpsTextBuilder;
            builder.Clear();
            builder.Append("net:    rtt ");
            AppendFixed(builder, networkingRttMs, "F1", 5);
            builder.Append("ms | pkt ");
            AppendFixed(builder, packetsPerSecond, "F1", 5);
            builder.Append("/s | data ");
            builder.Append(FormatCompactRate(bytesPerSecond, 7));

            builder.Append("\nrender: ");
            AppendFixed(builder, averageHz, "F0", 3);
            builder.Append("hz ");
            AppendFixed(builder, renderMs, "F2", 6);
            builder.Append("ms | cpu ");
            AppendFixed(builder, cpuFrameMs, "F2", 6);
            builder.Append("ms | gpu ");
            AppendFixed(builder, gpuCmdMs, "F2", 6);
            builder.Append("ms");

            builder.Append("\nloop:   update ");
            AppendFixed(builder, updateMs, "F2", 6);
            builder.Append("ms | fixed ");
            AppendFixed(builder, fixedMs, "F2", 6);
            builder.Append("ms");

            builder.Append("\ndraw:   calls ");
            builder.Append(FormatCompactCount(drawCalls, 5));
            builder.Append(" | multi ");
            builder.Append(FormatCompactCount(multiDrawCalls, 5));
            builder.Append(" | tris ");
            builder.Append(FormatCompactCount(trianglesRendered, 6));
            builder.Append(" | cpu fallback ");
            builder.Append(FormatCompactCount(fallbackEvents, 3));

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

            string fpsText = builder.ToString();
            t.Text = fpsText;
            t.Color = ResolveFpsOverlayColor(renderMs, cpuFrameMs, gpuCmdMs, networkingRttMs, fallbackEvents);
        }

        private static ColorF4 ResolveFpsOverlayColor(double renderMs, double cpuFrameMs, double gpuCmdMs, float rttMs, int fallbackEvents)
        {
            double primaryMs = Math.Max(renderMs, Math.Max(cpuFrameMs, gpuCmdMs));
            if (fallbackEvents > 0 || primaryMs >= 33.0 || rttMs >= 120.0f)
                return new ColorF4(1.0f, 0.82f, 0.32f, 1.0f);
            if (primaryMs >= 20.0 || rttMs >= 60.0f)
                return new ColorF4(1.0f, 0.92f, 0.48f, 1.0f);
            if (primaryMs >= 12.0 || rttMs >= 20.0f)
                return new ColorF4(0.98f, 0.98f, 0.85f, 1.0f);
            return new ColorF4(1.0f, 1.0f, 1.0f, 1.0f);
        }

        private static string FormatCompactCount(int value)
            => FormatCompactCount(value, width: 0);

        private static string FormatCompactCount(int value, int width)
        {
            string formatted;
            if (value >= 1_000_000)
                formatted = $"{value / 1_000_000.0:F2}M";
            else if (value >= 1_000)
                formatted = $"{value / 1_000.0:F1}K";
            else
                formatted = value.ToString("N0");

            return width > 0 ? formatted.PadLeft(width) : formatted;
        }

        private static string FormatCompactRate(int bytesPerSecond, int width)
        {
            string formatted;
            if (bytesPerSecond >= 1_048_576)
                formatted = $"{bytesPerSecond / 1_048_576.0:F1}MB/s";
            else if (bytesPerSecond >= 1024)
                formatted = $"{bytesPerSecond / 1024.0:F1}KB/s";
            else
                formatted = $"{bytesPerSecond}B/s";

            return formatted.PadLeft(width);
        }

        private static void AppendFixed(StringBuilder builder, double value, string format, int width)
            => builder.Append(value.ToString(format).PadLeft(width));

        private static void AppendFixed(StringBuilder builder, float value, string format, int width)
            => builder.Append(value.ToString(format).PadLeft(width));

        //Simple FPS counter in the bottom right for debugging.
        public static UITextComponent AddFPSText(FontGlyphSet? font, SceneNode parentNode)
        {
            SceneNode textNode = new(parentNode) { Name = "TestTextNode" };
            UITextComponent text = textNode.AddComponent<UITextComponent>()!;
            text.DisableBatching = false;
            text.RenderPass = (int)EDefaultRenderPass.OnTopForward;
            text.BatchedDebugMode = ResolveFpsTextDebugMode();
            text.RenderCommand2D.ZIndex = int.MaxValue;
            text.Font = font;
            text.FontSize = 26;
            text.HorizontalAlignment = EHorizontalAlignment.Center;
            text.VerticalAlignment = EVerticalAlignment.Center;
            text.WrapMode = FontGlyphSet.EWrapMode.None;
            text.HideOverflow = false;
            text.OutlineColor = new ColorF4(0.0f, 0.0f, 0.0f, 1.0f);
            text.OutlineThickness = 2.0f;
            text.OutlineAffectsSpacing = true;
            text.RegisterAnimationTick<UITextComponent>(TickFPS);
            var textTransform = textNode.GetTransformAs<UIBoundableTransform>(true)!;
            textTransform.Width = FpsOverlayWidth;
            textTransform.Height = FpsOverlayHeight;
            ConfigureFpsOverlayAnchor(textTransform);
            return text;
        }

        private static void ConfigureFpsOverlayAnchor(UIBoundableTransform transform)
        {
            if (DockFPSTopLeft)
            {
                transform.MinAnchor = new Vector2(0.0f, 1.0f);
                transform.MaxAnchor = new Vector2(0.0f, 1.0f);
                transform.NormalizedPivot = new Vector2(0.0f, 1.0f);
            }
            else
            {
                transform.MinAnchor = new Vector2(0.5f, 0.0f);
                transform.MaxAnchor = new Vector2(0.5f, 0.0f);
                transform.NormalizedPivot = new Vector2(0.5f, 0.0f);
            }

            transform.Margins = new Vector4(0.0f, FpsOverlayBottomMargin, 0.0f, 10.0f);
            transform.Scale = new Vector3(1.0f);
        }

        private static EBatchedTextDebugMode ResolveFpsTextDebugMode()
        {
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.FpsTextBatchedDebugMode);
            if (string.IsNullOrWhiteSpace(value))
                return EBatchedTextDebugMode.None;

            if (int.TryParse(value, out int numeric) &&
                Enum.IsDefined(typeof(EBatchedTextDebugMode), numeric))
            {
                return (EBatchedTextDebugMode)numeric;
            }

            if (Enum.TryParse(value, ignoreCase: true, out EBatchedTextDebugMode parsed))
                return parsed;

            Debug.LogWarning(
                ELogCategory.UI,
                "[FpsTextDiag] Ignoring unknown XRE_FPS_TEXT_BATCHED_DEBUG_MODE='{0}'.",
                value);
            return EBatchedTextDebugMode.None;
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
            Debug.Rendering(
                "[StartupUI] CreateEditorUI begin: DrawSpace={0}, EditorType={1}, Rive={2}",
                Toggles.CameraUIDrawSpaceOnInit,
                Toggles.EditorType,
                Toggles.RiveUI);

            // Create as a root/editor node (not parented under gameplay nodes).
            // Editor-only migration in the world instance only applies to root nodes.
            var rootCanvasNode = new SceneNode(parent.World, "TestUINode") { IsEditorOnly = true };
            var canvas = rootCanvasNode.AddComponent<UICanvasComponent>()!;
            var canvasTfm = canvas.CanvasTransform;
            bool addDearImGui = EditorUnitTests.Toggles.EditorType == UnitTestEditorType.IMGUI;
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

            if (addDearImGui && canvasTfm.DrawSpace != ECanvasDrawSpace.Screen && !useOffscreenForNonScreenSpaces)
                useOffscreenForNonScreenSpaces = true;

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

            screenSpaceCamera?.UserInterface = Toggles.CameraUIDrawSpaceOnInit == CameraUIDrawMode.Screen ? canvas : null;

            if (Toggles.VRPawn && Toggles.PreviewVRStereoViews)
                CreateVRStereoPreviewOverlay(rootCanvasNode);

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
                Debug.Rendering("[StartupUI] DearImGui node ready in {0:F1} ms.", imGuiStopwatch.Elapsed.TotalMilliseconds);
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
                Debug.Rendering("[StartupUI] Native editor UI ready in {0:F1} ms.", nativeUiStopwatch.Elapsed.TotalMilliseconds);
            }

            AddFPSText(null, rootCanvasNode);

            createUiStopwatch.Stop();
            Debug.Rendering("[StartupUI] CreateEditorUI complete in {0:F1} ms.", createUiStopwatch.Elapsed.TotalMilliseconds);

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
            leftTfm.MinAnchor = new Vector2(0.5f, 1.0f);
            leftTfm.MaxAnchor = new Vector2(0.5f, 1.0f);
            leftTfm.NormalizedPivot = new Vector2(1.0f, 1.0f);
            leftTfm.Width = VRStereoPreviewEyeWidth;
            leftTfm.Height = VRStereoPreviewEyeHeight;
            leftTfm.Margins = new Vector4(0.0f, 0.0f, VRStereoPreviewGap, VRStereoPreviewTopMargin);

            SceneNode rightNode = new(previewRoot) { Name = "Right Eye" };
            var rightTfm = rightNode.SetTransform<UIBoundableTransform>();
            rightTfm.MinAnchor = new Vector2(0.5f, 1.0f);
            rightTfm.MaxAnchor = new Vector2(0.5f, 1.0f);
            rightTfm.NormalizedPivot = new Vector2(0.0f, 1.0f);
            rightTfm.Width = VRStereoPreviewEyeWidth;
            rightTfm.Height = VRStereoPreviewEyeHeight;
            rightTfm.Margins = new Vector4(VRStereoPreviewGap, 0.0f, 0.0f, VRStereoPreviewTopMargin);

            var left = leftNode.AddComponent<UIMaterialComponent>()!;
            var right = rightNode.AddComponent<UIMaterialComponent>()!;
            left.RenderPass = (int)EDefaultRenderPass.Background;
            right.RenderPass = (int)EDefaultRenderPass.Background;
            left.RenderCommand2D.ZIndex = VRStereoPreviewZIndex;
            right.RenderCommand2D.ZIndex = VRStereoPreviewZIndex;
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

            if (!TryResolveVRStereoPreviewTextures(out XRTexture? leftTex, out XRTexture? rightTex, out bool isArray))
            {
                _vrStereoPreviewRoot.IsActiveSelf = false;
                return;
            }

            ApplyPreviewTexture(_vrStereoPreviewLeft, leftTex, isArray, ref _vrStereoPreviewLeftWasArray, ref _vrStereoPreviewLastLeft);
            ApplyPreviewTexture(_vrStereoPreviewRight, rightTex, isArray, ref _vrStereoPreviewRightWasArray, ref _vrStereoPreviewLastRight);
        }

        private static bool TryResolveVRStereoPreviewTextures(
            out XRTexture? leftTex,
            out XRTexture? rightTex,
            out bool isArray)
        {
            if (Engine.VRState.IsOpenXRActive)
            {
                leftTex = Engine.VRState.OpenXRApi?.PreviewLeftEyeTexture;
                rightTex = Engine.VRState.OpenXRApi?.PreviewRightEyeTexture;
                isArray = false;
                return leftTex is not null && rightTex is not null;
            }

            if (Engine.VRState.StereoLeftViewTexture is not null && Engine.VRState.StereoRightViewTexture is not null)
            {
                leftTex = Engine.VRState.StereoLeftViewTexture;
                rightTex = Engine.VRState.StereoRightViewTexture;
                isArray = true;
                return true;
            }

            leftTex = Engine.VRState.VRLeftEyeViewTexture;
            rightTex = Engine.VRState.VRRightEyeViewTexture;
            isArray = false;
            return leftTex is not null && rightTex is not null;
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

                var mat = new XRMaterial([texture], frag)
                {
                    RenderPass = (int)EDefaultRenderPass.Background
                };
                target.SetQuadMaterial(mat);
                target.RenderCommand2D.ZIndex = VRStereoPreviewZIndex;
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

            if (node.SuppressTransformTools)
            {
                TransformToolUndoAdapter.Attach(null);
                TransformTool3D.DestroyInstance();
                return;
            }

            //we have to wait for the scene node to be activated in the instance of the world before we can attach the transform tool
            void Edit(SceneNode x)
            {
                if (x.SuppressTransformTools)
                {
                    TransformToolUndoAdapter.Attach(null);
                    TransformTool3D.DestroyInstance();
                    x.Activated -= Edit;
                    return;
                }

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
