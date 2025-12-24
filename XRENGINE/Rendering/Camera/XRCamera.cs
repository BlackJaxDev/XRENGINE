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
    public enum ProjectionJitterSpace
    {
        ClipSpace,
        TexelSpace
    }

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

        public static ProjectionJitterRequest ClipSpace(Vector2 offset)
            => new(offset, ProjectionJitterSpace.ClipSpace, Vector2.One);

        public static ProjectionJitterRequest TexelSpace(Vector2 offset, Vector2 referenceResolution)
            => new(offset, ProjectionJitterSpace.TexelSpace, referenceResolution);

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

        public bool IsZero
            => MathF.Abs(Offset.X) <= float.Epsilon && MathF.Abs(Offset.Y) <= float.Epsilon;
    }

    public class XRCameraBase : XRBase
    {
        
    }
    public class VRCamera : XRCameraBase
    {

    }
    /// <summary>
    /// This class represents a camera in 3D space.
    /// It calculates the model-view-projection matrix driven by a transform and projection parameters.
    /// </summary>
    public class XRCamera : XRBase
    {
        [YamlIgnore]
        public EventList<XRViewport> Viewports { get; set; } = [];

        public event Action<XRCamera, XRViewport>? ViewportAdded;
        public event Action<XRCamera, XRViewport>? ViewportRemoved;

        public XRCamera()
        {
            Viewports.PostAnythingAdded += OnViewportAdded;
            Viewports.PostAnythingRemoved += OnViewportRemoved;
        }
        public XRCamera(TransformBase transform) : this()
            => Transform = transform;
        public XRCamera(TransformBase transform, XRCameraParameters parameters) : this(transform)
            => Parameters = parameters;

        private void OnViewportRemoved(XRViewport item)
            => ViewportRemoved?.Invoke(this, item);
        private void OnViewportAdded(XRViewport item)
            => ViewportAdded?.Invoke(this, item);

        private TransformBase? _transform;
        public TransformBase Transform
        {
            get => _transform ?? SetFieldReturn(ref _transform, new Transform())!;
            set => SetField(ref _transform, value);
        }

        private LayerMask _cullingMask = LayerMask.Everything;

        private float _shadowCollectMaxDistance = float.PositiveInfinity;

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

        public CameraPostProcessStateCollection PostProcessStates
        {
            get => _postProcessStates;
            set => SetField(ref _postProcessStates, value ?? new CameraPostProcessStateCollection());
        }

        public PipelinePostProcessState? GetActivePostProcessState()
        {
            var pipeline = _renderPipeline ?? RenderPipeline;
            return pipeline is null ? null : _postProcessStates.GetOrCreateState(pipeline);
        }

        public PostProcessStageState? GetPostProcessStageState(string stageKey)
            => GetActivePostProcessState()?.GetStage(stageKey);

        public PostProcessStageState? GetPostProcessStageState<TSettings>() where TSettings : class
            => GetActivePostProcessState()?.GetStage<TSettings>();

        public PostProcessStageState? FindPostProcessStageByParameter(string parameterName)
            => GetActivePostProcessState()?.FindStageByParameter(parameterName);

        //private Matrix4x4 _modelViewProjectionMatrix = Matrix4x4.Identity;
        //public Matrix4x4 WorldViewProjectionMatrix
        //{
        //    get
        //    {
        //        VerifyMVP();
        //        return _modelViewProjectionMatrix;
        //    }
        //    set
        //    {
        //        SetField(ref _modelViewProjectionMatrix, value);
        //        _inverseModelViewProjectionMatrix = null;
        //    }
        //}

        //private Matrix4x4? _inverseModelViewProjectionMatrix = Matrix4x4.Identity;
        //public Matrix4x4 InverseWorldViewProjectionMatrix
        //{
        //    get
        //    {
        //        if (_inverseModelViewProjectionMatrix != null)
        //            return _inverseModelViewProjectionMatrix.Value;

        //        if (!Matrix4x4.Invert(WorldViewProjectionMatrix, out Matrix4x4 inverted))
        //        {
        //            Debug.LogWarning($"Failed to invert {nameof(WorldViewProjectionMatrix)}");
        //            inverted = Matrix4x4.Identity;
        //        }
        //        _inverseModelViewProjectionMatrix = inverted;
        //        return inverted;
        //    }
        //    set
        //    {
        //        _inverseModelViewProjectionMatrix = value;
        //        if (!Matrix4x4.Invert(value, out Matrix4x4 inverted))
        //        {
        //            Debug.LogWarning($"Failed to invert value set to {nameof(InverseWorldViewProjectionMatrix)}");
        //            inverted = Matrix4x4.Identity;
        //        }
        //        WorldViewProjectionMatrix = inverted;
        //    }
        //}

        private Matrix4x4 _obliqueProjectionMatrix = Matrix4x4.Identity;
        private XRCameraParameters? _parameters;
        public XRCameraParameters Parameters
        {
            get => _parameters ?? SetFieldReturn(ref _parameters, GetDefaultCameraParameters())!;
            set => SetField(ref _parameters, value);
        }

        private readonly Stack<Vector2> _projectionJitterStack = new();
        public bool HasProjectionJitter
            => _projectionJitterStack.Count > 0 && !IsZeroVector(_projectionJitterStack.Peek());
        public Vector2 ProjectionJitter
            => _projectionJitterStack.TryPeek(out var jitter) ? jitter : Vector2.Zero;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            switch (propName)
            {
                case nameof(Transform):
                    if (_transform is not null)
                        _transform.RenderMatrixChanged -= RenderMatrixChanged;
                    break;
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                    if (_transform is not null)
                        _transform.RenderMatrixChanged += RenderMatrixChanged;
                    CalculateObliqueProjectionMatrix();
                    break;
            }
        }

        private void RenderMatrixChanged(TransformBase @base, Matrix4x4 renderMatrix)
            => CalculateObliqueProjectionMatrix();

        public Matrix4x4 ProjectionMatrix
            => ApplyProjectionJitter(GetBaseProjectionMatrix());

        /// <summary>
        /// Gets the projection matrix without any jitter applied.
        /// Useful for motion vectors and other passes that need consistent projections.
        /// </summary>
        public Matrix4x4 ProjectionMatrixUnjittered
            => GetBaseProjectionMatrix();

        public StateObject PushProjectionJitter(in ProjectionJitterRequest request)
        {
            Vector2 clipSpaceOffset = request.ToClipSpace();
            _projectionJitterStack.Push(clipSpaceOffset);
            return StateObject.New(PopProjectionJitter);
        }

        public void PopProjectionJitter()
        {
            if (_projectionJitterStack.Count <= 0)
                return;

            _projectionJitterStack.Pop();
        }

        public void ClearProjectionJitter()
            => _projectionJitterStack.Clear();

        private Matrix4x4 GetBaseProjectionMatrix()
            => _obliqueNearClippingPlane != null
                ? _obliqueProjectionMatrix
                : Parameters.GetProjectionMatrix();

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

        private static bool IsZeroVector(Vector2 value)
            => MathF.Abs(value.X) <= float.Epsilon && MathF.Abs(value.Y) <= float.Epsilon;

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

        private static XRPerspectiveCameraParameters GetDefaultCameraParameters()
            => new(90.0f, null, 0.1f, 10000.0f);

        //protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        //{
        //    bool canChange = base.OnPropertyChanging(propName, field, @new);
        //    if (canChange)
        //    {
        //        switch (propName)
        //        {
        //            case nameof(Transform):
        //                UnregisterWorldMatrixChanged();
        //                break;
        //        }
        //    }
        //    return canChange;
        //}
        //protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        //{
        //    switch (propName)
        //    {
        //        case nameof(Transform):
        //            RegisterWorldMatrixChanged();
        //            break;
        //    }
        //    base.OnPropertyChanged(propName, prev, field);
        //}

        //private void RegisterProjectionMatrixChanged(XRCameraParameters? parameters)
        //    => parameters?.ProjectionMatrixChanged.AddListener(ProjectionMatrixChanged);
        //private void UnregisterProjectionMatrixChanged(XRCameraParameters? parameters)
        //    => parameters?.ProjectionMatrixChanged.RemoveListener(ProjectionMatrixChanged);

        //private void RegisterWorldMatrixChanged()
        //{
        //    if (_transform is null)
        //        return;

        //    _transform.WorldMatrixChanged += WorldMatrixChanged;
        //}
        //private void UnregisterWorldMatrixChanged()
        //{
        //    if (_transform is null)
        //        return;

        //    _transform.WorldMatrixChanged -= WorldMatrixChanged;
        //}

        //private void ProjectionMatrixChanged(XRCameraParameters parameters)
        //{
        //    //InvalidateMVP();
        //}
        //private void WorldMatrixChanged(TransformBase transform)
        //{
        //    //InvalidateMVP();
        //}

        private CameraPostProcessStateCollection _postProcessStates = new();
        private XRMaterial? _postProcessMaterial;

        public float FarZ
        {
            get => Parameters.FarZ;
            set => Parameters.FarZ = value;
        }
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

        public Vector3 WorldForward => Transform.WorldForward;
        public Vector3 RenderForward => Transform.RenderForward;
        public Vector3 LocalForward => Transform.LocalForward;
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

        #region Lens Distortion helpers
        private readonly struct LensParams(ELensDistortionMode mode, float intensity, float paniniDistance, float paniniCrop, Vector2 paniniViewExtents)
        {
            public ELensDistortionMode Mode { get; } = mode;
            public float Intensity { get; } = intensity;
            public float PaniniDistance { get; } = paniniDistance;
            public float PaniniCrop { get; } = paniniCrop;
            public Vector2 PaniniViewExtents { get; } = paniniViewExtents;

            public bool Enabled
                => Mode != ELensDistortionMode.None && (Mode == ELensDistortionMode.Panini ? PaniniDistance > 0.0f : MathF.Abs(Intensity) > 0.0f);
        }

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

        private static Vector2 ApplyLensDistortionForward(Vector2 uv01, LensParams lens)
        {
            return lens.Mode switch
            {
                ELensDistortionMode.Radial or ELensDistortionMode.RadialAutoFromFOV => ApplyRadialDistortion(uv01, lens.Intensity),
                ELensDistortionMode.Panini => ApplyPaniniForward(uv01, lens),
                _ => uv01
            };
        }

        private static Vector2 ApplyLensDistortionInverse(Vector2 uv01, LensParams lens)
        {
            return lens.Mode switch
            {
                ELensDistortionMode.Radial or ELensDistortionMode.RadialAutoFromFOV => InvertRadialDistortion(uv01, lens.Intensity),
                ELensDistortionMode.Panini => InvertPanini(uv01, lens),
                _ => uv01
            };
        }

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

        private static Vector2 ApplyPaniniForward(Vector2 uv01, LensParams lens)
        {
            if (lens.PaniniDistance <= 0.0f)
                return uv01;

            Vector2 viewPos = (uv01 * 2.0f - Vector2.One) * lens.PaniniViewExtents * lens.PaniniCrop;
            Vector2 projPos = ApplyPaniniProjection(viewPos, lens.PaniniDistance);
            Vector2 projNdc = projPos / lens.PaniniViewExtents;
            return projNdc * 0.5f + new Vector2(0.5f, 0.5f);
        }

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

        private static Vector2 ApplyPaniniFromView(Vector2 viewPos, LensParams lens)
        {
            Vector2 projPos = ApplyPaniniProjection(viewPos, lens.PaniniDistance);
            Vector2 projNdc = projPos / lens.PaniniViewExtents;
            return projNdc * 0.5f + new Vector2(0.5f, 0.5f);
        }

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

        public Segment GetWorldSegment(Vector2 normalizedScreenPoint)
        {
            Vector3 start = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 0.0f);
            Vector3 end = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 1.0f);
            return new Segment(start, end);
        }
        public Ray GetWorldRay(Vector2 normalizedScreenPoint)
        {
            Vector3 start = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 0.0f);
            Vector3 end = NormalizedViewportToWorldCoordinate(normalizedScreenPoint, 1.0f);
            return new Ray(start, end - start);
        }
        public Vector3 GetPointAtDepth(Vector2 normalizedScreenPoint, float depth)
            => NormalizedViewportToWorldCoordinate(normalizedScreenPoint, depth);
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

        public virtual void SetAmbientOcclusionUniforms(XRRenderProgram program, AmbientOcclusionSettings.EType? overrideType = null)
        {
            var stage = GetPostProcessStageState<AmbientOcclusionSettings>();
            if (stage?.TryGetBacking(out AmbientOcclusionSettings? settings) == true)
            {
                settings.SetUniforms(program, overrideType);
                return;
            }

            program.Uniform("Radius", 0.9f);
            program.Uniform("Power", 1.4f);
        }

        // Post-process uniform setup moved to pipeline helpers.

        public XRMaterial? PostProcessMaterial
        {
            get => _postProcessMaterial;
            set => SetField(ref _postProcessMaterial, value);
        }

        private RenderPipeline? _renderPipeline = null;
        /// <summary>
        /// This is the rendering setup this viewport will use to render the scene the camera sees.
        /// A render pipeline is a collection of render passes that will be executed in order to render the scene and post-process the result, etc.
        /// </summary>
        [YamlIgnore]
        public RenderPipeline RenderPipeline
        {
            get => _renderPipeline ?? SetFieldReturn(ref _renderPipeline, Engine.Rendering.NewRenderPipeline())!;
            set => SetField(ref _renderPipeline, value);
        }

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
        
        private Plane? _obliqueNearClippingPlane = null;

        public void SetObliqueClippingPlane(Vector3 planePosWorld, Vector3 planeNormalWorld)
            => _obliqueNearClippingPlane = XRMath.CreatePlaneFromPointAndNormal(planePosWorld, planeNormalWorld);
        public void SetObliqueClippingPlane(Vector3 planeNormalWorld, float planeDistance)
            => _obliqueNearClippingPlane = new Plane(planeNormalWorld, planeDistance);
        public void SetObliqueClippingPlane(Plane plane)
            => _obliqueNearClippingPlane = plane;
        public void ClearObliqueClippingPlane()
            => _obliqueNearClippingPlane = null;

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
    }
}
