using System;
using System.Buffers;
using System.Collections;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shadows;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    public partial class DirectionalLightComponent
    {
        /// <summary>
        /// Thread-safe read view over the latest published cascade bounds.
        /// </summary>
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

        /// <summary>
        /// Published world-space bounds for one directional cascade.
        /// </summary>
        public readonly record struct CascadedShadowAabb(
            int FrustumIndex,
            int CascadeIndex,
            Vector3 Center,
            Vector3 HalfExtents,
            Quaternion Orientation);

        /// <summary>
        /// Resolved bias values for one cascade after manual overrides and texel-size scaling.
        /// </summary>
        public readonly record struct CascadeShadowBiasSettings(
            bool HasManualOverride,
            float BiasMin,
            float BiasMax,
            float ReceiverOffset,
            float TexelWorldSize);

        /// <summary>
        /// Optional user-authored receiver-bias override for a cascade.
        /// </summary>
        public readonly record struct CascadeShadowBiasOverride(
            bool Enabled,
            float BiasMin,
            float BiasMax,
            float ReceiverOffset);

        /// <summary>
        /// Shadow-atlas allocation published for one directional primary or cascade tile.
        /// </summary>
        public readonly record struct DirectionalCascadeAtlasSlot(
            bool HasAllocation,
            bool IsResident,
            int PageIndex,
            int RecordIndex,
            Vector4 UvScaleBias,
            float NearPlane,
            float FarPlane,
            float TexelSize,
            uint Resolution,
            ShadowFallbackMode Fallback,
            BoundingRectangle PixelRect,
            BoundingRectangle InnerPixelRect,
            ulong LastRenderedFrame);

        private const int MaxCascadeRenderCount = 8;
        private const float CascadeBoundsPadding = 0.05f;
        private const float ShadowBiasDepthRangeEpsilon = 1e-4f;
        private static readonly string[] CascadeViewProjectionMatrixUniformNames = CreateCascadeViewProjectionMatrixUniformNames();

        private int _cascadeCount = 4;
        private float[] _cascadePercentages = [0.1f, 0.2f, 0.3f, 0.4f];
        private CascadeShadowBiasOverride[] _cascadeBiasOverrides = CreateDefaultCascadeBiasOverrides(4);
        private float _cascadeOverlapPercent = 0.1f;
        private EDirectionalCascadeShadowRenderMode _cascadeShadowRenderMode = EDirectionalCascadeShadowRenderMode.Sequential;
        private bool _debugCascadeColors;
        private readonly object _cascadeDataLock = new();
        private readonly List<CascadedShadowAabb> _cascadeAabbs = new(4);
        private readonly List<CascadeShadowSlice> _cascadeShadowSlices = new(MaxCascadeRenderCount);
        private readonly CascadeAabbView _cascadeAabbView;
        private float _publishedCascadeRangeNear;
        private float _publishedCascadeRangeFar;
        private XRTexture2DArray? _cascadeShadowMapTexture;
        private XRFrameBuffer[] _cascadeShadowFrameBuffers = [];
        private XRFrameBuffer? _cascadeLayeredShadowFrameBuffer;
        private XRViewport[] _cascadeShadowViewports = [];
        private Transform[] _cascadeShadowTransforms = [];
        private XRCamera[] _cascadeShadowCameras = [];
        private readonly DirectionalCascadeAtlasSlot[] _cascadeAtlasSlots = new DirectionalCascadeAtlasSlot[MaxCascadeRenderCount];
        private DirectionalCascadeAtlasSlot _primaryAtlasSlot;
        private readonly Frustum[] _cascadeSourceFrusta = new Frustum[3];
        private XRMaterial? _shadowAtlasMaterial;
        private XRMaterial? _cascadeGeometryShadowMaterial;

        private static string[] CreateCascadeViewProjectionMatrixUniformNames()
        {
            string[] names = new string[MaxCascadeRenderCount];
            for (int i = 0; i < names.Length; i++)
                names[i] = $"CascadeViewProjectionMatrices[{i}]";
            return names;
        }

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
        /// Selects the render strategy used for the legacy cascaded texture-array shadow map.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Render Mode")]
        [Description("Controls whether cascades render as sequential layer passes, an instanced/layered path, or a geometry-shader layered pass.")]
        public EDirectionalCascadeShadowRenderMode CascadeShadowRenderMode
        {
            get => _cascadeShadowRenderMode;
            set => SetField(ref _cascadeShadowRenderMode, value);
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

        /// <summary>
        /// Legacy non-atlas texture array containing the rendered cascade depth slices.
        /// </summary>
        public XRTexture2DArray? CascadedShadowMapTexture => _cascadeShadowMapTexture;

        /// <summary>
        /// Number of cascades with currently published bounds and cameras.
        /// </summary>
        public int ActiveCascadeCount
        {
            get
            {
                lock (_cascadeDataLock)
                    return _cascadeShadowSlices.Count;
            }
        }

        /// <summary>
        /// Near distance of the camera range used to build the current cascades.
        /// </summary>
        public float CascadeRangeNear
        {
            get
            {
                lock (_cascadeDataLock)
                    return _publishedCascadeRangeNear;
            }
        }

        /// <summary>
        /// Far distance of the camera range used to build the current cascades.
        /// </summary>
        public float CascadeRangeFar
        {
            get
            {
                lock (_cascadeDataLock)
                    return _publishedCascadeRangeFar;
            }
        }

        /// <summary>
        /// Length of the current cascade coverage range in source-camera view space.
        /// </summary>
        public float EffectiveCascadeDistance
        {
            get
            {
                lock (_cascadeDataLock)
                    return MathF.Max(0.0f, _publishedCascadeRangeFar - _publishedCascadeRangeNear);
            }
        }

        /// <summary>
        /// Returns the far split distance for a published cascade, or float.MaxValue when unavailable.
        /// </summary>
        public float GetCascadeSplit(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].SplitFarDistance
                    : float.MaxValue;
        }

        /// <summary>
        /// Returns the world-to-light projection matrix for a published cascade.
        /// </summary>
        public Matrix4x4 GetCascadeMatrix(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].WorldToLightSpaceMatrix
                    : Matrix4x4.Identity;
        }

        /// <summary>
        /// Returns the world-space center of a published cascade bounds.
        /// </summary>
        public Vector3 GetCascadeCenter(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].Center
                    : Vector3.Zero;
        }

        /// <summary>
        /// Returns the world-space half extents of a published cascade bounds.
        /// </summary>
        public Vector3 GetCascadeHalfExtents(int index)
        {
            lock (_cascadeDataLock)
                return index >= 0 && index < _cascadeShadowSlices.Count
                    ? _cascadeShadowSlices[index].HalfExtents
                    : Vector3.Zero;
        }

        /// <summary>
        /// Returns effective receiver-bias settings for a published cascade or a safe fallback.
        /// </summary>
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

            return new CascadeShadowBiasSettings(false, 0.0f, ShadowSlopeBiasTexels, 0.0f, 0.0f);
        }

        /// <summary>
        /// Gets the manual bias override assigned to a cascade index.
        /// </summary>
        public CascadeShadowBiasOverride GetCascadeBiasOverride(int index)
            => index >= 0 && index < _cascadeBiasOverrides.Length
                ? _cascadeBiasOverrides[index]
                : new CascadeShadowBiasOverride(false, 0.0f, 0.0f, 0.0f);

        /// <summary>
        /// Sets a normalized manual bias override for one cascade.
        /// </summary>
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

        /// <summary>
        /// Gets the shadow camera for a cascade resource slot.
        /// </summary>
        public XRCamera? GetCascadeCamera(int index)
            => index >= 0 && index < _cascadeShadowCameras.Length
                ? _cascadeShadowCameras[index]
                : null;

        /// <summary>
        /// Gets the shadow viewport for a cascade resource slot.
        /// </summary>
        public XRViewport? GetCascadeViewport(int index)
            => index >= 0 && index < _cascadeShadowViewports.Length
                ? _cascadeShadowViewports[index]
                : null;

        /// <summary>
        /// Gets the shadow framebuffer for a cascade resource slot.
        /// </summary>
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
            float maxBias = MathF.Max(0.0f, value.BiasMax);
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

        /// <summary>
        /// Copies cascade split, bias, and matrix data into caller-provided stack buffers for uniform upload.
        /// </summary>
        internal void CopyPublishedCascadeUniformData(
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
                        CascadeShadowSlice slice = _cascadeShadowSlices[i];
                        splits[i] = slice.SplitFarDistance;
                        blendWidths[i] = slice.BlendWidth;
                        biasMins[i] = slice.BiasMin;
                        biasMaxes[i] = slice.BiasMax;
                        receiverOffsets[i] = slice.ReceiverOffset;
                        matrices[i] = slice.WorldToLightSpaceMatrix;
                    }
                    else
                    {
                        splits[i] = float.MaxValue;
                        blendWidths[i] = 0.0f;
                        biasMins[i] = 0.0f;
                        biasMaxes[i] = ShadowSlopeBiasTexels;
                        receiverOffsets[i] = 0.0f;
                        matrices[i] = Matrix4x4.Identity;
                    }
                }
            }
        }

        internal void ClearCascadeAtlasSlots()
        {
            lock (_cascadeDataLock)
            {
                _primaryAtlasSlot = default;
                Array.Clear(_cascadeAtlasSlots);
            }
        }

        private static DirectionalCascadeAtlasSlot CreateAtlasSlot(
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane)
        {
            float texelSize = allocation.Resolution > 0u ? 1.0f / allocation.Resolution : 0.0f;

            return new DirectionalCascadeAtlasSlot(
                HasAllocation: true,
                IsResident: allocation.IsResident,
                PageIndex: allocation.PageIndex,
                RecordIndex: recordIndex,
                UvScaleBias: allocation.UvScaleBias,
                NearPlane: nearPlane,
                FarPlane: farPlane,
                TexelSize: texelSize,
                Resolution: allocation.Resolution,
                Fallback: allocation.ActiveFallback,
                PixelRect: allocation.PixelRect,
                InnerPixelRect: allocation.InnerPixelRect,
                LastRenderedFrame: allocation.LastRenderedFrame);
        }

        internal void SetCascadeAtlasSlot(
            int index,
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return;

            lock (_cascadeDataLock)
                _cascadeAtlasSlots[index] = CreateAtlasSlot(allocation, recordIndex, nearPlane, farPlane);
        }

        internal void SetPrimaryAtlasSlot(
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane)
        {
            lock (_cascadeDataLock)
                _primaryAtlasSlot = CreateAtlasSlot(allocation, recordIndex, nearPlane, farPlane);
        }

        /// <summary>
        /// Gets the latest atlas allocation for a cascade when that slot is active.
        /// </summary>
        public bool TryGetCascadeAtlasSlot(int index, out DirectionalCascadeAtlasSlot slot)
        {
            lock (_cascadeDataLock)
            {
                if ((uint)index < (uint)_cascadeShadowSlices.Count && (uint)index < (uint)_cascadeAtlasSlots.Length)
                {
                    slot = _cascadeAtlasSlots[index];
                    return slot.HasAllocation;
                }
            }

            slot = default;
            return false;
        }

        /// <summary>
        /// Gets the latest atlas allocation for the primary non-cascaded directional shadow.
        /// </summary>
        public bool TryGetPrimaryAtlasSlot(out DirectionalCascadeAtlasSlot slot)
        {
            lock (_cascadeDataLock)
            {
                slot = _primaryAtlasSlot;
                return slot.HasAllocation;
            }
        }

        private static bool IsDirectionalAtlasSlotSampleable(in DirectionalCascadeAtlasSlot slot)
            => slot.HasAllocation &&
               slot.IsResident &&
               slot.LastRenderedFrame != 0u &&
               slot.PageIndex >= 0 &&
               slot.Fallback == ShadowFallbackMode.None;

        /// <summary>
        /// Copies cascade atlas metadata into caller-provided buffers for GPU light records.
        /// </summary>
        internal void CopyPublishedCascadeAtlasUniformData(
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
        {
            int copyCount = Math.Min(MaxCascadeRenderCount, Math.Min(packed0.Length, Math.Min(uvScaleBias.Length, depthParams.Length)));

            lock (_cascadeDataLock)
            {
                for (int i = 0; i < copyCount; i++)
                {
                    DirectionalCascadeAtlasSlot slot = i < _cascadeShadowSlices.Count ? _cascadeAtlasSlots[i] : default;
                    bool enabled = IsDirectionalAtlasSlotSampleable(slot);

                    ShadowFallbackMode fallback = slot.Fallback != ShadowFallbackMode.None
                        ? slot.Fallback
                        : ShadowFallbackMode.Lit;
                    int pageIndex = slot.HasAllocation ? slot.PageIndex : -1;
                    int recordIndex = slot.HasAllocation ? slot.RecordIndex : -1;
                    float nearPlane = slot.HasAllocation ? slot.NearPlane : NearZ;
                    float farPlane = slot.HasAllocation ? slot.FarPlane : 1.0f;

                    packed0[i] = new IVector4(enabled ? 1 : 0, pageIndex, (int)fallback, recordIndex);
                    uvScaleBias[i] = enabled ? slot.UvScaleBias : Vector4.Zero;
                    depthParams[i] = new Vector4(nearPlane, MathF.Max(farPlane, nearPlane + 0.001f), slot.TexelSize, 0.0f);
                }
            }
        }

        /// <summary>
        /// Copies either cascade or primary directional atlas metadata, depending on the active shadow path.
        /// </summary>
        internal void CopyPublishedDirectionalAtlasUniformData(
            bool useCascades,
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
        {
            if (useCascades)
            {
                CopyPublishedCascadeAtlasUniformData(packed0, uvScaleBias, depthParams);
                return;
            }

            int copyCount = Math.Min(MaxCascadeRenderCount, Math.Min(packed0.Length, Math.Min(uvScaleBias.Length, depthParams.Length)));

            lock (_cascadeDataLock)
            {
                for (int i = 0; i < copyCount; i++)
                {
                    DirectionalCascadeAtlasSlot slot = i == 0 ? _primaryAtlasSlot : default;
                    bool enabled = IsDirectionalAtlasSlotSampleable(slot);

                    ShadowFallbackMode fallback = slot.HasAllocation
                        ? (slot.Fallback != ShadowFallbackMode.None ? slot.Fallback : ShadowFallbackMode.Lit)
                        : ShadowFallbackMode.Legacy;
                    int pageIndex = slot.HasAllocation ? slot.PageIndex : -1;
                    int recordIndex = slot.HasAllocation ? slot.RecordIndex : -1;
                    float nearPlane = slot.HasAllocation ? slot.NearPlane : NearZ;
                    float farPlane = slot.HasAllocation ? slot.FarPlane : 1.0f;

                    packed0[i] = new IVector4(enabled ? 1 : 0, pageIndex, (int)fallback, recordIndex);
                    uvScaleBias[i] = enabled ? slot.UvScaleBias : Vector4.Zero;
                    depthParams[i] = new Vector4(nearPlane, MathF.Max(farPlane, nearPlane + 0.001f), slot.TexelSize, 0.0f);
                }
            }
        }

        private static Vector3 EvaluateCascadeCorner(Vector3 nearCorner, Vector3 farCorner, float t, Matrix4x4 worldToLight)
            => Vector3.Transform(Vector3.Lerp(nearCorner, farCorner, t), worldToLight);

        private static void IncludeCascadeCorner(Vector3 nearCorner, Vector3 farCorner, float t, Matrix4x4 worldToLight, ref Vector3 min, ref Vector3 max)
        {
            Vector3 point = EvaluateCascadeCorner(nearCorner, farCorner, t, worldToLight);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static void IncludeCascadeBoundsInLightSpace(Frustum cameraFrustum, float nearT, float farT, Matrix4x4 worldToLight, ref Vector3 min, ref Vector3 max)
        {
            IncludeCascadeCorner(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, nearT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, farT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, farT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, farT, worldToLight, ref min, ref max);
            IncludeCascadeCorner(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, farT, worldToLight, ref min, ref max);
        }

        private static void ExpandCascadeSphereRadius(Frustum cameraFrustum, float nearT, float farT, Matrix4x4 worldToLight, Vector3 centerLS, ref float radius)
        {
            IncludeCascadeSphereCorner(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, nearT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, nearT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, nearT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, nearT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, farT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, farT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, farT, worldToLight, centerLS, ref radius);
            IncludeCascadeSphereCorner(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, farT, worldToLight, centerLS, ref radius);
        }

        private static void IncludeCascadeSphereCorner(
            Vector3 nearCorner,
            Vector3 farCorner,
            float t,
            Matrix4x4 worldToLight,
            Vector3 centerLS,
            ref float radius)
        {
            Vector3 point = EvaluateCascadeCorner(nearCorner, farCorner, t, worldToLight);
            Vector2 delta = new(point.X - centerLS.X, point.Y - centerLS.Y);
            radius = MathF.Max(radius, delta.Length());
        }

        private static void ApplySphereFit(ReadOnlySpan<Frustum> cameraFrusta, float nearT, float farT, Matrix4x4 worldToLight, ref Vector3 min, ref Vector3 max)
        {
            Vector3 centerLS = (min + max) * 0.5f;
            float radius = 1e-3f;
            for (int i = 0; i < cameraFrusta.Length; i++)
                ExpandCascadeSphereRadius(cameraFrusta[i], nearT, farT, worldToLight, centerLS, ref radius);

            min.X = centerLS.X - radius;
            max.X = centerLS.X + radius;
            min.Y = centerLS.Y - radius;
            max.Y = centerLS.Y + radius;
        }

        private static void SnapCascadeCenterToTexels(ref Vector3 centerLS, Vector3 halfExtents, uint resolution)
        {
            float texelX = MathF.Max(halfExtents.X * 2.0f / MathF.Max(1.0f, resolution), 1e-6f);
            float texelY = MathF.Max(halfExtents.Y * 2.0f / MathF.Max(1.0f, resolution), 1e-6f);
            centerLS.X = MathF.Round(centerLS.X / texelX) * texelX;
            centerLS.Y = MathF.Round(centerLS.Y / texelY) * texelY;
        }

        private static void GetCascadeBoundsInLightSpace(ReadOnlySpan<Frustum> cameraFrusta, float nearT, float farT, Matrix4x4 worldToLight, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);

            for (int i = 0; i < cameraFrusta.Length; i++)
                IncludeCascadeBoundsInLightSpace(cameraFrusta[i], nearT, farT, worldToLight, ref min, ref max);

            ApplySphereFit(cameraFrusta, nearT, farT, worldToLight, ref min, ref max);
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
            ShadowMapTextureFormat shadowFormat = GetShadowMapTextureFormat(ShadowMapStorageFormat);

            bool recreateTexture = _cascadeShadowMapTexture is null ||
                _cascadeShadowMapTexture.Depth != (uint)requiredCascades ||
                _cascadeShadowMapTexture.Width != width ||
                _cascadeShadowMapTexture.Height != height ||
                _cascadeShadowMapTexture.SizedInternalFormat != shadowFormat.SizedInternalFormat;

            if (recreateTexture)
            {
                _cascadeShadowMapTexture?.Destroy();
                _cascadeShadowMapTexture = XRTexture2DArray.CreateFrameBufferTexture(
                    (uint)requiredCascades,
                    width,
                    height,
                    shadowFormat.InternalFormat,
                    shadowFormat.PixelFormat,
                    shadowFormat.PixelType,
                    EFrameBufferAttachment.DepthAttachment);
                _cascadeShadowMapTexture.SamplerName = "ShadowMapArray";
            }

            if (recreateTexture || _cascadeLayeredShadowFrameBuffer is null)
            {
                _cascadeLayeredShadowFrameBuffer ??= new XRFrameBuffer();
                _cascadeLayeredShadowFrameBuffer.SetRenderTargets(
                    (_cascadeShadowMapTexture!, EFrameBufferAttachment.DepthAttachment, 0, -1));
            }

            if (_cascadeShadowFrameBuffers.Length == requiredCascades && !recreateTexture)
                return;

            IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();

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
            _cascadeLayeredShadowFrameBuffer?.Destroy();
            _cascadeLayeredShadowFrameBuffer = null;
            if (_cascadeGeometryShadowMaterial is not null)
                _cascadeGeometryShadowMaterial.SettingShadowUniforms -= SetShadowMapUniforms;
            _cascadeGeometryShadowMaterial?.Destroy();
            _cascadeGeometryShadowMaterial = null;
            _cascadeShadowFrameBuffers = [];
            _cascadeShadowViewports = [];
            _cascadeShadowTransforms = [];
            _cascadeShadowCameras = [];
        }

        private static void UpdateCascadeShadowCamera(Transform transform, XRCamera camera, Vector3 center, Vector3 halfExtents, Quaternion orientation, Vector3 lightDirection, float nearZ)
        {
            transform.Translation = center - lightDirection * halfExtents.Z;
            transform.Rotation = orientation;

            // Cascade cameras are rebuilt during shadow collection, after the normal
            // world-to-render matrix handoff for the frame. Publish their render
            // matrices immediately so culling, rendering, and shader uniforms all
            // describe the same cascade for this frame.
            transform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

            float width = MathF.Max(halfExtents.X * 2.0f, 1e-3f);
            float height = MathF.Max(halfExtents.Y * 2.0f, 1e-3f);
            float depth = MathF.Max(halfExtents.Z * 2.0f, nearZ + 1e-3f);
            float farZ = MathF.Max(depth, nearZ + 1e-3f);
            if (camera.Parameters is not XROrthographicCameraParameters ortho)
            {
                ortho = new XROrthographicCameraParameters(width, height, nearZ, farZ);
                ortho.InheritAspectRatio = false;
                ortho.SetOriginPercentages(0.5f, 0.5f);
                camera.Parameters = ortho;
            }
            else
            {
                ortho.Resize(width, height); // Bypass InheritAspectRatio coupling
                ortho.NearZ = nearZ;
                ortho.FarZ = farZ;
            }
        }

        private CascadeShadowBiasSettings ResolveCascadeBiasSettings(
            int cascadeIndex,
            Vector3 halfExtents,
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

            float cascadeDepthRange = MathF.Max(ShadowBiasDepthRangeEpsilon, halfExtents.Z * 2.0f);
            float biasMin = texelWorldSize * ShadowDepthBiasTexels / cascadeDepthRange;
            float biasMax = ShadowSlopeBiasTexels;
            float receiverOffset = texelWorldSize * ShadowNormalBiasTexels;

            return new CascadeShadowBiasSettings(false, biasMin, biasMax, receiverOffset, texelWorldSize);
        }

        private int CopyCascadeSourceFrusta(XRCamera primaryCamera, Span<Frustum> destination)
        {
            if (destination.Length <= 0)
                return 0;

            int count = 0;
            destination[count++] = primaryCamera.WorldFrustum();

            if (!Engine.VRState.IsInVR)
                return count;

            IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
            AddEyeFrustum(Engine.VRState.LeftEyeViewport, primaryCamera, world, destination, ref count);
            AddEyeFrustum(Engine.VRState.RightEyeViewport, primaryCamera, world, destination, ref count);
            return count;
        }

        private static void AddEyeFrustum(XRViewport? viewport, XRCamera primaryCamera, IRuntimeRenderWorld? world, Span<Frustum> destination, ref int count)
        {
            if (count >= destination.Length ||
                viewport is null ||
                !ReferenceEquals(viewport.World, world) ||
                viewport.Suppress3DSceneRendering ||
                viewport.ActiveCamera is not XRCamera camera ||
                ReferenceEquals(camera, primaryCamera))
            {
                return;
            }

            destination[count++] = camera.WorldFrustum();
        }

        private uint GetCascadeFitResolution(int cascadeIndex, XRTexture2DArray cascadeTexture)
        {
            if (!Engine.Rendering.Settings.UseDirectionalShadowAtlas)
                return Math.Max(cascadeTexture.Width, cascadeTexture.Height);

            lock (_cascadeDataLock)
            {
                if ((uint)cascadeIndex < (uint)_cascadeAtlasSlots.Length &&
                    _cascadeAtlasSlots[cascadeIndex].Resolution > 0u)
                {
                    return _cascadeAtlasSlots[cascadeIndex].Resolution;
                }
            }

            uint requested = Math.Max(ShadowMapResolutionWidth, ShadowMapResolutionHeight);
            return ShadowAtlasManager.NormalizeTileResolution(
                requested,
                Engine.Rendering.Settings.MinShadowAtlasTileResolution,
                Engine.Rendering.Settings.MaxShadowAtlasTileResolution,
                Engine.Rendering.Settings.ShadowAtlasPageSize);
        }

        /// <summary>
        /// Clears all published cascade bounds, slices, and atlas slot metadata.
        /// </summary>
        internal void ClearCascadeShadows()
        {
            lock (_cascadeDataLock)
            {
                _cascadeAabbs.Clear();
                _cascadeShadowSlices.Clear();
                _primaryAtlasSlot = default;
                Array.Clear(_cascadeAtlasSlots);
                _publishedCascadeRangeNear = 0.0f;
                _publishedCascadeRangeFar = 0.0f;
            }
        }

        /// <summary>
        /// Rebuilds cascade cameras and published bounds from the active player camera for this frame.
        /// </summary>
        internal void UpdateCascadeShadows(XRCamera playerCamera)
        {
            if (!CastsShadows || !EnableCascadedShadows || ShadowCamera is null)
            {
                LogCascadeClearReason("disabled-or-missing-shadow-camera");
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
                LogCascadeClearReason($"invalid-resources texture={cascadeTexture is not null} cameras={camerasSnapshot.Length} transforms={transformsSnapshot.Length}");
                ClearCascadeShadows();
                return;
            }

            Span<Frustum> sourceFrusta = _cascadeSourceFrusta;
            int sourceFrustumCount = CopyCascadeSourceFrusta(playerCamera, sourceFrusta);
            if (sourceFrustumCount <= 0)
            {
                LogCascadeClearReason("no-source-frustum");
                ClearCascadeShadows();
                return;
            }

            BuildLightSpaceBasis(out Matrix4x4 worldToLight, out Matrix4x4 lightToWorld, out Quaternion lightRotation, out Vector3 lightDirection);
            ReadOnlySpan<Frustum> cascadeFrusta = sourceFrusta[..sourceFrustumCount];

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

            // Shadow caster capture depth - how far behind each cascade slice (in light
            // space) we extend to include potential casters. Scale.Z is used because it
            // already represents the user's intended shadow volume depth and 24-bit depth
            // precision is adequate even at large values (e.g. 900 to ~17K levels/unit).
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

                    GetCascadeBoundsInLightSpace(cascadeFrusta, nearT, farT, worldToLight, out Vector3 min, out Vector3 max);

                    // With the -Z forward convention, positive light-space Z points toward
                    // the light source. Extend max.Z to capture shadow casters upstream.
                    max.Z += shadowDepth;

                    Vector3 padding = Vector3.Max((max - min) * (CascadeBoundsPadding * 0.5f), new Vector3(1e-3f));
                    min -= padding;
                    max += padding;

                    if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
                        continue;

                    Vector3 halfExtents = Vector3.Max((max - min) * 0.5f, new Vector3(1e-3f, 1e-3f, NearZ + 1e-3f));
                    Vector3 centerLS = (min + max) * 0.5f;
                    uint fitResolution = GetCascadeFitResolution(resourceSlot, cascadeTexture);
                    SnapCascadeCenterToTexels(ref centerLS, halfExtents, fitResolution);
                    Vector3 centerWS = Vector3.Transform(centerLS, lightToWorld);

                    UpdateCascadeShadowCamera(transformsSnapshot[resourceSlot], camerasSnapshot[resourceSlot], centerWS, halfExtents, lightRotation, lightDirection, NearZ);

                    Matrix4x4 cascadeView = camerasSnapshot[resourceSlot].Transform.InverseRenderMatrix;
                    Matrix4x4 cascadeProj = camerasSnapshot[resourceSlot].ProjectionMatrix;
                    Matrix4x4 viewProj = cascadeView * cascadeProj;
                    CascadeShadowBiasSettings biasSettings = ResolveCascadeBiasSettings(
                        cascadeIndex,
                        halfExtents,
                        fitResolution,
                        fitResolution,
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

                LogCascadeUpdate(playerCamera, resourceSlot, cameraNear, effectiveCascadeFar, totalDepth, nextShadowSlices.AsSpan(0, resourceSlot));
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
                _cascadeShadowViewports[i].WorldInstanceOverride = WorldAs<IRuntimeRenderWorld>();
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
            IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
            bool needsPrimary = !EnableCascadedShadows ||
                CascadedShadowMapTexture is null ||
                ActiveCascadeCount <= 0 ||
                world is null ||
                world.Lights.NeedsPrimaryDirectionalShadowMap();

            return needsPrimary &&
                (ShadowMap is not null || Engine.Rendering.Settings.UseDirectionalShadowAtlas);
        }

        public override void CollectVisibleItems()
        {
            if (!CastsShadows)
                return;

            if (ShouldCollectPrimaryShadowViewport())
                PrimaryShadowViewport.CollectVisible(false);

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            int cascadeCount = GetPublishedCascadeViewportCount(cascadeShadowViewports);
            if (ShouldRenderCascadesWithGeometryShader(cascadeCount))
            {
                XRViewport viewport = cascadeShadowViewports[0];
                bool previousCullWithFrustum = viewport.CullWithFrustum;
                viewport.CullWithFrustum = false;
                try
                {
                    viewport.CollectVisible(false);
                }
                finally
                {
                    viewport.CullWithFrustum = previousCullWithFrustum;
                }
            }
            else
            {
                LogCascadeRenderModeFallbackIfNeeded(cascadeCount);
                for (int i = 0; i < cascadeCount; i++)
                    cascadeShadowViewports[i].CollectVisible(false, collectionVolumeOverride: GetPublishedCascadeCullVolume(i));
            }
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows)
                return;

            if (ShouldCollectPrimaryShadowViewport())
                PrimaryShadowViewport.SwapBuffers();

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            int cascadeCount = GetPublishedCascadeViewportCount(cascadeShadowViewports);
            if (ShouldRenderCascadesWithGeometryShader(cascadeCount))
            {
                cascadeShadowViewports[0].SwapBuffers();
            }
            else
            {
                LogCascadeRenderModeFallbackIfNeeded(cascadeCount);
                for (int i = 0; i < cascadeCount; i++)
                    cascadeShadowViewports[i].SwapBuffers();
            }

            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }

        private XRMaterial ShadowAtlasMaterial => _shadowAtlasMaterial ??= CreateShadowAtlasMaterial();

        private static XRMaterial CreateShadowAtlasMaterial()
        {
            // Cascade cameras are orthographic, so gl_FragCoord.z is already linear in cascade clip space.
            XRMaterial mat = new(new XRShader(EShaderType.Fragment, ShaderHelper.Frag_DepthOutput));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            return mat;
        }

        private XRMaterial CascadeGeometryShadowMaterial
            => _cascadeGeometryShadowMaterial ??= CreateCascadeGeometryShadowMaterial();

        private XRMaterial CreateCascadeGeometryShadowMaterial()
        {
            XRMaterial mat = new(
                XRShader.EngineShader("DirectionalCascadeShadowDepth.gs", EShaderType.Geometry),
                new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        /// <summary>
        /// Renders a cascade shadow camera into a reserved shadow-atlas tile.
        /// </summary>
        internal bool RenderCascadeShadowAtlasTile(int cascadeIndex, XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
        {
            if (!CastsShadows ||
                !EnableCascadedShadows ||
                World is null ||
                renderRect.Width <= 0 ||
                renderRect.Height <= 0)
                return false;

            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            int cascadeCount = GetPublishedCascadeViewportCount(cascadeShadowViewports);
            if ((uint)cascadeIndex >= (uint)cascadeCount)
                return false;

            XRViewport viewport = cascadeShadowViewports[cascadeIndex];
            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            shadowPipeline.PreserveExistingRenderArea = true;
            try
            {
                var state = viewport.RenderPipelineInstance.RenderState;
                using var renderArea = state.PushRenderArea(renderRect);
                using var cropArea = state.PushCropArea(renderRect);
                viewport.Render(atlasFbo, null, null, true, ShadowAtlasMaterial);
            }
            finally
            {
                shadowPipeline.PreserveExistingRenderArea = previousPreserveArea;
            }

            LogDirectionalAtlasTileRender("cascade", cascadeIndex, renderRect, collectVisibleNow, viewport.Camera);
            return true;
        }

        /// <summary>
        /// Renders the primary directional shadow camera into a reserved shadow-atlas tile.
        /// </summary>
        internal bool RenderPrimaryShadowAtlasTile(XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
        {
            if (!CastsShadows ||
                ShadowCamera is null ||
                World is null ||
                renderRect.Width <= 0 ||
                renderRect.Height <= 0)
                return false;

            XRViewport viewport = PrimaryShadowViewport;
            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            shadowPipeline.PreserveExistingRenderArea = true;
            try
            {
                var state = viewport.RenderPipelineInstance.RenderState;
                using var renderArea = state.PushRenderArea(renderRect);
                using var cropArea = state.PushCropArea(renderRect);
                viewport.Render(atlasFbo, null, null, true, ShadowAtlasMaterial);
            }
            finally
            {
                shadowPipeline.PreserveExistingRenderArea = previousPreserveArea;
            }

            LogDirectionalAtlasTileRender("primary", 0, renderRect, collectVisibleNow, viewport.Camera);
            return true;
        }

        public override void RenderShadowMap(bool collectVisibleNow = false)
            => RenderShadowMap(collectVisibleNow, renderCascades: true);

        internal void RenderShadowMap(bool collectVisibleNow, bool renderCascades)
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
            XRViewport[] cascadeShadowViewports = _cascadeShadowViewports;
            XRFrameBuffer[] cascadeShadowFrameBuffers = _cascadeShadowFrameBuffers;
            int cascadeCount = GetPublishedCascadeRenderCount(cascadeShadowViewports, cascadeShadowFrameBuffers);

            LogLegacyDirectionalShadowRender(renderCascades, shadowMap is not null, shadowMaterial is not null, cascadeCount);

            if (ShouldCollectPrimaryShadowViewport() && shadowMap is not null && shadowMaterial is not null)
                PrimaryShadowViewport.Render(shadowMap, null, null, true, shadowMaterial);

            if (shadowMaterial is null)
                return;

            if (renderCascades)
                RenderCascadeShadowMaps(cascadeShadowViewports, cascadeShadowFrameBuffers, cascadeCount, shadowMaterial);
        }

        private bool ShouldRenderCascadesWithGeometryShader(int cascadeCount)
            => cascadeCount > 0 &&
               _cascadeShadowRenderMode == EDirectionalCascadeShadowRenderMode.GeometryShader &&
               _cascadeLayeredShadowFrameBuffer is not null;

        private void RenderCascadeShadowMaps(
            XRViewport[] cascadeShadowViewports,
            XRFrameBuffer[] cascadeShadowFrameBuffers,
            int cascadeCount,
            XRMaterial sequentialShadowMaterial)
        {
            if (ShouldRenderCascadesWithGeometryShader(cascadeCount))
            {
                cascadeShadowViewports[0].Render(
                    _cascadeLayeredShadowFrameBuffer!,
                    null,
                    null,
                    true,
                    CascadeGeometryShadowMaterial);
                return;
            }

            LogCascadeRenderModeFallbackIfNeeded(cascadeCount);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].Render(cascadeShadowFrameBuffers[i], null, null, true, sequentialShadowMaterial);
        }

        private void LogCascadeRenderModeFallbackIfNeeded(int cascadeCount)
        {
            if (_cascadeShadowRenderMode != EDirectionalCascadeShadowRenderMode.InstancedLayered || cascadeCount <= 0)
                return;

            Debug.RenderingWarningEvery(
                $"DirectionalCascadeRenderModeFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(2.0),
                "[DirectionalShadowAudit] Instanced/layered directional cascades are not implemented yet; rendering '{0}' sequentially.",
                SceneNode?.Name ?? Name ?? GetType().Name);
        }

        private void LogCascadeClearReason(string reason)
        {
            if (!Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.CascadeClear.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Out(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][CascadeClear] frame={0} light='{1}' reason={2} casts={3} cascadesEnabled={4} shadowCamera={5} useDirAtlas={6}",
                Engine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                reason,
                CastsShadows,
                EnableCascadedShadows,
                ShadowCamera is not null,
                Engine.Rendering.Settings.UseDirectionalShadowAtlas);
        }

        private void LogCascadeUpdate(
            XRCamera sourceCamera,
            int cascadeCount,
            float cameraNear,
            float effectiveCascadeFar,
            float totalDepth,
            ReadOnlySpan<CascadeShadowSlice> slices)
        {
            if (!Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.CascadeUpdate.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Vector3 sourcePosition = sourceCamera.Transform.RenderTranslation;
            Debug.Out(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][CascadeUpdate] frame={0} light='{1}' sourceCamera={2} sourcePos={3} sourceNear={4:F3} sourceFar={5:F3} sourceShadowMax={6:F3} rangeNear={7:F3} rangeFar={8:F3} totalDepth={9:F3} activeCascades={10} requestedCascades={11} atlasSetting={12} cascadeTex={13}",
                Engine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                sourceCamera.GetHashCode(),
                FormatVector(sourcePosition),
                sourceCamera.NearZ,
                sourceCamera.FarZ,
                sourceCamera.ShadowCollectMaxDistance,
                cameraNear,
                effectiveCascadeFar,
                totalDepth,
                cascadeCount,
                CascadeCount,
                Engine.Rendering.Settings.UseDirectionalShadowAtlas,
                _cascadeShadowMapTexture is not null);

            int detailCount = Math.Min(cascadeCount, Math.Min(4, slices.Length));
            for (int i = 0; i < detailCount; i++)
            {
                CascadeShadowSlice slice = slices[i];
                Debug.Out(
                    EOutputVerbosity.Normal,
                    false,
                    "[DirectionalShadowAudit][CascadeSlice] frame={0} light='{1}' slot={2} cascadeIndex={3} splitFar={4:F3} blendWidth={5:F3} texelWorld={6:F6} center={7} halfExtents={8} biasMin={9:E3} biasMax={10:F3} receiverOffset={11:F6}",
                    Engine.Rendering.State.RenderFrameId,
                    SceneNode?.Name ?? Name ?? GetType().Name,
                    i,
                    slice.CascadeIndex,
                    slice.SplitFarDistance,
                    slice.BlendWidth,
                    slice.TexelWorldSize,
                    FormatVector(slice.Center),
                    FormatVector(slice.HalfExtents),
                    slice.BiasMin,
                    slice.BiasMax,
                    slice.ReceiverOffset);
            }
        }

        private void LogDirectionalAtlasTileRender(
            string projection,
            int cascadeIndex,
            BoundingRectangle renderRect,
            bool collectVisibleNow,
            XRCamera? camera)
        {
            if (!Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasTileRender.{GetHashCode()}.{projection}.{cascadeIndex}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Out(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][AtlasTileRender] frame={0} light='{1}' projection={2} cascadeOrFace={3} rect={4},{5},{6}x{7} collectVisibleNow={8} camera={9} splitFar={10:F3}",
                Engine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                projection,
                cascadeIndex,
                renderRect.X,
                renderRect.Y,
                renderRect.Width,
                renderRect.Height,
                collectVisibleNow,
                camera?.GetHashCode().ToString() ?? "<null>",
                projection == "cascade" ? GetCascadeSplit(cascadeIndex) : 0.0f);
        }

        private void LogLegacyDirectionalShadowRender(bool renderCascades, bool hasShadowMap, bool hasShadowMaterial, int cascadeCount)
        {
            if (!Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.LegacyRender.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Out(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][LegacyRender] frame={0} light='{1}' useDirAtlas={2} renderCascades={3} hasShadowMap={4} hasShadowMaterial={5} cascadeRenderCount={6} activeCascades={7} cascadeTex={8}",
                Engine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                Engine.Rendering.Settings.UseDirectionalShadowAtlas,
                renderCascades,
                hasShadowMap,
                hasShadowMaterial,
                cascadeCount,
                ActiveCascadeCount,
                _cascadeShadowMapTexture is not null);
        }

        private static string FormatVector(Vector3 value)
            => $"({value.X:F2},{value.Y:F2},{value.Z:F2})";
    }
}
