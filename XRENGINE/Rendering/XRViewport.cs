using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Input;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Rendering.UI;
using XREngine.Scene;
using YamlDotNet.Serialization;
using State = XREngine.Engine.Rendering.State;

namespace XREngine.Rendering
{
    /// <summary>
    /// Defines a rectangular area to render to.
    /// Can either be a window or render texture.
    /// </summary>
    [RuntimeOnly]
    public sealed class XRViewport : XRBase
    {
        #region Fields

        private XRCamera? _camera = null;
        private CameraComponent? _cameraComponent = null;
        private LocalPlayerController? _associatedPlayer = null;

        private BoundingRectangle _region = new();
        private BoundingRectangle _internalResolutionRegion = new();

        private readonly XRRenderPipelineInstance _renderPipeline = new();

        private bool _setRenderPipelineFromCamera = true;
        private bool _automaticallyCollectVisible = true;
        private bool _automaticallySwapBuffers = true;
        private bool _allowUIRender = true;
        private bool _cullWithFrustum = true;
        private int _index = 0;

        public float _leftPercentage = 0.0f;
        public float _rightPercentage = 1.0f;
        public float _bottomPercentage = 0.0f;
        public float _topPercentage = 1.0f;

        #endregion

        #region Events

        public event Action<XRViewport>? Resized;
        public event Action<XRViewport>? InternalResolutionResized;

        #endregion

        #region Properties

        public XRWindow? Window { get; set; }

        /// <summary>
        /// The world instance to render.
        /// This must be set when no camera component is set, and will take precedence over the camera component's world instance if both are set.
        /// </summary>
        public XRWorldInstance? WorldInstanceOverride { get; set; } = null;

        /// <summary>
        /// The world instance that will be rendered to this viewport.
        /// </summary>
        public XRWorldInstance? World => WorldInstanceOverride ?? CameraComponent?.SceneNode?.World;

        public BoundingRectangle Region => _region;
        public BoundingRectangle InternalResolutionRegion => _internalResolutionRegion;

        public int X
        {
            get => _region.X;
            set => _region.X = value;
        }

        public int Y
        {
            get => _region.Y;
            set => _region.Y = value;
        }

        public int Width
        {
            get => _region.Width;
            set => _region.Width = value;
        }

        public int Height
        {
            get => _region.Height;
            set => _region.Height = value;
        }

        public int InternalWidth => _internalResolutionRegion.Width;
        public int InternalHeight => _internalResolutionRegion.Height;

        /// <summary>
        /// The index of the viewport.
        /// Viewports with a lower index will be rendered first, and viewports with a higher index will be rendered last, on top of the previous viewports.
        /// </summary>
        public int Index
        {
            get => _index;
            set => SetField(ref _index, value);
        }

        /// <summary>
        /// The local player associated with this viewport.
        /// Usually player 1 unless the game supports multiple players.
        /// </summary>
        [YamlIgnore]
        public LocalPlayerController? AssociatedPlayer
        {
            get => _associatedPlayer;
            internal set
            {
                if (_associatedPlayer == value)
                    return;

                _associatedPlayer?.Viewport = null;
                SetField(ref _associatedPlayer, value);
                _associatedPlayer?.Viewport = this;
            }
        }

        public CameraComponent? CameraComponent
        {
            get => _cameraComponent;
            set => SetField(ref _cameraComponent, value);
        }

        /// <summary>
        /// The camera that will render to this viewport.
        /// </summary>
        public XRCamera? Camera
        {
            get => _camera;
            set => SetField(ref _camera, value);
        }

        public XRCamera? ActiveCamera => _cameraComponent?.Camera ?? _camera;

        [YamlIgnore]
        public XRRenderPipelineInstance RenderPipelineInstance => _renderPipeline;

        [YamlIgnore]
        public RenderPipeline? RenderPipeline
        {
            get => _renderPipeline.Pipeline;
            set => _renderPipeline.Pipeline = value;
        }

        public bool SetRenderPipelineFromCamera
        {
            get => _setRenderPipelineFromCamera;
            set => SetField(ref _setRenderPipelineFromCamera, value);
        }

        public bool AutomaticallyCollectVisible
        {
            get => _automaticallyCollectVisible;
            set => SetField(ref _automaticallyCollectVisible, value);
        }

        public bool AutomaticallySwapBuffers
        {
            get => _automaticallySwapBuffers;
            set => SetField(ref _automaticallySwapBuffers, value);
        }

        public bool AllowUIRender
        {
            get => _allowUIRender;
            set => SetField(ref _allowUIRender, value);
        }

        public bool CullWithFrustum
        {
            get => _cullWithFrustum;
            set => SetField(ref _cullWithFrustum, value);
        }

        #endregion

        #region Constructors

        public XRViewport(XRWindow? window)
        {
            Window = window;
            Index = 0;
            SetFullScreen();
        }

        public XRViewport(XRWindow? window, int x, int y, uint width, uint height, int index = 0)
        {
            Window = window;
            X = x;
            Y = y;
            Index = index;
            Resize(width, height);
        }

        public XRViewport(XRWindow? window, uint width, uint height)
        {
            Window = window;
            Index = 0;
            SetFullScreen();
            Resize(width, height);
        }

        #endregion

        #region Static Factory Methods

        public static XRViewport ForTotalViewportCount(XRWindow window, int totalViewportCount)
        {
            int index = totalViewportCount;
            XRViewport viewport = new(window);
            if (index == 0)
            {
                viewport.Index = index;
                viewport.SetFullScreen();
            }
            else
                viewport.ViewportCountChanged(
                    index,
                    totalViewportCount + 1,
                    Engine.GameSettings.TwoPlayerViewportPreference,
                    Engine.GameSettings.ThreePlayerViewportPreference);

            viewport.Index = index;
            return viewport;
        }

        #endregion

        #region Lifecycle

        public void Destroy()
        {
            Camera = null;
            CameraComponent = null;
        }

        /// <summary>
        /// Ensures this viewport is registered in the active camera's Viewports list.
        /// Call this after play mode transitions or snapshot restore to fix broken bindings.
        /// </summary>
        public void EnsureViewportBoundToCamera()
        {
            var camera = ActiveCamera;
            Debug.Out($"[XRViewport] EnsureViewportBoundToCamera: VP[{Index}] ActiveCamera={camera?.GetHashCode().ToString() ?? "NULL"} _camera={_camera?.GetHashCode().ToString() ?? "NULL"} _cameraComponent?.Camera={_cameraComponent?.Camera?.GetHashCode().ToString() ?? "NULL"}");
            if (camera is null)
                return;

            if (!camera.Viewports.Contains(this))
            {
                Debug.Out($"[XRViewport] EnsureViewportBoundToCamera: Adding VP[{Index}] to camera {camera.GetHashCode()} Viewports (was missing, now count={camera.Viewports.Count + 1})");
                camera.Viewports.Add(this);
            }
            else
            {
                Debug.Out($"[XRViewport] EnsureViewportBoundToCamera: VP[{Index}] already in camera {camera.GetHashCode()} Viewports (count={camera.Viewports.Count})");
            }
        }

        #endregion

        #region Property Change Handlers

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(AutomaticallySwapBuffers):
                        if (_automaticallySwapBuffers && _camera is not null)
                            Engine.Time.Timer.SwapBuffers -= SwapBuffersAutomatic;
                        break;
                    case nameof(AutomaticallyCollectVisible):
                        if (_automaticallyCollectVisible && _camera is not null)
                            Engine.Time.Timer.CollectVisible -= CollectVisibleAutomatic;
                        break;
                    case nameof(Camera):
                        if (_camera is not null)
                        {
                            _camera.Viewports.Remove(this);

                            if (AutomaticallySwapBuffers)
                                Engine.Time.Timer.SwapBuffers -= SwapBuffersAutomatic;

                            if (AutomaticallyCollectVisible)
                                Engine.Time.Timer.CollectVisible -= CollectVisibleAutomatic;
                        }
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(AutomaticallySwapBuffers):
                    if (_automaticallySwapBuffers && _camera is not null)
                        Engine.Time.Timer.SwapBuffers += SwapBuffersAutomatic;
                    break;
                case nameof(AutomaticallyCollectVisible):
                    if (_automaticallyCollectVisible && _camera is not null)
                        Engine.Time.Timer.CollectVisible += CollectVisibleAutomatic;
                    break;
                case nameof(Camera):
                    if (_camera is not null)
                    {
                        if (!_camera.Viewports.Contains(this))
                            _camera.Viewports.Add(this);
                        SetAspectRatioToCamera();

                        if (AutomaticallySwapBuffers)
                            Engine.Time.Timer.SwapBuffers += SwapBuffersAutomatic;

                        if (AutomaticallyCollectVisible)
                            Engine.Time.Timer.CollectVisible += CollectVisibleAutomatic;
                    }
                    if (SetRenderPipelineFromCamera)
                    {
                        _renderPipeline.Pipeline = _camera?.RenderPipeline;

                        // When the camera (and therefore pipeline) changes, we must push current
                        // sizing into the new pipeline instance. Otherwise the new pipeline can run
                        // with stale dimensions until a window resize occurs.
                        _renderPipeline.InternalResolutionResized(InternalWidth, InternalHeight);
                        _renderPipeline.ViewportResized(Width, Height);

                        // When the camera changes, destroy the pipeline cache to ensure
                        // stale textures/FBOs from the previous camera don't persist.
                        _renderPipeline.DestroyCache();
                    }
                    break;
                case nameof(CameraComponent):
                    ResizeCameraComponentUI();
                    // Set the Camera property - this will handle viewport binding if the reference changes
                    var newCam = CameraComponent?.Camera;
                    Debug.Out($"[XRViewport] CameraComponent changed: VP[{Index}] OldCamera={_camera?.GetHashCode().ToString() ?? "NULL"} NewCamera={newCam?.GetHashCode().ToString() ?? "NULL"} CamCompName={CameraComponent?.Name ?? "<null>"} CamCompHash={CameraComponent?.GetHashCode().ToString() ?? "null"}");
                    Camera = newCam;
                    // IMPORTANT: Even if Camera reference didn't change, we must ensure this viewport
                    // is in the camera's Viewports list. This can happen when:
                    // 1. The editor pawn survives snapshot restore (same objects reused)
                    // 2. The viewport was removed from camera.Viewports during play mode
                    // 3. SetField didn't detect a change because references are equal
                    EnsureViewportBoundToCamera();
                    Debug.Out($"[XRViewport] After EnsureViewportBoundToCamera: VP[{Index}] Camera.Viewports.Count={ActiveCamera?.Viewports.Count ?? -1}");
                    //_renderPipeline.Pipeline = CameraComponent?.RenderPipeline;
                    break;
            }
        }

        #endregion

        #region Visibility Collection

        /// <summary>
        /// Collects all visible items in the world and UI within the active camera for this viewport (ActiveCamera) and puts them into the render pipeline's render command collection.
        /// Use worldOverride to override the world instance to collect from.
        /// Use cameraOverride to override the camera to collect from.
        /// Use renderCommandsOverride to override the render command collection to collect into.
        /// </summary>
        /// <param name="collectMirrors"></param>
        /// <param name="worldOverride"></param>
        /// <param name="cameraOverride"></param>
        /// <param name="renderCommandsOverride"></param>
        public void CollectVisible(
            bool collectMirrors = true,
            XRWorldInstance? worldOverride = null,
            XRCamera? cameraOverride = null,
            RenderCommandCollection? renderCommandsOverride = null,
            bool allowScreenSpaceUICollectVisible = true,
            IVolume? collectionVolumeOverride = null)
        {
/*
            Debug.RenderingEvery(
                $"XRViewport.CollectVisible.Tick.{GetHashCode()}[{Index}].{Engine.PlayMode.State}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] CollectVisible tick. PlayMode={0} VP[{1}] AutoCollect={2} CameraNull={3} WorldNull={4} PipelineNull={5} AssocPlayer={6}",
                Engine.PlayMode.State,
                Index,
                AutomaticallyCollectVisible,
                ActiveCamera is null,
                (worldOverride ?? World) is null,
                _renderPipeline.Pipeline is null,
                AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");
*/

            XRCamera? camera = cameraOverride ?? ActiveCamera;
            if (camera is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.CollectVisible.NoCamera.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible skipped: no ActiveCamera. VP[{0}] AssocPlayer={1} CameraComponentNull={2} CameraFieldNull={3}",
                    Index,
                    AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>",
                    CameraComponent is null,
                    Camera is null);
                return;
            }

            XRWorldInstance? world = worldOverride ?? World;

            if (world is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.CollectVisible.NoWorld.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible skipped: no World. VP[{0}] WorldOverrideNull={1} WorldInstanceOverrideNull={2} CameraNodeWorldNull={3}",
                    Index,
                    worldOverride is null,
                    WorldInstanceOverride is null,
                    CameraComponent?.SceneNode?.World is null);
                return;
            }

            if (world.VisualScene is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.CollectVisible.NoVisualScene.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible skipped: world.VisualScene is null. VP[{0}] World={1}",
                    Index,
                    world.TargetWorld?.Name ?? "<unknown>");
                return;
            }

            // Only run player-view light intersection bookkeeping for player-associated viewports.
            if (AssociatedPlayer is not null && world is not null)
                world.Lights.UpdateCameraLightIntersections(camera);

            var commandCollection = renderCommandsOverride ?? _renderPipeline.MeshRenderCommands;
            int beforeUpdatingCount = 0;
            //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_SUBMIT") == "1")
                beforeUpdatingCount = commandCollection.GetUpdatingCommandCount();

            world?.VisualScene?.CollectRenderedItems(
                commandCollection,
                camera,
                CameraComponent?.CullWithFrustum ?? CullWithFrustum,
                CameraComponent?.CullingCameraOverride,
                collectionVolumeOverride,
                collectMirrors);

            //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_SUBMIT") == "1")
            {
                int afterUpdatingCount = commandCollection.GetUpdatingCommandCount();
                int delta = afterUpdatingCount - beforeUpdatingCount;

                string visualSceneType = world?.VisualScene?.GetType().Name ?? "<null>";
                int trackedRenderables = world?.VisualScene?.Renderables?.Count ?? -1;

                (uint Draws, uint Instances) gpuVisible = (0, 0);
                if (world?.VisualScene is VisualScene3D vs3d)
                    gpuVisible = vs3d.LastGpuVisibility;

/*
                Debug.RenderingEvery(
                    $"XRViewport.CollectVisible.Submit.{GetHashCode()}[{Index}].{Engine.PlayMode.State}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible submit. PlayMode={0} VP[{1}] CmdsUpdating={2} Delta={3} VisualScene={4} TrackedRenderables={5} GpuVisibleDraws={6} GpuVisibleInstances={7}",
                    Engine.PlayMode.State,
                    Index,
                    afterUpdatingCount,
                    delta,
                    visualSceneType,
                    trackedRenderables,
                    gpuVisible.Draws,
                    gpuVisible.Instances);
*/
            }

            if (allowScreenSpaceUICollectVisible)
                CollectVisible_ScreenSpaceUI();
        }

        /// <summary>
        /// Collects screen space UI items into the canvas' render pipeline.
        /// If AllowUIRender is false, the camera component has no UI canvas, or the canvas is not set to screen space, this will do nothing.
        /// </summary>
        private void CollectVisible_ScreenSpaceUI()
        {
            if (!AllowUIRender)
                return;

            UICanvasComponent? ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null)
                return;

            if (ui.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                ui?.CollectVisibleItemsScreenSpace();
        }

        private void CollectVisibleAutomatic()
        {
            CollectVisible();
        }

        #endregion

        #region Buffer Swapping

        public void SwapBuffers(
            RenderCommandCollection? renderCommandsOverride = null,
            bool allowScreenSpaceUISwap = true)
        {
            using var sample = Engine.Profiler.Start($"XRViewport.SwapBuffers[{Index}]");

/*
            Debug.RenderingEvery(
                $"XRViewport.SwapBuffers.Tick.{GetHashCode()}[{Index}].{Engine.PlayMode.State}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] SwapBuffers tick. PlayMode={0} VP[{1}] AutoSwap={2} AssocPlayer={3}",
                Engine.PlayMode.State,
                Index,
                AutomaticallySwapBuffers,
                AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");
*/
            var commandCollection = renderCommandsOverride ?? _renderPipeline.MeshRenderCommands;
            using (Engine.Profiler.Start("XRViewport.SwapBuffers.MeshCommands"))
            {
                commandCollection.SwapBuffers();
            }

            if (allowScreenSpaceUISwap)
            {
                using var uiSample = Engine.Profiler.Start("XRViewport.SwapBuffers.ScreenSpaceUI");
                SwapBuffers_ScreenSpaceUI();
            }
        }

        /// <summary>
        /// Swaps the screen space UI buffers.
        /// If AllowUIRender is false, the camera component has no UI canvas, or the canvas is not set to screen space, this will do nothing.
        /// </summary>
        private void SwapBuffers_ScreenSpaceUI()
        {
            if (!AllowUIRender)
                return;

            using var sample = Engine.Profiler.Start("XRViewport.SwapBuffers_ScreenSpaceUI");

            var ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null)
                return;

            if (ui.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                ui?.SwapBuffersScreenSpace();
        }

        private void SwapBuffersAutomatic()
        {
            using var sample = Engine.Profiler.Start("XRViewport.SwapBuffersAutomatic");
            SwapBuffers();
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Renders this camera's view to the specified viewport.
        /// </summary>
        /// <param name="targetFbo"></param>
        /// <param name="worldOverride"></param>
        /// <param name="cameraOverride"></param>
        /// <param name="shadowPass"></param>
        /// <param name="forcedMaterial"></param>
        public void Render(
            XRFrameBuffer? targetFbo = null,
            XRWorldInstance? worldOverride = null,
            XRCamera? cameraOverride = null,
            bool shadowPass = false,
            XRMaterial? forcedMaterial = null)
        {
            XRCamera? camera = cameraOverride ?? ActiveCamera;
            if (camera is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoCamera.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render skipped: no ActiveCamera. VP[{0}] AssocPlayer={1} CameraComponentNull={2} CameraFieldNull={3}",
                    Index,
                    AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>",
                    CameraComponent is null,
                    Camera is null);
                return;
            }

            var world = worldOverride ?? World;
            if (world is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoWorld.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render skipped: no World. VP[{0}] WorldOverrideNull={1} WorldInstanceOverrideNull={2} CameraNodeWorldNull={3}",
                    Index,
                    worldOverride is null,
                    WorldInstanceOverride is null,
                    CameraComponent?.SceneNode?.World is null);
                return;
            }

            if (world.VisualScene is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoVisualScene.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render skipped: world.VisualScene is null. VP[{0}] World={1}",
                    Index,
                    world.TargetWorld?.Name ?? "<unknown>");
                return;
            }

            if (_renderPipeline.Pipeline is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoPipeline.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render running with null pipeline. VP[{0}] CameraComponent={1} CameraRenderPipelineNull={2}",
                    Index,
                    CameraComponent?.GetType().Name ?? "<null>",
                    CameraComponent?.Camera.RenderPipeline is null);
            }

            if (State.RenderingPipelineState?.ViewportStack.Contains(this) ?? false)
            {
                Debug.LogWarning("Render recursion: Viewport is already currently rendering.");
                return;
            }

            // Diagnostic: Log camera transform state during render to help diagnose play mode transition issues
            Debug.RenderingEvery(
                $"XRViewport.Render.CameraState.{GetHashCode()}[{Index}]",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] VP[{0}] Render CameraState: CamHash={1} CamCompHash={2} CamCompName={3} TfmHash={4} TfmPos={5:F2},{6:F2},{7:F2} RenderPos={8:F2},{9:F2},{10:F2} PlayMode={11}",
                Index,
                camera.GetHashCode(),
                CameraComponent?.GetHashCode().ToString() ?? "null",
                CameraComponent?.Name ?? "<null>",
                camera.Transform?.GetHashCode().ToString() ?? "null",
                camera.Transform?.WorldTranslation.X ?? 0,
                camera.Transform?.WorldTranslation.Y ?? 0,
                camera.Transform?.WorldTranslation.Z ?? 0,
                camera.Transform?.RenderTranslation.X ?? 0,
                camera.Transform?.RenderTranslation.Y ?? 0,
                camera.Transform?.RenderTranslation.Z ?? 0,
                Engine.PlayMode.State);

            //using (Engine.Profiler.Start("XRViewport.Render"))
            {
                // Visibility-driven compute deformation (skinning/blendshapes).
                // This runs on the render thread and uses the swapped (rendering) command buffers.
                SkinningPrepassDispatcher.Instance.RunVisible(_renderPipeline.MeshRenderCommands);

                _renderPipeline.Render(
                    world.VisualScene,
                    camera,
                    null,
                    this,
                    targetFbo,
                    null,
                    shadowPass,
                    false,
                    forcedMaterial);

                RenderScreenSpaceUIOverlay(targetFbo);
            }
        }

        /// <summary>
        /// Renders this camera's view to the FBO in stereo.
        /// </summary>
        /// <param name="targetFbo"></param>
        /// <param name="leftCamera"></param>
        /// <param name="rightCamera"></param>
        /// <param name="worldOverride"></param>
        public void RenderStereo(
            XRFrameBuffer? targetFbo,
            XRCamera? leftCamera,
            XRCamera? rightCamera,
            XRWorldInstance? worldOverride = null)
        {
            var world = worldOverride ?? World;
            if (world is null)
            {
                Debug.LogWarning("No world is set to this viewport.");
                return;
            }

            if (State.RenderingPipelineState?.ViewportStack.Contains(this) ?? false)
            {
                Debug.LogWarning("Render recursion: Viewport is already currently rendering.");
                return;
            }

            _renderPipeline.Render(
                world.VisualScene,
                leftCamera,
                rightCamera,
                this,
                targetFbo,
                null,
                false,
                true,
                null);

            RenderScreenSpaceUIOverlay(targetFbo);
        }

        private void RenderScreenSpaceUIOverlay(XRFrameBuffer? targetFbo)
        {
            if (!AllowUIRender)
                return;

            var ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null || !ui.IsActive)
                return;

            if (ui.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
                return;

            // Render the UI using its own pipeline on top of whatever the camera pipeline produced.
            ui.RenderScreenSpace(this, targetFbo);
        }

        #endregion

        #region Resizing

        /// <summary>
        /// Sizes the viewport according to the window's size and the sizing percentages set to this viewport.
        /// </summary>
        /// <param name="windowWidth"></param>
        /// <param name="windowHeight"></param>
        /// <param name="setInternalResolution"></param>
        /// <param name="internalResolutionWidth"></param>
        /// <param name="internalResolutionHeight"></param>
        public void Resize(
            uint windowWidth,
            uint windowHeight,
            bool setInternalResolution = true,
            int internalResolutionWidth = -1,
            int internalResolutionHeight = -1)
        {
            float w = windowWidth.ClampMin(1u);
            float h = windowHeight.ClampMin(1u);

            _region.X = (int)(_leftPercentage * w);
            _region.Y = (int)(_bottomPercentage * h);
            _region.Width = (int)(_rightPercentage * w - _region.X);
            _region.Height = (int)(_topPercentage * h - _region.Y);

            if (setInternalResolution)
                SetInternalResolution(
                    internalResolutionWidth <= 0 ? _region.Width : internalResolutionWidth,
                    internalResolutionHeight <= 0 ? _region.Height : internalResolutionHeight,
                    true);

            ResizeCameraComponentUI();
            SetAspectRatioToCamera();
            ResizeRenderPipeline();
            Resized?.Invoke(this);
        }

        /// <summary>
        /// This is texture dimensions that the camera will render at, which will be scaled up to the actual resolution of the viewport.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="correctAspect"></param>
        public void SetInternalResolution(int width, int height, bool correctAspect)
        {
            _internalResolutionRegion.Width = width;
            _internalResolutionRegion.Height = height;
            if (correctAspect)
            {
                //Shrink the internal resolution to fit the aspect ratio of the viewport
                float aspect = (float)_region.Width / _region.Height;
                if (aspect > 1.0f)
                    _internalResolutionRegion.Height = (int)(_internalResolutionRegion.Width / aspect);
                else
                    _internalResolutionRegion.Width = (int)(_internalResolutionRegion.Height * aspect);
            }
            _renderPipeline.InternalResolutionResized(InternalWidth, InternalHeight);
            InternalResolutionResized?.Invoke(this);
        }

        public void SetInternalResolutionPercentage(float widthPercent, float heightPercent)
            => SetInternalResolution((int)(widthPercent * _region.Width), (int)(heightPercent * _region.Height), true);

        private void ResizeRenderPipeline()
            => _renderPipeline.ViewportResized(Width, Height);

        private void SetAspectRatioToCamera()
        {
            if (ActiveCamera?.Parameters is not XRPerspectiveCameraParameters p || !p.InheritAspectRatio)
                return;

            p.AspectRatio = (float)_region.Width / _region.Height;
        }

        private void ResizeCameraComponentUI()
        {
            var overlay = CameraComponent?.GetUserInterfaceOverlay();
            if (overlay is null)
                return;

            var tfm = overlay.CanvasTransform;
            tfm.Width = _region.Size.X;
            tfm.Height = _region.Size.Y;
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// Converts a window coordinate to a viewport coordinate.
        /// </summary>
        public Vector2 ScreenToViewportCoordinate(Vector2 coord)
            => new(coord.X - _region.X, coord.Y - _region.Y);

        public void ScreenToViewportCoordinate(ref Vector2 coord)
            => coord = ScreenToViewportCoordinate(coord);

        /// <summary>
        /// Converts a viewport coordinate to a window coordinate.
        /// </summary>
        public Vector2 ViewportToScreenCoordinate(Vector2 coord)
            => new(coord.X + _region.X, coord.Y + _region.Y);

        public void ViewportToScreenCoordinate(ref Vector2 coord)
            => coord = ViewportToScreenCoordinate(coord);

        public Vector2 ViewportToInternalCoordinate(Vector2 viewportPoint)
            => viewportPoint * (InternalResolutionRegion.Size / _region.Size);

        public Vector2 InternalToViewportCoordinate(Vector2 viewportPoint)
            => viewportPoint * (_region.Size / InternalResolutionRegion.Size);

        public Vector2 NormalizeViewportCoordinate(Vector2 viewportPoint)
            => viewportPoint / _region.Size;

        public Vector2 DenormalizeViewportCoordinate(Vector2 normalizedViewportPoint)
            => normalizedViewportPoint * _region.Size;

        public Vector2 NormalizeInternalCoordinate(Vector2 viewportPoint)
            => viewportPoint / _internalResolutionRegion.Size;

        public Vector2 DenormalizeInternalCoordinate(Vector2 normalizedViewportPoint)
            => normalizedViewportPoint * _internalResolutionRegion.Size;

        public Vector3 NormalizedViewportToWorldCoordinate(Vector2 normalizedViewportPoint, float depth)
        {
            if (_camera is null)
                throw new InvalidOperationException("No camera is set to this viewport.");

            return _camera.NormalizedViewportToWorldCoordinate(normalizedViewportPoint, depth);
        }

        public Vector3 NormalizedViewportToWorldCoordinate(Vector3 normalizedViewportPoint)
            => NormalizedViewportToWorldCoordinate(new Vector2(normalizedViewportPoint.X, normalizedViewportPoint.Y), normalizedViewportPoint.Z);

        public Vector3 WorldToNormalizedViewportCoordinate(Vector3 worldPoint)
        {
            if (_camera is null)
                throw new InvalidOperationException("No camera is set to this viewport.");

            return _camera.WorldToNormalizedViewportCoordinate(worldPoint);
        }

        #endregion

        #region Picking

        public float GetDepth(IVector2 viewportPoint)
        {
            State.UnbindFrameBuffers(EFramebufferTarget.ReadFramebuffer);
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetDepth(viewportPoint.X, viewportPoint.Y);
        }

        public byte GetStencil(Vector2 viewportPoint)
        {
            State.UnbindFrameBuffers(EFramebufferTarget.ReadFramebuffer);
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetStencilIndex(viewportPoint.X, viewportPoint.Y);
        }

        public float GetDepth(XRFrameBuffer fbo, IVector2 viewportPoint)
        {
            using var t = fbo.BindForReadingState();
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetDepth(viewportPoint.X, viewportPoint.Y);
        }

        public async Task<float> GetDepthAsync(XRFrameBuffer fbo, IVector2 viewportPoint)
        {
            return await State.GetDepthAsync(fbo, viewportPoint.X, viewportPoint.Y);
        }

        public byte GetStencil(XRFrameBuffer fbo, Vector2 viewportPoint)
        {
            using var t = fbo.BindForReadingState();
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetStencilIndex(viewportPoint.X, viewportPoint.Y);
        }

        /// <summary>
        /// Returns a ray projected from the given screen location.
        /// </summary>
        public Ray GetWorldRay(Vector2 viewportPoint)
        {
            if (_camera is null)
                throw new InvalidOperationException("No camera is set to this viewport.");

            return _camera.GetWorldRay(viewportPoint / _region.Size);
        }

        /// <summary>
        /// Returns a segment projected from the given screen location.
        /// Endpoints are located on the NearZ and FarZ planes.
        /// </summary>
        public Segment GetWorldSegment(Vector2 normalizedViewportPoint)
        {
            if (_camera is null)
                return new Segment(Vector3.Zero, Vector3.Zero);

            return _camera.GetWorldSegment(normalizedViewportPoint);
        }

        //TODO: provide PickScene with a List<(XRComponent item, object? data)> pool to take from and release to. As few allocations as possible for constant picking every frame.

        //private readonly RayTraceClosest _closestPick = new(Vector3.Zero, Vector3.Zero, 0, 0xFFFF);

        /// <summary>
        /// Tests against the HUD and the world for a collision hit at the provided viewport point ray.
        /// </summary>
        public void PickSceneAsync(
            Vector2 normalizedViewportPosition,
            bool testHud,
            bool testSceneOctree,
            bool testScenePhysics,
            LayerMask layerMask,
            AbstractPhysicsScene.IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedOctreeResults,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedPhysicsResults,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> octreeFinishedCallback,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>?> physicsFinishedCallback,
            ERaycastHitMode octreeHitMode = ERaycastHitMode.Faces,
            params XRComponent[] ignored)
        {
            if (CameraComponent is null)
                return;

            if (testHud)
            {
                var cameraCanvas = CameraComponent.GetUserInterfaceOverlay();
                if (cameraCanvas is not null && cameraCanvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.World)
                {
                    UIComponent?[] hudComps = cameraCanvas.FindDeepestComponents(normalizedViewportPosition);
                    foreach (var hudComp in hudComps)
                    {
                        if (hudComp is not UIInteractableComponent inter || !inter.IsActive)
                            continue;

                        float dist = 0.0f;
                        dist = CameraComponent.Camera.DistanceFrom(hudComp.Transform.WorldTranslation, false);
                        orderedPhysicsResults.Add(dist, [(inter, null)]);
                    }
                }
            }

            if (World is null)
                return;

            if (testSceneOctree)
            {
                World.RaycastOctreeAsync(
                    CameraComponent,
                    normalizedViewportPosition,
                    orderedOctreeResults,
                    octreeFinishedCallback,
                    octreeHitMode);
            }

            if (testScenePhysics)
            {
                World.RaycastPhysicsAsync(
                    CameraComponent,
                    normalizedViewportPosition,
                    layerMask,
                    filter,
                    orderedPhysicsResults,
                    physicsFinishedCallback);
            }
        }

        #endregion

        #region Viewport Layout

        public void ViewportCountChanged(int newIndex, int total, ETwoPlayerPreference twoPlayerPref, EThreePlayerPreference threePlayerPref)
        {
            Index = newIndex;
            switch (total)
            {
                case 1:
                    SetFullScreen();
                    break;
                case 2:
                    switch (newIndex)
                    {
                        case 0:
                            if (twoPlayerPref == ETwoPlayerPreference.SplitHorizontally)
                                SetTop();
                            else
                                SetLeft();
                            break;
                        case 1:
                            if (twoPlayerPref == ETwoPlayerPreference.SplitHorizontally)
                                SetBottom();
                            else
                                SetRight();
                            break;
                    }
                    break;
                case 3:
                    switch (newIndex)
                    {
                        case 0:
                            switch (threePlayerPref)
                            {
                                case EThreePlayerPreference.BlankBottomRight:
                                    SetTopLeft();
                                    break;
                                case EThreePlayerPreference.PreferFirstPlayer:
                                    SetTop();
                                    break;
                                case EThreePlayerPreference.PreferSecondPlayer:
                                    SetBottomLeft();
                                    break;
                                case EThreePlayerPreference.PreferThirdPlayer:
                                    SetTopLeft();
                                    break;
                            }
                            break;
                        case 1:
                            switch (threePlayerPref)
                            {
                                case EThreePlayerPreference.BlankBottomRight:
                                    SetTopRight();
                                    break;
                                case EThreePlayerPreference.PreferFirstPlayer:
                                    SetBottomLeft();
                                    break;
                                case EThreePlayerPreference.PreferSecondPlayer:
                                    SetTop();
                                    break;
                                case EThreePlayerPreference.PreferThirdPlayer:
                                    SetTopRight();
                                    break;
                            }
                            break;
                        case 2:
                            switch (threePlayerPref)
                            {
                                case EThreePlayerPreference.BlankBottomRight:
                                    SetBottomLeft();
                                    break;
                                case EThreePlayerPreference.PreferFirstPlayer:
                                    SetBottomRight();
                                    break;
                                case EThreePlayerPreference.PreferSecondPlayer:
                                    SetBottomRight();
                                    break;
                                case EThreePlayerPreference.PreferThirdPlayer:
                                    SetBottom();
                                    break;
                            }
                            break;
                    }
                    break;
                case 4:
                    switch (newIndex)
                    {
                        case 0: SetTopLeft(); break;
                        case 1: SetTopRight(); break;
                        case 2: SetBottomLeft(); break;
                        case 3: SetBottomRight(); break;
                    }
                    break;
            }
        }

        public void SetFullScreen()
        {
            _leftPercentage = _bottomPercentage = 0.0f;
            _rightPercentage = _topPercentage = 1.0f;
        }

        public void SetTop()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 1.0f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }

        public void SetBottom()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 1.0f;
            _topPercentage = 0.5f;
            _bottomPercentage = 0.0f;
        }

        public void SetLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.0f;
        }

        public void SetRight()
        {
            _leftPercentage = 0.5f;
            _rightPercentage = 1.0f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.0f;
        }

        public void SetTopLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }

        public void SetTopRight()
        {
            _leftPercentage = 0.5f;
            _rightPercentage = 1.0f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }

        public void SetBottomLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 0.5f;
            _bottomPercentage = 0.0f;
        }

        public void SetBottomRight()
        {
            _leftPercentage = 0.5f;
            _rightPercentage = 1.0f;
            _topPercentage = 0.5f;
            _bottomPercentage = 0.0f;
        }

        #endregion
    }
}