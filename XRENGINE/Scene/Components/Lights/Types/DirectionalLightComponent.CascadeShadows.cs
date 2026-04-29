using System;
using System.Buffers;
using System.Collections;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Components.Scene.Transforms;
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
            public required float BlendWidth { get; init; }
            public required bool HasManualBiasOverride { get; init; }
            public required float BiasMin { get; init; }
            public required float BiasMax { get; init; }
            public required float ReceiverOffset { get; init; }
            public required float TexelWorldSize { get; init; }
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

        public readonly record struct CascadeShadowBiasSettings(
            bool HasManualOverride,
            float BiasMin,
            float BiasMax,
            float ReceiverOffset,
            float TexelWorldSize);

        public readonly record struct CascadeShadowBiasOverride(
            bool Enabled,
            float BiasMin,
            float BiasMax,
            float ReceiverOffset);

        private const int MaxCascadeRenderCount = 8;
        private const float CascadeBoundsPadding = 0.05f;
        private const float CascadeAutoBiasReferenceResolution = 4096.0f;
        private const float CascadeAutoBiasCompareTexels = 2.0f;
        private const float CascadeAutoBiasMinTexels = 0.25f;
        private const float CascadeAutoReceiverOffsetTexels = 1.5f;

        private int _cascadeCount = 4;
        private float[] _cascadePercentages = [0.1f, 0.2f, 0.3f, 0.4f];
        private CascadeShadowBiasOverride[] _cascadeBiasOverrides = CreateDefaultCascadeBiasOverrides(4);
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
                {
                    NormalizeCascadePercentages();
                    NormalizeCascadeBiasOverrides();
                }
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
        /// Optional per-cascade receiver bias overrides. Disabled entries use automatic values
        /// derived from the base light bias, cascade texel size, split distance, and resolution.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Bias Overrides")]
        public CascadeShadowBiasOverride[] CascadeBiasOverrides
        {
            get => [.. _cascadeBiasOverrides];
            set => SetCascadeBiasOverrides(value);
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

        public CascadeShadowBiasSettings GetCascadeBiasSettings(int index)
        {
            lock (_cascadeDataLock)
            {
                if (index >= 0 && index < _cascadeShadowSlices.Count)
                {
                    CascadeShadowSlice slice = _cascadeShadowSlices[index];
                    return new CascadeShadowBiasSettings(
                        slice.HasManualBiasOverride,
                        slice.BiasMin,
                        slice.BiasMax,
                        slice.ReceiverOffset,
                        slice.TexelWorldSize);
                }
            }

            CascadeShadowBiasOverride manual = GetCascadeBiasOverride(index);
            if (manual.Enabled)
                return new CascadeShadowBiasSettings(true, manual.BiasMin, manual.BiasMax, manual.ReceiverOffset, 0.0f);

            return new CascadeShadowBiasSettings(false, ShadowMinBias, ShadowMaxBias, ShadowMaxBias, 0.0f);
        }

        public CascadeShadowBiasOverride GetCascadeBiasOverride(int index)
            => index >= 0 && index < _cascadeBiasOverrides.Length
                ? _cascadeBiasOverrides[index]
                : new CascadeShadowBiasOverride(false, 0.0f, 0.0f, 0.0f);

        public void SetCascadeBiasOverride(int index, CascadeShadowBiasOverride value)
        {
            if (index < 0 || index >= _cascadeCount)
                return;

            CascadeShadowBiasOverride[] next = CascadeBiasOverrides;
            if (next.Length != _cascadeCount)
                Array.Resize(ref next, _cascadeCount);

            next[index] = NormalizeCascadeBiasOverride(value);
            SetCascadeBiasOverrides(next);
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

        private static CascadeShadowBiasOverride[] CreateDefaultCascadeBiasOverrides(int count)
            => count <= 0 ? [] : new CascadeShadowBiasOverride[count];

        private static CascadeShadowBiasOverride NormalizeCascadeBiasOverride(CascadeShadowBiasOverride value)
        {
            float minBias = MathF.Max(0.0f, value.BiasMin);
            float maxBias = MathF.Max(minBias, value.BiasMax);
            float receiverOffset = MathF.Max(0.0f, value.ReceiverOffset);
            return new CascadeShadowBiasOverride(value.Enabled, minBias, maxBias, receiverOffset);
        }

        private void SetCascadeBiasOverrides(CascadeShadowBiasOverride[]? value)
        {
            CascadeShadowBiasOverride[] next = value is { Length: > 0 }
                ? [.. value]
                : CreateDefaultCascadeBiasOverrides(_cascadeCount);

            if (next.Length != _cascadeCount)
                Array.Resize(ref next, _cascadeCount);

            for (int i = 0; i < next.Length; i++)
                next[i] = NormalizeCascadeBiasOverride(next[i]);

            SetField(ref _cascadeBiasOverrides, next, nameof(CascadeBiasOverrides));
        }

        private void NormalizeCascadeBiasOverrides()
        {
            CascadeShadowBiasOverride[] next = CreateDefaultCascadeBiasOverrides(_cascadeCount);
            int copyCount = Math.Min(_cascadeBiasOverrides.Length, next.Length);
            for (int i = 0; i < copyCount; i++)
                next[i] = NormalizeCascadeBiasOverride(_cascadeBiasOverrides[i]);

            SetField(ref _cascadeBiasOverrides, next, nameof(CascadeBiasOverrides));
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

        private void CopyPublishedCascadeUniformData(
            Span<float> splits,
            Span<float> blendWidths,
            Span<float> biasMins,
            Span<float> biasMaxes,
            Span<float> receiverOffsets,
            Span<Matrix4x4> matrices,
            out int cascadeCount)
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
                        blendWidths[i] = _cascadeShadowSlices[i].BlendWidth;
                        biasMins[i] = _cascadeShadowSlices[i].BiasMin;
                        biasMaxes[i] = _cascadeShadowSlices[i].BiasMax;
                        receiverOffsets[i] = _cascadeShadowSlices[i].ReceiverOffset;
                        matrices[i] = _cascadeShadowSlices[i].WorldToLightSpaceMatrix;
                    }
                    else
                    {
                        splits[i] = float.MaxValue;
                        blendWidths[i] = 0.0f;
                        biasMins[i] = ShadowMinBias;
                        biasMaxes[i] = ShadowMaxBias;
                        receiverOffsets[i] = ShadowMaxBias;
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

            // Build fully-populated arrays in locals first, then publish the field
            // references atomically. Readers on the render thread snapshot these field
            // references and must never observe a partially-populated array.
            var frameBuffers = new XRFrameBuffer[requiredCascades];
            var viewports = new XRViewport[requiredCascades];
            var transforms = new Transform[requiredCascades];
            var cameras = new XRCamera[requiredCascades];

            for (int i = 0; i < requiredCascades; i++)
            {
                var transform = new Transform
                {
                    Order = XREngine.Animation.ETransformOrder.TRS,
                };

                XROrthographicCameraParameters parameters = new(1.0f, 1.0f, NearZ, 1.0f);
                parameters.InheritAspectRatio = false; // Shadow cameras need independent W/H
                parameters.SetOriginPercentages(0.5f, 0.5f);
                var camera = new XRCamera(transform, parameters)
                {
                    CullingMask = DefaultLayers.EverythingExceptGizmos,
                };
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

                transforms[i] = transform;
                cameras[i] = camera;
                viewports[i] = viewport;
                frameBuffers[i] = new XRFrameBuffer((_cascadeShadowMapTexture!, EFrameBufferAttachment.DepthAttachment, 0, i));
            }

            _cascadeShadowTransforms = transforms;
            _cascadeShadowCameras = cameras;
            _cascadeShadowViewports = viewports;
            _cascadeShadowFrameBuffers = frameBuffers;
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

        private static void UpdateCascadeShadowCamera(Transform transform, XRCamera camera, Vector3 center, Vector3 halfExtents, Quaternion orientation, Vector3 lightDirection, float nearZ)
        {
            transform.Translation = center - lightDirection * halfExtents.Z;
            transform.Rotation = orientation;

            transform.RecalculateMatrices(forceWorldRecalc: true);

            float width = MathF.Max(halfExtents.X * 2.0f, 1e-3f);
            float height = MathF.Max(halfExtents.Y * 2.0f, 1e-3f);
            float depth = MathF.Max(halfExtents.Z * 2.0f, nearZ + 1e-3f);
            if (camera.Parameters is not XROrthographicCameraParameters ortho)
            {
                ortho = new XROrthographicCameraParameters(width, height, nearZ, depth - nearZ);
                ortho.InheritAspectRatio = false;
                ortho.SetOriginPercentages(0.5f, 0.5f);
                camera.Parameters = ortho;
            }
            else
            {
                ortho.Resize(width, height); // Bypass InheritAspectRatio coupling
                ortho.NearZ = nearZ;
                ortho.FarZ = depth - nearZ;
            }
        }

        private CascadeShadowBiasSettings ResolveCascadeBiasSettings(
            int cascadeIndex,
            float splitStartDistance,
            float splitEndDistance,
            Vector3 halfExtents,
            float cascadeRangeFar,
            uint shadowMapWidth,
            uint shadowMapHeight,
            CascadeShadowBiasOverride[] biasOverrides)
        {
            float mapWidth = MathF.Max(1.0f, shadowMapWidth);
            float mapHeight = MathF.Max(1.0f, shadowMapHeight);
            float cascadeWidth = MathF.Max(1e-4f, halfExtents.X * 2.0f);
            float cascadeHeight = MathF.Max(1e-4f, halfExtents.Y * 2.0f);
            float texelWorldSize = MathF.Max(cascadeWidth / mapWidth, cascadeHeight / mapHeight);

            if (cascadeIndex >= 0 && cascadeIndex < biasOverrides.Length && biasOverrides[cascadeIndex].Enabled)
            {
                CascadeShadowBiasOverride manual = NormalizeCascadeBiasOverride(biasOverrides[cascadeIndex]);
                return new CascadeShadowBiasSettings(true, manual.BiasMin, manual.BiasMax, manual.ReceiverOffset, texelWorldSize);
            }

            float depthRange = MathF.Max(1e-4f, halfExtents.Z * 2.0f);
            float normalizedTexelDepth = texelWorldSize / depthRange;
            float rangeFar = MathF.Max(1e-4f, cascadeRangeFar);
            float distanceMidpoint = (splitStartDistance + splitEndDistance) * 0.5f;
            float distanceT = Math.Clamp(distanceMidpoint / rangeFar, 0.0f, 1.0f);
            float distanceScale = 0.75f + MathF.Sqrt(distanceT) * 1.25f;
            float resolutionScale = Math.Clamp(CascadeAutoBiasReferenceResolution / MathF.Max(mapWidth, mapHeight), 0.5f, 4.0f);
            float biasScale = Math.Clamp(distanceScale * resolutionScale, 0.35f, 8.0f);

            float minBias = MathF.Max(ShadowMinBias * biasScale, normalizedTexelDepth * CascadeAutoBiasMinTexels);
            float maxBias = MathF.Max(ShadowMaxBias * biasScale, normalizedTexelDepth * CascadeAutoBiasCompareTexels);
            maxBias = MathF.Max(minBias, maxBias);

            float receiverOffset = MathF.Max(ShadowMaxBias * biasScale, texelWorldSize * CascadeAutoReceiverOffsetTexels);
            return new CascadeShadowBiasSettings(false, minBias, maxBias, receiverOffset, texelWorldSize);
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

            // Snapshot the cascade resource arrays so iteration is stable against
            // concurrent Ensure/Release calls from property changes on other threads.
            Transform[] transformsSnapshot = _cascadeShadowTransforms;
            XRCamera[] camerasSnapshot = _cascadeShadowCameras;
            XRTexture2DArray? cascadeTexture = _cascadeShadowMapTexture;
            if (cascadeTexture is null || camerasSnapshot.Length == 0 || transformsSnapshot.Length != camerasSnapshot.Length)
            {
                ClearCascadeShadows();
                return;
            }

            Frustum playerFrustum = playerCamera.WorldFrustum();
            BuildLightSpaceBasis(out Matrix4x4 worldToLight, out Matrix4x4 lightToWorld, out Quaternion lightRotation, out Vector3 lightDirection);

            int maxCascadeCount = Math.Min(camerasSnapshot.Length, MaxCascadeRenderCount);
            Span<float> percentages = stackalloc float[MaxCascadeRenderCount];
            CopyEffectiveCascadePercentages(percentages, out int percentageCount);
            CascadeShadowBiasOverride[] biasOverrideSnapshot = _cascadeBiasOverrides;
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

                    UpdateCascadeShadowCamera(transformsSnapshot[resourceSlot], camerasSnapshot[resourceSlot], centerWS, halfExtents, lightRotation, lightDirection, NearZ);

                    Matrix4x4 cascadeView = camerasSnapshot[resourceSlot].Transform.InverseRenderMatrix;
                    Matrix4x4 cascadeProj = camerasSnapshot[resourceSlot].ProjectionMatrix;
                    Matrix4x4 viewProj = cascadeView * cascadeProj;
                    CascadeShadowBiasSettings biasSettings = ResolveCascadeBiasSettings(
                        cascadeIndex,
                        splitStart,
                        splitEnd,
                        halfExtents,
                        effectiveCascadeFar,
                        cascadeTexture.Width,
                        cascadeTexture.Height,
                        biasOverrideSnapshot);

                    nextShadowSlices[resourceSlot] = new CascadeShadowSlice
                    {
                        CascadeIndex = cascadeIndex,
                        SplitFarDistance = splitEnd,
                        BlendWidth = expand,
                        HasManualBiasOverride = biasSettings.HasManualOverride,
                        BiasMin = biasSettings.BiasMin,
                        BiasMax = biasSettings.BiasMax,
                        ReceiverOffset = biasSettings.ReceiverOffset,
                        TexelWorldSize = biasSettings.TexelWorldSize,
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

        private bool ShouldCollectPrimaryShadowViewport()
        {
            if (ShadowMap is null)
                return false;

            if (!EnableCascadedShadows || CascadedShadowMapTexture is null || ActiveCascadeCount <= 0)
                return true;

            XRWorldInstance? world = WorldAs<XRWorldInstance>();
            if (world is null)
                return true;

            return world.Lights.NeedsPrimaryDirectionalShadowMap();
        }

        public override void CollectVisibleItems()
        {
            if (!CastsShadows)
                return;

            if (ShouldCollectPrimaryShadowViewport())
                PrimaryShadowViewport.CollectVisible(false);

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            int cascadeCount = GetPublishedCascadeViewportCount(cascadeShadowViewports);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].CollectVisible(false, collectionVolumeOverride: GetPublishedCascadeCullVolume(i));
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows)
                return;

            if (ShouldCollectPrimaryShadowViewport())
                PrimaryShadowViewport.SwapBuffers();

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

            var shadowMap = ShadowMap;
            XRMaterial? shadowMaterial = shadowMap?.Material;

            if (ShouldCollectPrimaryShadowViewport() && shadowMap is not null && shadowMaterial is not null)
                PrimaryShadowViewport.Render(shadowMap, null, null, true, shadowMaterial);

            if (shadowMaterial is null)
                return;

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            XRFrameBuffer[] cascadeShadowFrameBuffers = _cascadeShadowFrameBuffers;
            int cascadeCount = GetPublishedCascadeRenderCount(cascadeShadowViewports, cascadeShadowFrameBuffers);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].Render(cascadeShadowFrameBuffers[i], null, null, true, shadowMaterial);
        }
    }
}