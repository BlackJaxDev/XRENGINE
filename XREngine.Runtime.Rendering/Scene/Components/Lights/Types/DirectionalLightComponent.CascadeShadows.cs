using System;
using System.Buffers;
using System.Collections;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
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
        private sealed class CascadeAabbView(DirectionalLightComponent owner, ShadowRequestSource source) : IReadOnlyList<CascadedShadowAabb>
        {
            private readonly DirectionalLightComponent _owner = owner;
            private readonly ShadowRequestSource _source = source;

            public int Count
            {
                get
                {
                    lock (_owner._cascadeDataLock)
                        return _owner.GetCascadeSourceState(_source).Aabbs.Count;
                }
            }

            public CascadedShadowAabb this[int index]
            {
                get
                {
                    lock (_owner._cascadeDataLock)
                        return _owner.GetCascadeSourceState(_source).Aabbs[index];
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
                        List<CascadedShadowAabb> aabbs = _owner.GetCascadeSourceState(_source).Aabbs;
                        if (index >= aabbs.Count)
                            yield break;

                        item = aabbs[index];
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

        private enum DirectionalCascadeShadowFallbackReason
        {
            None = 0,
            SequentialRequested = 1,
            NoActiveCascades = 2,
            MissingLayeredFramebuffer = 3,
            MissingCascadeTextureArray = 4,
            UnsupportedLayeredFramebuffer = 5,
            UnsupportedGeometryShader = 6,
            UnsupportedVertexStageLayerWrites = 7,
            UnsupportedViewportArray = 8,
            MissingGroupedAtlasAllocation = 9,
            UnsupportedViewportScissorArray = 10,
            UnsupportedVertexStageViewportIndexWrites = 11,
            UnsupportedGeometryStageViewportIndexWrites = 12,
            VulkanLayeredRenderingDisabled = 13,
            VulkanCascadeAtlasGroupedRenderingDisabled = 14,
            VulkanCascadeRenderingDisabled = 15,
        }

        private enum DirectionalCascadeShadowBackend
        {
            LegacyTextureArray = 0,
            AtlasPage = 1,
        }

        private readonly struct DirectionalCascadeShadowRenderPlan
        {
            public required EDirectionalCascadeShadowRenderMode RequestedMode { get; init; }
            public required EDirectionalCascadeShadowRenderMode SelectedMode { get; init; }
            public required DirectionalCascadeShadowBackend Backend { get; init; }
            public required int ActiveCascadeCount { get; init; }
            public required XRFrameBuffer? LayeredFrameBuffer { get; init; }
            public required XRTexture2DArray? CascadeTextureArray { get; init; }
            public required DirectionalCascadeShadowFallbackReason FallbackReason { get; init; }

            public bool IsLayered => SelectedMode is EDirectionalCascadeShadowRenderMode.GeometryShader or EDirectionalCascadeShadowRenderMode.InstancedLayered;
            public bool IsInstancedLayered => SelectedMode == EDirectionalCascadeShadowRenderMode.InstancedLayered;
            public bool IsAtlasPage => Backend == DirectionalCascadeShadowBackend.AtlasPage;
        }

        private sealed class DirectionalCascadeSourceState(ShadowRequestSource source)
        {
            public readonly ShadowRequestSource Source = source;
            public readonly List<CascadedShadowAabb> Aabbs = new(4);
            public readonly List<CascadeShadowSlice> Slices = new(MaxCascadeRenderCount);
            public DirectionalCascadeAtlasSlot[] AtlasSlots = new DirectionalCascadeAtlasSlot[MaxCascadeRenderCount];
            public DirectionalCascadeAtlasSlot[] PendingAtlasSlots = new DirectionalCascadeAtlasSlot[MaxCascadeRenderCount];
            public readonly DirectionalCascadeAtlasSlot[] PreviousAtlasSlots = new DirectionalCascadeAtlasSlot[MaxCascadeRenderCount];
            public readonly DirectionalCascadeSampleState[] RenderedSamples = new DirectionalCascadeSampleState[MaxCascadeRenderCount];
            public readonly ulong[] AtlasRequestContentHashes = new ulong[MaxCascadeRenderCount];
            public readonly ulong[] PreviousAtlasRequestContentHashes = new ulong[MaxCascadeRenderCount];
            public readonly int[] StableAtlasRequestFrameCounts = new int[MaxCascadeRenderCount];
            public readonly ulong[] AtlasCollectedContentHashes = new ulong[MaxCascadeRenderCount];
            public readonly bool[] AtlasCascadeRenderRequested = new bool[MaxCascadeRenderCount];
            public readonly bool[] AtlasCascadeCollectVisibleNeeded = new bool[MaxCascadeRenderCount];
            public readonly bool[] AtlasCascadeSwapNeeded = new bool[MaxCascadeRenderCount];
            public readonly bool[] AtlasCascadeVisibleSetCached = new bool[MaxCascadeRenderCount];
            public float RangeNear;
            public float RangeFar;
            public XRTexture2DArray? ShadowMapTexture;
            public XRTexture2DArray? RasterDepthTexture;
            public XRFrameBuffer[] FrameBuffers = [];
            public XRFrameBuffer? LayeredFrameBuffer;
            public XRViewport[] Viewports = [];
            public Transform[] Transforms = [];
            public XRCamera[] Cameras = [];
            public ulong ContentRevision;
            public ulong LegacyRenderedContentRevision;
            public int LegacyRenderedCascadeCount;
            public XRTexture2DArray? LegacyRenderedReceiverTexture;
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
        [CookedBinaryReflectionOnly]
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
            ShadowRequestKey Key,
            int AtlasId,
            int PageIndex,
            int RecordIndex,
            Vector4 UvScaleBias,
            float NearPlane,
            float FarPlane,
            float TexelSize,
            float ResolutionScale,
            uint Resolution,
            ShadowFallbackMode Fallback,
            BoundingRectangle PixelRect,
            BoundingRectangle InnerPixelRect,
            ulong LastRenderedFrame,
            ulong ContentVersion,
            bool HasCascadeUniformData,
            float SplitFarDistance,
            float BlendWidth,
            float BiasMin,
            float BiasMax,
            float ReceiverOffset,
            Matrix4x4 WorldToLightSpaceMatrix);

        private const int MaxCascadeRenderCount = 8;
        private const int MaxCascadeSourceFrustumCount = 8;
        private const float CascadeBoundsPadding = 0.05f;
        private const float ShadowBiasDepthRangeEpsilon = 1e-4f;
        private static readonly string[] CascadeViewProjectionMatrixUniformNames = CreateCascadeViewProjectionMatrixUniformNames();

        private int _cascadeCount = 4;
        private float[] _cascadePercentages = [0.1f, 0.2f, 0.3f, 0.4f];
        private CascadeShadowBiasOverride[] _cascadeBiasOverrides = CreateDefaultCascadeBiasOverrides(4);
        private float _cascadeOverlapPercent = 0.1f;
        private EDirectionalCascadeShadowRenderMode _cascadeShadowRenderMode = EDirectionalCascadeShadowRenderMode.Auto;
        private bool _debugCascadeColors;
        private readonly object _cascadeDataLock = new();
        private readonly DirectionalCascadeSourceState _desktopCascadeState = new(ShadowRequestSource.Desktop);
        private readonly DirectionalCascadeSourceState _hmdCascadeState = new(ShadowRequestSource.Hmd);
        private readonly CascadeAabbView _cascadeAabbView;
        private readonly BoundingRectangle[] _groupedAtlasClearRects = new BoundingRectangle[MaxCascadeRenderCount];
        private DirectionalCascadeAtlasSlot _primaryAtlasSlot;
        private DirectionalCascadeAtlasSlot _previousPrimaryAtlasSlot;
        private DirectionalCascadeAtlasSlot _pendingPrimaryAtlasSlot;
        private bool _pendingPrimaryAtlasSlotWritten;
        private bool _directionalAtlasSlotPublishInProgress;
        private readonly Frustum[] _cascadeSourceFrusta = new Frustum[MaxCascadeSourceFrustumCount];
        private readonly XRCamera?[] _cascadeSourceCameras = new XRCamera?[MaxCascadeSourceFrustumCount];
        private XRMaterial? _shadowAtlasMaterial;
        private XRMaterial? _cascadeGeometryShadowMaterial;
        private XRMaterial? _cascadeInstancedShadowMaterial;
        private XRMaterial? _cascadeAtlasGeometryShadowMaterial;
        private XRMaterial? _cascadeAtlasInstancedShadowMaterial;
        private EDirectionalCascadeShadowRenderMode _effectiveCascadeShadowRenderMode = EDirectionalCascadeShadowRenderMode.Sequential;
        private DirectionalCascadeShadowBackend _effectiveCascadeShadowBackend = DirectionalCascadeShadowBackend.LegacyTextureArray;
        private DirectionalCascadeShadowFallbackReason _cascadeShadowRenderFallbackReason = DirectionalCascadeShadowFallbackReason.None;

        private static string[] CreateCascadeViewProjectionMatrixUniformNames()
        {
            string[] names = new string[MaxCascadeRenderCount];
            for (int i = 0; i < names.Length; i++)
                names[i] = $"CascadeViewProjectionMatrices[{i}]";
            return names;
        }

        private DirectionalCascadeSourceState GetCascadeSourceState(ShadowRequestSource source)
            => source == ShadowRequestSource.Hmd
                ? _hmdCascadeState
                : _desktopCascadeState;

        internal static ShadowRequestSource GetCascadeSourceForCamera(XRCamera? camera)
            => IsHmdEyeCameraForDirectionalCascades(camera)
                ? ShadowRequestSource.Hmd
                : ShadowRequestSource.Desktop;

        private ShadowRequestSource ResolveCascadeSourceForCamera(XRCamera? camera)
        {
            if (camera is not null)
            {
                if (ContainsCascadeCamera(_desktopCascadeState, camera))
                    return ShadowRequestSource.Desktop;
                if (ContainsCascadeCamera(_hmdCascadeState, camera))
                    return ShadowRequestSource.Hmd;
            }

            return GetCascadeSourceForCamera(camera);
        }

        private ShadowRequestSource ResolveCurrentCascadeRenderSource()
            => ResolveCascadeSourceForCamera(RuntimeEngine.Rendering.State.RenderingCamera);

        private static bool ContainsCascadeCamera(DirectionalCascadeSourceState state, XRCamera camera)
        {
            XRCamera[] cameras = state.Cameras;
            for (int i = 0; i < cameras.Length; i++)
                if (ReferenceEquals(cameras[i], camera))
                    return true;

            return false;
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
        /// Selects the render strategy used for legacy texture-array cascades and atlas page cascade groups.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Render Mode")]
        [Description("Controls whether cascades render as sequential passes, an instanced grouped/layered path, or a geometry-shader grouped/layered pass.")]
        public EDirectionalCascadeShadowRenderMode CascadeShadowRenderMode
        {
            get => _cascadeShadowRenderMode;
            set => SetField(ref _cascadeShadowRenderMode, value);
        }

        /// <summary>
        /// Most recent render mode selected for the cascade shadow path.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Effective Cascade Render Mode")]
        [Description("Resolved cascade render path after backend capability checks and fallback handling.")]
        public EDirectionalCascadeShadowRenderMode EffectiveCascadeShadowRenderMode => _effectiveCascadeShadowRenderMode;

        /// <summary>
        /// Most recent cascade backend selected for shadow rendering.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Effective Cascade Backend")]
        [Description("Resolved cascade backend after atlas/legacy routing and capability checks.")]
        public string EffectiveCascadeShadowRenderBackend => FormatEffectiveCascadeShadowRenderBackend();

        /// <summary>
        /// Most recent fallback reason for the cascade shadow path.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Render Fallback")]
        [Description("Reason the requested cascade render mode could not be used in the most recent shadow pass.")]
        public string CascadeShadowRenderFallbackReason => _cascadeShadowRenderFallbackReason.ToString();

        private string FormatEffectiveCascadeShadowRenderBackend()
        {
            if (_effectiveCascadeShadowBackend == DirectionalCascadeShadowBackend.AtlasPage &&
                _effectiveCascadeShadowRenderMode == EDirectionalCascadeShadowRenderMode.Sequential &&
                RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan)
            {
                return "SequentialVulkanCascadeAtlas";
            }

            return _effectiveCascadeShadowBackend.ToString();
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
        public XRTexture2DArray? CascadedShadowMapTexture => GetCascadeSourceState(ShadowRequestSource.Desktop).ShadowMapTexture;

        /// <summary>
        /// Texture array that should be inspected when debugging cascade contents.
        /// On Vulkan depth-shadow paths this is the raster depth receiver, not the
        /// auxiliary color/moment output.
        /// </summary>
        public XRTexture2DArray? CascadedShadowPreviewTexture
            => CascadedShadowReceiverTexture ?? GetCascadeSourceState(ShadowRequestSource.Desktop).ShadowMapTexture;

        internal XRTexture2DArray? CascadedShadowReceiverTexture
            => GetCascadedShadowReceiverTexture(ShadowRequestSource.Desktop);

        internal XRTexture2DArray? GetCascadedShadowReceiverTexture(XRCamera? camera)
            => GetCascadedShadowReceiverTexture(GetCascadeSourceForCamera(camera));

        internal XRTexture2DArray? GetSampleableCascadedShadowReceiverTexture(XRCamera? camera)
            => GetSampleableCascadedShadowReceiverTexture(GetCascadeSourceForCamera(camera));

        internal XRTexture2DArray? GetSampleableCascadedShadowReceiverTexture(ShadowRequestSource source)
        {
            XRTexture2DArray? receiverTexture;
            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                int activeCascadeCount = state.Slices.Count;
                receiverTexture = SelectCascadeReceiverTexture(state);
                if (activeCascadeCount <= 0 ||
                    receiverTexture is null ||
                    state.ContentRevision == 0u ||
                    state.LegacyRenderedContentRevision != state.ContentRevision ||
                    state.LegacyRenderedCascadeCount < activeCascadeCount ||
                    !ReferenceEquals(state.LegacyRenderedReceiverTexture, receiverTexture))
                {
                    return null;
                }
            }

            AbstractRenderer? renderer = AbstractRenderer.Current;
            return renderer is null || renderer.IsTextureReadyForShaderSampling(receiverTexture)
                ? receiverTexture
                : null;
        }

        internal XRTexture2DArray? GetCascadedShadowReceiverTexture(ShadowRequestSource source)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
                return null;

            int requiredCascades = Math.Clamp(_cascadeCount, 1, MaxCascadeRenderCount);
            if (ShouldUseVulkanAtlasCascadeTargets(requiredCascades))
                return null;

            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            bool needsRasterDepth = ShouldUseVulkanRasterDepthReceiverTexture();
            if (CastsShadows &&
                EnableCascadedShadows &&
                (needsRasterDepth ? state.RasterDepthTexture is null : state.ShadowMapTexture is null))
            {
                EnsureCascadeShadowResources(source);
            }

            return needsRasterDepth
                ? state.RasterDepthTexture
                : state.ShadowMapTexture;
        }

        internal bool HasCascadeColorTexture => HasCascadeColorTextureForSource(ShadowRequestSource.Desktop);
        internal bool HasCascadeRasterDepthTexture => HasCascadeRasterDepthTextureForSource(ShadowRequestSource.Desktop);
        internal bool HasCascadeColorTextureForSource(ShadowRequestSource source) => GetCascadeSourceState(source).ShadowMapTexture is not null;
        internal bool HasCascadeRasterDepthTextureForSource(ShadowRequestSource source) => GetCascadeSourceState(source).RasterDepthTexture is not null;
        internal bool UsesCascadeRasterDepthReceiver => ShouldUseVulkanRasterDepthReceiverTexture();

        private XRTexture2DArray? SelectCascadeReceiverTexture(DirectionalCascadeSourceState state)
            => !CanRenderDirectionalCascadesForCurrentBackend()
                ? null
                : ShouldUseVulkanRasterDepthReceiverTexture()
                ? state.RasterDepthTexture
                : state.ShadowMapTexture;

        private bool ShouldUseVulkanAtlasCascadeTargets(int cascadeCount)
            => RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan &&
               CanUseDirectionalCascadeShadowAtlasForCurrentBackend(cascadeCount);

        private static bool CanRenderDirectionalCascadesForCurrentBackend()
        {
            if (!IsVulkanDirectionalShadowBackend())
                return true;

            if (TryResolveVulkanDirectionalCascadesOverride(out bool enabled))
                return enabled;

            // The original Vulkan safety stop was for OpenXR + Monado hangs while
            // rendering shared cascade texture arrays. Keep that runtime guarded by
            // default, but allow SteamVR and ordinary Vulkan sessions to exercise the
            // cascade path.
            return !IsKnownMonadoOpenXrRuntime();
        }

        private static bool TryResolveVulkanDirectionalCascadesOverride(out bool enabled)
        {
            enabled = false;
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDirectionalCascades);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "force", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
                return true;
            }

            return false;
        }

        internal static bool IsKnownMonadoOpenXrRuntime()
        {
            string runtimePath =
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson) ??
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson) ??
                string.Empty;

            return runtimePath.Contains("monado", StringComparison.OrdinalIgnoreCase);
        }

        private void PublishVulkanCascadeRenderingDisabledPlan(ShadowRequestSource source)
        {
            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            PublishCascadeShadowRenderPlan(CreateSequentialCascadeShadowRenderPlan(
                state,
                _cascadeShadowRenderMode,
                DirectionalCascadeShadowBackend.LegacyTextureArray,
                0,
                DirectionalCascadeShadowFallbackReason.VulkanCascadeRenderingDisabled));
        }

        private void LogVulkanCascadeRenderingDisabledIfNeeded(ShadowRequestSource source)
        {
            Debug.LightingWarningEvery(
                $"DirectionalCascade.VulkanDisabled.{GetHashCode()}.{source}",
                TimeSpan.FromSeconds(2.0),
                "[DirectionalShadowAudit] Directional cascades disabled for '{0}' source={1} on Vulkan. Primary directional shadows remain enabled while cascade texture-array ownership is fixed.",
                SceneNode?.Name ?? Name ?? GetType().Name,
                source);
        }

        /// <summary>
        /// Number of cascades with currently published bounds and cameras.
        /// </summary>
        public int ActiveCascadeCount
        {
            get
            {
                lock (_cascadeDataLock)
                    return Math.Max(_desktopCascadeState.Slices.Count, _hmdCascadeState.Slices.Count);
            }
        }

        internal int GetActiveCascadeCount(XRCamera? camera)
            => GetActiveCascadeCount(GetCascadeSourceForCamera(camera));

        internal int GetActiveCascadeCount(ShadowRequestSource source)
        {
            lock (_cascadeDataLock)
                return GetCascadeSourceState(source).Slices.Count;
        }

        internal bool PublishedCascadesMatchCamera(XRCamera? camera)
            => GetActiveCascadeCount(camera) > 0;

        internal bool HasPublishedCascades(ShadowRequestSource source)
        {
            lock (_cascadeDataLock)
                return GetCascadeSourceState(source).Slices.Count > 0;
        }

        /// <summary>
        /// Near distance of the camera range used to build the current cascades.
        /// </summary>
        public float CascadeRangeNear
        {
            get
            {
                lock (_cascadeDataLock)
                    return GetCascadeSourceState(ShadowRequestSource.Desktop).RangeNear;
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
                    return GetCascadeSourceState(ShadowRequestSource.Desktop).RangeFar;
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
                    return MathF.Max(0.0f, GetCascadeSourceState(ShadowRequestSource.Desktop).RangeFar - GetCascadeSourceState(ShadowRequestSource.Desktop).RangeNear);
            }
        }

        /// <summary>
        /// Returns the far split distance for a published cascade, or float.MaxValue when unavailable.
        /// </summary>
        public float GetCascadeSplit(int index)
            => GetCascadeSplit(ShadowRequestSource.Desktop, index);

        internal float GetCascadeSplit(ShadowRequestSource source, int index)
        {
            lock (_cascadeDataLock)
            {
                List<CascadeShadowSlice> slices = GetCascadeSourceState(source).Slices;
                return index >= 0 && index < slices.Count
                    ? slices[index].SplitFarDistance
                    : float.MaxValue;
            }
        }

        /// <summary>
        /// Returns the world-to-light projection matrix for a published cascade.
        /// </summary>
        public Matrix4x4 GetCascadeMatrix(int index)
            => GetCascadeMatrix(ShadowRequestSource.Desktop, index);

        internal Matrix4x4 GetCascadeMatrix(ShadowRequestSource source, int index)
        {
            lock (_cascadeDataLock)
            {
                List<CascadeShadowSlice> slices = GetCascadeSourceState(source).Slices;
                return index >= 0 && index < slices.Count
                    ? slices[index].WorldToLightSpaceMatrix
                    : Matrix4x4.Identity;
            }
        }

        /// <summary>
        /// Returns the world-space center of a published cascade bounds.
        /// </summary>
        public Vector3 GetCascadeCenter(int index)
            => GetCascadeCenter(ShadowRequestSource.Desktop, index);

        internal Vector3 GetCascadeCenter(ShadowRequestSource source, int index)
        {
            lock (_cascadeDataLock)
            {
                List<CascadeShadowSlice> slices = GetCascadeSourceState(source).Slices;
                return index >= 0 && index < slices.Count
                    ? slices[index].Center
                    : Vector3.Zero;
            }
        }

        /// <summary>
        /// Returns the world-space half extents of a published cascade bounds.
        /// </summary>
        public Vector3 GetCascadeHalfExtents(int index)
            => GetCascadeHalfExtents(ShadowRequestSource.Desktop, index);

        internal Vector3 GetCascadeHalfExtents(ShadowRequestSource source, int index)
        {
            lock (_cascadeDataLock)
            {
                List<CascadeShadowSlice> slices = GetCascadeSourceState(source).Slices;
                return index >= 0 && index < slices.Count
                    ? slices[index].HalfExtents
                    : Vector3.Zero;
            }
        }

        /// <summary>
        /// Returns effective receiver-bias settings for a published cascade or a safe fallback.
        /// </summary>
        public CascadeShadowBiasSettings GetCascadeBiasSettings(int index)
            => GetCascadeBiasSettings(ShadowRequestSource.Desktop, index);

        internal CascadeShadowBiasSettings GetCascadeBiasSettings(ShadowRequestSource source, int index)
        {
            lock (_cascadeDataLock)
            {
                List<CascadeShadowSlice> slices = GetCascadeSourceState(source).Slices;
                if (index >= 0 && index < slices.Count)
                {
                    CascadeShadowSlice slice = slices[index];
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
            => GetCascadeCamera(ShadowRequestSource.Desktop, index);

        internal XRCamera? GetCascadeCamera(ShadowRequestSource source, int index)
        {
            XRCamera[] cameras = GetCascadeSourceState(source).Cameras;
            return index >= 0 && index < cameras.Length
                ? cameras[index]
                : null;
        }

        /// <summary>
        /// Gets the shadow viewport for a cascade resource slot.
        /// </summary>
        public XRViewport? GetCascadeViewport(int index)
            => GetCascadeViewport(ShadowRequestSource.Desktop, index);

        internal XRViewport? GetCascadeViewport(ShadowRequestSource source, int index)
        {
            XRViewport[] viewports = GetCascadeSourceState(source).Viewports;
            return index >= 0 && index < viewports.Length
                ? viewports[index]
                : null;
        }

        /// <summary>
        /// Gets the shadow framebuffer for a cascade resource slot.
        /// </summary>
        public XRFrameBuffer? GetCascadeFrameBuffer(int index)
            => GetCascadeFrameBuffer(ShadowRequestSource.Desktop, index);

        internal XRFrameBuffer? GetCascadeFrameBuffer(ShadowRequestSource source, int index)
        {
            XRFrameBuffer[] frameBuffers = GetCascadeSourceState(source).FrameBuffers;
            return index >= 0 && index < frameBuffers.Length
                ? frameBuffers[index]
                : null;
        }

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

        private int GetPublishedCascadeViewportCount(ShadowRequestSource source, XRViewport[] viewports)
        {
            lock (_cascadeDataLock)
                return Math.Min(GetCascadeSourceState(source).Slices.Count, viewports.Length);
        }

        private int GetPublishedCascadeRenderCount(ShadowRequestSource source, XRViewport[] viewports, XRFrameBuffer[] frameBuffers)
        {
            lock (_cascadeDataLock)
                return Math.Min(GetCascadeSourceState(source).Slices.Count, Math.Min(viewports.Length, frameBuffers.Length));
        }

        private Box? GetPublishedCascadeCullVolume(ShadowRequestSource source, int index)
        {
            lock (_cascadeDataLock)
            {
                List<CascadedShadowAabb> aabbs = GetCascadeSourceState(source).Aabbs;
                if (index < 0 || index >= aabbs.Count)
                    return null;

                CascadedShadowAabb cascade = aabbs[index];
                Matrix4x4 transform =
                    Matrix4x4.CreateFromQuaternion(cascade.Orientation) *
                    Matrix4x4.CreateTranslation(cascade.Center);
                return new Box(Vector3.Zero, cascade.HalfExtents * 2.0f, transform);
            }
        }

        private Box? GetPublishedCascadeUnionCullVolume(ShadowRequestSource source, int cascadeCount)
        {
            lock (_cascadeDataLock)
            {
                List<CascadedShadowAabb> aabbs = GetCascadeSourceState(source).Aabbs;
                int count = Math.Min(cascadeCount, aabbs.Count);
                if (count <= 0)
                    return null;

                Vector3 min = new(float.MaxValue);
                Vector3 max = new(float.MinValue);
                for (int i = 0; i < count; i++)
                    IncludeCascadeAabbCorners(aabbs[i], ref min, ref max);

                if (max.X < min.X || max.Y < min.Y || max.Z < min.Z)
                    return null;

                return Box.FromMinMax(min, max);
            }
        }

        private static void IncludeCascadeAabbCorners(CascadedShadowAabb cascade, ref Vector3 min, ref Vector3 max)
        {
            Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(cascade.Orientation);
            Vector3 half = cascade.HalfExtents;
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3(-half.X, -half.Y, -half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3( half.X, -half.Y, -half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3(-half.X,  half.Y, -half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3( half.X,  half.Y, -half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3(-half.X, -half.Y,  half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3( half.X, -half.Y,  half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3(-half.X,  half.Y,  half.Z), ref min, ref max);
            IncludeCascadeAabbCorner(cascade.Center, rotation, new Vector3( half.X,  half.Y,  half.Z), ref min, ref max);
        }

        private static void IncludeCascadeAabbCorner(Vector3 center, Matrix4x4 rotation, Vector3 localCorner, ref Vector3 min, ref Vector3 max)
        {
            Vector3 worldCorner = center + Vector3.TransformNormal(localCorner, rotation);
            min = Vector3.Min(min, worldCorner);
            max = Vector3.Max(max, worldCorner);
        }

        /// <summary>
        /// Copies cascade split, bias, and matrix data into caller-provided stack buffers for uniform upload.
        /// </summary>
        internal void CopyPublishedCascadeUniformData(
            XRCamera? camera,
            Span<float> splits,
            Span<float> blendWidths,
            Span<float> biasMins,
            Span<float> biasMaxes,
            Span<float> receiverOffsets,
            Span<Matrix4x4> matrices,
            out int cascadeCount)
            => CopyPublishedCascadeUniformData(GetCascadeSourceForCamera(camera), splits, blendWidths, biasMins, biasMaxes, receiverOffsets, matrices, out cascadeCount);

        internal void CopyPublishedCascadeUniformData(
            ShadowRequestSource source,
            Span<float> splits,
            Span<float> blendWidths,
            Span<float> biasMins,
            Span<float> biasMaxes,
            Span<float> receiverOffsets,
            Span<Matrix4x4> matrices,
            out int cascadeCount)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                cascadeCount = 0;
                FillCascadeUniformDefaults(splits, blendWidths, biasMins, biasMaxes, receiverOffsets, matrices);
                return;
            }

            int copyCount = Math.Min(MaxCascadeRenderCount, Math.Min(splits.Length, matrices.Length));

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                List<CascadeShadowSlice> slices = state.Slices;
                cascadeCount = slices.Count;
                for (int i = 0; i < copyCount; i++)
                {
                    if (i < cascadeCount)
                    {
                        CascadeShadowSlice slice = slices[i];
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

        private void FillCascadeUniformDefaults(
            Span<float> splits,
            Span<float> blendWidths,
            Span<float> biasMins,
            Span<float> biasMaxes,
            Span<float> receiverOffsets,
            Span<Matrix4x4> matrices)
        {
            int copyCount = Math.Min(
                MaxCascadeRenderCount,
                Math.Min(
                    splits.Length,
                    Math.Min(
                        blendWidths.Length,
                        Math.Min(
                            biasMins.Length,
                            Math.Min(
                                biasMaxes.Length,
                                Math.Min(receiverOffsets.Length, matrices.Length))))));
            for (int i = 0; i < copyCount; i++)
            {
                splits[i] = float.MaxValue;
                blendWidths[i] = 0.0f;
                biasMins[i] = 0.0f;
                biasMaxes[i] = ShadowSlopeBiasTexels;
                receiverOffsets[i] = 0.0f;
                matrices[i] = Matrix4x4.Identity;
            }
        }

        /// <summary>
        /// Copies the cascade payload that produced the currently sampleable atlas slots.
        /// Current cascade arrays remain camera/current-frame data; these rendered arrays
        /// are used only when the shader samples a directional cascade atlas tile.
        /// </summary>
        internal void CopyPublishedRenderedCascadeUniformData(
            XRCamera? camera,
            Span<float> splits,
            Span<float> blendWidths,
            Span<float> biasMins,
            Span<float> biasMaxes,
            Span<float> receiverOffsets,
            Span<Matrix4x4> matrices,
            Span<float> staleAges,
            out int cascadeCount)
            => CopyPublishedRenderedCascadeUniformData(GetCascadeSourceForCamera(camera), splits, blendWidths, biasMins, biasMaxes, receiverOffsets, matrices, staleAges, out cascadeCount);

        internal void CopyPublishedRenderedCascadeUniformData(
            ShadowRequestSource source,
            Span<float> splits,
            Span<float> blendWidths,
            Span<float> biasMins,
            Span<float> biasMaxes,
            Span<float> receiverOffsets,
            Span<Matrix4x4> matrices,
            Span<float> staleAges,
            out int cascadeCount)
        {
            int copyCount = Math.Min(
                MaxCascadeRenderCount,
                Math.Min(
                    splits.Length,
                    Math.Min(
                        blendWidths.Length,
                        Math.Min(
                            biasMins.Length,
                            Math.Min(
                                biasMaxes.Length,
                                Math.Min(
                                    receiverOffsets.Length,
                                    Math.Min(matrices.Length, staleAges.Length)))))));
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                cascadeCount = 0;
                FillCascadeUniformDefaults(splits, blendWidths, biasMins, biasMaxes, receiverOffsets, matrices);
                for (int i = 0; i < copyCount; i++)
                    staleAges[i] = -1.0f;
                return;
            }

            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                List<CascadeShadowSlice> slices = state.Slices;
                cascadeCount = slices.Count;
                for (int i = 0; i < copyCount; i++)
                {
                    if (i < cascadeCount)
                    {
                        DirectionalCascadeAtlasSlot atlasSlot = state.AtlasSlots[i];
                        if (atlasSlot.HasCascadeUniformData &&
                            IsDirectionalAtlasSlotSampleable(atlasSlot))
                        {
                            splits[i] = atlasSlot.SplitFarDistance;
                            blendWidths[i] = atlasSlot.BlendWidth;
                            biasMins[i] = atlasSlot.BiasMin;
                            biasMaxes[i] = atlasSlot.BiasMax;
                            receiverOffsets[i] = atlasSlot.ReceiverOffset;
                            matrices[i] = atlasSlot.WorldToLightSpaceMatrix;
                            staleAges[i] = ResolveRenderedCascadeStaleAge(frameId, atlasSlot.LastRenderedFrame);
                        }
                        else
                        {
                            CascadeShadowSlice slice = slices[i];
                            splits[i] = slice.SplitFarDistance;
                            blendWidths[i] = slice.BlendWidth;
                            biasMins[i] = slice.BiasMin;
                            biasMaxes[i] = slice.BiasMax;
                            receiverOffsets[i] = slice.ReceiverOffset;
                            matrices[i] = slice.WorldToLightSpaceMatrix;
                            staleAges[i] = -1.0f;
                        }
                    }
                    else
                    {
                        splits[i] = float.MaxValue;
                        blendWidths[i] = 0.0f;
                        biasMins[i] = 0.0f;
                        biasMaxes[i] = ShadowSlopeBiasTexels;
                        receiverOffsets[i] = 0.0f;
                        matrices[i] = Matrix4x4.Identity;
                        staleAges[i] = -1.0f;
                    }
                }
            }
        }

        private static float ResolveRenderedCascadeStaleAge(ulong currentFrame, ulong renderedFrame)
        {
            if (renderedFrame == 0u || currentFrame < renderedFrame)
                return 0.0f;

            ulong age = currentFrame - renderedFrame;
            return age > 1_000_000u ? 1_000_000.0f : (float)age;
        }

        internal void BeginDirectionalAtlasSlotPublish()
        {
            lock (_cascadeDataLock)
            {
                if (_directionalAtlasSlotPublishInProgress)
                    return;

                CopyAtlasSlotsForPublish(_desktopCascadeState);
                CopyAtlasSlotsForPublish(_hmdCascadeState);
                _previousPrimaryAtlasSlot = _primaryAtlasSlot;
                _pendingPrimaryAtlasSlot = default;
                _pendingPrimaryAtlasSlotWritten = false;
                _directionalAtlasSlotPublishInProgress = true;
            }
        }

        private static void CopyAtlasSlotsForPublish(DirectionalCascadeSourceState state)
        {
            Array.Copy(state.AtlasSlots, state.PreviousAtlasSlots, state.AtlasSlots.Length);
            Array.Clear(state.PendingAtlasSlots);
        }

        internal void CompleteDirectionalAtlasSlotPublish(bool publish, ShadowAtlasManager shadowAtlas)
        {
            lock (_cascadeDataLock)
            {
                if (!_directionalAtlasSlotPublishInProgress)
                    return;

                if (publish)
                {
                    PublishPendingAtlasSlots(_desktopCascadeState);
                    PublishPendingAtlasSlots(_hmdCascadeState);
                    if (_pendingPrimaryAtlasSlotWritten)
                    {
                        _primaryAtlasSlot = _pendingPrimaryAtlasSlot;
                    }
                    else if (_previousPrimaryAtlasSlot.HasAllocation &&
                        shadowAtlas.TryGetPlanningAllocation(_previousPrimaryAtlasSlot.Key, out ShadowAtlasAllocation allocation) &&
                        TryRefreshPreservedPrimaryAtlasSlot(_previousPrimaryAtlasSlot, allocation, out DirectionalCascadeAtlasSlot preserved))
                    {
                        _primaryAtlasSlot = preserved;
                    }
                    else
                    {
                        _primaryAtlasSlot = default;
                    }
                }

                Array.Clear(_desktopCascadeState.PendingAtlasSlots);
                Array.Clear(_hmdCascadeState.PendingAtlasSlots);
                _previousPrimaryAtlasSlot = default;
                _pendingPrimaryAtlasSlot = default;
                _pendingPrimaryAtlasSlotWritten = false;
                _directionalAtlasSlotPublishInProgress = false;
            }
        }

        internal static bool TryRefreshPreservedPrimaryAtlasSlot(
            in DirectionalCascadeAtlasSlot previous,
            in ShadowAtlasAllocation allocation,
            out DirectionalCascadeAtlasSlot preserved)
        {
            bool allocationMatchesPublishedTile =
                previous.HasAllocation &&
                previous.IsResident &&
                previous.LastRenderedFrame != 0u &&
                allocation.IsResident &&
                allocation.LastRenderedFrame != 0u &&
                allocation.Key == previous.Key &&
                allocation.AtlasKind == EShadowAtlasKind.Directional &&
                allocation.AtlasId == previous.AtlasId &&
                allocation.PageIndex == previous.PageIndex &&
                allocation.PixelRect.Equals(previous.PixelRect) &&
                allocation.InnerPixelRect.Equals(previous.InnerPixelRect) &&
                allocation.ContentVersion == previous.ContentVersion &&
                allocation.LastRenderedFrame == previous.LastRenderedFrame &&
                allocation.ActiveFallback is ShadowFallbackMode.None
                    or ShadowFallbackMode.StaleTile
                    or ShadowFallbackMode.ContactOnly;

            if (!allocationMatchesPublishedTile)
            {
                preserved = default;
                return false;
            }

            preserved = previous with
            {
                IsResident = allocation.IsResident,
                Fallback = allocation.ActiveFallback,
            };
            return true;
        }

        private static void PublishPendingAtlasSlots(DirectionalCascadeSourceState state)
        {
            DirectionalCascadeAtlasSlot[] previouslyPublished = state.AtlasSlots;
            state.AtlasSlots = state.PendingAtlasSlots;
            state.PendingAtlasSlots = previouslyPublished;
        }

        internal bool TryCreateDirectionalCascadeSampleState(
            ShadowRequestSource source,
            int index,
            ulong contentHash,
            ulong renderedFrame,
            out DirectionalCascadeSampleState sample)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
            {
                sample = default;
                return false;
            }

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
                if ((uint)index >= (uint)state.Slices.Count)
                {
                    sample = default;
                    return false;
                }

                CascadeShadowSlice slice = state.Slices[index];
                sample = new DirectionalCascadeSampleState(
                    IsValid: true,
                    Source: resolvedSource,
                    CascadeIndex: index,
                    ContentHash: contentHash,
                    RenderedFrame: renderedFrame,
                    SplitFarDistance: slice.SplitFarDistance,
                    BlendWidth: slice.BlendWidth,
                    BiasMin: slice.BiasMin,
                    BiasMax: slice.BiasMax,
                    ReceiverOffset: slice.ReceiverOffset,
                    WorldToLightSpaceMatrix: slice.WorldToLightSpaceMatrix);
                return true;
            }
        }

        internal bool TryGetRenderedDirectionalCascadeSampleState(
            ShadowRequestSource source,
            int index,
            out DirectionalCascadeSampleState sample)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
            {
                sample = default;
                return false;
            }

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                sample = GetCascadeSourceState(resolvedSource).RenderedSamples[index];
                return sample.IsValid;
            }
        }

        internal int GetDirectionalCascadeStableRequestFrameCount(
            ShadowRequestSource source,
            int index,
            ulong contentHash)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount || contentHash == 0u)
                return 0;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
                return state.PreviousAtlasRequestContentHashes[index] == contentHash
                    ? state.StableAtlasRequestFrameCounts[index]
                    : 0;
            }
        }

        internal void BeginDirectionalCascadeAtlasRequestFrame(ShadowRequestSource source, int activeCascadeCount)
        {
            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
                int count = Math.Clamp(activeCascadeCount, 0, MaxCascadeRenderCount);
                for (int i = 0; i < MaxCascadeRenderCount; i++)
                {
                    state.AtlasRequestContentHashes[i] = 0u;
                    state.AtlasCascadeRenderRequested[i] = false;
                    state.AtlasCascadeCollectVisibleNeeded[i] = false;
                    state.AtlasCascadeSwapNeeded[i] = false;
                }

                for (int i = count; i < MaxCascadeRenderCount; i++)
                {
                    state.AtlasCascadeVisibleSetCached[i] = false;
                    state.PreviousAtlasRequestContentHashes[i] = 0u;
                    state.StableAtlasRequestFrameCounts[i] = 0;
                }
            }
        }

        internal void MarkDirectionalCascadeAtlasRequest(
            ShadowRequestSource source,
            int index,
            ulong contentHash,
            bool renderRequested)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
                bool cacheHit =
                    state.AtlasCascadeVisibleSetCached[index] &&
                    state.AtlasCollectedContentHashes[index] == contentHash;

                state.AtlasRequestContentHashes[index] = contentHash;
                if (contentHash != 0u &&
                    state.PreviousAtlasRequestContentHashes[index] == contentHash)
                {
                    state.StableAtlasRequestFrameCounts[index] = Math.Min(
                        int.MaxValue - 1,
                        state.StableAtlasRequestFrameCounts[index] + 1);
                }
                else
                {
                    state.PreviousAtlasRequestContentHashes[index] = contentHash;
                    state.StableAtlasRequestFrameCounts[index] = contentHash == 0u ? 0 : 1;
                }

                state.AtlasCascadeRenderRequested[index] = renderRequested;
                state.AtlasCascadeCollectVisibleNeeded[index] = renderRequested && !cacheHit;
            }
        }

        private bool HasDirectionalCascadeAtlasRenderRequest(ShadowRequestSource source, int activeCascadeCount)
        {
            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
                int count = Math.Clamp(activeCascadeCount, 0, MaxCascadeRenderCount);
                for (int i = 0; i < count; i++)
                    if (state.AtlasCascadeRenderRequested[i])
                        return true;
            }

            return false;
        }

        private bool ShouldCollectDirectionalCascadeAtlasViewport(ShadowRequestSource source, int index)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return false;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
                return GetCascadeSourceState(resolvedSource).AtlasCascadeCollectVisibleNeeded[index];
        }

        private void MarkDirectionalCascadeAtlasViewportCollected(ShadowRequestSource source, int index)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
                state.AtlasCollectedContentHashes[index] = state.AtlasRequestContentHashes[index];
                state.AtlasCascadeVisibleSetCached[index] = state.AtlasCollectedContentHashes[index] != 0u;
                state.AtlasCascadeCollectVisibleNeeded[index] = false;
                state.AtlasCascadeSwapNeeded[index] = true;
            }
        }

        private bool ShouldSwapDirectionalCascadeAtlasViewport(ShadowRequestSource source, int index)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return false;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
                return GetCascadeSourceState(resolvedSource).AtlasCascadeSwapNeeded[index];
        }

        private void MarkDirectionalCascadeAtlasViewportSwapped(ShadowRequestSource source, int index)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;

            lock (_cascadeDataLock)
                GetCascadeSourceState(resolvedSource).AtlasCascadeSwapNeeded[index] = false;
        }

        internal void ClearDirectionalAtlasSlots()
        {
            lock (_cascadeDataLock)
            {
                _primaryAtlasSlot = default;
                _previousPrimaryAtlasSlot = default;
                _pendingPrimaryAtlasSlot = default;
                _pendingPrimaryAtlasSlotWritten = false;
                _directionalAtlasSlotPublishInProgress = false;
                Array.Clear(_desktopCascadeState.AtlasSlots);
                Array.Clear(_desktopCascadeState.PendingAtlasSlots);
                Array.Clear(_desktopCascadeState.PreviousAtlasSlots);
                Array.Clear(_desktopCascadeState.RenderedSamples);
                Array.Clear(_desktopCascadeState.PreviousAtlasRequestContentHashes);
                Array.Clear(_desktopCascadeState.StableAtlasRequestFrameCounts);
                Array.Clear(_desktopCascadeState.AtlasCascadeVisibleSetCached);
                Array.Clear(_hmdCascadeState.AtlasSlots);
                Array.Clear(_hmdCascadeState.PendingAtlasSlots);
                Array.Clear(_hmdCascadeState.PreviousAtlasSlots);
                Array.Clear(_hmdCascadeState.RenderedSamples);
                Array.Clear(_hmdCascadeState.PreviousAtlasRequestContentHashes);
                Array.Clear(_hmdCascadeState.StableAtlasRequestFrameCounts);
                Array.Clear(_hmdCascadeState.AtlasCascadeVisibleSetCached);
            }
        }

        internal void ApplyDirectionalShadowAtlasMode(bool useDirectionalShadowAtlas)
        {
            ClearDirectionalAtlasSlots();

            if (!useDirectionalShadowAtlas)
            {
                EnsureShadowMapForActiveDynamicLight();
                EnsureCascadeShadowResources();
            }
        }

        internal void ClearCascadeAtlasSlots()
        {
            lock (_cascadeDataLock)
            {
                Array.Clear(_desktopCascadeState.AtlasSlots);
                Array.Clear(_desktopCascadeState.PendingAtlasSlots);
                Array.Clear(_desktopCascadeState.PreviousAtlasSlots);
                Array.Clear(_desktopCascadeState.RenderedSamples);
                Array.Clear(_desktopCascadeState.PreviousAtlasRequestContentHashes);
                Array.Clear(_desktopCascadeState.StableAtlasRequestFrameCounts);
                Array.Clear(_desktopCascadeState.AtlasCascadeVisibleSetCached);
                Array.Clear(_hmdCascadeState.AtlasSlots);
                Array.Clear(_hmdCascadeState.PendingAtlasSlots);
                Array.Clear(_hmdCascadeState.PreviousAtlasSlots);
                Array.Clear(_hmdCascadeState.RenderedSamples);
                Array.Clear(_hmdCascadeState.PreviousAtlasRequestContentHashes);
                Array.Clear(_hmdCascadeState.StableAtlasRequestFrameCounts);
                Array.Clear(_hmdCascadeState.AtlasCascadeVisibleSetCached);
            }
        }

        private DirectionalCascadeAtlasSlot[] GetAtlasSlotWriteTarget(DirectionalCascadeSourceState state)
            => _directionalAtlasSlotPublishInProgress
                ? state.PendingAtlasSlots
                : state.AtlasSlots;

        private static DirectionalCascadeAtlasSlot CreateAtlasSlot(
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution,
            bool hasCascadeUniformData,
            float splitFarDistance,
            float blendWidth,
            float biasMin,
            float biasMax,
            float receiverOffset,
            Matrix4x4 worldToLightSpaceMatrix)
        {
            uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
            float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
            float resolutionScale = sampleResolution > 0u
                ? MathF.Max(1.0f, Math.Max(1u, desiredResolution) / (float)sampleResolution)
                : 1.0f;

            return new DirectionalCascadeAtlasSlot(
                HasAllocation: true,
                IsResident: allocation.IsResident,
                Key: allocation.Key,
                AtlasId: allocation.AtlasId,
                PageIndex: allocation.PageIndex,
                RecordIndex: recordIndex,
                UvScaleBias: allocation.UvScaleBias,
                NearPlane: nearPlane,
                FarPlane: farPlane,
                TexelSize: texelSize,
                ResolutionScale: resolutionScale,
                Resolution: allocation.Resolution,
                Fallback: allocation.ActiveFallback,
                PixelRect: allocation.PixelRect,
                InnerPixelRect: allocation.InnerPixelRect,
                LastRenderedFrame: allocation.LastRenderedFrame,
                ContentVersion: allocation.ContentVersion,
                HasCascadeUniformData: hasCascadeUniformData,
                SplitFarDistance: splitFarDistance,
                BlendWidth: blendWidth,
                BiasMin: biasMin,
                BiasMax: biasMax,
                ReceiverOffset: receiverOffset,
                WorldToLightSpaceMatrix: worldToLightSpaceMatrix);
        }

        private static bool ShouldPreserveCascadeAtlasUniformData(
            in ShadowAtlasAllocation allocation,
            in DirectionalCascadeAtlasSlot previous)
        {
            if (!previous.HasAllocation ||
                !previous.HasCascadeUniformData ||
                !previous.IsResident ||
                previous.LastRenderedFrame == 0u ||
                !allocation.IsResident ||
                previous.PageIndex != allocation.PageIndex ||
                !previous.PixelRect.Equals(allocation.PixelRect) ||
                !previous.InnerPixelRect.Equals(allocation.InnerPixelRect))
            {
                return false;
            }

            return allocation.ActiveFallback is ShadowFallbackMode.None
                or ShadowFallbackMode.StaleTile
                or ShadowFallbackMode.ContactOnly;
        }

        private static bool DoesRenderedSampleMatchAllocation(
            in DirectionalCascadeSampleState sample,
            in ShadowAtlasAllocation allocation)
            => sample.IsValid &&
               allocation.IsResident &&
               allocation.LastRenderedFrame != 0u &&
               sample.ContentHash == allocation.ContentVersion &&
               sample.RenderedFrame >= allocation.LastRenderedFrame;

        private static bool CanUseRenderedSampleForStaleAllocation(
            in DirectionalCascadeSampleState sample,
            in ShadowAtlasAllocation allocation)
            => sample.IsValid &&
               sample.RenderedFrame != 0u &&
               sample.ContentHash != 0u &&
               allocation.IsResident &&
               allocation.LastRenderedFrame != 0u &&
               allocation.ActiveFallback is ShadowFallbackMode.StaleTile
                   or ShadowFallbackMode.None
                   or ShadowFallbackMode.ContactOnly &&
               (sample.ContentHash == allocation.ContentVersion ||
                sample.RenderedFrame == allocation.LastRenderedFrame);

        private static ShadowFallbackMode ResolvePreservedCascadeFallback(ShadowFallbackMode fallback)
            => fallback == ShadowFallbackMode.StaleTile
                ? ShadowFallbackMode.StaleTile
                : ShadowFallbackMode.None;

        private static ShadowFallbackMode ResolveStaleCascadeFallback(ShadowFallbackMode fallback)
            => fallback == ShadowFallbackMode.Legacy
                ? ShadowFallbackMode.Legacy
                : ShadowFallbackMode.StaleTile;

        private static ShadowAtlasAllocation CreateRenderedSampleAllocation(
            in ShadowAtlasAllocation allocation,
            in DirectionalCascadeSampleState sample,
            bool staleSample = false)
            => allocation with
            {
                ContentVersion = sample.ContentHash,
                LastRenderedFrame = sample.RenderedFrame,
                ActiveFallback = staleSample
                    ? ResolveStaleCascadeFallback(allocation.ActiveFallback)
                    : ResolvePreservedCascadeFallback(allocation.ActiveFallback),
            };

        private static DirectionalCascadeAtlasSlot CreateAtlasSlot(
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution,
            in DirectionalCascadeSampleState sample)
            => CreateAtlasSlot(
                allocation,
                recordIndex,
                nearPlane,
                farPlane,
                desiredResolution,
                hasCascadeUniformData: sample.IsValid,
                sample.SplitFarDistance,
                sample.BlendWidth,
                sample.BiasMin,
                sample.BiasMax,
                sample.ReceiverOffset,
                sample.WorldToLightSpaceMatrix);

        private DirectionalCascadeAtlasSlot CreateUnsampledAtlasSlot(
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution)
            => CreateAtlasSlot(
                allocation,
                recordIndex,
                nearPlane,
                farPlane,
                desiredResolution,
                hasCascadeUniformData: false,
                splitFarDistance: farPlane,
                blendWidth: 0.0f,
                biasMin: 0.0f,
                biasMax: ShadowSlopeBiasTexels,
                receiverOffset: 0.0f,
                worldToLightSpaceMatrix: Matrix4x4.Identity);

        private static DirectionalCascadeAtlasSlot RefreshAtlasSlotAllocation(
            in DirectionalCascadeAtlasSlot previous,
            in ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution)
        {
            uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
            float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
            float resolutionScale = sampleResolution > 0u
                ? MathF.Max(1.0f, Math.Max(1u, desiredResolution) / (float)sampleResolution)
                : 1.0f;

            return previous with
            {
                HasAllocation = true,
                IsResident = allocation.IsResident,
                PageIndex = allocation.PageIndex,
                RecordIndex = recordIndex,
                UvScaleBias = allocation.UvScaleBias,
                NearPlane = nearPlane,
                FarPlane = farPlane,
                TexelSize = texelSize,
                ResolutionScale = resolutionScale,
                Resolution = allocation.Resolution,
                Fallback = ResolvePreservedCascadeFallback(allocation.ActiveFallback),
                PixelRect = allocation.PixelRect,
                InnerPixelRect = allocation.InnerPixelRect,
                LastRenderedFrame = previous.LastRenderedFrame,
                ContentVersion = previous.ContentVersion,
            };
        }

        private static DirectionalCascadeAtlasSlot RefreshStaleAtlasSlotAllocation(
            in DirectionalCascadeAtlasSlot previous,
            in ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution)
            => RefreshAtlasSlotAllocation(
                previous,
                allocation,
                recordIndex,
                nearPlane,
                farPlane,
                desiredResolution) with
                {
                    Fallback = ResolveStaleCascadeFallback(allocation.ActiveFallback),
                };

        internal void SetCascadeAtlasSlot(
            ShadowRequestSource source,
            int index,
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution,
            DirectionalCascadeSampleState requestSample = default)
        {
            if ((uint)index >= (uint)MaxCascadeRenderCount)
                return;

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                DirectionalCascadeAtlasSlot[] targetSlots = GetAtlasSlotWriteTarget(state);
                DirectionalCascadeSampleState renderedSample = state.RenderedSamples[index];
                if (DoesRenderedSampleMatchAllocation(renderedSample, allocation))
                {
                    ShadowAtlasAllocation renderedAllocation = CreateRenderedSampleAllocation(allocation, renderedSample);
                    DirectionalCascadeAtlasSlot slot = CreateAtlasSlot(
                        renderedAllocation,
                        recordIndex,
                        nearPlane,
                        farPlane,
                        desiredResolution,
                        renderedSample);
                    targetSlots[index] = slot;
                    LogDirectionalCascadeProvenance(state, index, allocation, requestSample, renderedSample, slot, "RenderedSample");
                    return;
                }

                if (CanUseRenderedSampleForStaleAllocation(renderedSample, allocation))
                {
                    ShadowAtlasAllocation renderedAllocation = CreateRenderedSampleAllocation(allocation, renderedSample, staleSample: true);
                    DirectionalCascadeAtlasSlot slot = CreateAtlasSlot(
                        renderedAllocation,
                        recordIndex,
                        nearPlane,
                        farPlane,
                        desiredResolution,
                        renderedSample);
                    targetSlots[index] = slot;
                    LogDirectionalCascadeProvenance(state, index, allocation, requestSample, renderedSample, slot, "RenderedSampleStale");
                    return;
                }

                if (DoesRenderedSampleMatchAllocation(requestSample, allocation))
                {
                    state.RenderedSamples[index] = requestSample;
                    ShadowAtlasAllocation renderedAllocation = CreateRenderedSampleAllocation(allocation, requestSample);
                    DirectionalCascadeAtlasSlot slot = CreateAtlasSlot(
                        renderedAllocation,
                        recordIndex,
                        nearPlane,
                        farPlane,
                        desiredResolution,
                        requestSample);
                    targetSlots[index] = slot;
                    LogDirectionalCascadeProvenance(state, index, allocation, requestSample, requestSample, slot, "RequestSample");
                    return;
                }

                DirectionalCascadeAtlasSlot previous = state.PreviousAtlasSlots[index];
                if (ShouldPreserveCascadeAtlasUniformData(allocation, previous))
                {
                    DirectionalCascadeAtlasSlot slot = RefreshStaleAtlasSlotAllocation(
                        previous,
                        allocation,
                        recordIndex,
                        nearPlane,
                        farPlane,
                        desiredResolution);
                    targetSlots[index] = slot;
                    LogDirectionalCascadeProvenance(state, index, allocation, requestSample, default, slot, "PreservedPrevious");
                    return;
                }

                DirectionalCascadeAtlasSlot unsampledSlot = CreateUnsampledAtlasSlot(
                    allocation,
                    recordIndex,
                    nearPlane,
                    farPlane,
                    desiredResolution);
                targetSlots[index] = unsampledSlot;
                LogDirectionalCascadeProvenance(state, index, allocation, requestSample, default, unsampledSlot, "MixedGenerationPrevented");
            }
        }

        private void LogDirectionalCascadeProvenance(
            DirectionalCascadeSourceState state,
            int cascadeIndex,
            in ShadowAtlasAllocation allocation,
            in DirectionalCascadeSampleState requestSample,
            in DirectionalCascadeSampleState renderedSample,
            in DirectionalCascadeAtlasSlot slot,
            string decision)
        {
            int mixedPrevented = decision == "MixedGenerationPrevented" ? 1 : 0;
            int staleSampled = slot.HasCascadeUniformData && slot.Fallback == ShadowFallbackMode.StaleTile ? 1 : 0;
            RecordDirectionalCascadeProvenanceCounters(staleSampled, mixedPrevented);

            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                (uint)cascadeIndex >= (uint)state.Slices.Count ||
                !Debug.ShouldLogEvery(
                    $"DirectionalShadowAudit.CascadeProvenance.{ID}.{state.Source}.{cascadeIndex}.{decision}",
                    TimeSpan.FromSeconds(1.0)))
                return;

            CascadeShadowSlice currentSlice = state.Slices[cascadeIndex];
            ulong renderedMatrixHash = slot.HasCascadeUniformData
                ? HashMatrix(slot.WorldToLightSpaceMatrix)
                : 0u;
            ulong currentMatrixHash = HashMatrix(currentSlice.WorldToLightSpaceMatrix);

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][CascadeProvenance] frame={0} light='{1}' source={2} cascade={3} decision={4} requestContent={5} allocationContent={6} allocationRenderedFrame={7} slotSampleContent={8} slotSampleFrame={9} currentMatrixHash={10:X16} renderedMatrixHash={11:X16} fallback={12} page={13} rect={14} sampleable={15} DirectionalCascade.StaleSampled={16} DirectionalCascade.MixedGenerationPrevented={17} DirectionalCascade.PhysicalReprojected=0 DirectionalCascade.ForcedFreshRender=0",
                RuntimeEngine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                state.Source,
                cascadeIndex,
                decision,
                requestSample.IsValid ? requestSample.ContentHash : 0u,
                allocation.ContentVersion,
                allocation.LastRenderedFrame,
                slot.HasCascadeUniformData
                    ? slot.ContentVersion
                    : renderedSample.IsValid ? renderedSample.ContentHash : 0u,
                slot.HasCascadeUniformData
                    ? slot.LastRenderedFrame
                    : renderedSample.IsValid ? renderedSample.RenderedFrame : 0u,
                currentMatrixHash,
                renderedMatrixHash,
                slot.Fallback,
                slot.PageIndex,
                FormatAtlasRect(slot.InnerPixelRect),
                slot.HasCascadeUniformData && IsDirectionalAtlasSlotSampleable(slot),
                staleSampled,
                mixedPrevented);
        }

        private static void RecordDirectionalCascadeProvenanceCounters(int staleSampled, int mixedGenerationPrevented)
        {
            if (staleSampled > 0)
            {
                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(
                    ERendererProfilerCounter.DirectionalCascadeStaleSampled,
                    staleSampled);
            }

            if (mixedGenerationPrevented > 0)
            {
                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(
                    ERendererProfilerCounter.DirectionalCascadeMixedGenerationPrevented,
                    mixedGenerationPrevented);
            }
        }

        private static string FormatAtlasRect(BoundingRectangle rect)
            => $"{rect.X},{rect.Y},{rect.Width}x{rect.Height}";

        private static ulong HashMatrix(Matrix4x4 matrix)
        {
            ulong hash = 14695981039346656037UL;
            AddFloatToHash(ref hash, matrix.M11);
            AddFloatToHash(ref hash, matrix.M12);
            AddFloatToHash(ref hash, matrix.M13);
            AddFloatToHash(ref hash, matrix.M14);
            AddFloatToHash(ref hash, matrix.M21);
            AddFloatToHash(ref hash, matrix.M22);
            AddFloatToHash(ref hash, matrix.M23);
            AddFloatToHash(ref hash, matrix.M24);
            AddFloatToHash(ref hash, matrix.M31);
            AddFloatToHash(ref hash, matrix.M32);
            AddFloatToHash(ref hash, matrix.M33);
            AddFloatToHash(ref hash, matrix.M34);
            AddFloatToHash(ref hash, matrix.M41);
            AddFloatToHash(ref hash, matrix.M42);
            AddFloatToHash(ref hash, matrix.M43);
            AddFloatToHash(ref hash, matrix.M44);
            return hash;
        }

        private static void AddFloatToHash(ref ulong hash, float value)
        {
            hash ^= BitConverter.SingleToUInt32Bits(value);
            hash *= 1099511628211UL;
        }

        internal void CommitRenderedCascadeAtlasSlot(
            ShadowRequestSource source,
            int index,
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution,
            DirectionalCascadeSampleState renderedSample)
        {
            DirectionalCascadeAtlasRenderCommit commit = new(
                source,
                index,
                allocation,
                recordIndex,
                nearPlane,
                farPlane,
                desiredResolution,
                renderedSample);
            Span<DirectionalCascadeAtlasRenderCommit> commits = stackalloc DirectionalCascadeAtlasRenderCommit[1];
            commits[0] = commit;
            CommitRenderedCascadeAtlasSlots(commits);
        }

        internal void CommitRenderedCascadeAtlasSlots(ReadOnlySpan<DirectionalCascadeAtlasRenderCommit> commits)
        {
            lock (_cascadeDataLock)
            {
                for (int i = 0; i < commits.Length; i++)
                    CommitRenderedCascadeAtlasSlotNoLock(commits[i]);
            }
        }

        private void CommitRenderedCascadeAtlasSlotNoLock(in DirectionalCascadeAtlasRenderCommit commit)
        {
            int index = commit.CascadeIndex;
            DirectionalCascadeSampleState renderedSample = commit.RenderedSample;
            if ((uint)index >= (uint)MaxCascadeRenderCount || !renderedSample.IsValid)
                return;

            ShadowRequestSource resolvedSource = commit.Source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : commit.Source;
            DirectionalCascadeSampleState sample = renderedSample with
            {
                IsValid = true,
                Source = resolvedSource,
                CascadeIndex = index,
                ContentHash = renderedSample.ContentHash != 0u
                    ? renderedSample.ContentHash
                    : commit.Allocation.ContentVersion,
                RenderedFrame = renderedSample.RenderedFrame != 0u
                    ? renderedSample.RenderedFrame
                    : commit.Allocation.LastRenderedFrame,
            };
            ShadowAtlasAllocation renderedAllocation = commit.Allocation with
            {
                ContentVersion = sample.ContentHash,
                LastRenderedFrame = sample.RenderedFrame,
                ActiveFallback = ShadowFallbackMode.None,
                SkipReason = SkipReason.None,
            };

            DirectionalCascadeSourceState state = GetCascadeSourceState(resolvedSource);
            state.RenderedSamples[index] = sample;
            DirectionalCascadeAtlasSlot renderedSlot = CreateAtlasSlot(
                renderedAllocation,
                commit.RecordIndex,
                commit.NearPlane,
                commit.FarPlane,
                commit.DesiredResolution,
                sample);
            state.AtlasSlots[index] = renderedSlot;

            // A render completion can race metadata publication for the next frame.
            // Carry it into the staged generation only when that generation still
            // requests the exact rendered content; otherwise its newer request must
            // remain authoritative.
            if (_directionalAtlasSlotPublishInProgress &&
                state.AtlasRequestContentHashes[index] == sample.ContentHash)
            {
                state.PendingAtlasSlots[index] = renderedSlot;
            }
        }

        internal void SetCascadeAtlasSlot(
            int index,
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution)
            => SetCascadeAtlasSlot(ShadowRequestSource.Desktop, index, allocation, recordIndex, nearPlane, farPlane, desiredResolution);

        internal void SetPrimaryAtlasSlot(
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution)
        {
            lock (_cascadeDataLock)
            {
                DirectionalCascadeAtlasSlot slot = CreateAtlasSlot(
                    allocation,
                    recordIndex,
                    nearPlane,
                    farPlane,
                    desiredResolution,
                    hasCascadeUniformData: false,
                    splitFarDistance: farPlane,
                    blendWidth: 0.0f,
                    biasMin: 0.0f,
                    biasMax: ShadowSlopeBiasTexels,
                    receiverOffset: 0.0f,
                    worldToLightSpaceMatrix: Matrix4x4.Identity);
                if (_directionalAtlasSlotPublishInProgress)
                {
                    _pendingPrimaryAtlasSlot = slot;
                    _pendingPrimaryAtlasSlotWritten = true;
                }
                else
                    _primaryAtlasSlot = slot;
            }
        }

        /// <summary>
        /// Gets the latest atlas allocation for a cascade when that slot is active.
        /// </summary>
        public bool TryGetCascadeAtlasSlot(int index, out DirectionalCascadeAtlasSlot slot)
            => TryGetCascadeAtlasSlot(ShadowRequestSource.Desktop, index, out slot);

        public bool TryGetCascadeAtlasSlot(ShadowRequestSource source, int index, out DirectionalCascadeAtlasSlot slot)
        {
            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                if ((uint)index < (uint)state.Slices.Count && (uint)index < (uint)state.AtlasSlots.Length)
                {
                    slot = state.AtlasSlots[index];
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
               slot.Fallback is ShadowFallbackMode.None or ShadowFallbackMode.StaleTile;

        /// <summary>
        /// Copies cascade atlas metadata into caller-provided buffers for GPU light records.
        /// </summary>
        internal void CopyPublishedCascadeAtlasUniformData(
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
            => CopyPublishedCascadeAtlasUniformData(ShadowRequestSource.Desktop, packed0, uvScaleBias, depthParams);

        internal void CopyPublishedCascadeAtlasUniformData(
            XRCamera? camera,
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
            => CopyPublishedCascadeAtlasUniformData(GetCascadeSourceForCamera(camera), packed0, uvScaleBias, depthParams);

        internal void CopyPublishedCascadeAtlasUniformData(
            ShadowRequestSource source,
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
        {
            int copyCount = Math.Min(MaxCascadeRenderCount, Math.Min(packed0.Length, Math.Min(uvScaleBias.Length, depthParams.Length)));

            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                for (int i = 0; i < copyCount; i++)
                {
                    DirectionalCascadeAtlasSlot slot = i < state.Slices.Count ? state.AtlasSlots[i] : default;
                    bool enabled = slot.HasCascadeUniformData &&
                        IsDirectionalAtlasSlotSampleable(slot);

                    ShadowFallbackMode fallback = enabled
                        ? ShadowFallbackMode.None
                        : slot.Fallback != ShadowFallbackMode.None
                            ? slot.Fallback
                            : ShadowFallbackMode.Lit;
                    int pageIndex = slot.HasAllocation ? slot.PageIndex : -1;
                    int recordIndex = slot.HasAllocation ? slot.RecordIndex : -1;
                    float nearPlane = slot.HasAllocation ? slot.NearPlane : NearZ;
                    float farPlane = slot.HasAllocation ? slot.FarPlane : 1.0f;

                    packed0[i] = new IVector4(enabled ? 1 : 0, pageIndex, (int)fallback, recordIndex);
                    uvScaleBias[i] = enabled ? slot.UvScaleBias : Vector4.Zero;
                    depthParams[i] = new Vector4(nearPlane, MathF.Max(farPlane, nearPlane + 0.001f), slot.TexelSize, slot.ResolutionScale);
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
            => CopyPublishedDirectionalAtlasUniformData(ShadowRequestSource.Desktop, useCascades, packed0, uvScaleBias, depthParams);

        internal void CopyPublishedDirectionalAtlasUniformData(
            XRCamera? camera,
            bool useCascades,
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
            => CopyPublishedDirectionalAtlasUniformData(GetCascadeSourceForCamera(camera), useCascades, packed0, uvScaleBias, depthParams);

        internal void CopyPublishedDirectionalAtlasUniformData(
            ShadowRequestSource source,
            bool useCascades,
            Span<IVector4> packed0,
            Span<Vector4> uvScaleBias,
            Span<Vector4> depthParams)
        {
            if (useCascades)
            {
                CopyPublishedCascadeAtlasUniformData(source, packed0, uvScaleBias, depthParams);
                return;
            }

            int copyCount = Math.Min(MaxCascadeRenderCount, Math.Min(packed0.Length, Math.Min(uvScaleBias.Length, depthParams.Length)));

            lock (_cascadeDataLock)
            {
                for (int i = 0; i < copyCount; i++)
                {
                    DirectionalCascadeAtlasSlot slot = i == 0 ? _primaryAtlasSlot : default;
                    bool enabled = IsDirectionalAtlasSlotSampleable(slot);

                    ShadowFallbackMode fallback = enabled
                        ? ShadowFallbackMode.None
                        : slot.HasAllocation
                            ? slot.Fallback != ShadowFallbackMode.None ? slot.Fallback : ShadowFallbackMode.Lit
                            : ShadowFallbackMode.Legacy;
                    int pageIndex = slot.HasAllocation ? slot.PageIndex : -1;
                    int recordIndex = slot.HasAllocation ? slot.RecordIndex : -1;
                    float nearPlane = slot.HasAllocation ? slot.NearPlane : NearZ;
                    float farPlane = slot.HasAllocation ? slot.FarPlane : 1.0f;

                    packed0[i] = new IVector4(enabled ? 1 : 0, pageIndex, (int)fallback, recordIndex);
                    uvScaleBias[i] = enabled ? slot.UvScaleBias : Vector4.Zero;
                    depthParams[i] = new Vector4(nearPlane, MathF.Max(farPlane, nearPlane + 0.001f), slot.TexelSize, slot.ResolutionScale);
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
            EnsureCascadeShadowResources(ShadowRequestSource.Desktop);
            if (RuntimeEngine.VRState.IsInVR)
                EnsureCascadeShadowResources(ShadowRequestSource.Hmd);
        }

        private void EnsureCascadeShadowResources(ShadowRequestSource source)
        {
            if (!CastsShadows || !CanRenderDirectionalCascadesForCurrentBackend())
                return;

            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            int requiredCascades = Math.Clamp(_cascadeCount, 1, MaxCascadeRenderCount);
            uint width = Math.Max(1u, ShadowMapResolutionWidth);
            uint height = Math.Max(1u, ShadowMapResolutionHeight);
            if (ShouldUseVulkanAtlasCascadeTargets(requiredCascades))
            {
                ReleaseCascadeReceiverResources(state);
                if (CascadeViewportSlotsMatch(state, requiredCascades, width, height))
                    return;

                EnsureCascadeCameraViewportSlots(state, requiredCascades, width, height, createFrameBuffers: false);
                return;
            }

            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            bool momentEncoding = selection.Encoding != EShadowMapEncoding.Depth;
            ShadowMapTextureFormat shadowFormat = GetShadowMapTextureFormat(selection.Format.StorageFormat);
            EShadowMapStorageFormat depthStorageFormat = IsDepthShadowMapStorageFormat(ShadowMapStorageFormat)
                ? ShadowMapStorageFormat
                : DefaultShadowMapStorageFormat;
            ShadowMapTextureFormat depthFormat = GetShadowMapTextureFormat(depthStorageFormat);
            ETexMinFilter minFilter = selection.Format.RequiresLinearFiltering
                ? (ShadowMomentUseMipmaps ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear)
                : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = selection.Format.RequiresLinearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            int smallestAllowedMomentMip = ResolveShadowMomentSmallestAllowedMipmapLevel(momentEncoding, ShadowMomentUseMipmaps, width, height);

            // On the Vulkan depth-encoding path the raster depth array is the receiver;
            // the color/moment array would be cleared and written but never sampled, so
            // skip allocating it entirely and render the cascades depth-only.
            bool needsColorArray = !ShouldUseVulkanRasterDepthReceiverTexture();

            bool colorArrayMismatch = needsColorArray
                ? state.ShadowMapTexture is null ||
                    state.ShadowMapTexture.Depth != (uint)requiredCascades ||
                    state.ShadowMapTexture.Width != width ||
                    state.ShadowMapTexture.Height != height ||
                    state.ShadowMapTexture.SizedInternalFormat != shadowFormat.SizedInternalFormat ||
                    state.ShadowMapTexture.MinFilter != minFilter ||
                    state.ShadowMapTexture.MagFilter != magFilter ||
                    state.ShadowMapTexture.AutoGenerateMipmaps != (momentEncoding && ShadowMomentUseMipmaps) ||
                    state.ShadowMapTexture.SmallestAllowedMipmapLevel != smallestAllowedMomentMip
                : state.ShadowMapTexture is not null;

            bool recreateTexture = colorArrayMismatch ||
                state.RasterDepthTexture is null ||
                state.RasterDepthTexture.Depth != (uint)requiredCascades ||
                state.RasterDepthTexture.Width != width ||
                state.RasterDepthTexture.Height != height ||
                state.RasterDepthTexture.SizedInternalFormat != depthFormat.SizedInternalFormat;

            if (recreateTexture)
            {
                InvalidateLegacyCascadeRender(state);
                state.ShadowMapTexture?.Destroy();
                state.ShadowMapTexture = null;
                state.RasterDepthTexture?.Destroy();

                if (needsColorArray)
                {
                    state.ShadowMapTexture = XRTexture2DArray.CreateFrameBufferTexture(
                        (uint)requiredCascades,
                        width,
                        height,
                        shadowFormat.InternalFormat,
                        shadowFormat.PixelFormat,
                        shadowFormat.PixelType,
                        EFrameBufferAttachment.ColorAttachment0);
                    state.ShadowMapTexture.Name = GetCascadeShadowResourceName(source, "ColorArray");
                    state.ShadowMapTexture.SamplerName = "ShadowMapArray";
                    state.ShadowMapTexture.MinFilter = minFilter;
                    state.ShadowMapTexture.MagFilter = magFilter;
                    state.ShadowMapTexture.AutoGenerateMipmaps = momentEncoding && ShadowMomentUseMipmaps;
                    state.ShadowMapTexture.SmallestAllowedMipmapLevel = smallestAllowedMomentMip;
                }

                state.RasterDepthTexture = XRTexture2DArray.CreateFrameBufferTexture(
                    (uint)requiredCascades,
                    width,
                    height,
                    depthFormat.InternalFormat,
                    depthFormat.PixelFormat,
                    depthFormat.PixelType,
                    EFrameBufferAttachment.DepthAttachment);
                state.RasterDepthTexture.Name = GetCascadeShadowResourceName(source, "RasterDepthArray");
                state.RasterDepthTexture.SamplerName = "ShadowRasterDepthArray";
            }

            if (recreateTexture || state.LayeredFrameBuffer is null)
            {
                state.LayeredFrameBuffer ??= new XRFrameBuffer();
                state.LayeredFrameBuffer.Name = GetCascadeShadowResourceName(source, "LayeredFbo");
                if (state.ShadowMapTexture is not null)
                {
                    state.LayeredFrameBuffer.SetRenderTargets(
                        (state.ShadowMapTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                        (state.RasterDepthTexture!, EFrameBufferAttachment.DepthAttachment, 0, -1));
                }
                else
                {
                    state.LayeredFrameBuffer.SetRenderTargets(
                        (state.RasterDepthTexture!, EFrameBufferAttachment.DepthAttachment, 0, -1));
                }
            }

            if (state.FrameBuffers.Length == requiredCascades && !recreateTexture)
                return;

            EnsureCascadeCameraViewportSlots(state, requiredCascades, width, height, createFrameBuffers: true);
        }

        private static bool CascadeViewportSlotsMatch(
            DirectionalCascadeSourceState state,
            int requiredCascades,
            uint width,
            uint height)
        {
            if (state.Viewports.Length != requiredCascades ||
                state.Cameras.Length != requiredCascades ||
                state.Transforms.Length != requiredCascades)
            {
                return false;
            }

            if (requiredCascades == 0)
                return true;

            int expectedWidth = checked((int)Math.Min(width, (uint)int.MaxValue));
            int expectedHeight = checked((int)Math.Min(height, (uint)int.MaxValue));
            XRViewport viewport = state.Viewports[0];
            return viewport.Width == expectedWidth && viewport.Height == expectedHeight;
        }

        private void EnsureCascadeCameraViewportSlots(
            DirectionalCascadeSourceState state,
            int requiredCascades,
            uint width,
            uint height,
            bool createFrameBuffers)
        {
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
                if (createFrameBuffers)
                {
                    frameBuffers[i] = state.ShadowMapTexture is not null
                        ? new XRFrameBuffer(
                            (state.ShadowMapTexture, EFrameBufferAttachment.ColorAttachment0, 0, i),
                            (state.RasterDepthTexture!, EFrameBufferAttachment.DepthAttachment, 0, i))
                        {
                            Name = GetCascadeShadowResourceName(state.Source, $"Layer{i}Fbo"),
                        }
                        : new XRFrameBuffer(
                            (state.RasterDepthTexture!, EFrameBufferAttachment.DepthAttachment, 0, i))
                        {
                            Name = GetCascadeShadowResourceName(state.Source, $"Layer{i}Fbo"),
                        };
                }
            }

            state.Transforms = transforms;
            state.Cameras = cameras;
            state.Viewports = viewports;
            state.FrameBuffers = createFrameBuffers ? frameBuffers : [];
        }

        private string GetCascadeShadowResourceName(string suffix)
            => GetCascadeShadowResourceName(ShadowRequestSource.Desktop, suffix);

        private string GetCascadeShadowResourceName(ShadowRequestSource source, string suffix)
            => $"DirectionalShadow.{ID:N}.Cascade.{source}.{suffix}";

        private void ReleaseCascadeShadowResources()
        {
            ClearCascadeShadows();

            ReleaseCascadeSourceResources(_desktopCascadeState);
            ReleaseCascadeSourceResources(_hmdCascadeState);
            if (_cascadeGeometryShadowMaterial is not null)
                _cascadeGeometryShadowMaterial.SettingShadowUniforms -= SetShadowMapUniforms;
            _cascadeGeometryShadowMaterial?.Destroy();
            _cascadeGeometryShadowMaterial = null;
            if (_cascadeInstancedShadowMaterial is not null)
                _cascadeInstancedShadowMaterial.SettingShadowUniforms -= SetShadowMapUniforms;
            _cascadeInstancedShadowMaterial?.Destroy();
            _cascadeInstancedShadowMaterial = null;
            if (_cascadeAtlasGeometryShadowMaterial is not null)
                _cascadeAtlasGeometryShadowMaterial.SettingShadowUniforms -= SetShadowMapUniforms;
            _cascadeAtlasGeometryShadowMaterial?.Destroy();
            _cascadeAtlasGeometryShadowMaterial = null;
            if (_cascadeAtlasInstancedShadowMaterial is not null)
                _cascadeAtlasInstancedShadowMaterial.SettingShadowUniforms -= SetShadowMapUniforms;
            _cascadeAtlasInstancedShadowMaterial?.Destroy();
            _cascadeAtlasInstancedShadowMaterial = null;
            if (_shadowAtlasMaterial is not null)
                _shadowAtlasMaterial.SettingShadowUniforms -= SetShadowMapUniforms;
            _shadowAtlasMaterial?.Destroy();
            _shadowAtlasMaterial = null;
        }

        private static void ReleaseCascadeSourceResources(DirectionalCascadeSourceState state)
        {
            // Readers on the render thread snapshot these arrays without taking the
            // component lock. Withdraw the published slots first, then destroy the
            // retired viewports so a reader can observe either a complete live set or
            // a complete empty set, never a viewport whose camera was cleared in place.
            XRViewport[] retiredViewports = state.Viewports;
            state.Viewports = [];
            state.Transforms = [];
            state.Cameras = [];

            for (int i = 0; i < retiredViewports.Length; i++)
                retiredViewports[i].Destroy();

            ReleaseCascadeReceiverResources(state);
        }

        private static void ReleaseCascadeReceiverResources(DirectionalCascadeSourceState state)
        {
            InvalidateLegacyCascadeRender(state);
            state.ShadowMapTexture?.Destroy();
            state.ShadowMapTexture = null;
            state.RasterDepthTexture?.Destroy();
            state.RasterDepthTexture = null;
            state.LayeredFrameBuffer?.Destroy();
            state.LayeredFrameBuffer = null;
            state.FrameBuffers = [];
        }

        private static void InvalidateLegacyCascadeRender(DirectionalCascadeSourceState state)
        {
            state.LegacyRenderedContentRevision = 0u;
            state.LegacyRenderedCascadeCount = 0;
            state.LegacyRenderedReceiverTexture = null;
        }

        private void MarkLegacyCascadeRenderComplete(ShadowRequestSource source, int cascadeCount)
        {
            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                XRTexture2DArray? receiverTexture = SelectCascadeReceiverTexture(state);
                if (receiverTexture is null || cascadeCount <= 0 || cascadeCount < state.Slices.Count)
                {
                    InvalidateLegacyCascadeRender(state);
                    return;
                }

                state.LegacyRenderedContentRevision = state.ContentRevision;
                state.LegacyRenderedCascadeCount = cascadeCount;
                state.LegacyRenderedReceiverTexture = receiverTexture;
            }
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
                ortho = new(width, height, nearZ, farZ)
                {
                    InheritAspectRatio = false
                };
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

        private int CopyCascadeSourceFrusta(ShadowRequestSource source, XRCamera primaryCamera, Span<Frustum> destination)
        {
            if (destination.Length <= 0)
                return 0;

            int count = 0;
            Array.Clear(_cascadeSourceCameras);
            AddCascadeSourceCameraFrustum(primaryCamera, destination, _cascadeSourceCameras, ref count);

            if (source == ShadowRequestSource.Hmd && RuntimeEngine.VRState.IsInVR)
            {
                IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
                AddCascadeSourceViewportFrustum(RuntimeEngine.VRState.LeftEyeViewport, world, destination, _cascadeSourceCameras, ref count);
                AddCascadeSourceViewportFrustum(RuntimeEngine.VRState.RightEyeViewport, world, destination, _cascadeSourceCameras, ref count);
                TryAddHmdCombinedCascadeSourceFrustum(world, destination, ref count);
            }

            return count;
        }

        internal static bool IsHmdEyeCameraForDirectionalCascades(XRCamera? camera)
            => camera is not null &&
               (ReferenceEquals(RuntimeEngine.VRState.LeftEyeViewport?.ActiveCamera, camera) ||
                ReferenceEquals(RuntimeEngine.VRState.RightEyeViewport?.ActiveCamera, camera) ||
                ReferenceEquals(RuntimeEngine.VRState.ViewInformation.LeftEyeCamera, camera) ||
                ReferenceEquals(RuntimeEngine.VRState.ViewInformation.RightEyeCamera, camera));

        private static bool ViewportPrefersCascadedDirectionalShadowSource(XRViewport viewport)
        {
            if (viewport.CameraComponent is { } cameraComponent)
                return cameraComponent.DirectionalShadowRenderingMode == EDirectionalShadowRenderingMode.Cascaded;

            return viewport.RendersToExternalSwapchainTarget && viewport.ActiveCamera is not null;
        }

        private static void AddCascadeSourceViewportFrustum(
            XRViewport? viewport,
            IRuntimeRenderWorld? world,
            Span<Frustum> destination,
            XRCamera?[] sourceCameras,
            ref int count)
        {
            if (count >= destination.Length ||
                viewport is null ||
                !ReferenceEquals(viewport.World, world) ||
                viewport.Suppress3DSceneRendering ||
                viewport.ActiveCamera is not XRCamera camera ||
                !ViewportPrefersCascadedDirectionalShadowSource(viewport))
            {
                return;
            }

            AddCascadeSourceCameraFrustum(camera, destination, sourceCameras, ref count);
        }

        private static void AddCascadeSourceCameraFrustum(
            XRCamera camera,
            Span<Frustum> destination,
            XRCamera?[] sourceCameras,
            ref int count)
        {
            if (count >= destination.Length || ContainsCascadeSourceCamera(sourceCameras, count, camera))
                return;

            destination[count] = camera.WorldFrustum();
            sourceCameras[count] = camera;
            count++;
        }

        private static bool ContainsCascadeSourceCamera(XRCamera?[] sourceCameras, int count, XRCamera camera)
        {
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(sourceCameras[i], camera))
                    return true;
            }

            return false;
        }

        private void TryAddHmdCombinedCascadeSourceFrustum(
            IRuntimeRenderWorld? world,
            Span<Frustum> destination,
            ref int count)
        {
            if (count >= destination.Length ||
                !RuntimeEngine.VRState.IsInVR ||
                !TryGetCascadeSourceEyeCamera(RuntimeEngine.VRState.LeftEyeViewport, world, out XRCamera? leftCamera) ||
                !TryGetCascadeSourceEyeCamera(RuntimeEngine.VRState.RightEyeViewport, world, out XRCamera? rightCamera))
            {
                return;
            }

            var hmdNode = RuntimeEngine.VRState.ViewInformation.HMDNode;
            if (hmdNode is null)
                return;

            if (!ProjectionMatrixCombiner.TryCombineProjectionMatrices(
                leftCamera.ProjectionMatrix,
                leftCamera.Transform.InverseLocalMatrix,
                rightCamera.ProjectionMatrix,
                rightCamera.Transform.InverseLocalMatrix,
                out Matrix4x4 combinedProjection) ||
                !Matrix4x4.Invert(combinedProjection, out Matrix4x4 inverseCombinedProjection))
            {
                return;
            }

            Frustum combinedLocalFrustum = new(inverseCombinedProjection);
            destination[count++] = combinedLocalFrustum.TransformedBy(hmdNode.Transform.RenderMatrix);
        }

        private static bool TryGetCascadeSourceEyeCamera(
            XRViewport? viewport,
            IRuntimeRenderWorld? world,
            out XRCamera camera)
        {
            camera = null!;

            if (viewport is null ||
                !ReferenceEquals(viewport.World, world) ||
                viewport.Suppress3DSceneRendering ||
                viewport.ActiveCamera is not XRCamera activeCamera ||
                !ViewportPrefersCascadedDirectionalShadowSource(viewport))
            {
                return false;
            }

            camera = activeCamera;
            return true;
        }

        private uint GetCascadeFitResolution(ShadowRequestSource source, int cascadeIndex, XRTexture2DArray? cascadeTexture)
        {
            if (!UsesDirectionalShadowAtlasForCurrentEncoding)
                return cascadeTexture is not null
                    ? Math.Max(cascadeTexture.Width, cascadeTexture.Height)
                    : Math.Max(ShadowMapResolutionWidth, ShadowMapResolutionHeight);

            lock (_cascadeDataLock)
            {
                DirectionalCascadeAtlasSlot[] atlasSlots = GetCascadeSourceState(source).AtlasSlots;
                if ((uint)cascadeIndex < (uint)atlasSlots.Length &&
                    atlasSlots[cascadeIndex].InnerPixelRect.Width > 0 &&
                    atlasSlots[cascadeIndex].InnerPixelRect.Height > 0)
                {
                    DirectionalCascadeAtlasSlot slot = atlasSlots[cascadeIndex];
                    return (uint)Math.Max(1, Math.Max(slot.InnerPixelRect.Width, slot.InnerPixelRect.Height));
                }
            }

            uint requested = Math.Max(ShadowMapResolutionWidth, ShadowMapResolutionHeight);
            return ShadowAtlasManager.NormalizeTileResolution(
                requested,
                RuntimeEngine.Rendering.Settings.MinShadowAtlasTileResolution,
                RuntimeEngine.Rendering.Settings.MaxShadowAtlasTileResolution,
                RuntimeEngine.Rendering.Settings.ShadowAtlasPageSize);
        }

        /// <summary>
        /// Clears all published cascade bounds, slices, and cascade atlas slot metadata.
        /// </summary>
        internal void ClearCascadeShadows()
        {
            ClearCascadeShadows(ShadowRequestSource.Desktop);
            ClearCascadeShadows(ShadowRequestSource.Hmd);
        }

        internal void ClearCascadeShadows(ShadowRequestSource source)
        {
            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState state = GetCascadeSourceState(source);
                state.Aabbs.Clear();
                state.Slices.Clear();
                Array.Clear(state.AtlasSlots);
                Array.Clear(state.PendingAtlasSlots);
                state.RangeNear = 0.0f;
                state.RangeFar = 0.0f;
                unchecked { state.ContentRevision++; }
                InvalidateLegacyCascadeRender(state);
            }
        }

        /// <summary>
        /// Rebuilds cascade cameras and published bounds from the active player camera for this frame.
        /// </summary>
        internal void UpdateCascadeShadows(XRCamera playerCamera)
            => UpdateCascadeShadows(GetCascadeSourceForCamera(playerCamera), playerCamera);

        internal void UpdateCascadeShadows(ShadowRequestSource source, XRCamera playerCamera)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                LogVulkanCascadeRenderingDisabledIfNeeded(source);
                ClearCascadeShadows(source);
                PublishVulkanCascadeRenderingDisabledPlan(source);
                return;
            }

            if (!CastsShadows || !EnableCascadedShadows || ShadowCamera is null)
            {
                LogCascadeClearReason("disabled-or-missing-shadow-camera");
                ClearCascadeShadows(source);
                return;
            }

            EnsureCascadeShadowResources(source);
            DirectionalCascadeSourceState state = GetCascadeSourceState(source);

            // Snapshot the cascade resource arrays so iteration is stable against
            // concurrent Ensure/Release calls from property changes on other threads.
            Transform[] transformsSnapshot = state.Transforms;
            XRCamera[] camerasSnapshot = state.Cameras;
            XRTexture2DArray? cascadeTexture = SelectCascadeReceiverTexture(state);
            bool usesVulkanAtlasTargets = ShouldUseVulkanAtlasCascadeTargets(Math.Clamp(_cascadeCount, 1, MaxCascadeRenderCount));
            if ((!usesVulkanAtlasTargets && cascadeTexture is null) ||
                camerasSnapshot.Length == 0 ||
                transformsSnapshot.Length != camerasSnapshot.Length)
            {
                LogCascadeClearReason($"invalid-resources texture={cascadeTexture is not null} cameras={camerasSnapshot.Length} transforms={transformsSnapshot.Length}");
                ClearCascadeShadows(source);
                return;
            }

            Span<Frustum> sourceFrusta = _cascadeSourceFrusta;
            int sourceFrustumCount = CopyCascadeSourceFrusta(source, playerCamera, sourceFrusta);
            if (sourceFrustumCount <= 0)
            {
                LogCascadeClearReason("no-source-frustum");
                ClearCascadeShadows(source);
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
                    uint fitResolution = GetCascadeFitResolution(source, resourceSlot, cascadeTexture);
                    uint biasResolution = UsesDirectionalShadowAtlasForCurrentEncoding
                        ? GetDesiredShadowAtlasResolution()
                        : fitResolution;
                    SnapCascadeCenterToTexels(ref centerLS, halfExtents, fitResolution);
                    Vector3 centerWS = Vector3.Transform(centerLS, lightToWorld);

                    UpdateCascadeShadowCamera(transformsSnapshot[resourceSlot], camerasSnapshot[resourceSlot], centerWS, halfExtents, lightRotation, lightDirection, NearZ);

                    Matrix4x4 cascadeView = camerasSnapshot[resourceSlot].Transform.InverseRenderMatrix;
                    Matrix4x4 cascadeProj = camerasSnapshot[resourceSlot].ProjectionMatrix;
                    Matrix4x4 viewProj = cascadeView * cascadeProj;
                    CascadeShadowBiasSettings biasSettings = ResolveCascadeBiasSettings(
                        cascadeIndex,
                        halfExtents,
                        biasResolution,
                        biasResolution,
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
                    state.Slices.Clear();
                    for (int i = 0; i < resourceSlot; i++)
                        state.Slices.Add(nextShadowSlices[i]);

                    state.Aabbs.Clear();
                    for (int i = 0; i < resourceSlot; i++)
                        state.Aabbs.Add(nextCascadeAabbs[i]);

                    state.RangeNear = cameraNear;
                    state.RangeFar = effectiveCascadeFar;
                    unchecked { state.ContentRevision++; }
                    if (state.ContentRevision == 0u)
                        state.ContentRevision = 1u;
                }

                LogCascadeUpdate(source, playerCamera, resourceSlot, cameraNear, effectiveCascadeFar, totalDepth, nextShadowSlices.AsSpan(0, resourceSlot));
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
            SetCascadeSourceWorldOverride(_desktopCascadeState);
            SetCascadeSourceWorldOverride(_hmdCascadeState);
        }

        private void SetCascadeSourceWorldOverride(DirectionalCascadeSourceState state)
        {
            IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
            for (int i = 0; i < state.Viewports.Length; i++)
                state.Viewports[i].WorldInstanceOverride = world;
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
            bool hasAnyCascadeReceiver =
                SelectCascadeReceiverTexture(_desktopCascadeState) is not null ||
                SelectCascadeReceiverTexture(_hmdCascadeState) is not null;
            bool hasAnyPublishedCascades =
                HasPublishedCascades(ShadowRequestSource.Desktop) ||
                HasPublishedCascades(ShadowRequestSource.Hmd);
            bool needsPrimary = !EnableCascadedShadows ||
                !hasAnyCascadeReceiver ||
                !hasAnyPublishedCascades ||
                world is null ||
                world.Lights.NeedsPrimaryDirectionalShadowMap();

            return needsPrimary &&
                (ShadowMap is not null ||
                 UsesDirectionalShadowAtlasForCurrentEncoding);
        }

        private DirectionalCascadeShadowRenderPlan CreateCascadeShadowRenderPlan(int cascadeCount)
            => CreateCascadeShadowRenderPlan(
                ShadowRequestSource.Desktop,
                cascadeCount,
                CanUseDirectionalCascadeShadowAtlasForCurrentBackend(cascadeCount)
                    ? DirectionalCascadeShadowBackend.AtlasPage
                    : DirectionalCascadeShadowBackend.LegacyTextureArray,
                hasGroupedAtlasAllocation: false);

        private DirectionalCascadeShadowRenderPlan CreateLegacyCascadeShadowRenderPlan(ShadowRequestSource source, int cascadeCount)
            => CreateCascadeShadowRenderPlan(
                source,
                cascadeCount,
                DirectionalCascadeShadowBackend.LegacyTextureArray,
                hasGroupedAtlasAllocation: false);

        private DirectionalCascadeShadowRenderPlan CreateAtlasCascadeShadowRenderPlan(
            ShadowRequestSource source,
            int cascadeCount,
            bool hasGroupedAtlasAllocation)
            => CreateCascadeShadowRenderPlan(
                source,
                cascadeCount,
                DirectionalCascadeShadowBackend.AtlasPage,
                hasGroupedAtlasAllocation);

        private DirectionalCascadeShadowRenderPlan CreateCascadeShadowRenderPlan(
            ShadowRequestSource source,
            int cascadeCount,
            DirectionalCascadeShadowBackend backend,
            bool hasGroupedAtlasAllocation)
        {
            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            EDirectionalCascadeShadowRenderMode requestedMode = _cascadeShadowRenderMode;
            if (!CanRenderDirectionalCascadesForCurrentBackend())
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, 0, DirectionalCascadeShadowFallbackReason.VulkanCascadeRenderingDisabled);

            if (cascadeCount <= 0)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.NoActiveCascades);

            if (backend == DirectionalCascadeShadowBackend.AtlasPage)
                return CreateAtlasPageCascadeShadowRenderPlan(state, requestedMode, cascadeCount, hasGroupedAtlasAllocation);

            if (SelectCascadeReceiverTexture(state) is null)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.MissingCascadeTextureArray);

            if (requestedMode == EDirectionalCascadeShadowRenderMode.Sequential)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.SequentialRequested);

            if (state.LayeredFrameBuffer is null)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.MissingLayeredFramebuffer);

            if (!RuntimeEngine.Rendering.State.SupportsOpenGLLayeredFramebuffers)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedLayeredFramebuffer);

            EDirectionalCascadeShadowRenderMode selectedMode = requestedMode == EDirectionalCascadeShadowRenderMode.Auto
                ? SelectAutomaticCascadeShadowRenderMode(cascadeCount, backend)
                : requestedMode;

            return selectedMode switch
            {
                EDirectionalCascadeShadowRenderMode.InstancedLayered => CreateInstancedCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount),
                EDirectionalCascadeShadowRenderMode.GeometryShader => CreateGeometryCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount),
                _ => CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedVertexStageLayerWrites),
            };
        }

        private DirectionalCascadeShadowRenderPlan CreateAtlasPageCascadeShadowRenderPlan(
            DirectionalCascadeSourceState state,
            EDirectionalCascadeShadowRenderMode requestedMode,
            int cascadeCount,
            bool hasGroupedAtlasAllocation)
        {
            if (requestedMode == EDirectionalCascadeShadowRenderMode.Sequential)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, DirectionalCascadeShadowBackend.AtlasPage, cascadeCount, DirectionalCascadeShadowFallbackReason.SequentialRequested);

            if (!hasGroupedAtlasAllocation)
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, DirectionalCascadeShadowBackend.AtlasPage, cascadeCount, DirectionalCascadeShadowFallbackReason.MissingGroupedAtlasAllocation);

            if (!RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                cascadeCount > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
            {
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, DirectionalCascadeShadowBackend.AtlasPage, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedViewportScissorArray);
            }

            EDirectionalCascadeShadowRenderMode selectedMode = requestedMode == EDirectionalCascadeShadowRenderMode.Auto
                ? SelectAutomaticCascadeShadowRenderMode(cascadeCount, DirectionalCascadeShadowBackend.AtlasPage)
                : requestedMode;

            return selectedMode switch
            {
                EDirectionalCascadeShadowRenderMode.InstancedLayered => CreateInstancedCascadeShadowRenderPlan(state, requestedMode, DirectionalCascadeShadowBackend.AtlasPage, cascadeCount),
                EDirectionalCascadeShadowRenderMode.GeometryShader => CreateGeometryCascadeShadowRenderPlan(state, requestedMode, DirectionalCascadeShadowBackend.AtlasPage, cascadeCount),
                _ => CreateSequentialCascadeShadowRenderPlan(state, requestedMode, DirectionalCascadeShadowBackend.AtlasPage, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedVertexStageViewportIndexWrites),
            };
        }

        private DirectionalCascadeShadowRenderPlan CreateInstancedCascadeShadowRenderPlan(
            DirectionalCascadeSourceState state,
            EDirectionalCascadeShadowRenderMode requestedMode,
            DirectionalCascadeShadowBackend backend,
            int cascadeCount)
        {
            if (backend == DirectionalCascadeShadowBackend.AtlasPage)
            {
                if (!RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex)
                    return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedVertexStageViewportIndexWrites);
            }
            else
            {
                if (!RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray)
                    return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedViewportArray);

                if (!RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering)
                    return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedVertexStageLayerWrites);
            }

            return new DirectionalCascadeShadowRenderPlan
            {
                RequestedMode = requestedMode,
                SelectedMode = EDirectionalCascadeShadowRenderMode.InstancedLayered,
                Backend = backend,
                ActiveCascadeCount = cascadeCount,
                LayeredFrameBuffer = backend == DirectionalCascadeShadowBackend.LegacyTextureArray ? state.LayeredFrameBuffer : null,
                CascadeTextureArray = state.ShadowMapTexture,
                FallbackReason = DirectionalCascadeShadowFallbackReason.None,
            };
        }

        private DirectionalCascadeShadowRenderPlan CreateGeometryCascadeShadowRenderPlan(
            DirectionalCascadeSourceState state,
            EDirectionalCascadeShadowRenderMode requestedMode,
            DirectionalCascadeShadowBackend backend,
            int cascadeCount)
        {
            if (backend == DirectionalCascadeShadowBackend.AtlasPage)
            {
                if (!RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex)
                    return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedGeometryStageViewportIndexWrites);
            }
            else if (!RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering)
            {
                return CreateSequentialCascadeShadowRenderPlan(state, requestedMode, backend, cascadeCount, DirectionalCascadeShadowFallbackReason.UnsupportedGeometryShader);
            }

            return new DirectionalCascadeShadowRenderPlan
            {
                RequestedMode = requestedMode,
                SelectedMode = EDirectionalCascadeShadowRenderMode.GeometryShader,
                Backend = backend,
                ActiveCascadeCount = cascadeCount,
                LayeredFrameBuffer = backend == DirectionalCascadeShadowBackend.LegacyTextureArray ? state.LayeredFrameBuffer : null,
                CascadeTextureArray = state.ShadowMapTexture,
                FallbackReason = DirectionalCascadeShadowFallbackReason.None,
            };
        }

        private DirectionalCascadeShadowRenderPlan CreateSequentialCascadeShadowRenderPlan(
            DirectionalCascadeSourceState state,
            EDirectionalCascadeShadowRenderMode requestedMode,
            DirectionalCascadeShadowBackend backend,
            int cascadeCount,
            DirectionalCascadeShadowFallbackReason fallbackReason)
            => new()
            {
                RequestedMode = requestedMode,
                SelectedMode = EDirectionalCascadeShadowRenderMode.Sequential,
                Backend = backend,
                ActiveCascadeCount = cascadeCount,
                LayeredFrameBuffer = null,
                CascadeTextureArray = SelectCascadeReceiverTexture(state),
                FallbackReason = fallbackReason,
            };

        private EDirectionalCascadeShadowRenderMode SelectAutomaticCascadeShadowRenderMode(
            int cascadeCount,
            DirectionalCascadeShadowBackend backend)
        {
            if (backend == DirectionalCascadeShadowBackend.AtlasPage)
            {
                if (RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray &&
                    cascadeCount <= RuntimeEngine.Rendering.State.MaxOpenGLViewports &&
                    RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex)
                {
                    return EDirectionalCascadeShadowRenderMode.InstancedLayered;
                }

                if (RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray &&
                    cascadeCount <= RuntimeEngine.Rendering.State.MaxOpenGLViewports &&
                    RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex)
                {
                    return EDirectionalCascadeShadowRenderMode.GeometryShader;
                }

                return EDirectionalCascadeShadowRenderMode.Sequential;
            }

            if (RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray &&
                RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering)
            {
                return EDirectionalCascadeShadowRenderMode.InstancedLayered;
            }

            if (RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering)
                return EDirectionalCascadeShadowRenderMode.GeometryShader;

            return EDirectionalCascadeShadowRenderMode.Sequential;
        }

        private void PublishCascadeShadowRenderPlan(in DirectionalCascadeShadowRenderPlan plan)
        {
            if (_effectiveCascadeShadowRenderMode != plan.SelectedMode)
                _effectiveCascadeShadowRenderMode = plan.SelectedMode;
            if (_effectiveCascadeShadowBackend != plan.Backend)
                _effectiveCascadeShadowBackend = plan.Backend;
            if (_cascadeShadowRenderFallbackReason != plan.FallbackReason)
                _cascadeShadowRenderFallbackReason = plan.FallbackReason;
        }

        public override void CollectVisibleItems()
        {
            if (!IsActiveInHierarchy || !CastsShadows)
                return;

            if (ShouldCollectPrimaryShadowViewport() &&
                TryGetPrimaryShadowViewportForProcessing(out XRViewport primaryViewport))
            {
                primaryViewport.CollectVisible(false);
            }

            CollectCascadeSourceVisibleItems(ShadowRequestSource.Desktop);
            CollectCascadeSourceVisibleItems(ShadowRequestSource.Hmd);
        }

        internal ulong GetShadowCasterCommandSetSignature(
            EShadowProjectionType projectionType,
            ShadowRequestSource source,
            int faceOrCascadeIndex)
        {
            if (projectionType == EShadowProjectionType.DirectionalPrimary)
            {
                return TryGetPrimaryShadowViewportForProcessing(out XRViewport primaryViewport)
                    ? primaryViewport.RenderPipelineInstance.MeshRenderCommands.ShadowCasterCommandSetSignature
                    : 0u;
            }

            if (projectionType != EShadowProjectionType.DirectionalCascade)
                return 0u;

            ShadowRequestSource resolvedSource = source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : source;
            lock (_cascadeDataLock)
            {
                XRViewport[] viewports = GetCascadeSourceState(resolvedSource).Viewports;
                return (uint)faceOrCascadeIndex < (uint)viewports.Length
                    ? viewports[faceOrCascadeIndex].RenderPipelineInstance.MeshRenderCommands.ShadowCasterCommandSetSignature
                    : 0u;
            }
        }

        private void CollectCascadeSourceVisibleItems(ShadowRequestSource source)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                PublishVulkanCascadeRenderingDisabledPlan(source);
                return;
            }

            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            XRViewport[] cascadeShadowViewports = state.Viewports;
            int cascadeCount = GetPublishedCascadeViewportCount(source, cascadeShadowViewports);
            if (cascadeCount <= 0)
                return;

            bool prepareAtlasGroupedCommands = ShouldPrepareAtlasGroupedCascadeCollection(cascadeCount);
            DirectionalCascadeShadowRenderPlan plan = prepareAtlasGroupedCommands
                ? CreateAtlasCascadeShadowRenderPlan(source, cascadeCount, hasGroupedAtlasAllocation: true)
                : CreateCascadeShadowRenderPlan(source, cascadeCount, CanUseDirectionalCascadeShadowAtlasForCurrentBackend(cascadeCount)
                    ? DirectionalCascadeShadowBackend.AtlasPage
                    : DirectionalCascadeShadowBackend.LegacyTextureArray,
                    hasGroupedAtlasAllocation: false);
            PublishCascadeShadowRenderPlan(plan);
            bool atlasPage = plan.IsAtlasPage;
            bool hasAtlasRenderRequest = !atlasPage || HasDirectionalCascadeAtlasRenderRequest(source, cascadeCount);
            if (atlasPage && !hasAtlasRenderRequest)
                return;

            if (plan.IsLayered || prepareAtlasGroupedCommands)
            {
                XRViewport viewport = cascadeShadowViewports[0];
                using (RuntimeEngine.Profiler.Start("DirectionalCascade.Group.CollectVisible"))
                    viewport.CollectVisible(false, collectionVolumeOverride: GetPublishedCascadeUnionCullVolume(source, cascadeCount));
                if (atlasPage)
                    MarkDirectionalCascadeAtlasViewportCollected(source, 0);

                if (prepareAtlasGroupedCommands && plan.IsAtlasPage)
                {
                    for (int i = 1; i < cascadeCount; i++)
                    {
                        if (!ShouldCollectDirectionalCascadeAtlasViewport(source, i))
                            continue;

                        using (RuntimeEngine.Profiler.Start("DirectionalCascade.Cascade.CollectVisible"))
                            cascadeShadowViewports[i].CollectVisible(false, collectionVolumeOverride: GetPublishedCascadeCullVolume(source, i));
                        MarkDirectionalCascadeAtlasViewportCollected(source, i);
                    }
                }
            }
            else
            {
                LogCascadeRenderModeFallbackIfNeeded(plan);
                for (int i = 0; i < cascadeCount; i++)
                {
                    if (atlasPage && !ShouldCollectDirectionalCascadeAtlasViewport(source, i))
                        continue;

                    using (RuntimeEngine.Profiler.Start("DirectionalCascade.Cascade.CollectVisible"))
                        cascadeShadowViewports[i].CollectVisible(false, collectionVolumeOverride: GetPublishedCascadeCullVolume(source, i));
                    if (atlasPage)
                        MarkDirectionalCascadeAtlasViewportCollected(source, i);
                }
            }
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!IsActiveInHierarchy || !CastsShadows)
                return;

            if (ShouldCollectPrimaryShadowViewport() &&
                TryGetPrimaryShadowViewportForProcessing(out XRViewport primaryViewport))
            {
                primaryViewport.SwapBuffers();
            }

            SwapCascadeSourceBuffers(ShadowRequestSource.Desktop);
            SwapCascadeSourceBuffers(ShadowRequestSource.Hmd);

            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }

        private void SwapCascadeSourceBuffers(ShadowRequestSource source)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                PublishVulkanCascadeRenderingDisabledPlan(source);
                return;
            }

            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            XRViewport[] cascadeShadowViewports = state.Viewports;
            int cascadeCount = GetPublishedCascadeViewportCount(source, cascadeShadowViewports);
            if (cascadeCount <= 0)
                return;

            bool prepareAtlasGroupedCommands = ShouldPrepareAtlasGroupedCascadeCollection(cascadeCount);
            DirectionalCascadeShadowRenderPlan plan = prepareAtlasGroupedCommands
                ? CreateAtlasCascadeShadowRenderPlan(source, cascadeCount, hasGroupedAtlasAllocation: true)
                : CreateCascadeShadowRenderPlan(source, cascadeCount, CanUseDirectionalCascadeShadowAtlasForCurrentBackend(cascadeCount)
                    ? DirectionalCascadeShadowBackend.AtlasPage
                    : DirectionalCascadeShadowBackend.LegacyTextureArray,
                    hasGroupedAtlasAllocation: false);
            PublishCascadeShadowRenderPlan(plan);
            bool atlasPage = plan.IsAtlasPage;
            bool hasAtlasRenderRequest = !atlasPage || HasDirectionalCascadeAtlasRenderRequest(source, cascadeCount);
            if (atlasPage && !hasAtlasRenderRequest)
                return;

            if (plan.IsLayered && !prepareAtlasGroupedCommands)
            {
                if (!atlasPage || ShouldSwapDirectionalCascadeAtlasViewport(source, 0))
                {
                    cascadeShadowViewports[0].SwapBuffers();
                    if (atlasPage)
                        MarkDirectionalCascadeAtlasViewportSwapped(source, 0);
                }
            }
            else
            {
                if (!prepareAtlasGroupedCommands)
                    LogCascadeRenderModeFallbackIfNeeded(plan);

                for (int i = 0; i < cascadeCount; i++)
                {
                    if (atlasPage && !ShouldSwapDirectionalCascadeAtlasViewport(source, i))
                        continue;

                    cascadeShadowViewports[i].SwapBuffers();
                    if (atlasPage)
                        MarkDirectionalCascadeAtlasViewportSwapped(source, i);
                }
            }
        }

        private bool ShouldPrepareAtlasGroupedCascadeCollection(int cascadeCount)
            => CanUseDirectionalCascadeShadowAtlasForCurrentBackend(cascadeCount) &&
                cascadeCount > 1 &&
                SupportsDirectionalCascadeAtlasGroupedRendering(cascadeCount);

        internal bool CanUseDirectionalCascadeShadowAtlasForCurrentBackend(int cascadeCount)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
                return false;

            if (!UsesDirectionalShadowAtlasForCurrentEncoding)
                return false;

            if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan)
            {
                // Keep the known Monado OpenXR Vulkan path protected, but let
                // SteamVR and ordinary Vulkan sessions exercise the atlas toggle.
                // Grouped atlas rendering is still gated separately.
                return !IsKnownMonadoOpenXrRuntime();
            }

            return cascadeCount > 0;
        }

        internal bool CanUseLegacyLayeredDirectionalCascadeShadowRendering(int cascadeCount)
            => CanUseLegacyLayeredDirectionalCascadeShadowRendering(ShadowRequestSource.Desktop, cascadeCount);

        internal bool CanUseLegacyLayeredDirectionalCascadeShadowRendering(ShadowRequestSource source, int cascadeCount)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
                return false;

            if (cascadeCount <= 1)
                return false;

            DirectionalCascadeShadowRenderPlan plan = CreateLegacyCascadeShadowRenderPlan(source, cascadeCount);
            return plan.IsLayered;
        }

        internal bool CanRenderGroupedCascadeShadowAtlasTiles(in ShadowAtlasGroupedDirectionalCascadeAllocation group)
        {
            if (!CastsShadows ||
                !EnableCascadedShadows ||
                !CanRenderDirectionalCascadesForCurrentBackend() ||
                World is null ||
                group.CascadeCount <= 1 ||
                group.Members is null ||
                group.Members.Length < group.CascadeCount)
            {
                return false;
            }

            ShadowRequestSource source = group.Source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : group.Source;
            DirectionalCascadeSourceState sourceState = GetCascadeSourceState(source);
            XRViewport[] cascadeShadowViewports = sourceState.Viewports;
            int cascadeCount = GetPublishedCascadeViewportCount(source, cascadeShadowViewports);
            if (cascadeCount <= 1)
                return false;

            if (!SupportsDirectionalCascadeAtlasGroupedRendering(cascadeCount))
                return false;

            DirectionalCascadeShadowRenderPlan plan = CreateAtlasCascadeShadowRenderPlan(source, cascadeCount, hasGroupedAtlasAllocation: true);
            if (!plan.IsLayered ||
                cascadeShadowViewports[0].RenderPipeline is not ShadowRenderPipeline)
            {
                return false;
            }

            Span<Matrix4x4> publishedMatrices = stackalloc Matrix4x4[MaxCascadeRenderCount];
            int publishedMatrixCount = CopyPublishedCascadeMatrices(source, publishedMatrices);
            int groupedCount = Math.Min(group.CascadeCount, MaxCascadeRenderCount);
            for (int i = 0; i < groupedCount; i++)
            {
                ShadowAtlasGroupedAllocationMember member = group.Members[i];
                if ((uint)member.ViewportScissorIndex >= (uint)groupedCount ||
                    (uint)member.CascadeIndex >= (uint)publishedMatrixCount ||
                    member.InnerPixelRect.Width <= 0 ||
                    member.InnerPixelRect.Height <= 0)
                {
                    return false;
                }
            }

            return true;
        }

        private bool SupportsDirectionalCascadeAtlasGroupedRendering(int cascadeCount)
        {
            if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan)
            {
                // The grouped atlas path hang was observed with the Monado OpenXR
                // Vulkan runtime. SteamVR and ordinary Vulkan sessions should use
                // the same capability checks as OpenGL.
                if (IsKnownMonadoOpenXrRuntime())
                    return false;
            }

            if (cascadeCount <= 1 ||
                _cascadeShadowRenderMode == EDirectionalCascadeShadowRenderMode.Sequential ||
                !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                cascadeCount > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
            {
                return false;
            }

            return _cascadeShadowRenderMode switch
            {
                EDirectionalCascadeShadowRenderMode.InstancedLayered => RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex,
                EDirectionalCascadeShadowRenderMode.GeometryShader => RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex,
                EDirectionalCascadeShadowRenderMode.Auto => RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex ||
                    RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex,
                _ => false,
            };
        }

        private XRMaterial ShadowAtlasMaterial => _shadowAtlasMaterial ??= CreateShadowAtlasMaterial();

        private XRMaterial CreateShadowAtlasMaterial()
        {
            XRMaterial mat = new(new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CascadeGeometryShadowMaterial
            => _cascadeGeometryShadowMaterial ??= CreateCascadeGeometryShadowMaterial();

        private XRMaterial CascadeInstancedShadowMaterial
            => _cascadeInstancedShadowMaterial ??= CreateCascadeInstancedShadowMaterial();

        private XRMaterial CascadeAtlasGeometryShadowMaterial
            => _cascadeAtlasGeometryShadowMaterial ??= CreateCascadeAtlasGeometryShadowMaterial();

        private XRMaterial CascadeAtlasInstancedShadowMaterial
            => _cascadeAtlasInstancedShadowMaterial ??= CreateCascadeAtlasInstancedShadowMaterial();

        private XRMaterial CreateCascadeGeometryShadowMaterial()
        {
            XRMaterial mat = new(
                XRShader.EngineShader("DirectionalCascadeShadowDepth.gs", EShaderType.Geometry),
                new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.DirectionalCascadeShadowMaterialKind = EDirectionalCascadeShadowMaterialKind.GeometryShader;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreateCascadeInstancedShadowMaterial()
        {
            XRMaterial mat = new(new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.DirectionalCascadeShadowMaterialKind = EDirectionalCascadeShadowMaterialKind.InstancedLayered;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreateCascadeAtlasGeometryShadowMaterial()
        {
            XRMaterial mat = new(
                XRShader.EngineShader("DirectionalCascadeAtlasShadowDepth.gs", EShaderType.Geometry),
                new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.DirectionalCascadeShadowMaterialKind = EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreateCascadeAtlasInstancedShadowMaterial()
        {
            XRMaterial mat = new(new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.DirectionalCascadeShadowMaterialKind = EDirectionalCascadeShadowMaterialKind.AtlasInstancedLayered;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        /// <summary>
        /// Renders a cascade shadow camera into a reserved shadow-atlas tile.
        /// </summary>
        internal bool RenderCascadeShadowAtlasTile(int cascadeIndex, XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
            => RenderCascadeShadowAtlasTile(ShadowRequestSource.Desktop, cascadeIndex, atlasFbo, renderRect, collectVisibleNow);

        internal bool RenderCascadeShadowAtlasTile(ShadowRequestSource source, int cascadeIndex, XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
        {
            if (!CastsShadows ||
                !EnableCascadedShadows ||
                !CanRenderDirectionalCascadesForCurrentBackend() ||
                World is null ||
                renderRect.Width <= 0 ||
                renderRect.Height <= 0)
                return false;

            DirectionalCascadeSourceState sourceState = GetCascadeSourceState(source);
            XRViewport[] cascadeShadowViewports = sourceState.Viewports;
            int cascadeCount = GetPublishedCascadeViewportCount(source, cascadeShadowViewports);
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
            shadowPipeline.ClearColor = GetShadowMapClearColor();
            try
            {
                var state = viewport.RenderPipelineInstance.RenderState;
                using var renderArea = state.PushRenderArea(renderRect);
                using var cropArea = state.PushCropArea(renderRect);
                using var renderSample = RuntimeEngine.Profiler.Start("DirectionalCascade.Cascade.CommandRecording");
                viewport.Render(atlasFbo, null, null, true, ShadowAtlasMaterial);
            }
            finally
            {
                shadowPipeline.PreserveExistingRenderArea = previousPreserveArea;
            }

            LogDirectionalAtlasTileRender(source, "cascade", cascadeIndex, renderRect, collectVisibleNow, viewport.Camera);
            return true;
        }

        /// <summary>
        /// Renders all grouped directional cascades into one atlas page using indexed viewport/scissor state.
        /// </summary>
        internal bool RenderGroupedCascadeShadowAtlasTiles(
            in ShadowAtlasGroupedDirectionalCascadeAllocation group,
            XRFrameBuffer atlasFbo,
            bool collectVisibleNow)
        {
            if (!CastsShadows ||
                !EnableCascadedShadows ||
                !CanRenderDirectionalCascadesForCurrentBackend() ||
                World is null ||
                group.CascadeCount <= 1 ||
                group.Members is null ||
                group.Members.Length < group.CascadeCount ||
                atlasFbo.Width <= 0 ||
                atlasFbo.Height <= 0)
            {
                return false;
            }

            ShadowRequestSource source = group.Source == ShadowRequestSource.Default
                ? ShadowRequestSource.Desktop
                : group.Source;
            DirectionalCascadeSourceState sourceState = GetCascadeSourceState(source);
            XRViewport[] cascadeShadowViewports = sourceState.Viewports;
            int cascadeCount = GetPublishedCascadeViewportCount(source, cascadeShadowViewports);
            if (cascadeCount <= 0)
                return false;

            if (!SupportsDirectionalCascadeAtlasGroupedRendering(cascadeCount))
                return false;

            DirectionalCascadeShadowRenderPlan plan = CreateAtlasCascadeShadowRenderPlan(source, cascadeCount, hasGroupedAtlasAllocation: true);
            PublishCascadeShadowRenderPlan(plan);
            if (!plan.IsLayered)
            {
                LogCascadeRenderModeFallbackIfNeeded(plan);
                return false;
            }

            XRViewport viewport = cascadeShadowViewports[0];
            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            Span<Matrix4x4> publishedMatrices = stackalloc Matrix4x4[MaxCascadeRenderCount];
            int publishedMatrixCount = CopyPublishedCascadeMatrices(source, publishedMatrices);
            Span<Matrix4x4> groupedMatrices = stackalloc Matrix4x4[MaxCascadeRenderCount];
            Span<BoundingRectangle> indexedRects = stackalloc BoundingRectangle[MaxCascadeRenderCount];

            int groupedCount = Math.Min(group.CascadeCount, MaxCascadeRenderCount);
            for (int i = 0; i < groupedCount; i++)
            {
                ShadowAtlasGroupedAllocationMember member = group.Members[i];
                if ((uint)member.ViewportScissorIndex >= (uint)groupedCount ||
                    (uint)member.CascadeIndex >= (uint)publishedMatrixCount ||
                    member.InnerPixelRect.Width <= 0 ||
                    member.InnerPixelRect.Height <= 0)
                {
                    return false;
                }

                groupedMatrices[member.ViewportScissorIndex] = publishedMatrices[member.CascadeIndex];
                indexedRects[member.ViewportScissorIndex] = member.InnerPixelRect;
                _groupedAtlasClearRects[member.ViewportScissorIndex] = member.InnerPixelRect;
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            BoundingRectangle[]? previousIndexedClearRegions = shadowPipeline.IndexedClearRegions;
            int previousIndexedClearRegionCount = shadowPipeline.IndexedClearRegionCount;
            shadowPipeline.PreserveExistingRenderArea = true;
            shadowPipeline.IndexedClearRegions = _groupedAtlasClearRects;
            shadowPipeline.IndexedClearRegionCount = groupedCount;
            shadowPipeline.ClearColor = GetShadowMapClearColor();
            try
            {
                int pageWidth = checked((int)atlasFbo.Width);
                int pageHeight = checked((int)atlasFbo.Height);
                BoundingRectangle pageRect = new(0, 0, pageWidth, pageHeight);
                var state = viewport.RenderPipelineInstance.RenderState;
                using var renderArea = state.PushRenderArea(pageRect);
                using var cropArea = state.PushCropArea(pageRect);
                using var indexedState = state.PushIndexedViewportScissors(indexedRects[..groupedCount], indexedRects[..groupedCount]);
                using var directionalCascadePass = state.PushDirectionalCascadeLayeredShadowPass(
                    plan.IsInstancedLayered,
                    groupedMatrices[..groupedCount],
                    atlasGrouped: true);

                XRMaterial groupedMaterial = plan.IsInstancedLayered
                    ? CascadeAtlasInstancedShadowMaterial
                    : CascadeAtlasGeometryShadowMaterial;
                using var renderSample = RuntimeEngine.Profiler.Start("DirectionalCascade.Group.CommandRecording");
                viewport.Render(atlasFbo, null, null, true, groupedMaterial);
            }
            finally
            {
                shadowPipeline.PreserveExistingRenderArea = previousPreserveArea;
                shadowPipeline.IndexedClearRegions = previousIndexedClearRegions;
                shadowPipeline.IndexedClearRegionCount = previousIndexedClearRegionCount;
            }

            LogDirectionalAtlasGroupedRender(group, collectVisibleNow, viewport.Camera);
            return true;
        }

        /// <summary>
        /// Renders the primary directional shadow camera into a reserved shadow-atlas tile.
        /// </summary>
        internal bool RenderPrimaryShadowAtlasTile(XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
        {
            if (!CastsShadows ||
                World is null ||
                renderRect.Width <= 0 ||
                renderRect.Height <= 0 ||
                !TryGetPrimaryShadowViewportForProcessing(out XRViewport viewport))
                return false;

            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            shadowPipeline.PreserveExistingRenderArea = true;
            shadowPipeline.ClearColor = GetShadowMapClearColor();
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

            LogDirectionalAtlasTileRender(ShadowRequestSource.Default, "primary", 0, renderRect, collectVisibleNow, viewport.Camera);
            return true;
        }

        public override void RenderShadowMap(bool collectVisibleNow = false)
            => RenderShadowMap(collectVisibleNow, renderCascades: true);

        internal void RenderShadowMap(bool collectVisibleNow, bool renderCascades)
        {
            if (!IsActiveInHierarchy || !CastsShadows)
                return;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            var shadowMap = ShadowMap;
            XRMaterial? shadowMaterial = shadowMap?.Material;
            int cascadeCount = ActiveCascadeCount;

            LogLegacyDirectionalShadowRender(renderCascades, shadowMap is not null, shadowMaterial is not null, cascadeCount);

            if (ShouldCollectPrimaryShadowViewport() &&
                shadowMap is not null &&
                shadowMaterial is not null &&
                TryGetPrimaryShadowViewportForProcessing(out XRViewport primaryViewport))
            {
                if (primaryViewport.RenderPipeline is ShadowRenderPipeline shadowPipeline)
                    shadowPipeline.ClearColor = GetShadowMapClearColor();

                primaryViewport.Render(shadowMap, null, null, true, shadowMaterial);
                GenerateMomentShadowMipmapsIfNeeded();
            }

            if (shadowMaterial is null)
                return;

            if (renderCascades)
            {
                RenderCascadeShadowMaps(ShadowRequestSource.Desktop, shadowMaterial);
                RenderCascadeShadowMaps(ShadowRequestSource.Hmd, shadowMaterial);
            }
        }

        private void GenerateMomentShadowMipmapsIfNeeded()
        {
            if (!CastsShadows ||
                !ShadowMomentUseMipmaps ||
                ShadowMapEncoding == EShadowMapEncoding.Depth ||
                ShadowMap?.Material?.Textures is not { } textures)
            {
                return;
            }

            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            if (selection.Encoding == EShadowMapEncoding.Depth)
                return;

            for (int i = 0; i < textures.Count; i++)
            {
                if (textures[i] is XRTexture2D texture && texture.SamplerName == "ShadowMap")
                {
                    texture.GenerateMipmapsGPU();
                    return;
                }
            }
        }

        private void GenerateCascadeMomentShadowMipmapsIfNeeded(ShadowRequestSource source)
        {
            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            if (!CastsShadows ||
                !ShadowMomentUseMipmaps ||
                state.ShadowMapTexture is null)
            {
                return;
            }

            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            if (selection.Encoding == EShadowMapEncoding.Depth)
                return;

            state.ShadowMapTexture.GenerateMipmapsGPU();
        }

        private void RenderCascadeShadowMaps(ShadowRequestSource source, XRMaterial sequentialShadowMaterial)
        {
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                PublishVulkanCascadeRenderingDisabledPlan(source);
                return;
            }

            DirectionalCascadeSourceState state = GetCascadeSourceState(source);
            XRViewport[] cascadeShadowViewports = state.Viewports;
            XRFrameBuffer[] cascadeShadowFrameBuffers = state.FrameBuffers;
            int cascadeCount = GetPublishedCascadeRenderCount(source, cascadeShadowViewports, cascadeShadowFrameBuffers);
            if (cascadeCount <= 0)
                return;

            DirectionalCascadeShadowRenderPlan plan = CreateLegacyCascadeShadowRenderPlan(source, cascadeCount);
            PublishCascadeShadowRenderPlan(plan);
            var clearColor = GetShadowMapClearColor();
            for (int i = 0; i < cascadeCount; i++)
                if (cascadeShadowViewports[i].RenderPipeline is ShadowRenderPipeline shadowPipeline)
                    shadowPipeline.ClearColor = clearColor;

            if (plan.IsLayered && plan.LayeredFrameBuffer is not null)
            {
                Span<Matrix4x4> cascadeMatrices = stackalloc Matrix4x4[MaxCascadeRenderCount];
                int matrixCount = CopyPublishedCascadeMatrices(source, cascadeMatrices);
                int layerCount = Math.Min(plan.ActiveCascadeCount, matrixCount);
                XRMaterial layeredShadowMaterial = plan.IsInstancedLayered
                    ? CascadeInstancedShadowMaterial
                    : CascadeGeometryShadowMaterial;
                using var directionalCascadePass = cascadeShadowViewports[0].RenderPipelineInstance.RenderState
                    .PushDirectionalCascadeLayeredShadowPass(plan.IsInstancedLayered, cascadeMatrices[..layerCount]);
                cascadeShadowViewports[0].Render(plan.LayeredFrameBuffer, null, null, true, layeredShadowMaterial);
                MarkLegacyCascadeRenderComplete(source, layerCount);
                return;
            }

            LogCascadeRenderModeFallbackIfNeeded(plan);
            for (int i = 0; i < cascadeCount; i++)
                cascadeShadowViewports[i].Render(cascadeShadowFrameBuffers[i], null, null, true, sequentialShadowMaterial);

            GenerateCascadeMomentShadowMipmapsIfNeeded(source);
            MarkLegacyCascadeRenderComplete(source, cascadeCount);
        }

        private int CopyPublishedCascadeMatrices(Span<Matrix4x4> matrices)
            => CopyPublishedCascadeMatrices(ShadowRequestSource.Desktop, matrices);

        private int CopyPublishedCascadeMatrices(ShadowRequestSource source, Span<Matrix4x4> matrices)
        {
            int copyCount = Math.Min(MaxCascadeRenderCount, matrices.Length);
            lock (_cascadeDataLock)
            {
                List<CascadeShadowSlice> slices = GetCascadeSourceState(source).Slices;
                copyCount = Math.Min(copyCount, slices.Count);
                for (int i = 0; i < copyCount; i++)
                    matrices[i] = slices[i].WorldToLightSpaceMatrix;
            }

            return copyCount;
        }

        private void LogCascadeRenderModeFallbackIfNeeded(in DirectionalCascadeShadowRenderPlan plan)
        {
            if (plan.FallbackReason is DirectionalCascadeShadowFallbackReason.None or DirectionalCascadeShadowFallbackReason.SequentialRequested ||
                plan.ActiveCascadeCount <= 0)
            {
                return;
            }

            Debug.LightingWarningEvery(
                $"DirectionalCascadeRenderModeFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(2.0),
                "[DirectionalShadowAudit] Directional cascade render mode fallback for '{0}': requested={1}, effective={2}, reason={3}.",
                SceneNode?.Name ?? Name ?? GetType().Name,
                plan.RequestedMode,
                plan.SelectedMode,
                plan.FallbackReason);
        }

        private void LogCascadeClearReason(string reason)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.CascadeClear.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][CascadeClear] frame={0} light='{1}' reason={2} casts={3} cascadesEnabled={4} shadowCamera={5} useDirAtlas={6}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                reason,
                CastsShadows,
                EnableCascadedShadows,
                ShadowCamera is not null,
                RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas);
        }

        private void LogCascadeUpdate(
            ShadowRequestSource source,
            XRCamera sourceCamera,
            int cascadeCount,
            float cameraNear,
            float effectiveCascadeFar,
            float totalDepth,
            ReadOnlySpan<CascadeShadowSlice> slices)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.CascadeUpdate.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Vector3 sourcePosition = sourceCamera.Transform.RenderTranslation;
            DirectionalCascadeSourceState sourceState = GetCascadeSourceState(source);
            XRTexture2DArray? receiverTexture = SelectCascadeReceiverTexture(sourceState);
            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][CascadeUpdate] frame={0} light='{1}' source={2} sourceCamera={3} sourcePos={4} sourceNear={5:F3} sourceFar={6:F3} sourceShadowMax={7:F3} rangeNear={8:F3} rangeFar={9:F3} totalDepth={10:F3} activeCascades={11} requestedCascades={12} atlasSetting={13} cascadeColorTex={14} cascadeRasterDepthTex={15} cascadeReceiverTex={16}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                source,
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
                RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas,
                sourceState.ShadowMapTexture is not null,
                sourceState.RasterDepthTexture is not null,
                receiverTexture is not null);

            int detailCount = Math.Min(cascadeCount, Math.Min(4, slices.Length));
            for (int i = 0; i < detailCount; i++)
            {
                CascadeShadowSlice slice = slices[i];
                Debug.Lighting(
                    EOutputVerbosity.Normal,
                    false,
                    "[DirectionalShadowAudit][CascadeSlice] frame={0} light='{1}' slot={2} cascadeIndex={3} splitFar={4:F3} blendWidth={5:F3} texelWorld={6:F6} center={7} halfExtents={8} biasMin={9:E3} biasMax={10:F3} receiverOffset={11:F6}",
                    RuntimeEngine.Rendering.State.RenderFrameId,
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
            ShadowRequestSource source,
            string projection,
            int cascadeIndex,
            BoundingRectangle renderRect,
            bool collectVisibleNow,
            XRCamera? camera)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasTileRender.{GetHashCode()}.{projection}.{cascadeIndex}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][AtlasTileRender] frame={0} light='{1}' projection={2} cascadeOrFace={3} rect={4},{5},{6}x{7} collectVisibleNow={8} camera={9} splitFar={10:F3}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                projection,
                cascadeIndex,
                renderRect.X,
                renderRect.Y,
                renderRect.Width,
                renderRect.Height,
                collectVisibleNow,
                camera?.GetHashCode().ToString() ?? "<null>",
                projection == "cascade" ? GetCascadeSplit(source, cascadeIndex) : 0.0f);
        }

        private void LogDirectionalAtlasGroupedRender(
            in ShadowAtlasGroupedDirectionalCascadeAllocation group,
            bool collectVisibleNow,
            XRCamera? camera)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasGroupedRender.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][AtlasGroupedRender] frame={0} light='{1}' cascades={2} page={3} mode={4} backend={5} collectVisibleNow={6} camera={7}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                group.CascadeCount,
                group.PageIndex,
                _effectiveCascadeShadowRenderMode,
                _effectiveCascadeShadowBackend,
                collectVisibleNow,
                camera?.GetHashCode().ToString() ?? "<null>");
        }

        private void LogLegacyDirectionalShadowRender(bool renderCascades, bool hasShadowMap, bool hasShadowMaterial, int cascadeCount)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.LegacyRender.{GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][LegacyRender] frame={0} light='{1}' useDirAtlas={2} renderCascades={3} hasShadowMap={4} hasShadowMaterial={5} cascadeRenderCount={6} activeCascades={7} cascadeColorTex={8} cascadeRasterDepthTex={9} cascadeReceiverTex={10}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                SceneNode?.Name ?? Name ?? GetType().Name,
                RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas,
                renderCascades,
                hasShadowMap,
                hasShadowMaterial,
                cascadeCount,
                ActiveCascadeCount,
                _desktopCascadeState.ShadowMapTexture is not null,
                _desktopCascadeState.RasterDepthTexture is not null,
                SelectCascadeReceiverTexture(_desktopCascadeState) is not null);
        }

        private static string FormatVector(Vector3 value)
            => $"({value.X:F2},{value.Y:F2},{value.Z:F2})";
    }
}
