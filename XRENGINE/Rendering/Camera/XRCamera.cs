using Extensions;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    /// <summary>
    /// Defines the coordinate space that a projection jitter offset is expressed in.
    /// </summary>
    public enum ProjectionJitterSpace
    {
        /// <summary>
        /// Offset is expressed in clip space (NDC) units.
        /// </summary>
        ClipSpace,

        /// <summary>
        /// Offset is expressed in texel/pixel units relative to a reference resolution.
        /// </summary>
        TexelSpace
    }

    /// <summary>
    /// A request to apply a temporary sub-pixel offset to the projection matrix.
    /// </summary>
    public readonly struct ProjectionJitterRequest
    {
        public ProjectionJitterRequest(Vector2 offset, ProjectionJitterSpace space, Vector2 referenceResolution)
        {
            Offset = offset;
            Space = space;
            ReferenceResolution = referenceResolution;
        }

        public Vector2 Offset { get; }
        public ProjectionJitterSpace Space { get; }
        public Vector2 ReferenceResolution { get; }

        /// <summary>
        /// Creates a jitter request where <paramref name="offset"/> is already in clip space.
        /// </summary>
        public static ProjectionJitterRequest ClipSpace(Vector2 offset)
            => new(offset, ProjectionJitterSpace.ClipSpace, Vector2.One);

        /// <summary>
        /// Creates a jitter request where <paramref name="offset"/> is in texel/pixel units.
        /// </summary>
        public static ProjectionJitterRequest TexelSpace(Vector2 offset, Vector2 referenceResolution)
            => new(offset, ProjectionJitterSpace.TexelSpace, referenceResolution);

        /// <summary>
        /// Converts this request into clip space (NDC) units.
        /// </summary>
        public Vector2 ToClipSpace()
        {
            if (Space == ProjectionJitterSpace.ClipSpace)
                return Offset;

            // Convert a texel sized offset into clip space so it can be baked into the projection matrix.
            Vector2 resolution = ReferenceResolution;
            float width = MathF.Abs(resolution.X);
            float height = MathF.Abs(resolution.Y);
            return new Vector2(
                width <= float.Epsilon ? 0.0f : Offset.X * 2.0f / width,
                height <= float.Epsilon ? 0.0f : Offset.Y * 2.0f / height);
        }

        /// <summary>
        /// True if the requested offset is effectively zero.
        /// </summary>
        public bool IsZero
            => MathF.Abs(Offset.X) <= float.Epsilon && MathF.Abs(Offset.Y) <= float.Epsilon;
    }

    /// <summary>
    /// Placeholder base for specialized camera implementations.
    /// </summary>
    public class XRCameraBase : XRBase
    {
    }

    /// <summary>
    /// Placeholder VR camera type.
    /// </summary>
    public class VRCamera : XRCameraBase
    {

    }
    /// <summary>
    /// Camera component responsible for view/projection setup, frustum helpers, coordinate conversion,
    /// and per-camera render pipeline/post-process state.
    ///
    /// Projection jitter is supported via a stack so multiple systems can apply temporary jitter
    /// (e.g. temporal AA, temporal upscalers) without stomping one another.
    /// </summary>
    public class XRCamera : XRBase
    {
        #region Fields

        /// <summary>
        /// The transform that defines this camera's position and orientation in world space.
        /// Used to calculate view matrices and camera direction vectors.
        /// </summary>
        private TransformBase? _transform;

        /// <summary>
        /// Bit mask determining which layers this camera renders.
        /// Objects on layers not included in this mask are culled during visibility collection.
        /// Defaults to LayerMask.Everything (all layers visible).
        /// </summary>
        private LayerMask _cullingMask = LayerMask.Everything;

        /// <summary>
        /// Maximum distance from the camera at which shadows are collected.
        /// Lights beyond this distance won't cast shadows for this camera.
        /// Default is PositiveInfinity, meaning shadows are collected up to FarZ.
        /// </summary>
        private float _shadowCollectMaxDistance = float.PositiveInfinity;

        /// <summary>
        /// The projection parameters (FOV, aspect ratio, near/far planes) for this camera.
        /// Can be perspective or orthographic depending on the parameter type.
        /// </summary>
        private XRCameraParameters? _parameters;

        /// <summary>
        /// Collection of post-processing states keyed by render pipeline.
        /// Each pipeline can have its own set of post-process effect configurations.
        /// </summary>
        private CameraPostProcessStateCollection _postProcessStates = new();

        /// <summary>
        /// Optional custom material used for post-processing passes.
        /// When set, overrides the default post-process material.
        /// </summary>
        private XRMaterial? _postProcessMaterial;

        /// <summary>
        /// The render pipeline defining how this camera renders the scene.
        /// Contains the sequence of render passes (G-buffer, lighting, post-process, etc.).
        /// </summary>
        private RenderPipeline? _renderPipeline = null;

        /// <summary>
        /// Cached projection matrix with oblique near plane clipping applied.
        /// Recalculated when transform changes or oblique plane is set.
        /// </summary>
        private Matrix4x4 _obliqueProjectionMatrix = Matrix4x4.Identity;

        /// <summary>
        /// Optional oblique near clipping plane in world space.
        /// When set, modifies the projection to clip against this plane instead of the standard near plane.
        /// Used for portal/mirror rendering and water reflections.
        /// </summary>
        private Plane? _obliqueNearClippingPlane = null;

        /// <summary>
        /// Stack of projection jitter offsets in clip space.
        /// Multiple systems (TAA, DLSS, etc.) can push jitter without conflicting.
        /// The topmost value is applied to the projection matrix.
        /// </summary>
        private readonly Stack<Vector2> _projectionJitterStack = new();

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new camera with default settings.
        /// Sets up viewport event handlers for tracking viewport additions/removals.
        /// </summary>
        public XRCamera()
        {
            Viewports.PostAnythingAdded += OnViewportAdded;
            Viewports.PostAnythingRemoved += OnViewportRemoved;
        }

        /// <summary>
        /// Creates a new camera with the specified transform.
        /// The transform defines the camera's position and orientation in world space.
        /// </summary>
        /// <param name="transform">The transform to use for view matrix calculation.</param>
        public XRCamera(TransformBase transform) : this()
            => Transform = transform;

        /// <summary>
        /// Creates a new camera with specified transform and projection parameters.
        /// </summary>
        /// <param name="transform">The transform to use for view matrix calculation.</param>
        /// <param name="parameters">The projection parameters (FOV, aspect ratio, near/far planes).</param>
        public XRCamera(TransformBase transform, XRCameraParameters parameters) : this(transform)
            => Parameters = parameters;

        #endregion

        #region Viewports

        /// <summary>
        /// Collection of viewports that this camera renders into.
        /// A single camera can render to multiple viewports (e.g., split-screen, picture-in-picture).
        /// Not serialized as viewport associations are established at runtime.
        /// </summary>
        [YamlIgnore]
        public EventList<XRViewport> Viewports { get; set; } = [];

        /// <summary>
        /// Raised when a viewport is added to this camera's Viewports collection.
        /// Provides both the camera and the newly added viewport.
        /// </summary>
        public event Action<XRCamera, XRViewport>? ViewportAdded;

        /// <summary>
        /// Raised when a viewport is removed from this camera's Viewports collection.
        /// Provides both the camera and the removed viewport.
        /// </summary>
        public event Action<XRCamera, XRViewport>? ViewportRemoved;

        /// <summary>
        /// Internal handler for viewport removal events.
        /// Forwards the event to ViewportRemoved subscribers.
        /// </summary>
        /// <param name="item">The viewport that was removed.</param>
        private void OnViewportRemoved(XRViewport item)
            => ViewportRemoved?.Invoke(this, item);

        /// <summary>
        /// Internal handler for viewport addition events.
        /// Forwards the event to ViewportAdded subscribers.
        /// </summary>
        /// <param name="item">The viewport that was added.</param>
        private void OnViewportAdded(XRViewport item)
            => ViewportAdded?.Invoke(this, item);

        #endregion

        #region Transform / culling
        /// <summary>
        /// Transform driving this camera's view matrix.
        /// If unset, a default <see cref="Transform"/> is created lazily.
        /// </summary>
        public TransformBase Transform
        {
            get => _transform ?? SetFieldReturn(ref _transform, new Transform())!;
            set => SetField(ref _transform, value);
        }

        /// <summary>
        /// Maximum distance from this camera to consider a light for shadow-map collection.
        /// Used by shadow-culling in <see cref="Scene.Lights3DCollection"/>.
        /// If set to <see cref="float.PositiveInfinity"/>, the camera's <see cref="FarZ"/> is used.
        /// </summary>
        public float ShadowCollectMaxDistance
        {
            get => _shadowCollectMaxDistance;
            set => SetField(ref _shadowCollectMaxDistance, value);
        }
        /// <summary>
        /// Determines which layers this camera renders. Only renderables whose Layer
        /// is included in this mask will be collected during the visible pass.
        /// </summary>
        public LayerMask CullingMask
        {
            get => _cullingMask;
            set => SetField(ref _cullingMask, value);
        }

        #endregion

        #region Post-processing state

        /// <summary>
        /// Collection of post-processing states for different render pipelines.
        /// Each pipeline can have its own configuration of post-process effects.
        /// </summary>
        public CameraPostProcessStateCollection PostProcessStates
        {
            get => _postProcessStates;
            set => SetField(ref _postProcessStates, value ?? new CameraPostProcessStateCollection());
        }

        /// <summary>
        /// Gets the active post-process state for the current render pipeline.
        /// Creates a new state if one doesn't exist for the active pipeline.
        /// </summary>
        /// <returns>The post-process state for the active pipeline, or null if no pipeline is set.</returns>
        public PipelinePostProcessState? GetActivePostProcessState()
        {
            var pipeline = _renderPipeline ?? RenderPipeline;
            return pipeline is null ? null : _postProcessStates.GetOrCreateState(pipeline);
        }

        /// <summary>
        /// Gets the state for a specific post-process stage by its key identifier.
        /// </summary>
        /// <param name="stageKey">The unique key identifying the post-process stage.</param>
        /// <returns>The stage state, or null if not found.</returns>
        public PostProcessStageState? GetPostProcessStageState(string stageKey)
            => GetActivePostProcessState()?.GetStage(stageKey);

        /// <summary>
        /// Gets the state for a post-process stage by its settings type.
        /// </summary>
        /// <typeparam name="TSettings">The settings class type for the post-process stage.</typeparam>
        /// <returns>The stage state, or null if not found.</returns>
        public PostProcessStageState? GetPostProcessStageState<TSettings>() where TSettings : class
            => GetActivePostProcessState()?.GetStage<TSettings>();

        /// <summary>
        /// Finds a post-process stage that contains a parameter with the specified name.
        /// Useful for dynamic parameter lookup without knowing the exact stage type.
        /// </summary>
        /// <param name="parameterName">The name of the parameter to search for.</param>
        /// <returns>The stage state containing the parameter, or null if not found.</returns>
        public PostProcessStageState? FindPostProcessStageByParameter(string parameterName)
            => GetActivePostProcessState()?.FindStageByParameter(parameterName);

        #endregion

        #region Parameters / projection

        /// <summary>
        /// Projection parameters defining FOV/aspect/near/far/etc.
        /// If unset, defaults to a perspective camera.
        /// </summary>
        public XRCameraParameters Parameters
        {
            get => _parameters ?? SetFieldReturn(ref _parameters, GetDefaultCameraParameters())!;
            set => SetField(ref _parameters, value);
        }

        #endregion

        #region Property change handlers

        /// <summary>
        /// Called before a property value changes. Handles cleanup of old values.
        /// Specifically unsubscribes from the old transform's matrix change events.
        /// </summary>
        /// <typeparam name="T">The type of the property being changed.</typeparam>
        /// <param name="propName">The name of the property being changed.</param>
        /// <param name="field">The current (old) value of the property.</param>
        /// <param name="new">The new value being assigned to the property.</param>
        /// <returns>True if the change should proceed, false to cancel the change.</returns>
        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            switch (propName)
            {
                case nameof(Transform):
                    _transform?.RenderMatrixChanged -= RenderMatrixChanged;
                    break;
            }
            return change;
        }

        /// <summary>
        /// Called after a property value has changed. Handles setup for new values.
        /// Specifically subscribes to the new transform's matrix change events
        /// and recalculates the oblique projection matrix.
        /// </summary>
        /// <typeparam name="T">The type of the property that changed.</typeparam>
        /// <param name="propName">The name of the property that changed.</param>
        /// <param name="prev">The previous value of the property.</param>
        /// <param name="field">The new (current) value of the property.</param>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                    _transform?.RenderMatrixChanged += RenderMatrixChanged;
                    CalculateObliqueProjectionMatrix();
                    break;
            }
        }

        /// <summary>
        /// Callback invoked when the camera's transform render matrix changes.
        /// Triggers recalculation of the oblique projection matrix if one is set.
        /// </summary>
        /// <param name="base">The transform that changed.</param>
        /// <param name="renderMatrix">The new render matrix value.</param>
        private void RenderMatrixChanged(TransformBase @base, Matrix4x4 renderMatrix)
            => CalculateObliqueProjectionMatrix();

        #endregion

        #region Projection jitter

        /// <summary>
        /// True if any non-zero projection jitter is currently active.
        /// </summary>
        public bool HasProjectionJitter
            => _projectionJitterStack.Count > 0 && !IsZeroVector(_projectionJitterStack.Peek());

        /// <summary>
        /// The current projection jitter offset in clip space.
        /// </summary>
        public Vector2 ProjectionJitter
            => _projectionJitterStack.TryPeek(out var jitter) ? jitter : Vector2.Zero;

        /// <summary>
        /// Gets the projection matrix with any active jitter applied.
        /// </summary>
        public Matrix4x4 ProjectionMatrix
            => ApplyProjectionJitter(GetBaseProjectionMatrix());

        /// <summary>
        /// Gets the projection matrix without any jitter applied.
        /// Useful for motion vectors and other passes that need consistent projections.
        /// </summary>
        public Matrix4x4 ProjectionMatrixUnjittered
            => GetBaseProjectionMatrix();

        /// <summary>
        /// Pushes a jitter request onto the jitter stack.
        /// The returned <see cref="StateObject"/> will pop when disposed.
        /// </summary>
        public StateObject PushProjectionJitter(in ProjectionJitterRequest request)
        {
            Vector2 clipSpaceOffset = request.ToClipSpace();
            _projectionJitterStack.Push(clipSpaceOffset);
            return StateObject.New(PopProjectionJitter);
        }

        /// <summary>
        /// Removes the topmost jitter offset from the projection jitter stack.
        /// Called automatically when a StateObject from PushProjectionJitter is disposed.
        /// Safe to call when the stack is empty (no-op).
        /// </summary>
        public void PopProjectionJitter()
        {
            if (_projectionJitterStack.Count <= 0)
                return;

            _projectionJitterStack.Pop();
        }

        /// <summary>
        /// Removes all jitter offsets from the projection jitter stack.
        /// Use to reset jitter state completely, such as when changing render modes.
        /// </summary>
        public void ClearProjectionJitter()
            => _projectionJitterStack.Clear();

        /// <summary>
        /// Gets the base projection matrix without jitter applied.
        /// Returns the oblique projection matrix if an oblique clipping plane is set,
        /// otherwise returns the standard projection from Parameters.
        /// </summary>
        /// <returns>The base projection matrix.</returns>
        private Matrix4x4 GetBaseProjectionMatrix()
            => _obliqueNearClippingPlane != null
                ? _obliqueProjectionMatrix
                : Parameters.GetProjectionMatrix();

        /// <summary>
        /// Applies the current jitter offset to a projection matrix.
        /// Modifies the appropriate matrix elements based on projection type
        /// (orthographic vs perspective use different elements for translation).
        /// </summary>
        /// <param name="projection">The projection matrix to modify.</param>
        /// <returns>The jittered projection matrix.</returns>
        private Matrix4x4 ApplyProjectionJitter(Matrix4x4 projection)
        {
            if (_projectionJitterStack.Count <= 0)
                return projection;

            Vector2 jitter = _projectionJitterStack.Peek();
            if (IsZeroVector(jitter))
                return projection;

            if (Parameters is XROrthographicCameraParameters)
            {
                projection.M14 += jitter.X;
                projection.M24 += jitter.Y;
            }
            else
            {
                projection.M13 += jitter.X;
                projection.M23 += jitter.Y;
            }

            return projection;
        }

        /// <summary>
        /// Checks if a Vector2 is effectively zero (within floating-point epsilon).
        /// </summary>
        /// <param name="value">The vector to check.</param>
        /// <returns>True if both components are within epsilon of zero.</returns>
        private static bool IsZeroVector(Vector2 value)
            => MathF.Abs(value.X) <= float.Epsilon && MathF.Abs(value.Y) <= float.Epsilon;

        #endregion

        #region Oblique near clipping

        /// <summary>
        /// Recalculates the oblique projection matrix when the transform or clipping plane changes.
        /// Transforms the oblique plane from world space to view space and applies it to the projection.
        /// Only has effect when an oblique near clipping plane is set.
        /// </summary>
        private void CalculateObliqueProjectionMatrix()
        {
            var nearPlane = _obliqueNearClippingPlane;
            if (nearPlane is null)
                return;

            var transform = _transform;
            if (transform is null)
                return;

            Plane plane = nearPlane.Value;
            Matrix4x4 viewMatrix = transform.InverseRenderMatrix;
            Vector3 planePositionView = Vector3.Transform(XRMath.GetPlanePoint(plane), viewMatrix);
            Vector3 planeNormalView = Vector3.TransformNormal(plane.Normal.Normalized(), viewMatrix);
            _obliqueProjectionMatrix = CalculateObliqueProjectionMatrix(Parameters.GetProjectionMatrix(), new(planeNormalView.X, planeNormalView.Y, planeNormalView.Z, XRMath.GetPlaneDistance(planePositionView, planeNormalView)));
        }

        /// <summary>
        /// Gets the inverse of the current projection matrix.
        /// Used for unprojecting screen coordinates back to world space.
        /// Logs a warning and returns identity if the matrix cannot be inverted.
        /// </summary>
        public Matrix4x4 InverseProjectionMatrix
        {
            get
            {
                if (!Matrix4x4.Invert(ProjectionMatrix, out Matrix4x4 inverted))
                {
                    Debug.LogWarning($"Failed to invert {nameof(ProjectionMatrix)}. Parameters: {Parameters}");
                    inverted = Matrix4x4.Identity;
                }
                return inverted;
            }
        }

        /// <summary>
        /// Creates default perspective camera parameters.
        /// Returns a perspective camera with 90° FOV, no fixed aspect ratio, 0.1 near, 10000 far.
        /// </summary>
        /// <returns>Default perspective camera parameters.</returns>
        private static XRPerspectiveCameraParameters GetDefaultCameraParameters()
            => new(90.0f, null, 0.1f, 10000.0f);

        #endregion

        #region Near/far plane helpers

        /// <summary>
        /// Far clipping distance shortcut.
        /// </summary>
        public float FarZ
        {
            get => Parameters.FarZ;
            set => Parameters.FarZ = value;
        }

        /// <summary>
        /// Near clipping distance shortcut.
        /// </summary>
        public float NearZ
        {
            get => Parameters.NearZ;
            set => Parameters.NearZ = value;
        }

        /// <summary>
        /// Returns the camera's near plane as a plane object.
        /// The normal is the camera's forward vector.
        /// </summary>
        /// <returns></returns>
        public Plane GetNearPlane()
            => XRMath.CreatePlaneFromPointAndNormal(CenterPointNearPlane, GetWorldForward());

        /// <summary>
        /// Returns the camera's far plane as a plane object.
        /// The normal is the opposite of the camera's forward vector.
        /// </summary>
        /// <returns></returns>
        public Plane GetFarPlane()
            => XRMath.CreatePlaneFromPointAndNormal(CenterPointFarPlane, -GetWorldForward());

        /// <summary>
        /// The center point of the camera's near plane in world space.
        /// </summary>
        public Vector3 CenterPointNearPlane
            => Transform.WorldTranslation + GetWorldForward() * Parameters.NearZ;

        /// <summary>
        /// The center point of the camera's far plane in world space.
        /// </summary>
        public Vector3 CenterPointFarPlane
            => Transform.WorldTranslation + GetWorldForward() * Parameters.FarZ;

        /// <summary>
        /// The distance from the camera's position to the given point in world space.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public float DistanceFromWorldPosition(Vector3 point)
            => Vector3.Distance(Transform.WorldTranslation, point);

        /// <summary>
        /// Returns the distance from the camera's near plane to the given point.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public float DistanceFromNearPlane(Vector3 point)
        {
            Vector3 forward = GetWorldForward();
            Vector3 nearPoint = Transform.WorldTranslation + forward * Parameters.NearZ;
            return GeoUtil.DistancePlanePoint(forward, XRMath.GetPlaneDistance(nearPoint, forward), point);
        }

        /// <summary>
        /// Returns the distance from the camera's far plane to the given point.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public float DistanceFromFarPlane(Vector3 point)
        {
            Vector3 forward = GetWorldForward();
            Vector3 farPoint = Transform.WorldTranslation + forward * Parameters.FarZ;
            return GeoUtil.DistancePlanePoint(-forward, XRMath.GetPlaneDistance(farPoint, -forward), point);
        }

        /// <summary>
        /// Returns the point on the near plane closest to the given point.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public Vector3 ClosestPointOnNearPlane(Vector3 point)
        {
            Vector3 forward = GetWorldForward();
            Vector3 nearPoint = Transform.WorldTranslation + forward * Parameters.NearZ;
            return GeoUtil.ClosestPlanePointToPoint(forward, XRMath.GetPlaneDistance(nearPoint, forward), point);
        }

        /// <summary>
        /// Returns the point on the far plane closest to the given point.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public Vector3 ClosestPointOnFarPlane(Vector3 point)
        {
            Vector3 forward = GetWorldForward();
            Vector3 farPoint = Transform.WorldTranslation + forward * Parameters.FarZ;
            return GeoUtil.ClosestPlanePointToPoint(-forward, XRMath.GetPlaneDistance(farPoint, -forward), point);
        }

        /// <summary>
        /// The camera's forward direction in world space (cached from transform).
        /// </summary>
        public Vector3 WorldForward => Transform.WorldForward;

        /// <summary>
        /// The camera's forward direction used for rendering (may differ from WorldForward during interpolation).
        /// </summary>
        public Vector3 RenderForward => Transform.RenderForward;

        /// <summary>
        /// The camera's forward direction in local space (typically negative Z).
        /// </summary>
        public Vector3 LocalForward => Transform.LocalForward;

        /// <summary>
        /// Gets the camera's current forward direction in world space.
        /// Recalculates from the transform if needed.
        /// </summary>
        /// <returns>The world-space forward direction vector.</returns>
        public Vector3 GetWorldForward()
            => Transform.GetWorldForward();

        /// <summary>
        /// The frustum of this camera in world space.
        /// </summary>
        /// <returns></returns>
        public Frustum WorldFrustum()
            => UntransformedFrustum().TransformedBy(Transform.RenderMatrix);

        /// <summary>
        /// The projection frustum of this camera with no transformation applied.
        /// </summary>
        /// <returns></returns>
        public Frustum UntransformedFrustum()
            => Parameters.GetUntransformedFrustum();

        /// <summary>
        /// Returns a scale value that maintains the size of an object relative to the camera's near plane.
        /// </summary>
        /// <param name="worldPoint">The point to evaluate scale at</param>
        /// <param name="refDistance">The distance from the camera to be at a scale of 1.0</param>
        public float DistanceScaleOrthographic(Vector3 worldPoint, float refDistance)
            => DistanceFromNearPlane(worldPoint) / refDistance;
        /// <summary>
        /// Returns a scale value that maintains the size of an object relative to the camera's position.
        /// </summary>
        /// <param name="worldPoint"></param>
        /// <param name="refDistance"></param>
        /// <returns></returns>
        public float DistanceScalePerspective(Vector3 worldPoint, float refDistance)
            => DistanceFromWorldPosition(worldPoint) / refDistance;

        #endregion

        #region Coordinate conversion

        /// <summary>
        /// Returns a normalized X, Y coordinate relative to the camera's origin (center for perspective, bottom-left for orthographic) 
        /// with Z being the normalized depth (0.0f - 1.0f) from NearDepth (0.0f) to FarDepth (1.0f).
        /// </summary>
        public void WorldToNormalizedViewportCoordinate(Vector3 worldPoint, out Vector2 screenPoint, out float depth)
        {
            Vector3 xyd = WorldToNormalizedViewportCoordinate(worldPoint);
            screenPoint = new Vector2(xyd.X, xyd.Y);
            depth = xyd.Z;
        }
        /// <summary>
        /// Returns a normalized X, Y coordinate relative to the camera's origin (center for perspective, bottom-left for orthographic) 
        /// with Z being the normalized depth (0.0f - 1.0f) from NearDepth (0.0f) to FarDepth (1.0f).
        /// </summary>
        public void WorldToNormalizedViewportCoordinate(Vector3 worldPoint, out float x, out float y, out float depth)
        {
            Vector3 xyd = WorldToNormalizedViewportCoordinate(worldPoint);
            x = xyd.X;
            y = xyd.Y;
            depth = xyd.Z;
        }
        /// <summary>
        /// Returns a normalized X, Y coordinate relative to the camera's origin (center for perspective, bottom-left for orthographic) 
        /// with Z being the normalized depth (0.0f - 1.0f) from NearDepth (0.0f) to FarDepth (1.0f).
        /// </summary>
        public Vector3 WorldToNormalizedViewportCoordinate(Vector3 worldPoint)
        {
            // Project to normalized [0,1] UV with depth in [0,1].
            Vector3 clip01 = (Vector3.Transform(Vector3.Transform(worldPoint, Transform.InverseWorldMatrix), ProjectionMatrix) + Vector3.One) * new Vector3(0.5f);

            // Apply forward lens distortion so predicted screen UV matches post-process sampling.
            var lens = GetActiveLensParams();
            if (lens.Enabled)
                clip01 = new Vector3(ApplyLensDistortionInverse(clip01.XY(), lens), clip01.Z);

            return clip01;
        }

        /// <summary>
        /// Takes an X, Y coordinate relative to the camera's origin along with the normalized depth (0.0f - 1.0f) from NearDepth (0.0f) to FarDepth (1.0f), and returns a position in the world.
        /// </summary>
        public Vector3 NormalizedViewportToWorldCoordinate(Vector2 normalizedViewportPoint, float depth)
            => NormalizedViewportToWorldCoordinate(normalizedViewportPoint.X, normalizedViewportPoint.Y, depth);
        /// <summary>
        /// Takes an X, Y coordinate relative to the camera's Origin along with the normalized depth (0.0f - 1.0f) from NearDepth (0.0f) to FarDepth (1.0f), and returns a position in the world.
        /// </summary>
        public Vector3 NormalizedViewportToWorldCoordinate(float normalizedX, float normalizedY, float depth)
            => NormalizedViewportToWorldCoordinate(new Vector3(normalizedX, normalizedY, depth));
        /// <summary>
        /// Takes an X, Y coordinate relative to the camera's Origin, with Z being the normalized depth (0.0f - 1.0f) from NearDepth (0.0f) to FarDepth (1.0f), and returns a position in the world.
        /// </summary>
        public Vector3 NormalizedViewportToWorldCoordinate(Vector3 normalizedPointDepth, ERange xyRange = ERange.ZeroToOne, ERange depthRange = ERange.ZeroToOne)
        {
            Vector3 clipSpacePos = normalizedPointDepth;

            // Undo lens distortion on the incoming mouse/screen UV so we unproject the same rays used for rendering.
            if (xyRange == ERange.ZeroToOne)
            {
                var lens = GetActiveLensParams();
                if (lens.Enabled)
                {
                    Vector2 undistorted = ApplyLensDistortionForward(clipSpacePos.XY(), lens);
                    clipSpacePos.X = undistorted.X;
                    clipSpacePos.Y = undistorted.Y;
                }
            }

            if (xyRange == ERange.ZeroToOne)
            {
                clipSpacePos.X = clipSpacePos.X * 2.0f - 1.0f;
                clipSpacePos.Y = clipSpacePos.Y * 2.0f - 1.0f;
            }

            if (depthRange == ERange.ZeroToOne)
                clipSpacePos.Z = normalizedPointDepth.Z * 2.0f - 1.0f;

            Vector4 viewSpacePos = Vector4.Transform(clipSpacePos, InverseProjectionMatrix * Transform.WorldMatrix);
            if (viewSpacePos.W != 0.0f)
                viewSpacePos /= viewSpacePos.W;
            return viewSpacePos.XYZ();
        }

        public enum ERange
        {
            NegativeOneToOne,
            ZeroToOne
        }

        #endregion

        #region Lens Distortion helpers

        /// <summary>
        /// Immutable struct containing lens distortion parameters for coordinate conversion.
        /// Encapsulates all settings needed to apply or invert lens distortion effects.
        /// </summary>
        /// <param name="mode">The type of lens distortion (None, Radial, RadialAutoFromFOV, Panini).</param>
        /// <param name="intensity">The intensity of radial distortion (positive = barrel, negative = pincushion).</param>
        /// <param name="paniniDistance">The Panini projection distance parameter (0 = rectilinear, 1 = cylindrical).</param>
        /// <param name="paniniCrop">Scale factor to fit the Panini projection within the frame.</param>
        /// <param name="paniniViewExtents">The view extents for Panini projection based on FOV and aspect.</param>
        private readonly struct LensParams(ELensDistortionMode mode, float intensity, float paniniDistance, float paniniCrop, Vector2 paniniViewExtents)
        {
            /// <summary>The type of lens distortion being applied.</summary>
            public ELensDistortionMode Mode { get; } = mode;

            /// <summary>Radial distortion intensity (positive = barrel, negative = pincushion).</summary>
            public float Intensity { get; } = intensity;

            /// <summary>Panini projection distance (0-1, where 0 is rectilinear and 1 is cylindrical).</summary>
            public float PaniniDistance { get; } = paniniDistance;

            /// <summary>Scale factor applied to fit Panini projection within the frame.</summary>
            public float PaniniCrop { get; } = paniniCrop;

            /// <summary>The view extents used for Panini projection calculations.</summary>
            public Vector2 PaniniViewExtents { get; } = paniniViewExtents;

            /// <summary>
            /// Returns true if lens distortion is enabled and will have a visible effect.
            /// For Panini, requires distance > 0. For radial, requires non-zero intensity.
            /// </summary>
            public bool Enabled
                => Mode != ELensDistortionMode.None && (Mode == ELensDistortionMode.Panini ? PaniniDistance > 0.0f : MathF.Abs(Intensity) > 0.0f);
        }

        /// <summary>
        /// Retrieves the current lens distortion parameters from the post-process settings.
        /// Returns disabled parameters if no lens distortion settings are found or camera is not perspective.
        /// </summary>
        /// <returns>The active lens distortion parameters.</returns>
        private LensParams GetActiveLensParams()
        {
            var stage = GetPostProcessStageState<LensDistortionSettings>();
            if (stage is null)
                return new LensParams(ELensDistortionMode.None, 0.0f, 0.0f, 1.0f, Vector2.One);

            // Only use the backing instance to avoid concurrent dictionary access on stage values.
            if (!stage.TryGetBacking(out LensDistortionSettings? backing) || backing is null)
                return new LensParams(ELensDistortionMode.None, 0.0f, 0.0f, 1.0f, Vector2.One);

            // Lens needs camera FOV/aspect; fall back to disabled if not perspective.
            if (Parameters is not XRPerspectiveCameraParameters persp)
                return new LensParams(ELensDistortionMode.None, 0.0f, 0.0f, 1.0f, Vector2.One);

            float fovDeg = persp.VerticalFieldOfView;
            float aspect = persp.AspectRatio;

            ELensDistortionMode mode = backing.Mode;
            float intensity = backing.Mode switch
            {
                ELensDistortionMode.Radial => backing.Intensity,
                ELensDistortionMode.RadialAutoFromFOV => LensDistortionSettings.ComputeCorrectionFromFovDegrees(fovDeg),
                _ => 0.0f
            };

            float paniniDistance = backing.Mode == ELensDistortionMode.Panini ? backing.PaniniDistance : 0.0f;
            float paniniCrop = 1.0f;
            Vector2 paniniViewExtents = Vector2.One;
            if (backing.Mode == ELensDistortionMode.Panini && paniniDistance > 0.0f)
            {
                float fovRad = fovDeg * MathF.PI / 180.0f;
                paniniViewExtents = LensDistortionSettings.CalcViewExtents(fovRad, aspect);
                var cropExtents = LensDistortionSettings.CalcCropExtents(fovRad, paniniDistance, aspect);
                float scaleX = cropExtents.X / paniniViewExtents.X;
                float scaleY = cropExtents.Y / paniniViewExtents.Y;
                float scaleF = MathF.Min(scaleX, scaleY);
                paniniCrop = float.Lerp(1.0f, Math.Clamp(scaleF, 0.0f, 1.0f), backing.PaniniCropToFit);
            }

            return new LensParams(mode, intensity, paniniDistance, paniniCrop, paniniViewExtents);
        }

        /// <summary>
        /// Applies forward lens distortion to UV coordinates.
        /// Transforms undistorted UVs to distorted screen-space UVs.
        /// Used when projecting world points to screen coordinates.
        /// </summary>
        /// <param name="uv01">Undistorted UV coordinates in [0,1] range.</param>
        /// <param name="lens">The lens distortion parameters to apply.</param>
        /// <returns>Distorted UV coordinates.</returns>
        private static Vector2 ApplyLensDistortionForward(Vector2 uv01, LensParams lens)
        {
            return lens.Mode switch
            {
                ELensDistortionMode.Radial or ELensDistortionMode.RadialAutoFromFOV => ApplyRadialDistortion(uv01, lens.Intensity),
                ELensDistortionMode.Panini => ApplyPaniniForward(uv01, lens),
                _ => uv01
            };
        }

        /// <summary>
        /// Inverts lens distortion from UV coordinates.
        /// Transforms distorted screen-space UVs back to undistorted UVs.
        /// Used when unprojecting screen coordinates to world rays.
        /// </summary>
        /// <param name="uv01">Distorted UV coordinates in [0,1] range.</param>
        /// <param name="lens">The lens distortion parameters to invert.</param>
        /// <returns>Undistorted UV coordinates.</returns>
        private static Vector2 ApplyLensDistortionInverse(Vector2 uv01, LensParams lens)
        {
            return lens.Mode switch
            {
                ELensDistortionMode.Radial or ELensDistortionMode.RadialAutoFromFOV => InvertRadialDistortion(uv01, lens.Intensity),
                ELensDistortionMode.Panini => InvertPanini(uv01, lens),
                _ => uv01
            };
        }

        /// <summary>
        /// Applies radial (barrel/pincushion) distortion to UV coordinates.
        /// Positive intensity creates barrel distortion, negative creates pincushion.
        /// Uses the standard polynomial model: r' = r(1 + k*r²).
        /// </summary>
        /// <param name="uv01">Input UV coordinates in [0,1] range.</param>
        /// <param name="intensity">Distortion intensity coefficient.</param>
        /// <returns>Distorted UV coordinates.</returns>
        private static Vector2 ApplyRadialDistortion(Vector2 uv01, float intensity)
        {
            if (MathF.Abs(intensity) <= float.Epsilon)
                return uv01;

            Vector2 centered = uv01 - new Vector2(0.5f, 0.5f);
            float r = centered.Length();
            if (r <= float.Epsilon)
                return uv01;

            float theta = MathF.Atan2(centered.Y, centered.X);
            float rd = r * (1.0f + intensity * r * r);
            Vector2 distorted = new(MathF.Cos(theta) * rd, MathF.Sin(theta) * rd);
            return distorted + new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// Inverts radial distortion using Newton-Raphson iteration.
        /// Finds the undistorted radius r such that r(1 + k*r²) = r_distorted.
        /// </summary>
        /// <param name="uv01">Distorted UV coordinates in [0,1] range.</param>
        /// <param name="intensity">Distortion intensity coefficient.</param>
        /// <returns>Undistorted UV coordinates.</returns>
        private static Vector2 InvertRadialDistortion(Vector2 uv01, float intensity)
        {
            if (MathF.Abs(intensity) <= float.Epsilon)
                return uv01;

            Vector2 centered = uv01 - new Vector2(0.5f, 0.5f);
            float rd = centered.Length();
            if (rd <= float.Epsilon)
                return new Vector2(0.5f, 0.5f);

            Vector2 dir = centered / rd;
            float r = rd;
            for (int i = 0; i < 6; i++)
            {
                float f = r + intensity * r * r * r - rd; // f(r) = r + k r^3 - rd
                float df = 1.0f + 3.0f * intensity * r * r;
                if (MathF.Abs(df) <= 1e-6f)
                    break;
                r -= f / df;
            }

            r = MathF.Max(r, 0.0f);
            return dir * r + new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// Applies Panini projection distortion to UV coordinates.
        /// Panini projection reduces peripheral stretching in wide-FOV images.
        /// </summary>
        /// <param name="uv01">Input UV coordinates in [0,1] range.</param>
        /// <param name="lens">Lens parameters containing Panini settings.</param>
        /// <returns>Distorted UV coordinates with Panini projection applied.</returns>
        private static Vector2 ApplyPaniniForward(Vector2 uv01, LensParams lens)
        {
            if (lens.PaniniDistance <= 0.0f)
                return uv01;

            Vector2 viewPos = (uv01 * 2.0f - Vector2.One) * lens.PaniniViewExtents * lens.PaniniCrop;
            Vector2 projPos = ApplyPaniniProjection(viewPos, lens.PaniniDistance);
            Vector2 projNdc = projPos / lens.PaniniViewExtents;
            return projNdc * 0.5f + new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// Inverts Panini projection using iterative numerical solving.
        /// Finds the undistorted view-space position whose Panini-projected UV matches the input.
        /// Uses Newton-Raphson with numerical Jacobian for convergence.
        /// </summary>
        /// <param name="uv01">Distorted UV coordinates in [0,1] range.</param>
        /// <param name="lens">Lens parameters containing Panini settings.</param>
        /// <returns>Undistorted UV coordinates.</returns>
        private static Vector2 InvertPanini(Vector2 uv01, LensParams lens)
        {
            if (lens.PaniniDistance <= 0.0f)
                return uv01;

            // Iteratively solve for the undistorted view-space position whose distorted UV matches the input.
            // Start from a view guess without pre-applying crop to avoid over-scaling the estimate.
            Vector2 view = (uv01 * 2.0f - Vector2.One) * lens.PaniniViewExtents;

            const int iterations = 10;
            const float delta = 1e-3f;
            for (int i = 0; i < iterations; i++)
            {
                Vector2 uvGuess = ApplyPaniniFromView(view, lens);
                Vector2 err = uvGuess - uv01;
                if (err.LengthSquared() < 1e-10f)
                    break;

                // Numerical Jacobian
                Vector2 uvDx = ApplyPaniniFromView(view + new Vector2(delta, 0.0f), lens);
                Vector2 uvDy = ApplyPaniniFromView(view + new Vector2(0.0f, delta), lens);

                Vector2 jx = (uvDx - uvGuess) / delta;
                Vector2 jy = (uvDy - uvGuess) / delta;

                float det = jx.X * jy.Y - jx.Y * jy.X;
                if (MathF.Abs(det) <= 1e-10f)
                    break;

                // Inverse Jacobian * error
                Vector2 step;
                step.X = ( jy.Y * err.X - jx.Y * err.Y) / det;
                step.Y = (-jy.X * err.X + jx.X * err.Y) / det;

                view -= step;
            }

            Vector2 uvUndistorted = (view / (lens.PaniniViewExtents * lens.PaniniCrop)) * 0.5f + new Vector2(0.5f, 0.5f);
            return uvUndistorted;
        }

        /// <summary>
        /// Helper method that applies Panini projection from view-space coordinates.
        /// Converts view position through Panini projection and back to UV space.
        /// </summary>
        /// <param name="viewPos">Position in view space.</param>
        /// <param name="lens">Lens parameters containing Panini settings.</param>
        /// <returns>UV coordinates after Panini projection.</returns>
        private static Vector2 ApplyPaniniFromView(Vector2 viewPos, LensParams lens)
        {
            Vector2 projPos = ApplyPaniniProjection(viewPos, lens.PaniniDistance);
            Vector2 projNdc = projPos / lens.PaniniViewExtents;
            return projNdc * 0.5f + new Vector2(0.5f, 0.5f);
        }

        /// <summary>
        /// Applies the core Panini projection transformation.
        /// Projects a view-space position onto a cylindrical surface and then onto the image plane.
        /// The distance parameter d controls interpolation between rectilinear (d=0) and cylindrical (d=1).
        /// </summary>
        /// <param name="viewPos">Position in view space to project.</param>
        /// <param name="d">Panini distance parameter (0-1).</param>
        /// <returns>Projected position on the image plane.</returns>
        private static Vector2 ApplyPaniniProjection(Vector2 viewPos, float d)
        {
            float viewDist = 1.0f + d;
            float viewHypSq = viewPos.X * viewPos.X + viewDist * viewDist;

            float isectD = viewPos.X * d;
            float isectDiscrim = viewHypSq - isectD * isectD;

            float cylDistMinusD = (-isectD * viewPos.X + viewDist * MathF.Sqrt(MathF.Max(isectDiscrim, 0.0f))) / viewHypSq;
            float cylDist = cylDistMinusD + d;

            Vector2 cylPos = viewPos * (cylDist / viewDist);
            return cylPos / (cylDist - d);
        }
        #endregion

        #region Rays / queries

        /// <summary>
        /// Creates a line segment from near plane to far plane through the specified screen point.
        /// The segment endpoints are world-space positions on the camera's clipping planes.
        /// </summary>
        /// <param name="normalizedScreenPoint">Normalized screen coordinates in [0,1] range.</param>
        /// <returns>A segment from near plane to far plane through the screen point.</returns>
        public Segment GetWorldSegment(Vector2 normalizedScreenPoint)
        {
            Vector3 start = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 0.0f);
            Vector3 end = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 1.0f);
            return new Segment(start, end);
        }

        /// <summary>
        /// Creates a ray from the camera's near plane through the specified screen point.
        /// The ray originates at the near plane position and extends in the view direction.
        /// </summary>
        /// <param name="normalizedScreenPoint">Normalized screen coordinates in [0,1] range.</param>
        /// <returns>A ray from near plane through the screen point into the scene.</returns>
        public Ray GetWorldRay(Vector2 normalizedScreenPoint)
        {
            Vector3 start = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 0.0f);
            Vector3 end = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 1.0f);
            return new Ray(start, end - start);
        }

        /// <summary>
        /// Gets the world-space position at a specific normalized depth through the screen point.
        /// </summary>
        /// <param name="normalizedScreenPoint">Normalized screen coordinates in [0,1] range.</param>
        /// <param name="depth">Normalized depth (0 = near plane, 1 = far plane).</param>
        /// <returns>World-space position at the specified depth.</returns>
        public Vector3 GetPointAtDepth(Vector2 normalizedScreenPoint, float depth)
            => NormalizedViewportToWorldCoordinate(normalizedScreenPoint, depth);

        /// <summary>
        /// Gets the world-space position at a specific distance along the view ray.
        /// Unlike GetPointAtDepth, this uses linear distance from the camera rather than depth buffer values.
        /// </summary>
        /// <param name="normalizedScreenPoint">Normalized screen coordinates in [0,1] range.</param>
        /// <param name="distance">Linear distance from the camera in world units.</param>
        /// <returns>World-space position at the specified distance.</returns>
        public Vector3 GetPointAtDistance(Vector2 normalizedScreenPoint, float distance)
            => GetWorldSegment(normalizedScreenPoint).PointAtLineDistance(distance);

        /// <summary>
        /// Sets RenderDistance by calculating the distance between the provided camera and point.
        /// If planar is true, distance is calculated to the camera's near plane.
        /// If false, the distance is calculated to the camera's world position.
        /// </summary>
        /// <param name="camera"></param>
        /// <param name="point"></param>
        /// <param name="planar"></param>
        public float DistanceFrom(Vector3 point, bool planar)
            => planar
                ? DistanceFromNearPlane(point)
                : DistanceFromWorldPosition(point);

        #endregion

        #region Rendering uniforms / pipeline

        /// <summary>
        /// Sets ambient occlusion shader uniforms from this camera's post-process settings.
        /// Falls back to default values if no AO settings are configured.
        /// </summary>
        /// <param name="program">The shader program to set uniforms on.</param>
        /// <param name="overrideType">Optional override for the AO algorithm type.</param>
        public virtual void SetAmbientOcclusionUniforms(XRRenderProgram program, AmbientOcclusionSettings.EType? overrideType = null)
        {
            var stage = GetPostProcessStageState<AmbientOcclusionSettings>();
            if (stage?.TryGetBacking(out AmbientOcclusionSettings? settings) == true)
            {
                settings?.SetUniforms(program, overrideType);
                return;
            }

            program.Uniform("Radius", 0.9f);
            program.Uniform("Power", 1.4f);
        }

        // Post-process uniform setup moved to pipeline helpers.

        /// <summary>
        /// Optional custom material for post-processing passes.
        /// When set, used instead of the default post-process material.
        /// </summary>
        public XRMaterial? PostProcessMaterial
        {
            get => _postProcessMaterial;
            set => SetField(ref _postProcessMaterial, value);
        }

        /// <summary>
        /// This is the rendering setup this viewport will use to render the scene the camera sees.
        /// A render pipeline is a collection of render passes that will be executed in order to render the scene and post-process the result, etc.
        /// </summary>
        public RenderPipeline RenderPipeline
        {
            get => _renderPipeline ?? SetFieldReturn(ref _renderPipeline, Engine.Rendering.NewRenderPipeline())!;
            set => SetField(ref _renderPipeline, value);
        }

        /// <summary>
        /// Sets all camera-related shader uniforms for rendering.
        /// Includes view/projection matrices, camera position, and direction vectors.
        /// Handles both mono and stereo rendering modes with appropriate uniform names.
        /// </summary>
        /// <param name="program">The shader program to set uniforms on.</param>
        /// <param name="stereoLeftEye">If true, sets left-eye uniforms; if false, sets right-eye uniforms (stereo mode only).</param>
        public virtual void SetUniforms(XRRenderProgram program, bool stereoLeftEye = true)
        {
            var tfm = Transform;
            Matrix4x4 renderMtx = tfm.RenderMatrix;
            Matrix4x4 projMtx = ProjectionMatrix;

            bool stereoPass = Engine.Rendering.State.IsStereoPass;
            if (stereoPass)
            {
                if (stereoLeftEye)
                {
                    program.Uniform(EEngineUniform.LeftEyeInverseViewMatrix.ToString(), renderMtx);
                    program.Uniform(EEngineUniform.LeftEyeProjMatrix.ToString(), projMtx);
                }
                else
                {
                    program.Uniform(EEngineUniform.RightEyeInverseViewMatrix.ToString(), renderMtx);
                    program.Uniform(EEngineUniform.RightEyeProjMatrix.ToString(), projMtx);
                }
            }
            else
            {
                program.Uniform(EEngineUniform.InverseViewMatrix.ToString(), renderMtx);
                program.Uniform(EEngineUniform.ProjMatrix.ToString(), projMtx);
            }

            program.Uniform(EEngineUniform.CameraPosition.ToString(), tfm.RenderTranslation);
            program.Uniform(EEngineUniform.CameraForward.ToString(), tfm.RenderForward);
            program.Uniform(EEngineUniform.CameraUp.ToString(), tfm.RenderUp);
            program.Uniform(EEngineUniform.CameraRight.ToString(), tfm.RenderRight);

            Parameters.SetUniforms(program);
        }

        /// <summary>
        /// Gets the orthographic camera's bounds as a 2D rectangle if using orthographic projection.
        /// Returns null if the camera is not using orthographic projection.
        /// The bounds are translated by the camera's world position.
        /// </summary>
        /// <returns>The camera's view bounds, or null if not orthographic.</returns>
        public BoundingRectangleF? GetOrthoCameraBounds()
        {
            if (Parameters is not XROrthographicCameraParameters op)
                return null;
            
            var b = op.GetBounds();
            var t = Transform.WorldTranslation;
            b = b.Translated(t.X, -t.Y);
            //TODO: scale the rect
            return b;
        }

        #endregion

        #region Oblique projection helpers

        /// <summary>
        /// Calculates an oblique near plane projection matrix.
        /// Given an original projection matrix and a clipping plane in view space,
        /// this method adjusts the projection matrix to clip against the given plane.
        /// </summary>
        /// <param name="projection">The original projection matrix.</param>
        /// <param name="clipPlane">The clipping plane in the form (a, b, c, d).</param>
        /// <returns>The modified projection matrix with an oblique near plane.</returns>
        public static Matrix4x4 CalculateObliqueProjectionMatrix(Matrix4x4 projection, Vector4 clipPlane)
        {
            Vector4 Q = new(
                (float.Sign(clipPlane.X) + projection.M31) / projection.M11,
                (float.Sign(clipPlane.Y) + projection.M32) / projection.M22,
                -1.0f,
                (1.0f + projection.M33) / projection.M43);

            float dot = clipPlane.Dot(Q);
            Vector4 c = clipPlane * (2.0f / dot);
            projection.M13 = c.X;
            projection.M23 = c.Y;
            projection.M33 = c.Z + 1.0f;
            projection.M43 = c.W;

            return projection;
        }

        /// <summary>
        /// Sets an oblique clipping plane from a world-space position and normal.
        /// </summary>
        public void SetObliqueClippingPlane(Vector3 planePosWorld, Vector3 planeNormalWorld)
            => _obliqueNearClippingPlane = XRMath.CreatePlaneFromPointAndNormal(planePosWorld, planeNormalWorld);

        /// <summary>
        /// Sets an oblique clipping plane from a normal and distance.
        /// </summary>
        public void SetObliqueClippingPlane(Vector3 planeNormalWorld, float planeDistance)
            => _obliqueNearClippingPlane = new Plane(planeNormalWorld, planeDistance);

        /// <summary>
        /// Sets an oblique clipping plane directly.
        /// </summary>
        public void SetObliqueClippingPlane(Plane plane)
            => _obliqueNearClippingPlane = plane;

        /// <summary>
        /// Clears any oblique clipping plane.
        /// </summary>
        public void ClearObliqueClippingPlane()
            => _obliqueNearClippingPlane = null;

        #endregion

        #region Depth conversion

        /// <summary>
        /// Converts nonlinear normalized depth between 0.0f and 1.0f
        /// to a linear distance value between nearZ and farZ.
        /// </summary>
        public float DepthToDistance(float depth)
        {
            float nearZ = NearZ;
            float farZ = FarZ;
            float depthSample = 2.0f * depth - 1.0f;
            float zLinear = 2.0f * nearZ * farZ / (farZ + nearZ - depthSample * (farZ - nearZ));
            return zLinear;
        }
        /// <summary>
        /// Converts a linear distance value between nearZ and farZ
        /// to nonlinear normalized depth between 0.0f and 1.0f.
        /// </summary>
        public float DistanceToDepth(float z)
        {
            float nearZ = NearZ;
            float farZ = FarZ;
            float nonLinearDepth = (farZ + nearZ - 2.0f * nearZ * farZ / z.ClampMin(0.001f)) / (farZ - nearZ);
            nonLinearDepth = (nonLinearDepth + 1.0f) / 2.0f;
            return nonLinearDepth;
        }

        #endregion
    }
}
