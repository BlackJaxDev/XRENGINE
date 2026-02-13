using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Components
{
    /// <summary>
    /// Defines how the camera's internal rendering resolution is determined.
    /// </summary>
    public enum EInternalResolutionMode
    {
        /// <summary>
        /// Internal resolution matches the viewport's full resolution (no scaling).
        /// </summary>
        [Description("Internal resolution matches the viewport's full resolution.")]
        FullResolution,

        /// <summary>
        /// Internal resolution is scaled by a factor relative to the viewport size.
        /// For example, 0.5 renders at half resolution and upscales.
        /// </summary>
        [Description("Internal resolution is scaled by a factor (e.g., 0.5 = half resolution).")]
        Scale,

        /// <summary>
        /// Internal resolution is manually specified in pixels.
        /// </summary>
        [Description("Internal resolution is manually specified in pixels.")]
        Manual
    }

    /// <summary>
    /// This component wraps a camera object.
    /// </summary>
    [Category("Rendering")]
    [DisplayName("Camera")]
    [Description("Renders the world from this scene node and can optionally composite UI canvases.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.CameraComponentEditor")]
    public class CameraComponent : XRComponent
    {
        private readonly Lazy<XRCamera> _camera;
        public XRCamera Camera => _camera.Value;

        private XRFrameBuffer? _defaultRenderTarget = null;
        /// <summary>
        /// Optional framebuffer target for off-screen rendering.
        /// </summary>
        [Category("Rendering")]
        [DisplayName("Default Render Target")]
        [Description("Optional framebuffer target for off-screen rendering.")]
        public XRFrameBuffer? DefaultRenderTarget 
        {
            get => _defaultRenderTarget;
            set => SetField(ref _defaultRenderTarget, value);
        }

        private EInternalResolutionMode _internalResolutionMode = EInternalResolutionMode.FullResolution;
        /// <summary>
        /// Determines how the camera's internal rendering resolution is calculated.
        /// </summary>
        [Category("Rendering")]
        [DisplayName("Internal Resolution Mode")]
        [Description("How the camera's internal rendering resolution is determined.")]
        public EInternalResolutionMode InternalResolutionMode
        {
            get => _internalResolutionMode;
            set => SetField(ref _internalResolutionMode, value);
        }

        private float _internalResolutionScale = 1.0f;
        /// <summary>
        /// Scale factor for internal resolution when using Scale mode.
        /// 1.0 = full resolution, 0.5 = half resolution, 2.0 = double resolution.
        /// </summary>
        [Category("Rendering")]
        [DisplayName("Resolution Scale")]
        [Description("Scale factor for internal resolution (1.0 = full, 0.5 = half, 2.0 = double).")]
        public float InternalResolutionScale
        {
            get => _internalResolutionScale;
            set => SetField(ref _internalResolutionScale, Math.Clamp(value, 0.1f, 4.0f));
        }

        private int _manualInternalWidth = 1920;
        /// <summary>
        /// Manual internal resolution width in pixels when using Manual mode.
        /// </summary>
        [Category("Rendering")]
        [DisplayName("Manual Width")]
        [Description("Manual internal resolution width in pixels.")]
        public int ManualInternalWidth
        {
            get => _manualInternalWidth;
            set => SetField(ref _manualInternalWidth, Math.Max(1, value));
        }

        private int _manualInternalHeight = 1080;
        /// <summary>
        /// Manual internal resolution height in pixels when using Manual mode.
        /// </summary>
        [Category("Rendering")]
        [DisplayName("Manual Height")]
        [Description("Manual internal resolution height in pixels.")]
        public int ManualInternalHeight
        {
            get => _manualInternalHeight;
            set => SetField(ref _manualInternalHeight, Math.Max(1, value));
        }

        /// <summary>
        /// Applies the internal resolution settings to the given viewport.
        /// </summary>
        public void ApplyInternalResolutionToViewport(XRViewport viewport)
        {
            switch (_internalResolutionMode)
            {
                case EInternalResolutionMode.FullResolution:
                    viewport.SetInternalResolution(viewport.Width, viewport.Height, true);
                    break;
                case EInternalResolutionMode.Scale:
                    viewport.SetInternalResolutionPercentage(_internalResolutionScale, _internalResolutionScale);
                    break;
                case EInternalResolutionMode.Manual:
                    viewport.SetInternalResolution(_manualInternalWidth, _manualInternalHeight, true);
                    break;
            }
        }

        /// <summary>
        /// Returns true if this camera is actively being used for rendering by any viewport or local player.
        /// </summary>
        [Browsable(false)]
        public bool IsActivelyRendering
        {
            get
            {
                if (DefaultRenderTarget is not null)
                    return true;

                // Fast path: XRCamera-maintained list.
                if (Camera.Viewports.Count > 0)
                    return true;

                // Snapshot restore / play-mode transitions can temporarily desync runtime-only links.
                // The authoritative source of "is this camera used" is whether any live viewport points at it.
                foreach (var viewport in Engine.EnumerateActiveViewports())
                    if (ReferenceEquals(viewport.CameraComponent, this))
                        return true;

                return false;
            }
        }

        /// <summary>
        /// Finds the local player controller that is using this camera for rendering, if any.
        /// </summary>
        /// <returns>The local player controller using this camera, or null if not in use by any local player.</returns>
        public LocalPlayerController? GetUsingLocalPlayer()
        {
            foreach (var player in Engine.State.LocalPlayers)
            {
                if (player is null)
                    continue;

                if (player.Viewport?.CameraComponent == this)
                    return player;
            }

            // Fallback: player.Viewport is runtime-only and can be temporarily null/stale after snapshot restore.
            // If a viewport is bound to this camera, trust the viewport's AssociatedPlayer.
            foreach (var viewport in Engine.EnumerateActiveViewports())
                if (ReferenceEquals(viewport.CameraComponent, this) && viewport.AssociatedPlayer is not null)
                    return viewport.AssociatedPlayer;

            return null;
        }

        /// <summary>
        /// Finds the pawn component that provides this camera to a local player, if any.
        /// </summary>
        /// <returns>The pawn component using this camera, or null if not in use by any pawn.</returns>
        public PawnComponent? GetUsingPawn()
        {
            var player = GetUsingLocalPlayer();
            if (player?.ControlledPawn?.GetCamera() == this)
                return player.ControlledPawn;

            // Also check if a sibling pawn is using this camera
            if (SceneNode?.TryGetComponent<PawnComponent>(out var siblingPawn) == true && siblingPawn is not null)
                if (siblingPawn.CameraComponent == this || siblingPawn.GetCamera() == this)
                    return siblingPawn;

            return null;
        }

        /// <summary>
        /// Gets usage information as a descriptive string for debugging/editor purposes.
        /// </summary>
        /// <returns>A string describing how this camera is being used.</returns>
        public string GetUsageDescription()
        {
            var player = GetUsingLocalPlayer();
            var pawn = GetUsingPawn();
            
            var parts = new List<string>();

            if (player is not null)
                parts.Add($"Player {(int)player.LocalPlayerIndex + 1}");
            
            if (pawn is not null)
            {
                string pawnName = pawn.SceneNode?.Name ?? pawn.GetType().Name;
                parts.Add($"Pawn: {pawnName}");
            }

            if (Camera.Viewports.Count > 0)
                parts.Add($"{Camera.Viewports.Count} viewport(s)");

            if (DefaultRenderTarget is not null)
                parts.Add($"FBO: {DefaultRenderTarget.Name ?? "unnamed"}");

            return parts.Count > 0 ? string.Join(", ", parts) : "Not in use";
        }

        private UICanvasComponent? _userInterface;
        /// <summary>
        /// Provides the option for the user to manually set a canvas to render on top of the camera.
        /// </summary>
        [Category("UI")]
        [DisplayName("User Interface")]
        [Description("Canvas overlay rendered on top of this camera's output.")]
        public UICanvasComponent? UserInterface
        {
            get => _userInterface;
            set => SetField(ref _userInterface, value);
        }

        /// <summary>
        /// Retrieves the user interface overlay for this camera, either from the UserInterfaceOverlay property or from a sibling component.
        /// </summary>
        /// <returns></returns>
        public UICanvasComponent? GetUserInterfaceOverlay()
        {
            if (_userInterface is not null)
                return _userInterface;

            if (GetSiblingComponent<UICanvasComponent>() is UICanvasComponent ui)
                return ui;

            return null;
        }

        private bool _cullWithFrustum = true;
        /// <summary>
        /// If true, the camera will cull objects that are not within the camera's frustum.
        /// This should always be true in production, but can be set to false for debug purposes.
        /// </summary>
        [Category("Culling")]
        [DisplayName("Cull With Frustum")]
        [Description("When enabled, objects outside the camera frustum are not rendered.")]
        public bool CullWithFrustum
        {
            get => _cullWithFrustum;
            set => SetField(ref _cullWithFrustum, value);
        }

        private Func<XRCamera>? _cullingCameraOverride = null;
        /// <summary>
        /// When CullWithFrustum is true and this property is not null, this method retrieves the camera frustum to cull with.
        /// </summary>
        [Browsable(false)]
        public Func<XRCamera>? CullingCameraOverride
        {
            get => _cullingCameraOverride;
            set => SetField(ref _cullingCameraOverride, value);
        }

        private XRCamera CameraFactory()
        {
            var cam = new XRCamera(Transform);
            cam.PropertyChanged += CameraPropertyChanged;
            cam.ViewportAdded += ViewportAdded;
            cam.ViewportRemoved += ViewportRemoved;
            cam.Parameters.PropertyChanged += CameraParameterPropertyChanged;
            if (cam.Viewports.Count > 0)
            {
                foreach (var vp in cam.Viewports)
                    ViewportAdded(cam, vp);
                ViewportResized(cam.Viewports[0]); //TODO: support rendering in screenspace to more than one viewport?
            }
            CameraResized(cam.Parameters);
            return cam;
        }

        public CameraComponent() : base()
        {
            _camera = new(CameraFactory, true);
        }

        protected override void OnDestroying()
        {
            if (!_camera.IsValueCreated)
                return;

            Camera.PropertyChanged -= CameraPropertyChanged;
            Camera.ViewportAdded -= ViewportAdded;
            Camera.ViewportRemoved -= ViewportRemoved;
            Camera.Parameters.PropertyChanged -= CameraParameterPropertyChanged;
            if (Camera.Viewports.Count > 0)
                foreach (var vp in Camera.Viewports)
                    ViewportRemoved(Camera, vp);
        }

        /// <summary>
        /// Helper method to set this camera as the view of the player with the given index.
        /// Creates a new pawn component and uses this camera as the view via being a sibling component.
        /// </summary>
        /// <param name="playerIndex"></param>
        public PawnComponent SetAsPlayerView(ELocalPlayerIndex playerIndex)
        {
            if (!SceneNode.TryGetComponent<PawnComponent>(out var pawn) || pawn is null)
                pawn = SceneNode.AddComponent<PawnComponent>()!;
            
            pawn.EnqueuePossessionByLocalPlayer(playerIndex);
            return pawn;
        }

        /// <summary>
        /// Helper method to set this camera as the view of the player with the given index.
        /// Creates a new pawn component of the given type and uses this camera as the view via being a sibling component.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="playerIndex"></param>
        public void SetAsPlayerView<T>(ELocalPlayerIndex playerIndex) where T : PawnComponent
            => SceneNode.AddComponent<T>()?.EnqueuePossessionByLocalPlayer(playerIndex);

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            if (_camera.IsValueCreated)
                Camera.Transform = Transform;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    //case nameof(LocalPlayerIndex):
                    //    if (LocalPlayerIndex is not null)
                    //        Engine.State.GetLocalPlayer(LocalPlayerIndex.Value)?.Cameras.Remove(this);
                    //    break;
                    case nameof(DefaultRenderTarget):
                        if (DefaultRenderTarget is not null && World is not null)
                            World.FramebufferCameras.Remove(this);
                        break;
                    case nameof(World):
                        if (DefaultRenderTarget is not null && World is not null)
                            World.FramebufferCameras.Remove(this);
                        break;
                    case nameof(UserInterface):
                            if (UserInterface is not null)
                            {
                                var canvasTransform = UserInterface.TransformAs<UICanvasTransform>(false);
                                if (canvasTransform is not null)
                                    canvasTransform.CameraSpaceCamera = null;
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
                case nameof(DefaultRenderTarget):
                    if (DefaultRenderTarget is not null && World is not null)
                        if (!World.FramebufferCameras.Contains(this))
                            World.FramebufferCameras.Add(this);
                    break;
                case nameof(World):
                    if (DefaultRenderTarget is not null && World is not null)
                        if (!World.FramebufferCameras.Contains(this))
                            World.FramebufferCameras.Add(this);
                    break;
                case nameof(UserInterface):
                    if (UserInterface is not null)
                    {
                        var canvasTransform = UserInterface.TransformAs<UICanvasTransform>(false);
                        if (canvasTransform is null)
                            break;

                        // Only set camera-space sizing for Camera draw space.
                        // Screen-space canvases get their size from the viewport.
                        if (canvasTransform.DrawSpace == ECanvasDrawSpace.Camera)
                        {
                            canvasTransform.SetSize(Camera.Parameters.GetFrustumSizeAtDistance(canvasTransform.CameraDrawSpaceDistance));
                            canvasTransform.CameraSpaceCamera = Camera;
                        }
                    }
                    break;
                case nameof(InternalResolutionMode):
                case nameof(InternalResolutionScale):
                case nameof(ManualInternalWidth):
                case nameof(ManualInternalHeight):
                    ApplyInternalResolutionToAllViewports();
                    break;
            }
        }

        /// <summary>
        /// Applies the internal resolution settings to all viewports using this camera.
        /// </summary>
        private void ApplyInternalResolutionToAllViewports()
        {
            if (!_camera.IsValueCreated)
                return;

            foreach (var viewport in Camera.Viewports)
                ApplyInternalResolutionToViewport(viewport);
        }

        private void CameraParameterPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRPerspectiveCameraParameters.VerticalFieldOfView):
                case nameof(XRPerspectiveCameraParameters.AspectRatio):
                case nameof(XROrthographicCameraParameters.Width):
                case nameof(XROrthographicCameraParameters.Height):
                    CameraResized(Camera.Parameters);
                    break;
            }
        }

        private void ViewportRemoved(XRCamera camera, XRViewport viewport)
        {
            viewport.Resized -= ViewportResized;
        }
        private void ViewportAdded(XRCamera camera, XRViewport viewport)
        {
            viewport.Resized += ViewportResized;
            // Apply initial internal resolution settings to the new viewport
            ApplyInternalResolutionToViewport(viewport);
        }

        private void ViewportResized(XRViewport viewport)
        {
            if (UserInterface is not null)
            {
                var canvasTransform = UserInterface.TransformAs<UICanvasTransform>(false);
                if (canvasTransform is not null && canvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                    canvasTransform.SetSize(viewport.Region.Size);
            }

            // Reapply internal resolution settings after viewport resize
            ApplyInternalResolutionToViewport(viewport);
        }
        private void CameraResized(XRCameraParameters parameters)
        {
            if (UserInterface is null)
                return;

            var canvasTransform = UserInterface.TransformAs<UICanvasTransform>(false);
            if (canvasTransform is null || canvasTransform.DrawSpace != ECanvasDrawSpace.Camera)
                return;

            //Calculate world-space size of the camera frustum at draw distance
            canvasTransform.SetSize(parameters.GetFrustumSizeAtDistance(canvasTransform.CameraDrawSpaceDistance));
        }

        private void CameraPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                //The user is not allowed to update the camera's transform provider
                case nameof(Camera.Transform):
                    Camera.Transform = Transform;
                    break;
            }
        }

        /// <summary>
        /// Helper method to set the camera to orthographic projection.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="nearPlane"></param>
        /// <param name="farPlane"></param>
        public void SetOrthographic(float width, float height, float nearPlane, float farPlane)
            => Camera.Parameters = new XROrthographicCameraParameters(width, height, nearPlane, farPlane);

        /// <summary>
        /// Helper method to set the camera to perspective projection.
        /// </summary>
        /// <param name="verticalFieldOfView"></param>
        /// <param name="nearPlane"></param>
        /// <param name="farPlane"></param>
        /// <param name="aspectRatio"></param>
        public void SetPerspective(float verticalFieldOfView, float nearPlane, float farPlane, float? aspectRatio = null)
            => Camera.Parameters = new XRPerspectiveCameraParameters(verticalFieldOfView, aspectRatio, nearPlane, farPlane);

        //private void SceneNodePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        //{
        //    switch (e.PropertyName)
        //    {
        //        case nameof(XREngine.Scene.SceneNode.Transform):
        //            Camera.Transform = Transform;
        //            break;
        //    }
        //}

        //public List<View> CalculateMirrorBounces(int max = 4)
        //{
        //    List<View> bounces = [];
        //    if (max < 1)
        //        return bounces;

        //    Frustum lastFrustum = Camera.WorldFrustum();
        //    bounces.Add(new View(Camera.WorldViewProjectionMatrix, lastFrustum));
        //    //Determine if there are any mirror components that intersect the camera frustum
        //    if (World?.VisualScene?.RenderablesTree is not I3DRenderTree tree)
        //        return bounces;

        //    SortedSet<RenderInfo3D> mirrors = [];
        //    tree.CollectIntersecting(lastFrustum, false, x =>
        //    {
        //        if (x is RenderInfo3D info && info.Owner is MirrorComponent mirror)
        //            return true;
        //    });
        //    Matrix4x4 mirrorScaleZ = Matrix4x4.CreateScale(1, 1, -1);
        //    foreach (var mirror in Engine.State.GetComponents<MirrorComponent>())
        //    {
        //        if (mirror.CullWithFrustum && !lastFrustum.Intersects(mirror.WorldVolume))
        //            continue;

        //        //Calculate the reflection matrix
        //        Matrix4x4 mirrorMatrix = mirror.WorldMatrix;
        //        Matrix4x4 reflectionMatrix = mirrorScaleZ * mirrorMatrix;
        //        Matrix4x4 mvp = Camera.WorldViewProjectionMatrix * reflectionMatrix;
        //        Frustum frustum = lastFrustum.Transform(reflectionMatrix);
        //        bounces.Add(new View(mvp, frustum));
        //        lastFrustum = frustum;
        //    }


        //    return bounces;
        //}
    }

    internal record struct View(Matrix4x4 MVP, Frustum Frustum)
    {
        public static implicit operator (Matrix4x4 mvp, Frustum frustum)(View value)
            => (value.MVP, value.Frustum);
        public static implicit operator View((Matrix4x4 mvp, Frustum frustum) value)
            => new(value.mvp, value.frustum);
    }
}
