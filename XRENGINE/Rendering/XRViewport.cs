using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Input;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using YamlDotNet.Serialization;
using State = XREngine.Engine.Rendering.State;

namespace XREngine.Rendering
{
    /// <summary>
    /// Defines a rectangular area to render to.
    /// Can either be a window or render texture.
    /// </summary>
    public sealed class XRViewport : XRBase
    {
        public XRWindow? Window { get; set; }

        private XRCamera? _camera = null;

        private BoundingRectangle _region = new();
        private BoundingRectangle _internalResolutionRegion = new();

        public BoundingRectangle Region => _region;
        public BoundingRectangle InternalResolutionRegion => _internalResolutionRegion;

        public float _leftPercentage = 0.0f;
        public float _rightPercentage = 1.0f;
        public float _bottomPercentage = 0.0f;
        public float _topPercentage = 1.0f;
        private LocalPlayerController? _associatedPlayer = null;

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

                if (_associatedPlayer is not null)
                    _associatedPlayer.Viewport = null;
                SetField(ref _associatedPlayer, value);
                if (_associatedPlayer is not null)
                    _associatedPlayer.Viewport = this;
            }
        }

        /// <summary>
        /// The world instance to render.
        /// This must be set when no camera component is set, and will take precedence over the camera component's world instance if both are set.
        /// </summary>
        public XRWorldInstance? WorldInstanceOverride { get; set; } = null;
        /// <summary>
        /// The world instance that will be rendered to this viewport.
        /// </summary>
        public XRWorldInstance? World => WorldInstanceOverride ?? CameraComponent?.SceneNode?.World;

        public void Destroy()
        {
            Camera = null;
            CameraComponent = null;
        }

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

        private void CollectVisibleInternal()
        {
            if (AutomaticallyCollectVisible)
                CollectVisible(null, null);
        }
        public void CollectVisible(XRWorldInstance? worldOverride, XRCamera? cameraOverride)
        {
            XRCamera? camera = cameraOverride ?? ActiveCamera;
            if (camera is null)
                return;

            (worldOverride ?? World)?.VisualScene?.CollectRenderedItems(
                _renderPipeline.MeshRenderCommands,
                camera,
                CameraComponent?.CullWithFrustum ?? true,
                CameraComponent?.CullingCameraOverride);

            CollectVisibleScreenSpaceUI();
        }

        private void CollectVisibleScreenSpaceUI()
        {
            if (!AllowUIRender)
                return;

            var ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null)
                return;

            if (ui.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                ui?.CollectVisibleItemsScreenSpace();
        }

        private void SwapBuffersInternal()
        {
            if (!AutomaticallySwapBuffers)
                return;

            XRCamera? camera = ActiveCamera;
            if (camera is null)
                return;

            SwapBuffers();
        }
        public void SwapBuffers()
        {
            _renderPipeline.MeshRenderCommands.SwapBuffers();
            SwapScreenSpaceUIBuffers();
        }

        private void SwapScreenSpaceUIBuffers()
        {
            if (!AllowUIRender)
                return;

            var ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null)
                return;

            if (ui.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                ui?.SwapBuffersScreenSpace();
        }

        /// <summary>
        /// Renders this camera's view to the specified viewport.
        /// </summary>
        /// <param name="vp"></param>
        /// <param name="targetFbo"></param>
        public void Render(
            XRFrameBuffer? targetFbo = null,
            XRWorldInstance? worldOverride = null,
            XRCamera? cameraOverride = null,
            bool shadowPass = false,
            XRMaterial? forcedMaterial = null)
        {
            XRCamera? camera = cameraOverride ?? ActiveCamera;
            if (camera is null)
                return;

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
                camera,
                null,
                this,
                targetFbo,
                AllowUIRender ? CameraComponent?.GetUserInterfaceOverlay() : null,
                shadowPass,
                false,
                forcedMaterial);
        }
        /// <summary>
        /// Renders this camera's view to the FBO in stereo.
        /// </summary>
        /// <param name="vp"></param>
        /// <param name="targetFbo"></param>
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
                AllowUIRender ? CameraComponent?.GetUserInterfaceOverlay() : null,
                false,
                true,
                null);
        }

        private CameraComponent? _cameraComponent = null;
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

        private readonly XRRenderPipelineInstance _renderPipeline = new();
        public XRRenderPipelineInstance RenderPipelineInstance => _renderPipeline;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Camera):
                        if (_camera is not null)
                        {
                            _camera.Viewports.Remove(this);
                            Engine.Time.Timer.SwapBuffers -= SwapBuffersInternal;
                            Engine.Time.Timer.CollectVisible -= CollectVisibleInternal;
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
                case nameof(Camera):
                    if (_camera is not null)
                    {
                        if (!_camera.Viewports.Contains(this))
                            _camera.Viewports.Add(this);
                        SetAspectRatioToCamera();
                        Engine.Time.Timer.SwapBuffers += SwapBuffersInternal;
                        Engine.Time.Timer.CollectVisible += CollectVisibleInternal;
                    }
                    if (SetRenderPipelineFromCamera)
                        _renderPipeline.Pipeline = _camera?.RenderPipeline;
                    break;
                case nameof(CameraComponent):
                    ResizeCameraComponentUI();
                    Camera = CameraComponent?.Camera;
                    //_renderPipeline.Pipeline = CameraComponent?.RenderPipeline;
                    break;
            }
        }

        public RenderPipeline? RenderPipeline
        {
            get => _renderPipeline.Pipeline;
            set => _renderPipeline.Pipeline = value;
        }

        private bool _setRenderPipelineFromCamera = true;
        private bool _automaticallyCollectVisible = true;
        private bool _automaticallySwapBuffers = true;
        private bool _allowUIRender = true;
        private int _index = 0;

        public bool SetRenderPipelineFromCamera
        {
            get => _setRenderPipelineFromCamera;
            set => SetField(ref _setRenderPipelineFromCamera, value);
        }

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

        public event Action<XRViewport>? Resized;
        public event Action<XRViewport>? InternalResolutionResized;

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

        /// <summary>
        /// This is texture dimensions that the camera will render at, which will be scaled up to the actual resolution of the viewport.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
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

        #region Coordinate conversion
        /// <summary>
        /// Converts a window coordinate to a viewport coordinate.
        /// </summary>
        /// <param name="coord"></param>
        /// <returns></returns>
        public Vector2 ScreenToViewportCoordinate(Vector2 coord)
            => new(coord.X - _region.X, coord.Y - _region.Y);
        public void ScreenToViewportCoordinate(ref Vector2 coord)
            => coord = ScreenToViewportCoordinate(coord);
        /// <summary>
        /// Converts a viewport coordinate to a window coordinate.
        /// </summary>
        /// <param name="coord"></param>
        /// <returns></returns>
        public Vector2 ViewportToScreenCoordinate(Vector2 coord)
            => new(coord.X + _region.X, coord.Y + _region.Y);
        public void ViewportToScreenCoordinate(ref Vector2 coord)
            => coord = ViewportToScreenCoordinate(coord);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="viewportPoint"></param>
        /// <returns></returns>
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
        /// <param name="normalizedViewportPosition"></param>
        /// <param name="testHud"></param>
        /// <param name="interactableHudOnly"></param>
        /// <param name="testSceneOctree"></param>
        /// <param name="hitNormalWorld"></param>
        /// <param name="hitPositionWorld"></param>
        /// <param name="hitDistance"></param>
        /// <param name="ignored"></param>
        /// <returns></returns>
        public bool PickScene(
            Vector2 normalizedViewportPosition,
            bool testHud,
            bool testSceneOctree,
            bool testScenePhysics,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedOctreeResults,
            SortedDictionary<float, List<(XRComponent item, object? data)>> orderedPhysicsResults,
            params XRComponent[] ignored)
        {
            bool hasMatches = false;
            if (CameraComponent is null)
                return hasMatches;

            //if (testHud)
            //{
            //    var cameraCanvas = CameraComponent.GetUserInterfaceOverlay();
            //    if (cameraCanvas is not null && cameraCanvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.World)
            //    {
            //        UIComponent?[] hudComps = cameraCanvas.FindDeepestComponents(normalizedViewportPosition);
            //        foreach (var hudComp in hudComps)
            //        {
            //            if (hudComp is not UIInteractableComponent inter || !inter.IsActive)
            //                continue;
                        
            //            hasMatches = true;
            //            float dist = 0.0f;
            //            dist = CameraComponent.Camera.DistanceFrom(hudComp.Transform.WorldTranslation, false);
            //            orderedPhysicsResults.Add(dist, [(inter, null)]);
            //        }
            //    }
            //}

            if (testSceneOctree)
            {
                hasMatches |= World?.RaycastOctree(
                    CameraComponent,
                    normalizedViewportPosition,
                    orderedOctreeResults) ?? false;
            }

            if (testScenePhysics)
            {
                hasMatches |= World?.RaycastPhysics(
                    CameraComponent, 
                    normalizedViewportPosition,
                    orderedPhysicsResults) ?? false;
            }

            return hasMatches;
        }
        #endregion

        #region Viewport Resizing
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
        public void SetTopLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }
        public void SetTopRight()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }
        public void SetBottomLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }
        public void SetBottomRight()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
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
        public void SetFullScreen()
        {
            _leftPercentage = _bottomPercentage = 0.0f;
            _rightPercentage = _topPercentage = 1.0f;
        }
        #endregion
    }
}