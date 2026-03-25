using System;
using System.Buffers;
using System.Collections;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    public partial class DirectionalLightComponent
    {
        private sealed class CascadeAabbView(DirectionalLightComponent owner) : IReadOnlyList<CascadedShadowAabb>
        {
            private readonly DirectionalLightComponent _owner = owner;

            public int Count
            {
                get
                {
                    lock (_owner._cascadeDataLock)
                        return _owner._cascadeAabbs.Count;
                }
            }

            public CascadedShadowAabb this[int index]
            {
                get
                {
                    lock (_owner._cascadeDataLock)
                        return _owner._cascadeAabbs[index];
                }
            }

            public IEnumerator<CascadedShadowAabb> GetEnumerator()
            {
                int index = 0;
                while (true)
                {
                    CascadedShadowAabb item;
                    lock (_owner._cascadeDataLock)
                    {
                        if (index >= _owner._cascadeAabbs.Count)
                            yield break;

                        item = _owner._cascadeAabbs[index];
                    }

                    yield return item;
                    index++;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
                => GetEnumerator();
        }

        private readonly struct CascadeShadowSlice
        {
            public required int CascadeIndex { get; init; }
            public required float SplitFarDistance { get; init; }
            public required Vector3 Center { get; init; }
            public required Vector3 HalfExtents { get; init; }
            public required Quaternion Orientation { get; init; }
            public required Matrix4x4 WorldToLightSpaceMatrix { get; init; }
        }

        public readonly record struct CascadedShadowAabb(
            int FrustumIndex,
            int CascadeIndex,
            Vector3 Center,
            Vector3 HalfExtents,
            Quaternion Orientation);

        private const int MaxCascadeRenderCount = 8;
        private const float CascadeBoundsPadding = 0.05f;

        private int _cascadeCount = 4;
        private float[] _cascadePercentages = [0.1f, 0.2f, 0.3f, 0.4f];
        private float _cascadeOverlapPercent = 0.1f;
        private bool _debugCascadeColors;
        private readonly object _cascadeDataLock = new();
        private readonly List<CascadedShadowAabb> _cascadeAabbs = new(4);
        private readonly List<CascadeShadowSlice> _cascadeShadowSlices = new(MaxCascadeRenderCount);
        private readonly CascadeAabbView _cascadeAabbView;
        private float _publishedCascadeRangeNear;
        private float _publishedCascadeRangeFar;
        private XRTexture2DArray? _cascadeShadowMapTexture;
        private XRFrameBuffer[] _cascadeShadowFrameBuffers = [];
        private XRViewport[] _cascadeShadowViewports = [];
        private Transform[] _cascadeShadowTransforms = [];
        private XRCamera[] _cascadeShadowCameras = [];

        /// <summary>
        /// Number of cascaded shadow map splits to generate within the camera/light intersection AABB.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Count")]
        public int CascadeCount
        {
            get => _cascadeCount;
            set
            {
                int clamped = Math.Clamp(value, 1, 8);
                if (SetField(ref _cascadeCount, clamped))
                    NormalizeCascadePercentages();
            }
        }

        /// <summary>
        /// Symmetric overlap applied to each cascade slice along the forward axis (0-1 of slice length).
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Overlap %")]
        public float CascadeOverlapPercent
        {
            get => _cascadeOverlapPercent;
            set => SetField(ref _cascadeOverlapPercent, Math.Clamp(value, 0.0f, 1.0f));
        }

        /// <summary>
        /// When true, the shader replaces lighting output with a per-cascade color overlay for debugging.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Debug Cascade Colors")]
        [Description("When enabled, each cascade is tinted with a distinct color in the viewport for visual debugging.")]
        public bool DebugCascadeColors
        {
            get => _debugCascadeColors;
            set => SetField(ref _debugCascadeColors, value);
        }

        /// <summary>
        /// Percentages (should sum to 1) allocated to each cascade along the camera forward axis.
        /// Length is clamped/expanded to match CascadeCount and normalized on assignment.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Percentages")]
        public float[] CascadePercentages
        {
            get => [.. _cascadePercentages];
            set => SetCascadePercentages(value);
        }

        /// <summary>
        /// Cascaded shadow AABBs derived from the current camera/light intersection.
        /// </summary>
        public IReadOnlyList<CascadedShadowAabb> CascadedShadowAabbs => _cascadeAabbView;

        public XRTexture2DArray? CascadedShadowMapTexture => _cascadeShadowMapTexture;
        public int ActiveCascadeCount
        {
            get
            {
                lock (_cascadeDataLock)
                    return _cascadeShadowSlices.Count;
            }
        }

        public float CascadeRangeNear
        {
            get
            {
                lock (_cascadeDataLock)
                    return _publishedCascadeRangeNear;
            }
        }

        public float CascadeRangeFar
        {
            get
            {
                lock (_cascadeDataLock)
                    return _publishedCascadeRangeFar;
            }
        }

        public float EffectiveCascadeDistance
        {
            get
            {
                lock (_cascadeDataLock)
                    return MathF.Max(0.0f, _publishedCascadeRangeFar - _publishedCascadeRangeNear);
            }
        }

        public float GetCascadeSplit(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].SplitFarDistance
                    : float.MaxValue;
        }

        public Matrix4x4 GetCascadeMatrix(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].WorldToLightSpaceMatrix
                    : Matrix4x4.Identity;
        }

        public Vector3 GetCascadeCenter(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].Center
                    : Vector3.Zero;
        }

        public Vector3 GetCascadeHalfExtents(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].HalfExtents
                    : Vector3.Zero;
        }

        public XRCamera? GetCascadeCamera(int index)
            => index >= 0 && index < _cascadeShadowCameras.Length
                ? _cascadeShadowCameras[index]
                : null;

        public XRViewport? GetCascadeViewport(int index)
            => index >= 0 && index < _cascadeShadowViewports.Length
                ? _cascadeShadowViewports[index]
                : null;

        public XRFrameBuffer? GetCascadeFrameBuffer(int index)
            => index >= 0 && index < _cascadeShadowFrameBuffers.Length
                ? _cascadeShadowFrameBuffers[index]
                : null;

        private static float[] CreateUniformPercentages(int count)
        {
            if (count <= 0)
                return [];

            float uniform = 1.0f / count;
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
                result[i] = uniform;
            return result;
        }

        private void SetCascadePercentages(float[]? value)
        {
            float[] next;
            if (value is null || value.Length == 0)
            {
                next = CreateUniformPercentages(_cascadeCount);
            }
            else
            {
                next = [.. value];
            }

            if (next.Length != _cascadeCount)
                Array.Resize(ref next, _cascadeCount);

            float sum = 0.0f;
            for (int i = 0; i < _cascadeCount; i++)
                sum += MathF.Abs(next[i]);

            if (sum <= float.Epsilon)
                next = CreateUniformPercentages(_cascadeCount);
            else
            {
                for (int i = 0; i < _cascadeCount; i++)
                    next[i] = MathF.Abs(next[i]) / sum;
            }

            SetField(ref _cascadePercentages, next, nameof(CascadePercentages));
        }

        private void NormalizeCascadePercentages()
        {
            if (_cascadePercentages.Length != _cascadeCount)
                Array.Resize(ref _cascadePercentages, _cascadeCount);

            float sum = 0.0f;
            for (int i = 0; i < _cascadeCount; i++)
                sum += MathF.Abs(_cascadePercentages[i]);

            if (sum <= float.Epsilon)
            {
                _cascadePercentages = CreateUniformPercentages(_cascadeCount);
                return;
            }

            for (int i = 0; i < _cascadeCount; i++)
                _cascadePercentages[i] = MathF.Abs(_cascadePercentages[i]) / sum;
        }

        private void CopyEffectiveCascadePercentages(Span<float> destination, out int count)
        {
            count = Math.Min(Math.Clamp(_cascadeCount, 0, MaxCascadeRenderCount), destination.Length);
            if (count <= 0)
                return;

            if (_cascadePercentages.Length != _cascadeCount)
                NormalizeCascadePercentages();

            float sum = 0.0f;
            for (int i = 0; i < count; i++)
                sum += _cascadePercentages[i];

            if (sum <= float.Epsilon)
            {
                float uniform = 1.0f / count;
                for (int i = 0; i < count; i++)
                    destination[i] = uniform;
                return;
            }

            float invSum = 1.0f / sum;
            for (int i = 0; i < count; i++)
                destination[i] = _cascadePercentages[i] * invSum;
        }

        private int GetPublishedCascadeViewportCount(XRViewport[] viewports)
        {
            lock (_cascadeDataLock)
                return Math.Min(_cascadeShadowSlices.Count, viewports.Length);
        }

        private int GetPublishedCascadeRenderCount(XRViewport[] viewports, XRFrameBuffer[] frameBuffers)
        {
            lock (_cascadeDataLock)
                return Math.Min(_cascadeShadowSlices.Count, Math.Min(viewports.Length, frameBuffers.Length));
        }

        private Box? GetPublishedCascadeCullVolume(int index)
        {
            lock (_cascadeDataLock)
            {
                if (index < 0 || index >= _cascadeAabbs.Count)
                    return null;

                CascadedShadowAabb cascade = _cascadeAabbs[index];
                Matrix4x4 transform =
                    Matrix4x4.CreateFromQuaternion(cascade.Orientation) *
                    Matrix4x4.CreateTranslation(cascade.Center);
                return new Box(Vector3.Zero, cascade.HalfExtents * 2.0f, transform);
            }
        }

        private void CopyPublishedCascadeUniformData(Span<float> splits, Span<Matrix4x4> matrices, out int cascadeCount)
        {
            int copyCount = Math.Min(MaxCascadeRenderCount, Math.Min(splits.Length, matrices.Length));

            lock (_cascadeDataLock)
            {
                cascadeCount = _cascadeShadowSlices.Count;
                for (int i = 0; i < copyCount; i++)
                {
                    if (i < cascadeCount)
                    {
                        splits[i] = _cascadeShadowSlices[i].SplitFarDistance;
                        matrices[i] = _cascadeShadowSlices[i].WorldToLightSpaceMatrix;
                    }
                    else
                    {
                        splits[i] = float.MaxValue;
                        matrices[i] = Matrix4x4.Identity;
                    }
                }
            }
        }

        private static void IncludeCascadeCorner(Vector3 nearCorner, Vector3 farCorner, float t, Matrix4x4 worldToLight, ref Vector3 min, ref Vector3 max)
        {
            Vector3 point = Vector3.Transform(Vector3.Lerp(nearCorner, farCorner, t), worldToLight);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static void GetCascadeBoundsInLightSpace(Frustum cameraFrustum, float nearT, float farT, Matrix4x4 worldToLight, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);

            IncludeCascadeCorner(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, farT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, farT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, farT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, farT, worldToLight, ref min, ref max);
        }

        private void BuildLightSpaceBasis(out Matrix4x4 worldToLight, out Matrix4x4 lightToWorld, out Quaternion lightRotation, out Vector3 lightDir)
        {
            lightDir = Transform.WorldForward;
            if (lightDir.LengthSquared() < 1e-6f)
                lightDir = Vector3.UnitZ;
            lightDir = Vector3.Normalize(lightDir);

            Vector3 up = Transform.WorldUp;
            if (MathF.Abs(Vector3.Dot(lightDir, up)) > 0.99f)
                up = Vector3.UnitX;

            Vector3 right = Vector3.Normalize(Vector3.Cross(up, lightDir));
            up = Vector3.Normalize(Vector3.Cross(lightDir, right));

            // The engine's forward direction is -Z (Globals.Forward = (0,0,-1)).
            // To make the cascade camera look along lightDir, the camera's local -Z
            // must map to lightDir, i.e. local +Z must map to -lightDir.
            // Negate both Y and Z to maintain a valid rotation (det = +1).
            worldToLight = new(
                right.X, -up.X, -lightDir.X, 0,
                right.Y, -up.Y, -lightDir.Y, 0,
                right.Z, -up.Z, -lightDir.Z, 0,
                0, 0, 0, 1);

            Matrix4x4.Invert(worldToLight, out lightToWorld);
            lightRotation = Quaternion.CreateFromRotationMatrix(lightToWorld);
        }

        private void EnsureCascadeShadowResources()
        {
            if (!CastsShadows)
                return;

            int requiredCascades = Math.Clamp(_cascadeCount, 1, MaxCascadeRenderCount);
            uint width = Math.Max(1u, ShadowMapResolutionWidth);
            uint height = Math.Max(1u, ShadowMapResolutionHeight);

            bool recreateTexture = _cascadeShadowMapTexture is null ||
                _cascadeShadowMapTexture.Depth != (uint)requiredCascades ||
                _cascadeShadowMapTexture.Width != width ||
                _cascadeShadowMapTexture.Height != height;

            if (recreateTexture)
            {
                _cascadeShadowMapTexture?.Destroy();
                _cascadeShadowMapTexture = XRTexture2DArray.CreateFrameBufferTexture(
                    (uint)requiredCascades,
                    width,
                    height,
                    GetShadowDepthMapFormat(EDepthPrecision.Int24),
                    EPixelFormat.DepthComponent,
                    EPixelType.Float,
                    EFrameBufferAttachment.DepthAttachment);
                _cascadeShadowMapTexture.SamplerName = "ShadowMapArray";
            }

            if (_cascadeShadowFrameBuffers.Length == requiredCascades && !recreateTexture)
                return;

            XRWorldInstance? world = WorldAs<XRWorldInstance>();
            _cascadeShadowFrameBuffers = new XRFrameBuffer[requiredCascades];
            _cascadeShadowViewports = new XRViewport[requiredCascades];
            _cascadeShadowTransforms = new Transform[requiredCascades];
            _cascadeShadowCameras = new XRCamera[requiredCascades];

            for (int i = 0; i < requiredCascades; i++)
            {
                var transform = new Transform
                {
                    Order = XREngine.Animation.ETransformOrder.TRS,
                };

                XROrthographicCameraParameters parameters = new(1.0f, 1.0f, NearZ, 1.0f);
                parameters.InheritAspectRatio = false; // Shadow cameras need independent W/H
                parameters.SetOriginPercentages(0.5f, 0.5f);
                var camera = new XRCamera(transform, parameters);
                var viewport = new XRViewport(null, width, height)
                {
                    RenderPipeline = new ShadowRenderPipeline(),
                    SetRenderPipelineFromCamera = false,
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false,
                    AllowUIRender = false,
                    CullWithFrustum = true,
                    WorldInstanceOverride = world,
                    Camera = camera,
                };

                _cascadeShadowTransforms[i] = transform;
                _cascadeShadowCameras[i] = camera;
                _cascadeShadowViewports[i] = viewport;
                _cascadeShadowFrameBuffers[i] = new XRFrameBuffer((_cascadeShadowMapTexture!, EFrameBufferAttachment.DepthAttachment, 0, i));
            }
        }

        private void ReleaseCascadeShadowResources()
        {
            ClearCascadeShadows();

            for (int i = 0; i < _cascadeShadowViewports.Length; i++)
            {
                _cascadeShadowViewports[i].WorldInstanceOverride = null;
                _cascadeShadowViewports[i].Camera = null;
            }

            _cascadeShadowMapTexture?.Destroy();
            _cascadeShadowMapTexture = null;
            _cascadeShadowFrameBuffers = [];
            _cascadeShadowViewports = [];
            _cascadeShadowTransforms = [];
            _cascadeShadowCameras = [];
        }

        private void UpdateCascadeShadowCamera(int slot, Vector3 center, Vector3 halfExtents, Quaternion orientation, Vector3 lightDirection)
        {
            Transform transform = _cascadeShadowTransforms[slot];
            transform.Translation = center - lightDirection * halfExtents.Z;
            transform.Rotation = orientation;

            transform.RecalculateMatrices(forceWorldRecalc: true);

            XRCamera camera = _cascadeShadowCameras[slot];
            float width = MathF.Max(halfExtents.X * 2.0f, 1e-3f);
            float height = MathF.Max(halfExtents.Y * 2.0f, 1e-3f);
            float depth = MathF.Max(halfExtents.Z * 2.0f, NearZ + 1e-3f);
            if (camera.Parameters is not XROrthographicCameraParameters ortho)
            {
                ortho = new XROrthographicCameraParameters(width, height, NearZ, depth - NearZ);
                ortho.InheritAspectRatio = false;
                ortho.SetOriginPercentages(0.5f, 0.5f);
                camera.Parameters = ortho;
            }
            else
            {
                ortho.Resize(width, height); // Bypass InheritAspectRatio coupling
                ortho.NearZ = NearZ;
                ortho.FarZ = depth - NearZ;
            }
        }

        internal void ClearCascadeShadows()
        {
            lock (_cascadeDataLock)
            {
                _cascadeAabbs.Clear();
                _cascadeShadowSlices.Clear();
                _publishedCascadeRangeNear = 0.0f;
                _publishedCascadeRangeFar = 0.0f;
            }
        }

        internal void UpdateCascadeShadows(XRCamera playerCamera)
        {
            if (!CastsShadows || !EnableCascadedShadows || ShadowCamera is null)
            {
                ClearCascadeShadows();
                return;
            }

            EnsureCascadeShadowResources();
            if (_cascadeShadowMapTexture is null || _cascadeShadowCameras.Length == 0)
            {
                ClearCascadeShadows();
                return;
            }

            Frustum playerFrustum = playerCamera.WorldFrustum();
            BuildLightSpaceBasis(out Matrix4x4 worldToLight, out Matrix4x4 lightToWorld, out Quaternion lightRotation, out Vector3 lightDirection);

            int maxCascadeCount = Math.Min(_cascadeShadowCameras.Length, MaxCascadeRenderCount);
            Span<float> percentages = stackalloc float[MaxCascadeRenderCount];
            CopyEffectiveCascadePercentages(percentages, out int percentageCount);
            CascadeShadowSlice[] nextShadowSlices = ArrayPool<CascadeShadowSlice>.Shared.Rent(maxCascadeCount);
            CascadedShadowAabb[] nextCascadeAabbs = ArrayPool<CascadedShadowAabb>.Shared.Rent(maxCascadeCount);

            float cameraNear = playerCamera.NearZ;
            float sourceCameraFar = playerCamera.FarZ;
            if (!float.IsFinite(sourceCameraFar) || sourceCameraFar <= cameraNear)
                sourceCameraFar = cameraNear + 1.0f;

            float effectiveCascadeFar = GetEffectiveCascadedShadowFarDistance(playerCamera);
            float totalDepth = MathF.Max(effectiveCascadeFar - cameraNear, 1e-4f);
            float sourceFrustumDepth = MathF.Max(sourceCameraFar - cameraNear, 1e-4f);

            // Shadow caster capture depth — how far behind each cascade slice (in light
            // space) we extend to include potential casters. Scale.Z is used because it
            // already represents the user's intended shadow volume depth and 24-bit depth
            // precision is adequate even at large values (e.g. 900 → ~17K levels/unit).
            float shadowDepth = MathF.Max(Scale.Z, totalDepth);
            float cumulative = 0.0f;
            int resourceSlot = 0;

            try
            {
                for (int cascadeIndex = 0; cascadeIndex < Math.Min(percentageCount, maxCascadeCount); cascadeIndex++)
                {
                    float pct = percentages[cascadeIndex];
                    if (pct <= 0.0f)
                        continue;

                    float splitStart = cameraNear + totalDepth * cumulative;
                    float splitEnd = splitStart + totalDepth * pct;
                    cumulative += pct;

                    float sliceDepth = splitEnd - splitStart;
                    float expand = sliceDepth * _cascadeOverlapPercent * 0.5f;
                    float expandedStart = MathF.Max(cameraNear, splitStart - expand);
                    float expandedEnd = MathF.Min(effectiveCascadeFar, splitEnd + expand);
                    float nearT = Math.Clamp((expandedStart - cameraNear) / sourceFrustumDepth, 0.0f, 1.0f);
                    float farT = Math.Clamp((expandedEnd - cameraNear) / sourceFrustumDepth, 0.0f, 1.0f);

                    GetCascadeBoundsInLightSpace(playerFrustum, nearT, farT, worldToLight, out Vector3 min, out Vector3 max);

                    // With the -Z forward convention, positive light-space Z points toward
                    // the light source. Extend max.Z to capture shadow casters upstream.
                    max.Z += shadowDepth;

                    Vector3 padding = Vector3.Max((max - min) * (CascadeBoundsPadding * 0.5f), new Vector3(1e-3f));
                    min -= padding;
                    max += padding;

                    if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
                        continue;

                    Vector3 centerLS = (min + max) * 0.5f;
                    Vector3 halfExtents = Vector3.Max((max - min) * 0.5f, new Vector3(1e-3f, 1e-3f, NearZ + 1e-3f));
                    Vector3 centerWS = Vector3.Transform(centerLS, lightToWorld);

                    UpdateCascadeShadowCamera(resourceSlot, centerWS, halfExtents, lightRotation, lightDirection);

                    Matrix4x4 cascadeView = _cascadeShadowCameras[resourceSlot].Transform.InverseRenderMatrix;
                    Matrix4x4 cascadeProj = _cascadeShadowCameras[resourceSlot].ProjectionMatrix;
                    Matrix4x4 viewProj = cascadeView * cascadeProj;

                    nextShadowSlices[resourceSlot] = new CascadeShadowSlice
                    {
                        CascadeIndex = cascadeIndex,
                        SplitFarDistance = splitEnd,
                        Center = centerWS,
                        HalfExtents = halfExtents,
                        Orientation = lightRotation,
                        WorldToLightSpaceMatrix = viewProj,
                    };

                    nextCascadeAabbs[resourceSlot] = new CascadedShadowAabb(0, cascadeIndex, centerWS, halfExtents, lightRotation);
                    resourceSlot++;
                }

                lock (_cascadeDataLock)
                {
                    _cascadeShadowSlices.Clear();
                    for (int i = 0; i < resourceSlot; i++)
                        _cascadeShadowSlices.Add(nextShadowSlices[i]);

                    _cascadeAabbs.Clear();
                    for (int i = 0; i < resourceSlot; i++)
                        _cascadeAabbs.Add(nextCascadeAabbs[i]);

                    _publishedCascadeRangeNear = cameraNear;
                    _publishedCascadeRangeFar = effectiveCascadeFar;
                }
            }
            finally
            {
                ArrayPool<CascadeShadowSlice>.Shared.Return(nextShadowSlices, clearArray: false);
                ArrayPool<CascadedShadowAabb>.Shared.Return(nextCascadeAabbs, clearArray: false);
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureCascadeShadowResources();
            for (int i = 0; i < _cascadeShadowViewports.Length; i++)
                _cascadeShadowViewports[i].WorldInstanceOverride = WorldAs<XRWorldInstance>();
        }

        protected override void OnComponentDeactivated()
        {
            ReleaseCascadeShadowResources();
            base.OnComponentDeactivated();
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            base.SetShadowMapResolution(width, height);
            EnsureCascadeShadowResources();
        }

        public override void CollectVisibleItems()
        {
            if (!CastsShadows)
                return;

            if (ShadowMap is not null)
                _viewport.CollectVisible(false);

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            int cascadeCount = GetPublishedCascadeViewportCount(cascadeShadowViewports);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].CollectVisible(false, collectionVolumeOverride: GetPublishedCascadeCullVolume(i));
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows)
                return;

            if (ShadowMap is not null)
                _viewport.SwapBuffers();

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            int cascadeCount = GetPublishedCascadeViewportCount(cascadeShadowViewports);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].SwapBuffers();

            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }

        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!CastsShadows)
                return;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            if (ShadowMap is not null)
                _viewport.Render(ShadowMap, null, null, true, ShadowMap.Material);

            if (ShadowMap?.Material is null)
                return;

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            XRFrameBuffer[] cascadeShadowFrameBuffers = _cascadeShadowFrameBuffers;
            int cascadeCount = GetPublishedCascadeRenderCount(cascadeShadowViewports, cascadeShadowFrameBuffers);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].Render(cascadeShadowFrameBuffers[i], null, null, true, ShadowMap.Material);
        }
    }
}