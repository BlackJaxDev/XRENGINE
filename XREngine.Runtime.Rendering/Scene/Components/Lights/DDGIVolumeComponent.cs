using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.GI.DDGI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Represents a dynamic or baked DDGI (Dynamic Diffuse Global Illumination) probe volume.
    /// Manages grid extents, probe density, radiance/irradiance/visibility atlas dimensions,
    /// temporal hysteresis, probe relocation/classification, and probe debugging flags.
    /// </summary>
    public class DDGIVolumeComponent : XRComponent
    {
        private Vector3 _halfExtents = new(10.0f, 6.0f, 10.0f);
        private IVector3 _probeCounts = new(16, 8, 16);
        private int _raysPerProbe = 128;
        private int _maxProbesUpdatedPerFrame = 0;
        private float _hysteresis = 0.97f;
        private float _normalBias = 0.1f;
        private float _viewBias = 0.2f;
        private float _chebyshevPower = 4.0f;
        private bool _relocationEnabled = true;
        private bool _classificationEnabled = true;
        private float _fixedTimeBudgetMs = 0.0f;
        private float _relocationMinDistance = 0.0f;
        private float _relocationStepSize = 0.1f;
        private float _backfaceHitRatioThreshold = 0.85f;
        private EDDGIUpdateMode _updateMode = EDDGIUpdateMode.Dynamic;
        private int _slowUpdateIntervalFrames = 30;
        private string? _bakedAssetPath;
        private DDGIBakedAsset? _bakedAsset;
        private bool _debugDrawProbes = false;
        private EDDGIDebugMode _debugMode = EDDGIDebugMode.None;
        private bool _volumeEnabled = true;
        private float _intensity = 1.0f;
        private ColorF4 _tint = ColorF4.White;
        private int _cascadeCount = 1;
        private bool _cameraScrolling = false;
        private float _cascadeSpacingMultiplier = 2.0f;
        private bool _disableCoarseVisibility = true;
        private float _cascadeBlendMargin = 0.1f;
        private bool _applyAmbientOcclusion = true;

        private IRuntimeRenderWorld? _registeredWorld;

        /// <summary>
        /// Authoritative runtime state representation for this volume.
        /// </summary>
        [Browsable(false)]
        public DDGIVolumeRuntimeState RuntimeState { get; } = new();

        /// <summary>
        /// True when this component can contribute an active DDGI volume to its world.
        /// </summary>
        [Browsable(false)]
        public bool HasValidVolume => _volumeEnabled && IsActiveInHierarchy && TotalProbeCount > 0;

        /// <summary>
        /// Half-size of the volume bounds in local space units.
        /// </summary>
        [Category("DDGI Volume")]
        public Vector3 HalfExtents
        {
            get => _halfExtents;
            set
            {
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) ||
                    value.X <= 0.0f || value.Y <= 0.0f || value.Z <= 0.0f)
                    throw new ArgumentOutOfRangeException(nameof(value), "DDGI volume half extents must be greater than zero on every axis.");

                SetField(ref _halfExtents, value);
            }
        }

        /// <summary>
        /// Number of probes along each axis (X, Y, Z). A singleton axis samples the centered probe plane.
        /// </summary>
        [Category("DDGI Volume")]
        public IVector3 ProbeCounts
        {
            get => _probeCounts;
            set
            {
                IVector3 validated = ValidateProbeCounts(value);
                ValidateRayConfiguration(_raysPerProbe, validated);
                SetField(ref _probeCounts, validated);
            }
        }

        /// <summary>
        /// Total number of probes in this volume (X * Y * Z).
        /// </summary>
        [Browsable(false)]
        public int TotalProbeCount => checked(_probeCounts.X * _probeCounts.Y * _probeCounts.Z);

        /// <summary>
        /// Spacing between adjacent probes along each axis in local coordinates.
        /// </summary>
        [Browsable(false)]
        public Vector3 ProbeSpacing => new(
            _probeCounts.X > 1 ? (_halfExtents.X * 2.0f) / (_probeCounts.X - 1) : 0.0f,
            _probeCounts.Y > 1 ? (_halfExtents.Y * 2.0f) / (_probeCounts.Y - 1) : 0.0f,
            _probeCounts.Z > 1 ? (_halfExtents.Z * 2.0f) / (_probeCounts.Z - 1) : 0.0f);

        /// <summary>
        /// Number of rays traced per probe per frame (e.g. 128, 256).
        /// </summary>
        [Category("DDGI Tracing")]
        public int RaysPerProbe
        {
            get => _raysPerProbe;
            set
            {
                ValidateRayConfiguration(value, _probeCounts);
                SetField(ref _raysPerProbe, value);
            }
        }

        /// <summary>
        /// Maximum number of probes updated per frame (0 = all probes updated every frame).
        /// </summary>
        [Category("DDGI Tracing")]
        public int MaxProbesUpdatedPerFrame
        {
            get => _maxProbesUpdatedPerFrame;
            set => SetField(ref _maxProbesUpdatedPerFrame, Math.Max(0, value));
        }

        /// <summary>
        /// Target GPU execution budget in milliseconds for probe tracing in fixed-time mode (0.0 = disabled / fixed-quality mode).
        /// </summary>
        [Category("DDGI Tracing")]
        public float FixedTimeBudgetMs
        {
            get => _fixedTimeBudgetMs;
            set => SetField(ref _fixedTimeBudgetMs, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Temporal blend factor (0.0 - 0.999). Higher values provide smoother lighting with slower adaptation.
        /// </summary>
        [Category("DDGI Blending")]
        public float Hysteresis
        {
            get => _hysteresis;
            set => SetField(ref _hysteresis, Math.Clamp(value, 0.0f, 0.999f));
        }

        /// <summary>
        /// Surface normal bias in world units to avoid self-shadowing and light leaks.
        /// </summary>
        [Category("DDGI Blending")]
        public float NormalBias
        {
            get => _normalBias;
            set => SetField(ref _normalBias, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// View direction bias in world units towards the camera to avoid backface leaks.
        /// </summary>
        [Category("DDGI Blending")]
        public float ViewBias
        {
            get => _viewBias;
            set => SetField(ref _viewBias, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Exponent applied in the Chebyshev visibility test to sharpen shadow transitions.
        /// </summary>
        [Category("DDGI Blending")]
        public float ChebyshevPower
        {
            get => _chebyshevPower;
            set => SetField(ref _chebyshevPower, MathF.Max(1.0f, value));
        }

        /// <summary>
        /// Enables automatic probe relocation away from inside geometry.
        /// </summary>
        [Category("DDGI Features")]
        public bool RelocationEnabled
        {
            get => _relocationEnabled;
            set => SetField(ref _relocationEnabled, value);
        }

        /// <summary>
        /// Enables probe state classification (active vs inactive) to skip fully enclosed probes.
        /// </summary>
        [Category("DDGI Features")]
        public bool ClassificationEnabled
        {
            get => _classificationEnabled;
            set => SetField(ref _classificationEnabled, value);
        }

        /// <summary>
        /// Minimum surface distance threshold in world units to push probes away (0.0 = auto derived from grid spacing).
        /// </summary>
        [Category("DDGI Features")]
        public float RelocationMinDistance
        {
            get => _relocationMinDistance;
            set => SetField(ref _relocationMinDistance, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Step multiplier applied to relocation push vectors (0.01 - 1.0).
        /// </summary>
        [Category("DDGI Features")]
        public float RelocationStepSize
        {
            get => _relocationStepSize;
            set => SetField(ref _relocationStepSize, Math.Clamp(value, 0.01f, 1.0f));
        }

        /// <summary>
        /// Fraction of ray hits on backfaces required to classify a probe as inactive (0.1 - 1.0, default 0.85).
        /// </summary>
        [Category("DDGI Features")]
        public float BackfaceHitRatioThreshold
        {
            get => _backfaceHitRatioThreshold;
            set => SetField(ref _backfaceHitRatioThreshold, Math.Clamp(value, 0.1f, 1.0f));
        }

        /// <summary>
        /// Update mode for this DDGI volume (Dynamic, SlowUpdate / infinite-latency, or Baked).
        /// </summary>
        [Category("DDGI Mode")]
        public EDDGIUpdateMode UpdateMode
        {
            get => _updateMode;
            set => SetField(ref _updateMode, value);
        }

        /// <summary>
        /// Frame interval between probe ray tracing updates in SlowUpdate mode (default 30).
        /// </summary>
        [Category("DDGI Mode")]
        public int SlowUpdateIntervalFrames
        {
            get => _slowUpdateIntervalFrames;
            set => SetField(ref _slowUpdateIntervalFrames, Math.Max(1, value));
        }

        /// <summary>
        /// File path to the baked DDGI asset (.ddgi) associated with this volume.
        /// </summary>
        [Category("DDGI Mode")]
        public string? BakedAssetPath
        {
            get => _bakedAssetPath;
            set
            {
                if (SetField(ref _bakedAssetPath, value))
                    BakedAsset = null;
            }
        }

        /// <summary>
        /// In-memory baked DDGI asset container.
        /// </summary>
        [Browsable(false)]
        public DDGIBakedAsset? BakedAsset
        {
            get => _bakedAsset;
            set => SetField(ref _bakedAsset, value);
        }

        /// <summary>
        /// If true, volume probes are baked statically and not ray-traced dynamically at runtime.
        /// Maps directly to UpdateMode == EDDGIUpdateMode.Baked.
        /// </summary>
        [Category("DDGI Mode")]
        public bool BakedMode
        {
            get => _updateMode == EDDGIUpdateMode.Baked;
            set => UpdateMode = value ? EDDGIUpdateMode.Baked : EDDGIUpdateMode.Dynamic;
        }

        /// <summary>
        /// Enables debug visualization of probe positions, states, or irradiance in the viewport.
        /// </summary>
        [Category("DDGI Debug")]
        public bool DebugDrawProbes
        {
            get => _debugDrawProbes;
            set => SetField(ref _debugDrawProbes, value);
        }

        /// <summary>
        /// Debug visualization mode for DDGI indirect lighting and probe neighborhoods.
        /// </summary>
        [Category("DDGI Debug")]
        public EDDGIDebugMode DebugMode
        {
            get => _debugMode;
            set => SetField(ref _debugMode, value);
        }

        /// <summary>
        /// Enables this DDGI volume.
        /// </summary>
        [Category("DDGI Volume")]
        public bool VolumeEnabled
        {
            get => _volumeEnabled;
            set => SetField(ref _volumeEnabled, value);
        }

        /// <summary>
        /// Multiplicative color applied when sampling the indirect radiance field.
        /// </summary>
        [Category("DDGI Volume")]
        public ColorF4 Tint
        {
            get => _tint;
            set => SetField(ref _tint, value);
        }

        /// <summary>
        /// Scalar brightness multiplier applied after tinting.
        /// </summary>
        [Category("DDGI Volume")]
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Number of nested probe grid cascades (1 - 4). Cascade 0 is finest; higher indices cover wider areas with coarser spacing.
        /// </summary>
        [Category("DDGI Cascades")]
        public int CascadeCount
        {
            get => _cascadeCount;
            set
            {
                if (value is < 1 or > (int)DDGIVolumeRuntimeState.DefaultMaxCascades)
                    throw new ArgumentOutOfRangeException(nameof(value), $"DDGI supports between one and {DDGIVolumeRuntimeState.DefaultMaxCascades} cascades.");

                SetField(ref _cascadeCount, value);
            }
        }

        /// <summary>
        /// Enables camera-relative scrolling with grid snapping for cascades.
        /// When true, cascade centers track camera movement while snapping to grid spacing intervals.
        /// </summary>
        [Category("DDGI Cascades")]
        public bool CameraScrolling
        {
            get => _cameraScrolling;
            set => SetField(ref _cameraScrolling, value);
        }

        /// <summary>
        /// Spacing expansion factor applied per cascade level (e.g. 2.0 doubles grid spacing with each cascade level).
        /// </summary>
        [Category("DDGI Cascades")]
        public float CascadeSpacingMultiplier
        {
            get => _cascadeSpacingMultiplier;
            set => SetField(ref _cascadeSpacingMultiplier, MathF.Max(1.0f, value));
        }

        /// <summary>
        /// If true, omits visibility storage on the outermost coarse cascade where occlusion detail is minimal, saving VRAM.
        /// </summary>
        [Category("DDGI Cascades")]
        public bool DisableCoarseVisibility
        {
            get => _disableCoarseVisibility;
            set => SetField(ref _disableCoarseVisibility, value);
        }

        /// <summary>
        /// Outer margin fraction (0.01 - 0.5, default 0.1) across which smoothstep boundary blending occurs between adjacent cascades.
        /// </summary>
        [Category("DDGI Cascades")]
        public float CascadeBlendMargin
        {
            get => _cascadeBlendMargin;
            set => SetField(ref _cascadeBlendMargin, Math.Clamp(value, 0.01f, 0.5f));
        }

        /// <summary>
        /// When true, modulates screen-space indirect diffuse lighting by the resolved near-field ambient occlusion (GTAO/SSAO).
        /// </summary>
        [Category("DDGI Shading")]
        public bool ApplyAmbientOcclusion
        {
            get => _applyAmbientOcclusion;
            set => SetField(ref _applyAmbientOcclusion, value);
        }

        /// <summary>
        /// Computes a transform that converts world space coordinates into local volume space.
        /// </summary>
        public bool TryGetWorldToLocal(out Matrix4x4 worldToLocal)
            => Matrix4x4.Invert(Transform.RenderMatrix, out worldToLocal);

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            RuntimeState.Synchronize(this);

            switch (propName)
            {
                case nameof(World):
                case nameof(IsActive):
                case nameof(VolumeEnabled):
                    RefreshRegistration();
                    break;
            }

            if (RequiresLightingInvalidation(propName))
                RuntimeState.Invalidate();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RefreshRegistration();
            RuntimeState.Synchronize(this);
            RuntimeState.Invalidate();
        }

        protected override void OnComponentDeactivated()
        {
            Unregister();
            RuntimeState.Invalidate();
            base.OnComponentDeactivated();
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            RuntimeState.Synchronize(this);
            RuntimeState.Invalidate();
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            Unregister();
        }

        private static IVector3 ValidateProbeCounts(IVector3 value)
        {
            if (value.X < 1 || value.Y < 1 || value.Z < 1)
                throw new ArgumentOutOfRangeException(nameof(value), "DDGI requires at least one probe on every axis.");

            try
            {
                _ = checked(value.X * value.Y * value.Z);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DDGI probe count product exceeds the supported signed 32-bit range.");
            }

            return new IVector3(value.X, value.Y, value.Z);
        }

        private static void ValidateRayConfiguration(int raysPerProbe, IVector3 probeCounts)
        {
            if (raysPerProbe <= 0)
                throw new ArgumentOutOfRangeException(nameof(raysPerProbe), "DDGI rays per probe must be greater than zero.");

            try
            {
                _ = checked(probeCounts.X * probeCounts.Y * probeCounts.Z * raysPerProbe);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(raysPerProbe), "The DDGI probe grid and rays per probe exceed the supported signed 32-bit dispatch range.");
            }
        }

        private static bool RequiresLightingInvalidation(string? propertyName)
            => propertyName is nameof(HalfExtents)
                or nameof(ProbeCounts)
                or nameof(RaysPerProbe)
                or nameof(MaxProbesUpdatedPerFrame)
                or nameof(FixedTimeBudgetMs)
                or nameof(Hysteresis)
                or nameof(NormalBias)
                or nameof(ViewBias)
                or nameof(ChebyshevPower)
                or nameof(RelocationEnabled)
                or nameof(ClassificationEnabled)
                or nameof(RelocationMinDistance)
                or nameof(RelocationStepSize)
                or nameof(BackfaceHitRatioThreshold)
                or nameof(UpdateMode)
                or nameof(SlowUpdateIntervalFrames)
                or nameof(BakedAssetPath)
                or nameof(BakedAsset)
                or nameof(VolumeEnabled)
                or nameof(Tint)
                or nameof(Intensity)
                or nameof(CascadeCount)
                or nameof(CameraScrolling)
                or nameof(CascadeSpacingMultiplier)
                or nameof(DisableCoarseVisibility)
                or nameof(CascadeBlendMargin)
                or nameof(ApplyAmbientOcclusion);

        private void RefreshRegistration()
        {
            IRuntimeRenderWorld? world = World.GetRenderWorld();
            bool shouldRegister = world is not null && IsActiveInHierarchy && _volumeEnabled;

            if (_registeredWorld is not null && (!shouldRegister || _registeredWorld != world))
                Unregister();

            if (shouldRegister && _registeredWorld is null && world is not null)
            {
                Registry.Register(world, this);
                _registeredWorld = world;
            }
        }

        private void Unregister()
        {
            if (_registeredWorld is null)
                return;

            Registry.Unregister(_registeredWorld, this);
            _registeredWorld = null;
        }

        /// <summary>
        /// Tracks DDGI volume components per world so render passes can query active volumes quickly.
        /// </summary>
        public static class Registry
        {
            private static readonly Dictionary<IRuntimeRenderWorld, List<DDGIVolumeComponent>> s_perWorld = new();
            private static readonly object s_lock = new();

            public static void Register(IRuntimeRenderWorld world, DDGIVolumeComponent component)
            {
                lock (s_lock)
                {
                    if (!s_perWorld.TryGetValue(world, out List<DDGIVolumeComponent>? list))
                    {
                        list = [];
                        s_perWorld[world] = list;
                    }

                    if (!list.Contains(component))
                        list.Add(component);
                }
            }

            public static void Unregister(IRuntimeRenderWorld world, DDGIVolumeComponent component)
            {
                lock (s_lock)
                {
                    if (!s_perWorld.TryGetValue(world, out var list))
                        return;

                    list.Remove(component);
                    if (list.Count == 0)
                        s_perWorld.Remove(world);
                }
            }

            public static bool TryGetFirstActive(IRuntimeRenderWorld world, out DDGIVolumeComponent? component)
            {
                lock (s_lock)
                {
                    if (s_perWorld.TryGetValue(world, out List<DDGIVolumeComponent>? list))
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            DDGIVolumeComponent candidate = list[i];
                            if (candidate.HasValidVolume)
                            {
                                component = candidate;
                                return true;
                            }
                        }
                    }
                }

                component = null;
                return false;
            }

            public static IReadOnlyList<DDGIVolumeComponent> GetAllActive(IRuntimeRenderWorld world)
            {
                lock (s_lock)
                {
                    if (s_perWorld.TryGetValue(world, out List<DDGIVolumeComponent>? list))
                        return [.. list];
                    return Array.Empty<DDGIVolumeComponent>();
                }
            }
        }
    }
}
