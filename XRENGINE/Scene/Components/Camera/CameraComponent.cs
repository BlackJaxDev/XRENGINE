using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Components
{
    /// <summary>
    /// This component wraps a camera object.
    /// </summary>
    public class CameraComponent : XRComponent
    {
        private readonly Lazy<XRCamera> _camera;
        public XRCamera Camera => _camera.Value;

        private XRFrameBuffer? _defaultRenderTarget = null;
        public XRFrameBuffer? DefaultRenderTarget 
        {
            get => _defaultRenderTarget;
            set => SetField(ref _defaultRenderTarget, value);
        }

        //private ELocalPlayerIndex? _localPlayerIndex = null;
        //public ELocalPlayerIndex? LocalPlayerIndex
        //{
        //    get => _localPlayerIndex;
        //    set => SetField(ref _localPlayerIndex, value);
        //}

        public UICanvasComponent? _userInterface;
        /// <summary>
        /// Provides the option for the user to manually set a canvas to render on top of the camera.
        /// </summary>
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
        public bool CullWithFrustum
        {
            get => _cullWithFrustum;
            set => SetField(ref _cullWithFrustum, value);
        }

        private Func<XRCamera>? _cullingCameraOverride = null;
        /// <summary>
        /// When CullWithFrustum is true and this property is not null, this method retrieves the camera frustum to cull with.
        /// </summary>
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
                            UserInterface.CanvasTransform.CameraSpaceCamera = null;
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
                //case nameof(LocalPlayerIndex):
                //    if (LocalPlayerIndex is not null)
                //        Engine.State.GetLocalPlayer(LocalPlayerIndex.Value)?.Cameras.Add(this);
                //    break;
                //case nameof(RenderPipeline):
                //    if (_fboRenderPipeline is not null)
                //        _fboRenderPipeline.Pipeline = RenderPipeline;
                //    break;
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
                        UserInterface.CanvasTransform.SetSize(Camera.Parameters.GetFrustumSizeAtDistance(UserInterface.CanvasTransform.CameraDrawSpaceDistance));
                        UserInterface.CanvasTransform.CameraSpaceCamera = Camera;
                    }
                    break;
            }
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
        }

        private void ViewportResized(XRViewport viewport)
        {
            if (UserInterface is not null && UserInterface.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                UserInterface.CanvasTransform.SetSize(viewport.Region.Size);
        }
        private void CameraResized(XRCameraParameters parameters)
        {
            if (UserInterface is null || UserInterface.CanvasTransform.DrawSpace != ECanvasDrawSpace.Camera)
                return;

            //Calculate world-space size of the camera frustum at draw distance
            UserInterface.CanvasTransform.SetSize(parameters.GetFrustumSizeAtDistance(UserInterface.CanvasTransform.CameraDrawSpaceDistance));
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
