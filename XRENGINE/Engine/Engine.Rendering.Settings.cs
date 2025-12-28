using MagicPhysX;
using MemoryPack;
using System;
using System.ComponentModel;
using System.Threading;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering.DLSS;
using XREngine.Scene;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static event Action? SettingsChanged;

            private static EngineSettings _settings = new();
            static Rendering()
            {
                _settings.PropertyChanged += HandleSettingsPropertyChanged;
                _settings.PhysicsVisualizeSettings.PropertyChanged += HandlePhysicsVisualizeSettingsChanged;
                _settings.PhysicsGpuMemorySettings.PropertyChanged += HandlePhysicsGpuMemorySettingsChanged;
            }
            /// <summary>
            /// The global rendering settings for the engine.
            /// </summary>
            public static EngineSettings Settings
            {
                get => _settings;
                set
                {
                    if (ReferenceEquals(_settings, value) && value is not null)
                        return;

                    if (_settings is not null)
                    {
                        _settings.PropertyChanged -= HandleSettingsPropertyChanged;
                        _settings.PhysicsVisualizeSettings.PropertyChanged -= HandlePhysicsVisualizeSettingsChanged;
                        _settings.PhysicsGpuMemorySettings.PropertyChanged -= HandlePhysicsGpuMemorySettingsChanged;
                    }

                    _settings = value ?? new EngineSettings();
                    _settings.PropertyChanged += HandleSettingsPropertyChanged;
                    _settings.PhysicsVisualizeSettings.PropertyChanged += HandlePhysicsVisualizeSettingsChanged;
                    _settings.PhysicsGpuMemorySettings.PropertyChanged += HandlePhysicsGpuMemorySettingsChanged;
                    ApplyEngineSettingChange(null);
                    SettingsChanged?.Invoke();
                }
            }

            private static void HandleSettingsPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(EngineSettings.PhysicsVisualizeSettings))
                {
                    if (e.PreviousValue is PhysicsVisualizeSettings previous)
                        previous.PropertyChanged -= HandlePhysicsVisualizeSettingsChanged;

                    if (e.NewValue is PhysicsVisualizeSettings current)
                        current.PropertyChanged += HandlePhysicsVisualizeSettingsChanged;
                }

                if (e.PropertyName == nameof(EngineSettings.PhysicsGpuMemorySettings))
                {
                    if (e.PreviousValue is PhysicsGpuMemorySettings previous)
                        previous.PropertyChanged -= HandlePhysicsGpuMemorySettingsChanged;

                    if (e.NewValue is PhysicsGpuMemorySettings current)
                        current.PropertyChanged += HandlePhysicsGpuMemorySettingsChanged;
                }

                ApplyEngineSettingChange(e.PropertyName);
                SettingsChanged?.Invoke();
            }

            private static void HandlePhysicsVisualizeSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                SettingsChanged?.Invoke();
            }

            private static void HandlePhysicsGpuMemorySettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                SettingsChanged?.Invoke();
            }
            
            // Note: ELoopType and EAntiAliasingMode are now defined in XREngine.Data as top-level enums
            // for use in the cascading settings system. The aliases below maintain API compatibility.
            
            /// <summary>
            /// Contains global rendering settings.
            /// </summary>
            [MemoryPackable(GenerateType.NoGenerate)]
            public partial class EngineSettings : XRAsset
            {
                #region Debug/Logging Settings (moved from UserSettings)

                private bool _enableFrameLogging = true;
                private float _debugOutputMinElapsedMs = 1.0f;
                private double _debugOutputRecencySeconds = 0.0;
                private bool _enableGpuIndirectDebugLogging = true;
                private bool _enableGpuIndirectCpuFallback = false;
                private bool _enableGpuIndirectValidationLogging = false;
                private bool _useDebugOpaquePipeline = false;

                /// <summary>
                /// Whether to enable frame logging for performance profiling.
                /// </summary>
                [Category("Debug")]
                [Description("Whether to enable frame logging for performance profiling.")]
                public bool EnableFrameLogging
                {
                    get => _enableFrameLogging;
                    set => SetField(ref _enableFrameLogging, value);
                }

                /// <summary>
                /// Minimum elapsed milliseconds for debug output to be displayed.
                /// </summary>
                [Category("Debug")]
                [Description("Minimum elapsed milliseconds for debug output to be displayed.")]
                public float DebugOutputMinElapsedMs
                {
                    get => _debugOutputMinElapsedMs;
                    set => SetField(ref _debugOutputMinElapsedMs, value);
                }

                /// <summary>
                /// How recent (in seconds) debug output must be to be displayed.
                /// </summary>
                [Category("Debug")]
                [Description("How recent (in seconds) debug output must be to be displayed.")]
                public double DebugOutputRecencySeconds
                {
                    get => _debugOutputRecencySeconds;
                    set => SetField(ref _debugOutputRecencySeconds, value);
                }

                /// <summary>
                /// Whether to enable GPU indirect rendering debug logging.
                /// </summary>
                [Category("Debug")]
                [Description("Whether to enable GPU indirect rendering debug logging.")]
                public bool EnableGpuIndirectDebugLogging
                {
                    get => _enableGpuIndirectDebugLogging;
                    set => SetField(ref _enableGpuIndirectDebugLogging, value);
                }

                /// <summary>
                /// Whether to enable CPU fallback for GPU indirect rendering when errors occur.
                /// </summary>
                [Category("Debug")]
                [Description("Whether to enable CPU fallback for GPU indirect rendering when errors occur.")]
                public bool EnableGpuIndirectCpuFallback
                {
                    get => _enableGpuIndirectCpuFallback;
                    set => SetField(ref _enableGpuIndirectCpuFallback, value);
                }

                /// <summary>
                /// Whether to run extra GPU indirect validation (CPU comparison and overflow logging).
                /// </summary>
                [Category("Debug")]
                [Description("Whether to run extra GPU indirect validation (CPU comparison and overflow logging).")]
                public bool EnableGpuIndirectValidationLogging
                {
                    get => _enableGpuIndirectValidationLogging;
                    set => SetField(ref _enableGpuIndirectValidationLogging, value);
                }

                /// <summary>
                /// Whether to use the debug opaque render pipeline for debugging purposes.
                /// </summary>
                [Category("Debug")]
                [Description("Whether to use the debug opaque render pipeline for debugging purposes.")]
                public bool UseDebugOpaquePipeline
                {
                    get => _useDebugOpaquePipeline;
                    set => SetField(ref _useDebugOpaquePipeline, value);
                }

                #endregion

                #region Job Manager Settings (moved from GameStartupSettings)

                private int? _jobWorkers = null;
                private int? _jobWorkerCap = null;
                private int? _jobQueueLimit = null;
                private int? _jobQueueWarningThreshold = null;
                private EOutputVerbosity _outputVerbosity = EOutputVerbosity.Verbose;
                private bool _useIntegerWeightingIds = true;

                /// <summary>
                /// Optional override for the number of job worker threads. If null, defaults are used.
                /// </summary>
                [Category("Threading")]
                [Description("Optional override for the number of job worker threads. If null, defaults are used.")]
                public int? JobWorkers
                {
                    get => _jobWorkers;
                    set => SetField(ref _jobWorkers, value);
                }

                /// <summary>
                /// Optional cap for the maximum number of job worker threads.
                /// </summary>
                [Category("Threading")]
                [Description("Optional cap for the maximum number of job worker threads.")]
                public int? JobWorkerCap
                {
                    get => _jobWorkerCap;
                    set => SetField(ref _jobWorkerCap, value);
                }

                /// <summary>
                /// Optional limit on queued jobs; if null, the JobManager default or environment override is used.
                /// </summary>
                [Category("Threading")]
                [Description("Optional limit on queued jobs; if null, the JobManager default or environment override is used.")]
                public int? JobQueueLimit
                {
                    get => _jobQueueLimit;
                    set => SetField(ref _jobQueueLimit, value);
                }

                /// <summary>
                /// Optional threshold at which queue length warnings are emitted.
                /// </summary>
                [Category("Threading")]
                [Description("Optional threshold at which queue length warnings are emitted.")]
                public int? JobQueueWarningThreshold
                {
                    get => _jobQueueWarningThreshold;
                    set => SetField(ref _jobQueueWarningThreshold, value);
                }

                /// <summary>
                /// The verbosity level for engine output messages.
                /// </summary>
                [Category("Debug")]
                [Description("The verbosity level for engine output messages.")]
                public EOutputVerbosity OutputVerbosity
                {
                    get => _outputVerbosity;
                    set => SetField(ref _outputVerbosity, value);
                }

                /// <summary>
                /// When true, integer IDs are used for bone weighting instead of floats.
                /// </summary>
                [Category("Performance")]
                [Description("When true, integer IDs are used for bone weighting instead of floats.")]
                public bool UseIntegerWeightingIds
                {
                    get => _useIntegerWeightingIds;
                    set => SetField(ref _useIntegerWeightingIds, value);
                }

                #endregion

                private Vector3 _defaultLuminance = new(0.299f, 0.587f, 0.114f);
                private bool _outputHDR = false;
                private EAntiAliasingMode _antiAliasingMode = EAntiAliasingMode.Fxaa;
                private float _tsrRenderScale = 0.67f;
                private bool _enableNvidiaDlss = false;
                private EDlssQualityMode _dlssQuality = EDlssQualityMode.Quality;
                private float _dlssCustomScale = 0.77f;
                private float _dlssSharpness = 0.2f;
                private bool _dlssEnableFrameSmoothing = true;
                private float _dlssFrameSmoothingStrength = 0.15f;
                private bool _enableIntelXess = false;
                private EXessQualityMode _xessQuality = EXessQualityMode.Quality;
                private float _xessCustomScale = 0.77f;
                private float _xessSharpness = 0.2f;
                private bool _enableIntelXessFrameGeneration = false;
                private uint _msaaSampleCount = 4u;
                private bool _allowShaderPipelines = true;
                private bool _useIntegerUniformsInShaders = true;
                private bool _optimizeTo4Weights = false;
                private bool _optimizeWeightsIfPossible = true;
                private bool _tickGroupedItemsInParallel = true;
                private ELoopType _recalcChildMatricesLoopType = ELoopType.Asynchronous;
                private uint _lightProbeResolution = 512u;
                private bool _lightProbesCaptureDepth = false;
                private uint _lightProbeDepthResolution = 256u;
                private bool _allowBinaryProgramCaching = true;
                private bool _calculateBlendshapesInComputeShader = false;
                private bool _calculateSkinningInComputeShader = false;
                private int _shaderConfigVersion = 0;
                private bool _useGpuBvh = false;
                private uint _bvhLeafMaxPrims = 4u;
                private EBvhMode _bvhMode = EBvhMode.Morton;
                private bool _bvhRefitOnlyWhenStable = true;
                private uint _raycastBufferSize = 1024u;

                private void BumpShaderConfigVersion()
                    => Interlocked.Increment(ref _shaderConfigVersion);
                private bool _calculateSkinnedBoundsInComputeShader = false;
                private string _defaultFontFolder = "Roboto";
                private string _defaultFontFileName = "Roboto-Medium.ttf";
                private bool _renderTransformDebugInfo = false;
                private bool _renderMesh3DBounds = false;
                private bool _renderMesh2DBounds = false;
                private bool _renderUITransformCoordinate = false;
                private bool _renderTransformLines = false;
                private bool _renderTransformPoints = false;
                private bool _renderTransformCapsules = false;
                private bool _visualizeDirectionalLightVolumes = false;
                private bool _preview3DWorldOctree = false;
                private bool _preview2DWorldQuadtree = false;
                private bool _previewTraces = false;
                private ColorF4 _quadtreeIntersectedBoundsColor = ColorF4.LightGray;
                private ColorF4 _quadtreeContainedBoundsColor = ColorF4.Yellow;
                private ColorF4 _octreeIntersectedBoundsColor = ColorF4.LightGray;
                private ColorF4 _octreeContainedBoundsColor = ColorF4.Yellow;
                private ColorF4 _bounds2DColor = ColorF4.LightLavender;
                private ColorF4 _bounds3DColor = ColorF4.LightLavender;
                private ColorF4 _transformPointColor = ColorF4.Orange;
                private ColorF4 _transformLineColor = ColorF4.LightRed;
                private ColorF4 _transformCapsuleColor = ColorF4.LightOrange;
                private bool _allowSkinning = true;
                private bool _allowBlendshapes = true;
                private bool _remapBlendshapeDeltas = true;
                private bool _useAbsoluteBlendshapePositions = false;
                private bool _logVRFrameTimes = false;
                private bool _preferNVStereo = true;
                private bool _renderVRSinglePassStereo = false;
                private bool _renderWindowsWhileInVR = true;
                private bool _populateVertexDataInParallel = false;
                private bool _processMeshImportsAsynchronously = true;
                private bool _useInterleavedMeshBuffer = false;
                private bool _enableSecondaryGpuCompute = false;
                private bool _allowSecondaryContextSharingFallback = false;
                private bool _transformCullingIsAxisAligned = true;
                private bool _renderCullingVolumes = false;
                private float _debugTextMaxLifespan = 0.0f;
                private bool _logMissingShaderSamplers = true;

                private bool _cullShadowCollectionByCameraFrusta = true;

                /// <summary>
                /// If true, logs a warning when a texture sampler uniform is not found during binding.
                /// This helps diagnose mismatched texture SamplerName properties vs shader sampler uniform names.
                /// Does not log warnings for optional engine uniforms like matrices that shaders may not use.
                /// </summary>
                [Category("Debug")]
                [Description("If true, logs a warning when a texture sampler uniform is not found. Useful for diagnosing mismatched SamplerName properties vs shader sampler declarations.")]
                public bool LogMissingShaderSamplers
                {
                    get => _logMissingShaderSamplers;
                    set => SetField(ref _logMissingShaderSamplers, value);
                }

                /// <summary>
                /// If true, shadow-map collection work is culled using the active camera frusta
                /// from all rendering windows/viewports.
                /// This can drastically reduce CPU spikes when many lights are present.
                /// </summary>
                [Category("Performance")]
                [Description("If true, culls shadow-map collection using active viewport camera frusta.")]
                public bool CullShadowCollectionByCameraFrusta
                {
                    get => _cullShadowCollectionByCameraFrusta;
                    set => SetField(ref _cullShadowCollectionByCameraFrusta, value);
                }

                /// <summary>
                /// If true, the engine will render the octree for the 3D world.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will render the octree for the 3D world.")]
                public bool Preview3DWorldOctree
                {
                    get => _preview3DWorldOctree;
                    set => SetField(ref _preview3DWorldOctree, value);
                }

                /// <summary>
                /// If true, the engine will render the quadtree for the 2D world.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will render the quadtree for the 2D world.")]
                public bool Preview2DWorldQuadtree
                {
                    get => _preview2DWorldQuadtree;
                    set => SetField(ref _preview2DWorldQuadtree, value);
                }

                /// <summary>
                /// If true, the engine will render physics traces.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will render physics traces.")]
                public bool PreviewTraces
                {
                    get => _previewTraces;
                    set => SetField(ref _previewTraces, value);
                }

                /// <summary>
                /// The default luminance used for calculation of exposure, etc.
                /// </summary>
                [Category("Performance")]
                [Description("The default luminance used for calculation of exposure, etc.")]
                public Vector3 DefaultLuminance
                {
                    get => _defaultLuminance;
                    set => SetField(ref _defaultLuminance, value);
                }

                /// <summary>
                /// When true, skip LDR tonemapping and keep the swap chain in HDR space.
                /// </summary>
                [Category("Performance")]
                [Description("When true, skip LDR tonemapping and keep the swap chain in HDR space.")]
                public bool OutputHDR
                {
                    get => _outputHDR;
                    set => SetField(ref _outputHDR, value);
                }

                /// <summary>
                /// Number of samples to use when MSAA is enabled (set to 1 to disable).
                /// </summary>
                [Category("Performance")]
                [Description("Number of samples to use when MSAA is enabled (set to 1 to disable).")]
                public uint MsaaSampleCount
                {
                    get => _msaaSampleCount;
                    set => SetField(ref _msaaSampleCount, Math.Clamp(value, 1u, 8u));
                }

                /// <summary>
                /// Selects which anti-aliasing technique to use. Future modes (TAA/TSR) fall back to None until implemented.
                /// </summary>
                [Category("Performance")]
                [Description("Selects which anti-aliasing technique to use.")]
                public EAntiAliasingMode AntiAliasingMode
                {
                    get => _antiAliasingMode;
                    set
                    {
                        if (!SetField(ref _antiAliasingMode, value))
                            return;
                    }
                }

                /// <summary>
                /// When TSR is enabled, scales the internal render resolution before temporal upscaling.
                /// </summary>
                [Category("Performance")]
                [Description("Internal resolution scale used by temporal super-resolution (TSR). Values below 1.0 render below native and upscale temporally.")]
                public float TsrRenderScale
                {
                    get => _tsrRenderScale;
                    set => SetField(ref _tsrRenderScale, Math.Clamp(value, 0.5f, 1.0f));
                }

                /// <summary>
                /// Enables NVIDIA DLSS frame upscaling when supported hardware and drivers are present.
                /// </summary>
                [Category("Upscaling")]
                [Description("Enables NVIDIA DLSS frame upscaling when supported hardware and drivers are present.")]
                public bool EnableNvidiaDlss
                {
                    get => _enableNvidiaDlss;
                    set => SetField(ref _enableNvidiaDlss, value);
                }

                /// <summary>
                /// DLSS quality/performance trade-off. Custom allows explicit scaling control.
                /// </summary>
                [Category("Upscaling")]
                [Description("DLSS quality/performance trade-off. Custom allows explicit scaling control.")]
                public EDlssQualityMode DlssQuality
                {
                    get => _dlssQuality;
                    set => SetField(ref _dlssQuality, value);
                }

                /// <summary>
                /// Custom render scale when DlssQuality is set to Custom. Values are clamped to 25%-100%.
                /// </summary>
                [Category("Upscaling")]
                [Description("Custom render scale when DlssQuality is set to Custom. Values are clamped to 25%-100%.")]
                public float DlssCustomScale
                {
                    get => _dlssCustomScale;
                    set => SetField(ref _dlssCustomScale, Math.Clamp(value, 0.25f, 1.0f));
                }

                /// <summary>
                /// DLSS sharpening amount forwarded to the runtime when available.
                /// </summary>
                [Category("Upscaling")]
                [Description("DLSS sharpening amount forwarded to the runtime when available.")]
                public float DlssSharpness
                {
                    get => _dlssSharpness;
                    set => SetField(ref _dlssSharpness, Math.Clamp(value, 0.0f, 1.0f));
                }

                /// <summary>
                /// Smooths resolution transitions to avoid visible oscillation when DLSS settings change.
                /// </summary>
                [Category("Upscaling")]
                [Description("Smooths resolution transitions to avoid visible oscillation when DLSS settings change.")]
                public bool DlssEnableFrameSmoothing
                {
                    get => _dlssEnableFrameSmoothing;
                    set => SetField(ref _dlssEnableFrameSmoothing, value);
                }

                /// <summary>
                /// Lerp factor used when smoothing DLSS resolution changes. Higher values converge faster.
                /// </summary>
                [Category("Upscaling")]
                [Description("Lerp factor used when smoothing DLSS resolution changes. Higher values converge faster.")]
                public float DlssFrameSmoothingStrength
                {
                    get => _dlssFrameSmoothingStrength;
                    set => SetField(ref _dlssFrameSmoothingStrength, Math.Clamp(value, 0.0f, 1.0f));
                }

                /// <summary>
                /// Enables Intel XeSS frame upscaling when supported hardware and drivers are present. Requires Vulkan.
                /// </summary>
                [Category("Upscaling")]
                [Description("Enables Intel XeSS frame upscaling when supported hardware and drivers are present. Requires Vulkan.")]
                public bool EnableIntelXess
                {
                    get => _enableIntelXess;
                    set => SetField(ref _enableIntelXess, value);
                }

                /// <summary>
                /// XeSS quality/performance trade-off. Custom allows explicit scaling control.
                /// </summary>
                [Category("Upscaling")]
                [Description("XeSS quality/performance trade-off. Custom allows explicit scaling control.")]
                public EXessQualityMode XessQuality
                {
                    get => _xessQuality;
                    set => SetField(ref _xessQuality, value);
                }

                /// <summary>
                /// Custom render scale when XessQuality is set to Custom. Values are clamped to 25%-100%.
                /// </summary>
                [Category("Upscaling")]
                [Description("Custom render scale when XessQuality is set to Custom. Values are clamped to 25%-100%.")]
                public float XessCustomScale
                {
                    get => _xessCustomScale;
                    set => SetField(ref _xessCustomScale, Math.Clamp(value, 0.25f, 1.0f));
                }

                /// <summary>
                /// XeSS sharpening amount forwarded to the runtime when available.
                /// </summary>
                [Category("Upscaling")]
                [Description("XeSS sharpening amount forwarded to the runtime when available.")]
                public float XessSharpness
                {
                    get => _xessSharpness;
                    set => SetField(ref _xessSharpness, Math.Clamp(value, 0.0f, 1.0f));
                }

                /// <summary>
                /// Enables XeSS frame generation when supported. Requires Windows with the XeSS-FG runtime (DirectX 12 swap chain path).
                /// </summary>
                [Category("Upscaling")]
                [Description("Enables XeSS frame generation when supported. Requires Windows with the XeSS-FG runtime (DirectX 12 swap chain path).")]
                public bool EnableIntelXessFrameGeneration
                {
                    get => _enableIntelXessFrameGeneration;
                    set => SetField(ref _enableIntelXessFrameGeneration, value);
                }

                /// <summary>
                /// Shader pipelines allow for dynamic combination of shaders at runtime, such as mixing and matching vertex and fragment shaders.
                /// When this is off, a new shader program must be compiled for each unique combination of shaders.
                /// Note that some mesh rendering versions may not support this feature anyways, like when using OVR_MultiView2.
                /// </summary>
                [Category("Performance")]
                [Description("Shader pipelines allow for dynamic combination of shaders at runtime, such as mixing and matching vertex and fragment shaders. When this is off, a new shader program must be compiled for each unique combination of shaders. Note that some mesh rendering versions may not support this feature anyways, like when using OVR_MultiView2.")]
                public bool AllowShaderPipelines
                {
                    get => _allowShaderPipelines;
                    set => SetField(ref _allowShaderPipelines, value);
                }

                /// <summary>
                /// When true, the engine will use integers in shaders instead of floats when needed.
                /// </summary>
                [Category("Performance")]
                [Description("When true, the engine will use integers in shaders instead of floats when needed.")]
                public bool UseIntegerUniformsInShaders
                {
                    get => _useIntegerUniformsInShaders;
                    set => SetField(ref _useIntegerUniformsInShaders, value);
                }

                /// <summary>
                /// When true, the engine will optimize the number of bone weights used per vertex if any vertex uses more than 4 weights.
                /// Will reduce shader calculations at the expense of skinning quality.
                /// </summary>
                [Category("Performance")]
                [Description("When true, the engine will optimize the number of bone weights used per vertex if any vertex uses more than 4 weights. Will reduce shader calculations at the expense of skinning quality.")]
                public bool OptimizeSkinningTo4Weights
                {
                    get => _optimizeTo4Weights;
                    set => SetField(ref _optimizeTo4Weights, value);
                }

                /// <summary>
                /// This will pass vertex weights and indices to the shader as elements of a vec4 instead of using SSBO remaps for more straightforward calculation.
                /// Will not result in any quality loss and should be enabled if possible.
                /// </summary>
                [Category("Performance")]
                [Description("This will pass vertex weights and indices to the shader as elements of a vec4 instead of using SSBO remaps for more straightforward calculation. Will not result in any quality loss and should be enabled if possible.")]
                public bool OptimizeSkinningWeightsIfPossible
                {
                    get => _optimizeWeightsIfPossible;
                    set => SetField(ref _optimizeWeightsIfPossible, value);
                }

                /// <summary>
                /// When items in the same group also have the same order value, this will dictate whether they are ticked in parallel or sequentially.
                /// Depending on how many items are in a singular tick order, this could be faster or slower.
                /// </summary>
                [Category("Performance")]
                [Description("When items in the same group also have the same order value, this will dictate whether they are ticked in parallel or sequentially. Depending on how many items are in a singular tick order, this could be faster or slower.")]
                public bool TickGroupedItemsInParallel
                {
                    get => _tickGroupedItemsInParallel;
                    set => SetField(ref _tickGroupedItemsInParallel, value);
                }
                
                /// <summary>
                /// If true, when calculating matrix hierarchies, the engine will calculate a transform's child matrices sequentially, asynchronously, or in parallel.
                /// </summary>
                [Category("Performance")]
                [Description("If true, when calculating matrix hierarchies, the engine will calculate a transform's child matrices sequentially, asynchronously, or in parallel.")]
                public ELoopType RecalcChildMatricesLoopType
                {
                    get => _recalcChildMatricesLoopType;
                    set => SetField(ref _recalcChildMatricesLoopType, value);
                }

                /// <summary>
                /// The default resolution of the light probe color texture.
                /// </summary>
                [Category("Performance")]
                [Description("The default resolution of the light probe color texture.")]
                public uint LightProbeResolution
                {
                    get => _lightProbeResolution;
                    set => SetField(ref _lightProbeResolution, value);
                }

                /// <summary>
                /// If true, the light probes will also capture depth information.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the light probes will also capture depth information.")]
                public bool LightProbesCaptureDepth
                {
                    get => _lightProbesCaptureDepth;
                    set => SetField(ref _lightProbesCaptureDepth, value);
                }

                /// <summary>
                /// If true, the engine will cache compiled binary programs for faster loading times on next startups until the GPU driver is updated.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will cache compiled binary programs for faster loading times on next startups until the GPU driver is updated.")]
                public bool AllowBinaryProgramCaching 
                {
                    get => _allowBinaryProgramCaching;
                    set => SetField(ref _allowBinaryProgramCaching, value);
                }

                /// <summary>
                /// If true, the engine will render the bounds of each 3D mesh.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render the bounds of each 3D mesh.")]
                public bool RenderMesh3DBounds 
                {
                    get => _renderMesh3DBounds;
                    set => SetField(ref _renderMesh3DBounds, value);
                }

                /// <summary>
                /// If true, the engine will render the bounds of each UI mesh.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render the bounds of each UI mesh.")]
                public bool RenderMesh2DBounds
                {
                    get => _renderMesh2DBounds;
                    set => SetField(ref _renderMesh2DBounds, value);
                }

                /// <summary>
                /// If true, the engine will render all transforms in the scene as lines and points.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render all transforms in the scene as lines and points.")]
                public bool RenderTransformDebugInfo
                {
                    get => _renderTransformDebugInfo;
                    set => SetField(ref _renderTransformDebugInfo, value);
                }

                /// <summary>
                /// If true, the engine will render the coordinate system of UI transforms.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render the coordinate system of UI transforms.")]
                public bool RenderUITransformCoordinate
                {
                    get => _renderUITransformCoordinate;
                    set => SetField(ref _renderUITransformCoordinate, value);
                }

                /// <summary>
                /// If true, the engine will render all transforms in the scene as lines.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render all transforms in the scene as lines.")]
                public bool RenderTransformLines
                {
                    get => _renderTransformLines;
                    set => SetField(ref _renderTransformLines, value);
                }

                /// <summary>
                /// If true, the engine will render all transforms in the scene as points.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render all transforms in the scene as points.")]
                public bool RenderTransformPoints
                {
                    get => _renderTransformPoints;
                    set => SetField(ref _renderTransformPoints, value);
                }

                /// <summary>
                /// If true, the engine will render capsules around transforms for debugging purposes.
                /// </summary>
                [Category("Debug")]
                [Description("If true, the engine will render capsules around transforms for debugging purposes.")]
                public bool RenderTransformCapsules
                {
                    get => _renderTransformCapsules;
                    set => SetField(ref _renderTransformCapsules, value);
                }

                /// <summary>
                /// If true, the engine will visualize the volumes of directional lights.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will visualize the volumes of directional lights.")]
                public bool VisualizeDirectionalLightVolumes
                {
                    get => _visualizeDirectionalLightVolumes;
                    set => SetField(ref _visualizeDirectionalLightVolumes, value);
                }

                /// <summary>
                /// If true, the engine will calculate blendshapes in a compute shader rather than the vertex shader.
                /// Improves performance because blendshapes are calculated once per vertex in global render pre-pass instead of once per instance in every render pass (like shadow map or light probe passes).
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will calculate blendshapes in a compute shader rather than the vertex shader. Improves performance because blendshapes are calculated once per vertex in global render pre-pass instead of once per instance in every render pass (like shadow map or light probe passes).")]
                public bool CalculateBlendshapesInComputeShader
                {
                    get => _calculateBlendshapesInComputeShader;
                    set => SetField(ref _calculateBlendshapesInComputeShader, value, null, _ => BumpShaderConfigVersion());
                }
                
                /// <summary>
                /// If true, the engine will calculate skinning in a compute shader rather than the vertex shader.
                /// Improves performance because skinning is calculated once per vertex in global render pre-pass instead of once per instance in every render pass (like shadow map or light probe passes).
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will calculate skinning in a compute shader rather than the vertex shader. Improves performance because skinning is calculated once per vertex in global render pre-pass instead of once per instance in every render pass (like shadow map or light probe passes).")]
                public bool CalculateSkinningInComputeShader
                {
                    get => _calculateSkinningInComputeShader;
                    set => SetField(ref _calculateSkinningInComputeShader, value, null, _ => BumpShaderConfigVersion());
                }

                internal int ShaderConfigVersion => _shaderConfigVersion;

                /// <summary>
                /// If true, the engine will use a compute shader to evaluate skinned mesh bounds and BVH inputs.
                /// Falls back to CPU calculations if the mesh layout is unsupported on the GPU.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will use a compute shader to evaluate skinned mesh bounds and BVH inputs. Falls back to CPU calculations if the mesh layout is unsupported on the GPU.")]
                public bool CalculateSkinnedBoundsInComputeShader
                {
                    get => _calculateSkinnedBoundsInComputeShader;
                    set => SetField(ref _calculateSkinnedBoundsInComputeShader, value);
                }

                /// <summary>
                /// If true, BVH construction and refits will prefer GPU compute paths when supported.
                /// Disabled by default for CPU-friendly behavior.
                /// </summary>
                [Category("BVH")]
                [Description("If true, BVH construction and refits will prefer GPU compute paths when supported. Disabled by default for CPU-friendly behavior.")]
                public bool UseGpuBvh
                {
                    get => _useGpuBvh;
                    set => SetField(ref _useGpuBvh, value);
                }

                /// <summary>
                /// Maximum number of primitives allowed in a BVH leaf node when using GPU builds.
                /// </summary>
                [Category("BVH")]
                [Description("Maximum number of primitives allowed in a BVH leaf node when using GPU builds.")]
                public uint BvhLeafMaxPrims
                {
                    get => _bvhLeafMaxPrims;
                    set => SetField(ref _bvhLeafMaxPrims, Math.Max(1u, value));
                }

                /// <summary>
                /// Controls which BVH build strategy to use for GPU construction.
                /// </summary>
                [Category("BVH")]
                [Description("Controls which BVH build strategy to use for GPU construction.")]
                public EBvhMode BvhMode
                {
                    get => _bvhMode;
                    set => SetField(ref _bvhMode, value);
                }

                /// <summary>
                /// When enabled, BVH updates will prefer refit-only passes if the object count is stable.
                /// </summary>
                [Category("BVH")]
                [Description("When enabled, BVH updates will prefer refit-only passes if the object count is stable.")]
                public bool BvhRefitOnlyWhenStable
                {
                    get => _bvhRefitOnlyWhenStable;
                    set => SetField(ref _bvhRefitOnlyWhenStable, value);
                }

                /// <summary>
                /// Size in bytes of the GPU BVH raycast readback buffer.
                /// </summary>
                [Category("BVH")]
                [Description("Size in bytes of the GPU BVH raycast readback buffer.")]
                public uint RaycastBufferSize
                {
                    get => _raycastBufferSize;
                    set => SetField(ref _raycastBufferSize, Math.Max(1u, value));
                }

                /// <summary>
                /// The name of the default font's folder within the engine's font directory.
                /// </summary>
                [Category("Appearance")]
                [Description("The name of the default font's folder within the engine's font directory.")]
                public string DefaultFontFolder 
                {
                    get => _defaultFontFolder;
                    set => SetField(ref _defaultFontFolder, value);
                }
                
                /// <summary>
                /// The name of the font file within the DefaultFontFolder directory.
                /// TTF or OTF files are supported, and the extension should be included in the string.
                /// </summary>
                [Category("Appearance")]
                [Description("The name of the font file within the DefaultFontFolder directory. TTF or OTF files are supported, and the extension should be included in the string.")]
                public string DefaultFontFileName 
                {
                    get => _defaultFontFileName;
                    set => SetField(ref _defaultFontFileName, value);
                }

                /// <summary>
                /// The color used to represent quadtree intersected bounds in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent quadtree intersected bounds in the engine.")]
                public ColorF4 QuadtreeIntersectedBoundsColor
                {
                    get => _quadtreeIntersectedBoundsColor;
                    set => SetField(ref _quadtreeIntersectedBoundsColor, value);
                }

                /// <summary>
                /// The color used to represent quadtree contained bounds in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent quadtree contained bounds in the engine.")]
                public ColorF4 QuadtreeContainedBoundsColor
                {
                    get => _quadtreeContainedBoundsColor;
                    set => SetField(ref _quadtreeContainedBoundsColor, value);
                }

                /// <summary>
                /// The color used to represent octree intersected bounds in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent octree intersected bounds in the engine.")]
                public ColorF4 OctreeIntersectedBoundsColor
                {
                    get => _octreeIntersectedBoundsColor;
                    set => SetField(ref _octreeIntersectedBoundsColor, value);
                }

                /// <summary>
                /// The color used to represent octree contained bounds in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent octree contained bounds in the engine.")]
                public ColorF4 OctreeContainedBoundsColor
                {
                    get => _octreeContainedBoundsColor;
                    set => SetField(ref _octreeContainedBoundsColor, value);
                }

                /// <summary>
                /// The color used to represent 2D bounds in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent 2D bounds in the engine.")]
                public ColorF4 Bounds2DColor
                {
                    get => _bounds2DColor;
                    set => SetField(ref _bounds2DColor, value);
                }

                /// <summary>
                /// The color used to represent 3D bounds in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent 3D bounds in the engine.")]
                public ColorF4 Bounds3DColor
                {
                    get => _bounds3DColor;
                    set => SetField(ref _bounds3DColor, value);
                }

                /// <summary>
                /// The color used to represent transform points in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent transform points in the engine.")]
                public ColorF4 TransformPointColor
                {
                    get => _transformPointColor;
                    set => SetField(ref _transformPointColor, value);
                }

                /// <summary>
                /// The color used to represent transform lines in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent transform lines in the engine.")]
                public ColorF4 TransformLineColor
                {
                    get => _transformLineColor;
                    set => SetField(ref _transformLineColor, value);
                }

                /// <summary>
                /// The color used to represent transform capsules in the engine.
                /// </summary>
                [Category("Appearance")]
                [Description("The color used to represent transform capsules in the engine.")]
                public ColorF4 TransformCapsuleColor
                {
                    get => _transformCapsuleColor;
                    set => SetField(ref _transformCapsuleColor, value);
                }

                /// <summary>
                /// If true, the engine will allow skinning.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will allow skinning.")]
                public bool AllowSkinning
                {
                    get => _allowSkinning;
                    set => SetField(ref _allowSkinning, value);
                }

                /// <summary>
                /// If true, the engine will allow blendshapes.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will allow blendshapes.")]
                public bool AllowBlendshapes
                {
                    get => _allowBlendshapes;
                    set => SetField(ref _allowBlendshapes, value);
                }

                /// <summary>
                /// If true, the engine will remap blendshape deltas.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will remap blendshape deltas.")]
                public bool RemapBlendshapeDeltas
                {
                    get => _remapBlendshapeDeltas;
                    set => SetField(ref _remapBlendshapeDeltas, value);
                }

                /// <summary>
                /// If true, the engine will use absolute positions for blendshape vertices instead of relative deltas.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will use absolute positions for blendshape vertices instead of relative deltas.")]
                public bool UseAbsoluteBlendshapePositions
                {
                    get => _useAbsoluteBlendshapePositions;
                    set => SetField(ref _useAbsoluteBlendshapePositions, value);
                }

                /// <summary>
                /// If true, the engine will log VR frame times to the console for performance monitoring.
                /// </summary>
                [Category("VR")]
                [Description("If true, the engine will log VR frame times to the console for performance monitoring.")]
                public bool LogVRFrameTimes
                {
                    get => _logVRFrameTimes;
                    set => SetField(ref _logVRFrameTimes, value);
                }

                /// <summary>
                /// If true, the engine will prefer NVidia stereo rendering over OVR_MultiView2.
                /// NV supports geometry, tess eval, and tess control shaders in stereo mode, but only supports 2 layers.
                /// OVR does not support extra shaders, but supports more layers.
                /// </summary>
                [Category("VR")]
                [Description("If true, the engine will prefer NVidia stereo rendering over OVR_MultiView2. NV supports geometry, tess eval, and tess control shaders in stereo mode, but only supports 2 layers. OVR does not support extra shaders, but supports more layers.")]
                public bool PreferNVStereo
                {
                    get => _preferNVStereo;
                    set => SetField(ref _preferNVStereo, value);
                }

                /// <summary>
                /// If true, VR single-pass stereo rendering will be enabled.
                /// </summary>
                [Category("VR")]
                [Description("If true, VR single-pass stereo rendering will be enabled.")]
                public bool RenderVRSinglePassStereo
                {
                    get => _renderVRSinglePassStereo;
                    set => SetField(ref _renderVRSinglePassStereo, value);
                }

                /// <summary>
                /// If true, windows will be rendered while in VR mode.
                /// </summary>
                [Category("VR")]
                [Description("If true, windows will be rendered while in VR mode.")]
                public bool RenderWindowsWhileInVR
                {
                    get => _renderWindowsWhileInVR;
                    set => SetField(ref _renderWindowsWhileInVR, value);
                }

                private PhysicsGpuMemorySettings _physicsGpuMemorySettings = new();
                /// <summary>
                /// Settings related to GPU memory allocation for physics simulations.
                /// </summary>
                [Category("Performance")]
                [Description("Settings related to GPU memory allocation for physics simulations.")]
                public PhysicsGpuMemorySettings PhysicsGpuMemorySettings
                {
                    get => _physicsGpuMemorySettings;
                    set => SetField(ref _physicsGpuMemorySettings, value);
                }

                private PhysicsVisualizeSettings _physicsVisualizeSettings = new();
                /// <summary>
                /// If true, physics visualization will be enabled for debugging purposes.
                /// </summary>
                [Category("Debug")]
                [Description("If true, physics visualization will be enabled for debugging purposes.")]
                public PhysicsVisualizeSettings PhysicsVisualizeSettings
                {
                    get => _physicsVisualizeSettings;
                    set => SetField(ref _physicsVisualizeSettings, value);
                }

                /// <summary>
                /// If true, vertex data population will be performed in parallel to improve performance.
                /// </summary>
                [Category("Performance")]
                [Description("If true, vertex data population will be performed in parallel to improve performance.")]
                public bool PopulateVertexDataInParallel
                {
                    get => _populateVertexDataInParallel;
                    set => SetField(ref _populateVertexDataInParallel, value);
                }

                /// <summary>
                /// If true, mesh imports will be processed asynchronously to avoid blocking the main thread.
                /// </summary>
                [Category("Performance")]
                [Description("If true, mesh imports will be processed asynchronously to avoid blocking the main thread.")]
                public bool ProcessMeshImportsAsynchronously
                {
                    get => _processMeshImportsAsynchronously;
                    set => SetField(ref _processMeshImportsAsynchronously, value);
                }

                /// <summary>
                /// If true, mesh buffers will use an interleaved layout for vertex attributes.
                /// </summary>
                [Category("Performance")]
                [Description("If true, mesh buffers will use an interleaved layout for vertex attributes.")]
                public bool UseInterleavedMeshBuffer
                {
                    get => _useInterleavedMeshBuffer;
                    set => SetField(ref _useInterleavedMeshBuffer, value);
                }

                /// <summary>
                /// Enables a secondary render context for GPU compute when a second adapter is present.
                /// </summary>
                [Category("Performance")]
                [Description("Enables a secondary render context for GPU compute when a second adapter is present.")]
                public bool EnableSecondaryGpuCompute
                {
                    get => _enableSecondaryGpuCompute;
                    set => SetField(ref _enableSecondaryGpuCompute, value);
                }

                /// <summary>
                /// Allows spawning a shared-context compute thread when only one adapter is detected.
                /// This keeps async readback from blocking the main swap chain even without a second GPU.
                /// </summary>
                [Category("Performance")]
                [Description("Allows spawning a shared-context compute thread when only one adapter is detected. This keeps async readback from blocking the main swap chain even without a second GPU.")]
                public bool AllowSecondaryContextSharingFallback
                {
                    get => _allowSecondaryContextSharingFallback;
                    set => SetField(ref _allowSecondaryContextSharingFallback, value);
                }

                /// <summary>
                /// If true, culling volumes will be axis-aligned boxes in local space. If false, they will be boxes oriented to world space.
                /// </summary>
                [Category("Performance")]
                [Description("If true, culling volumes will be axis-aligned boxes in local space. If false, they will be boxes oriented to world space.")]
                public bool TransformCullingIsAxisAligned
                {
                    get => _transformCullingIsAxisAligned;
                    set => SetField(ref _transformCullingIsAxisAligned, value);
                }

                /// <summary>
                /// If true, culling volumes will be rendered for debugging purposes.
                /// </summary>
                [Category("Debug")]
                [Description("If true, culling volumes will be rendered for debugging purposes.")]
                public bool RenderCullingVolumes
                {
                    get => _renderCullingVolumes;
                    set => SetField(ref _renderCullingVolumes, value);
                }

                /// <summary>
                /// How long a cache object for text rendering should exist for without receiving any further updates.
                /// </summary>
                [Category("Debug")]
                [Description("How long a cache object for text rendering should exist for without receiving any further updates.")]
                public float DebugTextMaxLifespan
                {
                    get => _debugTextMaxLifespan;
                    set => SetField(ref _debugTextMaxLifespan, value);
                }
                public bool RenderLightProbeTetrahedra { get; set; } = true;
            }

            private static void ApplyEngineSettingChange(string? propertyName)
            {
                bool applyAll = string.IsNullOrEmpty(propertyName);

                if (applyAll || propertyName == nameof(EngineSettings.RenderMesh3DBounds))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll || propertyName == nameof(EngineSettings.RenderTransformDebugInfo))
                    ApplyTransformDebugSetting();

                //if (applyAll || propertyName == nameof(EngineSettings.EnableNvidiaDlss)
                //    || propertyName == nameof(EngineSettings.DlssQuality)
                //    || propertyName == nameof(EngineSettings.DlssCustomScale)
                //    || propertyName == nameof(EngineSettings.DlssSharpness)
                //    || propertyName == nameof(EngineSettings.DlssEnableFrameSmoothing)
                //    || propertyName == nameof(EngineSettings.DlssFrameSmoothingStrength))
                //{
                //    Engine.ApplyNvidiaDlssPreference();
                //}
            }

            private static void ApplyRenderMeshBoundsSetting()
            {
                bool renderBounds = Settings.RenderMesh3DBounds;

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                    {
                        foreach (SceneNode rootNode in worldInstance.RootNodes)
                        {
                            rootNode.IterateComponents<RenderableComponent>(component =>
                            {
                                foreach (var mesh in component.Meshes.ToArray())
                                    mesh.RenderBounds = renderBounds;
                            }, true);
                        }
                    }
                }

                Engine.InvokeOnMainThread(() => Apply(), true);
            }

            private static void ApplyTransformDebugSetting()
            {
                bool enable = Settings.RenderTransformDebugInfo;

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                    {
                        foreach (SceneNode rootNode in worldInstance.RootNodes)
                        {
                            rootNode.IterateHierarchy(node => node.Transform.DebugRender = enable);
                        }
                    }
                }

                Engine.InvokeOnMainThread(() => Apply(), true);
            }
        }
    }
}
