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
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using TextureRuntimeLogMode = XREngine.Rendering.TextureRuntimeLogMode;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static event Action? SettingsChanged;
            public static event Action? AntiAliasingSettingsChanged;

            /// <summary>
            /// When the editor is using an ImGui viewport panel presentation mode, this optional callback
            /// can provide the framebuffer-space render bounds (in window coordinates) that all viewports
            /// should render into for that frame.
            /// </summary>
            public static Func<global::XREngine.Rendering.XRWindow, global::XREngine.Data.Geometry.BoundingRectangle?>? ScenePanelRenderRegionProvider { get; set; }

            /// <summary>
            /// Active engine-default settings used by rendering and effective settings.
            /// </summary>
            private static EngineSettings _settings = new();
            private static EngineSettings _globalDefaultSettings = _settings;
            private static EngineSettings? _projectDefaultSettings;
            static Rendering()
            {
                _settings.PropertyChanged += HandleSettingsPropertyChanged;
                _settings.PhysicsVisualizeSettings.PropertyChanged += HandlePhysicsVisualizeSettingsChanged;
                _settings.PhysicsGpuMemorySettings.PropertyChanged += HandlePhysicsGpuMemorySettingsChanged;
            }
            /// <summary>
            /// The active rendering settings for the engine.
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

                    if (_projectDefaultSettings is not null)
                        _projectDefaultSettings = _settings;
                    else
                        _globalDefaultSettings = _settings;

                    ApplyEngineSettingChange(null);
                    global::XREngine.Rendering.OpenGL.OpenGLRenderer.HandleShaderPipelineModeChanged(_settings.AllowShaderPipelines);
                    global::XREngine.Rendering.XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();
                    SettingsChanged?.Invoke();
                }
            }

            /// <summary>
            /// Global baseline persisted outside any project. Projects can replace the active
            /// <see cref="Settings"/> object with their own defaults while preserving this source.
            /// </summary>
            public static EngineSettings GlobalDefaultSettings
            {
                get => _globalDefaultSettings;
                set
                {
                    if (ReferenceEquals(_globalDefaultSettings, value) && value is not null)
                        return;

                    _globalDefaultSettings = value ?? new EngineSettings();

                    if (_projectDefaultSettings is null)
                        Settings = _globalDefaultSettings;
                }
            }

            /// <summary>
            /// Project-local engine defaults. When present, this object is the active
            /// <see cref="Settings"/> source and overrides <see cref="GlobalDefaultSettings"/>.
            /// </summary>
            public static EngineSettings? ProjectDefaultSettings
            {
                get => _projectDefaultSettings;
                set
                {
                    if (ReferenceEquals(_projectDefaultSettings, value))
                        return;

                    _projectDefaultSettings = value;
                    Settings = _projectDefaultSettings ?? _globalDefaultSettings;
                }
            }

            /// <summary>
            /// Active baseline used as the engine-default layer for resolved settings.
            /// This is the project default asset when a project override is loaded, otherwise
            /// it is the global default asset.
            /// </summary>
            public static EngineSettings DefaultSettings
            {
                get => Settings;
                set => Settings = value;
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
                if (e.PropertyName == nameof(EngineSettings.AllowSkinning))
                    XREngine.Debug.Rendering($"[RenderSettings] AllowSkinning changed to {_settings.AllowSkinning}; ShaderConfigVersion={_settings.ShaderConfigVersion}");
                if (e.PropertyName == nameof(EngineSettings.AllowShaderPipelines))
                {
                    global::XREngine.Rendering.OpenGL.OpenGLRenderer.HandleShaderPipelineModeChanged(_settings.AllowShaderPipelines);
                    global::XREngine.Rendering.XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();
                    XREngine.Debug.Rendering($"[RenderSettings] AllowShaderPipelines changed to {_settings.AllowShaderPipelines}; ShaderConfigVersion={_settings.ShaderConfigVersion}");
                }
                SettingsChanged?.Invoke();
            }

            private static void HandlePhysicsVisualizeSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                // Physics scenes subscribe to this asset directly. Forwarding these
                // diagnostic-only changes through SettingsChanged rebuilds every render
                // target and shader-dependent resource, which is both unnecessary and
                // capable of exhausting Vulkan memory while a prior generation is active.
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
            public partial class EngineSettings : OverrideableSettingsAssetBase
            {
                public EngineSettings()
                {
                    AttachRenderSubSettings(_openGL, _vulkan);
                    TrackOverrideableSettings();
                }

                private OpenGLRenderSettings _openGL = new();
                private VulkanRenderSettings _vulkan = new();

                [Category("Rendering")]
                [DisplayName("OpenGL")]
                [Description("OpenGL-specific runtime settings.")]
                public OpenGLRenderSettings OpenGL
                {
                    get => _openGL;
                    set => SetField(ref _openGL, value ?? new OpenGLRenderSettings());
                }

                [Category("Rendering")]
                [DisplayName("Vulkan")]
                [Description("Vulkan-specific runtime settings.")]
                public VulkanRenderSettings Vulkan
                {
                    get => _vulkan;
                    set => SetField(ref _vulkan, value ?? new VulkanRenderSettings());
                }

                protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
                {
                    base.OnPropertyChanged(propName, prev, field);

                    if (propName == nameof(OpenGL) || propName == nameof(Vulkan))
                        RefreshRenderSubSettings(prev, field);
                }

                private void AttachRenderSubSettings(params IXRNotifyPropertyChanged?[] subSettings)
                {
                    for (int i = 0; i < subSettings.Length; i++)
                    {
                        if (subSettings[i] is not null)
                            subSettings[i]!.PropertyChanged += HandleRenderSubSettingsChanged;
                    }
                }

                private void RefreshRenderSubSettings<T>(T previous, T current)
                {
                    if (previous is IXRNotifyPropertyChanged previousNotify)
                        previousNotify.PropertyChanged -= HandleRenderSubSettingsChanged;

                    if (current is IXRNotifyPropertyChanged currentNotify)
                        currentNotify.PropertyChanged += HandleRenderSubSettingsChanged;
                }

                private void HandleRenderSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
                {
                    string? propertyName = e.PropertyName;
                    if (propertyName == nameof(OpenGLRenderSettings.AllowProgramPipelines))
                    {
                        BumpShaderConfigVersion();
                        OnPropertyChanged(nameof(AllowShaderPipelines), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanGpuDrivenSettings.Profile))
                    {
                        OnPropertyChanged(nameof(VulkanGpuDrivenProfile), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanSynchronizationSettings.QueueOverlapMode))
                    {
                        OnPropertyChanged(nameof(VulkanQueueOverlapMode), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanDescriptorSettings.EnableDescriptorIndexing))
                    {
                        OnPropertyChanged(nameof(EnableVulkanDescriptorIndexing), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanDescriptorSettings.EnableBindlessMaterialTable))
                    {
                        OnPropertyChanged(nameof(EnableVulkanBindlessMaterialTable), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanDescriptorSettings.BindlessMaterialMode))
                    {
                        OnPropertyChanged(nameof(VulkanBindlessMaterialMode), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanDescriptorSettings.ValidateContracts))
                    {
                        OnPropertyChanged(nameof(ValidateVulkanDescriptorContracts), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanGpuDrivenSettings.GeometryFetchMode))
                    {
                        OnPropertyChanged(nameof(VulkanGeometryFetchMode), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName is nameof(VulkanTargetModeSettings.RenderTargetMode))
                    {
                        OnPropertyChanged(nameof(VulkanRenderTargetMode), e.PreviousValue, e.NewValue);
                        return;
                    }

                    if (propertyName == nameof(VulkanRenderSettings.Robustness))
                    {
                        OnPropertyChanged(nameof(VulkanRobustnessSettings), e.PreviousValue, e.NewValue);
                        return;
                    }

                    OnPropertyChanged(propertyName, e.PreviousValue, e.NewValue);
                }

                #region Debug/Logging Settings (moved from UserSettings)

                private bool _enableFrameLogging = true;
                private float _debugOutputMinElapsedMs = 1.0f;
                private double _debugOutputRecencySeconds = 0.0;
                private bool _enableGpuIndirectDebugLogging = false;
                private bool _enableGpuIndirectCpuFallback = false;
                private bool _enableGpuIndirectValidationLogging = false;
                private bool _enableGpuMeshBvhPickLogging = false;
                private bool _enableZeroReadbackMaterialScatter = false;
                private EZeroReadbackMaterialDrawPath _zeroReadbackMaterialDrawPath = EZeroReadbackMaterialDrawPath.FullBucketScan;
                private EMeshSubmissionStrategy? _forceMeshSubmissionStrategy = null;
                private TextureRuntimeLogMode _textureLogMode = TextureRuntimeLogMode.Summary;
                private double _textureSlowCpuDecodeResizeMilliseconds = 5.0;
                private double _textureSlowMipBuildMilliseconds = 5.0;
                private double _textureSlowUploadChunkMilliseconds = 2.0;
                private double _textureSlowTransitionMilliseconds = 8.0;
                private double _textureSlowQueueWaitMilliseconds = 100.0;
                private double _textureUploadFrameBudgetMilliseconds = 2.0;

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
                /// Whether to emit verbose diagnostics for the GPU mesh-BVH editor pick path
                /// (dispatch decisions, ray transforms, and readback hit/miss results).
                /// </summary>
                [Category("Debug")]
                [Description("Whether to emit verbose diagnostics for the GPU mesh-BVH editor pick path (dispatch decisions, ray transforms, and readback hit/miss results).")]
                public bool EnableGpuMeshBvhPickLogging
                {
                    get => _enableGpuMeshBvhPickLogging;
                    set => SetField(ref _enableGpuMeshBvhPickLogging, value);
                }

                [Category("Debug")]
                [Description("Texture runtime log detail written to log_textures.log. Summary logs periodic summaries and high-severity events; SlowOnly logs slow/high-severity events; Verbose logs every texture transition/upload diagnostic.")]
                public TextureRuntimeLogMode TextureLogMode
                {
                    get => _textureLogMode;
                    set => SetField(ref _textureLogMode, value);
                }

                [Category("Debug")]
                [Description("Texture CPU decode or resize operations at or above this duration are logged as slow.")]
                public double TextureSlowCpuDecodeResizeMilliseconds
                {
                    get => _textureSlowCpuDecodeResizeMilliseconds;
                    set => SetField(ref _textureSlowCpuDecodeResizeMilliseconds, Math.Max(0.0, value));
                }

                [Category("Debug")]
                [Description("Texture mip build operations at or above this duration are logged as slow.")]
                public double TextureSlowMipBuildMilliseconds
                {
                    get => _textureSlowMipBuildMilliseconds;
                    set => SetField(ref _textureSlowMipBuildMilliseconds, Math.Max(0.0, value));
                }

                [Category("Debug")]
                [Description("Render-thread texture upload chunks at or above this duration are logged as slow.")]
                public double TextureSlowUploadChunkMilliseconds
                {
                    get => _textureSlowUploadChunkMilliseconds;
                    set => SetField(ref _textureSlowUploadChunkMilliseconds, Math.Max(0.0, value));
                }

                [Category("Debug")]
                [Description("Full texture residency transitions at or above this duration are logged as slow.")]
                public double TextureSlowTransitionMilliseconds
                {
                    get => _textureSlowTransitionMilliseconds;
                    set => SetField(ref _textureSlowTransitionMilliseconds, Math.Max(0.0, value));
                }

                [Category("Debug")]
                [Description("Queued texture work that waits this long before execution is logged as slow.")]
                public double TextureSlowQueueWaitMilliseconds
                {
                    get => _textureSlowQueueWaitMilliseconds;
                    set => SetField(ref _textureSlowQueueWaitMilliseconds, Math.Max(0.0, value));
                }

                [Category("Performance")]
                [Description("Approximate per-frame render-thread budget for background texture upload work.")]
                public double TextureUploadFrameBudgetMilliseconds
                {
                    get => _textureUploadFrameBudgetMilliseconds;
                    set => SetField(ref _textureUploadFrameBudgetMilliseconds, Math.Clamp(value, 0.1, 100.0));
                }

                /// <summary>
                /// Whether to enable zero-readback material scatter for GPU-driven rendering.
                /// When enabled, the GPU scatter shader writes per-material indirect draw commands
                /// and the CPU never reads GPU buffers during the rendering hot path.
                /// </summary>
                [Category("GPU Rendering")]
                [Description("Enable zero-readback material scatter for GPU-driven rendering. Eliminates CPU readback of GPU batch ranges.")]
                public bool EnableZeroReadbackMaterialScatter
                {
                    get => _enableZeroReadbackMaterialScatter;
                    set => SetField(ref _enableZeroReadbackMaterialScatter, value);
                }

                /// <summary>
                /// Selects how zero-readback GPU indirect material draws are submitted.
                /// </summary>
                [Category("GPU Rendering")]
                [Description("Selects the material draw path used by GpuIndirectZeroReadback.")]
                public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath
                {
                    get => _zeroReadbackMaterialDrawPath;
                    set => SetField(ref _zeroReadbackMaterialDrawPath, value);
                }

                /// <summary>
                /// Optional diagnostic override for the resolved mesh submission strategy.
                /// Leave null for profile and capability based resolution.
                /// </summary>
                [Category("GPU Rendering")]
                [Description("Optional diagnostic override for the resolved mesh submission strategy. Leave null for profile and capability based resolution. Note: GpuMeshlet* strategies require a backend that supports mesh shaders (Vulkan with VK_EXT_mesh_shader, or OpenGL with the rare GL_EXT_mesh_shader). When mesh shaders aren't available the resolver downgrades to GpuIndirectZeroReadback (or CpuDirect under strict no-fallback profiles); the Occlusion panel surfaces the active downgrade reason.")]
                public EMeshSubmissionStrategy? ForceMeshSubmissionStrategy
                {
                    get => _forceMeshSubmissionStrategy;
                    set => SetField(ref _forceMeshSubmissionStrategy, value);
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
                private bool _enableNvidiaDlssFrameGeneration = false;
                private ENvidiaDlssFrameGenerationMode _nvidiaDlssFrameGenerationMode = ENvidiaDlssFrameGenerationMode.Off;
                private bool _enableIntelXess = false;
                private EXessQualityMode _xessQuality = EXessQualityMode.Quality;
                private float _xessCustomScale = 0.77f;
                private float _xessSharpness = 0.2f;
                private bool _enableIntelXessFrameGeneration = false;
                private uint _msaaSampleCount = 4u;
                private bool _useIntegerUniformsInShaders = true;
                private bool _tickGroupedItemsInParallel = true;
                private ELoopType _recalcChildMatricesLoopType = ELoopType.Asynchronous;
                private ERenderMatrixUpdateMode _renderMatrixUpdateMode = ERenderMatrixUpdateMode.Default;
                private uint _lightProbeResolution = 512u;
                private bool _lightProbesCaptureDepth = false;
                private bool _calculateBlendshapesInComputeShader = true;
                private bool _calculateSkinningInComputeShader = true;
                private bool _enableBlendshapePrecombinePass = false;
                private bool _enableBlendshapePrecombineForDirectVertexPath = true;
                private bool _enableBlendshapePcaBasisCompression = false;
                private int _blendshapePrecombineComputeMinActiveShapes = 8;
                private int _blendshapePrecombineDirectMinActiveShapes = 8;
                private int _blendshapePrecombineMinAffectedVertices = 1024;
                private bool _useGlobalSkinPaletteBufferForComputeSkinning = false;
                private bool _useGlobalBlendshapeWeightsBufferForComputeSkinning = false;
                private ESkinnedBoundsRecomputePolicy _skinnedBoundsRecomputePolicy = ESkinnedBoundsRecomputePolicy.Never;
                private bool _allowInitialSkinnedBoundsBuildWhenNever = true;
                private int _shaderConfigVersion = 0;
                private global::XREngine.Rendering.ERenderClipSpaceYDirection _clipSpaceYDirection = global::XREngine.Rendering.ERenderClipSpaceYDirection.YUp;
                private global::XREngine.Rendering.ERenderClipDepthRange _clipDepthRange = global::XREngine.Rendering.ERenderClipDepthRange.ZeroToOne;
                private ECpuSceneCullingStructure _cpuSceneCullingStructure = ECpuSceneCullingStructure.Bvh;
                private EGpuCullingDataLayout _gpuCullingDataLayout = EGpuCullingDataLayout.AoSHot;
                private EGpuSortDomainPolicy _gpuSortDomainPolicy = EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront;
                private EOcclusionCullingMode _gpuOcclusionCullingMode = EOcclusionCullingMode.GpuHiZ;
                private bool _cacheGpuHiZOcclusionOncePerFrame = false;
                private bool _streamMeshLodsOnDemand = false;
                private int _meshLodStreamingDrainIntervalFrames = 4;
                private int _meshLodStreamingMaxLoadsPerDrain = 8;
                private int _cpuQueryOcclusionRetestPeriodFrames = 6;
                private int _cpuQueryOcclusionMaxQueriesPerFrame = 64;
                private float _cpuQueryOcclusionVisibleDemotionBudgetFraction = 0.25f;
                private int _cpuQueryOcclusionRecoveryMinCadenceFrames = 2;
                private float _cpuQueryOcclusionSmallMotionMeters = 0.02f;
                private float _cpuQueryOcclusionMediumMotionMeters = 0.25f;
                private float _cpuQueryOcclusionLargeMotionMeters = 2.0f;
                private float _cpuQueryOcclusionCameraCutMeters = 12.0f;
                private float _cpuQueryOcclusionSmallRotationDegrees = 1.0f;
                private float _cpuQueryOcclusionMediumRotationDegrees = 5.0f;
                private float _cpuQueryOcclusionLargeRotationDegrees = 15.0f;
                private float _cpuQueryOcclusionCameraCutRotationDegrees = 55.0f;
                private float _cpuQueryOcclusionVrHeadMotionMeters = 0.25f;
                private float _cpuQueryOcclusionVrHeadRotationDegrees = 20.0f;
                private ECpuQueryStereoMode _cpuQueryOcclusionStereoMode = ECpuQueryStereoMode.PerEyeSequential;
                private int _cpuQueryOcclusionMaxPendingFrames = 6;
                private bool _enableCpuSoftwareOcclusionCulling = false;
                private int _cpuSocBufferWidth = 256;
                private int _cpuSocBufferHeight = 128;
                private int _cpuSocOccluderTriangleBudget = 5000;
                private int _cpuSocMaxOccluders = 64;
                private float _cpuSocMinOccluderScreenArea = 0.005f;
                private bool _cpuSocUseAvx2 = true;
                private bool _cpuSocDebugVisualization = false;
                private bool _cpuSocDebugForceVisible = false;
                private uint _bvhLeafMaxPrims = 4u;
                private EBvhMode _bvhMode = EBvhMode.Morton;
                private bool _bvhRefitOnlyWhenStable = true;
                private bool _useSkinnedBvhRefitOptimize = false;
                private uint _raycastBufferSize = 1024u;
                private bool _enableGpuBvhTimingQueries = false;

                private float _transformReplicationKeyframeIntervalSec = 5.0f;
                private float _timeBetweenReplications = 0.1f;

                private void BumpShaderConfigVersion()
                    => Interlocked.Increment(ref _shaderConfigVersion);
                private bool _calculateSkinnedBoundsInComputeShader = false;
                private bool _skinnedBoundsGpuDirectAabbWrite = false;
                private string _defaultFontFolder = "Roboto";
                private string _defaultFontFileName = "Roboto-Medium.ttf";

                /// <summary>
                /// The interval in seconds between full keyframes sent to the network for this transform.
                /// All other updates are sent as deltas.
                /// </summary>
                [Category("Networking")]
                [Description("The interval in seconds between full keyframes sent to the network for this transform. All other updates are sent as deltas.")]
                public float TransformReplicationKeyframeIntervalSec
                {
                    get => _transformReplicationKeyframeIntervalSec;
                    set => SetField(ref _transformReplicationKeyframeIntervalSec, value);
                }

                /// <summary>
                /// The minimum interval in seconds between replicated tick updates for world objects.
                /// Helps limit bandwidth usage for high-frequency properties.
                /// </summary>
                [Category("Networking")]
                [Description("The minimum interval in seconds between replicated tick updates for world objects. Helps limit bandwidth usage for high-frequency properties.")]
                public float TimeBetweenReplications
                {
                    get => _timeBetweenReplications;
                    set => SetField(ref _timeBetweenReplications, value);
                }
                private bool _allowSkinning = true;
                private bool _allowBlendshapes = true;
                private bool _remapBlendshapeDeltas = true;
                private bool _useAbsoluteBlendshapePositions = false;
                private bool _logVRFrameTimes = false;
                private bool _preferNVStereo = true;
                private EVrViewRenderMode _vrViewRenderMode = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrViewRenderMode;
                private EVrMirrorMode _vrMirrorMode = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrMirrorMode;
                private bool _renderWindowsWhileInVR = true;
                private bool _vrMirrorComposeFromEyeTextures = true;
                private bool _vrCopyEyePreviewTextures = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrCopyEyePreviewTextures;
                private float _vrLeftEyeTargetRateHz = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrOutputTargetRateHz;
                private float _vrRightEyeTargetRateHz = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrOutputTargetRateHz;
                private float _vrDesktopEditorTargetRateHz = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrOutputTargetRateHz;
                private float _vrCyclopeanDesktopTargetRateHz = 60.0f;
                private bool _vrDesktopAutoSkipWhenOverBudget = true;
                private bool _enableVrFoveatedViewSet = false;
                private EVrFoveationMode _vrFoveationMode = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrFoveationMode;
                private EVrFoveationQualityPreset _vrFoveationQualityPreset = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrFoveationQualityPreset;
                private bool _vrFoveationRequireRequested = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.VrFoveationRequireRequested;
                private ERvcPipelineMode _rvcPipelineMode = ERvcPipelineMode.Off;
                private bool _rvcQuadViewEnabled = false;
                private bool _rvcStereoReuseEnabled = false;
                private bool _rvcInsetWideReuseEnabled = true;
                private bool _rvcTemporalReuseEnabled = false;
                private bool _rvcPeripheralLightAggregationEnabled = false;
                private bool _rvcDiagnosticOverlayEnabled = false;
                private ERvcDebugViewMode _rvcDebugViewMode = ERvcDebugViewMode.Disabled;
                private ERvcLightGridSpace _rvcLightGridSpace = ERvcLightGridSpace.WorldAlignedCameraRelative;
                private float _rvcFovealRadiusDegrees = RvcQualitySettings.Defaults.FovealRadiusDegrees;
                private float _rvcGuardBandDegrees = RvcQualitySettings.Defaults.GuardBandDegrees;
                private float _rvcMidFieldRadiusDegrees = RvcQualitySettings.Defaults.MidFieldRadiusDegrees;
                private ERvcShadeletDensity _rvcPeripheralMaxRate = RvcQualitySettings.Defaults.PeripheralMaxRate;
                private float _rvcForceFullResNearDistanceMeters = RvcQualitySettings.Defaults.ForceFullResNearDistanceMeters;
                private ERvcDerivativeStrategy _rvcDerivativeStrategy = RvcQualitySettings.Defaults.DerivativeStrategy;
                private ERvcFovealAntiAliasingPath _rvcFovealAntiAliasingPath = RvcQualitySettings.Defaults.FovealAntiAliasingPath;
                private float _rvcReuseMaxNormalAngleDegrees = RvcQualitySettings.Defaults.ReuseMaxNormalAngleDegrees;
                private float _rvcReuseMaxDepthDeltaMeters = RvcQualitySettings.Defaults.ReuseMaxDepthDeltaMeters;
                private byte _rvcReuseMaxRoughnessBucketDelta = RvcQualitySettings.Defaults.ReuseMaxRoughnessBucketDelta;
                private EOpenXrEyeResolutionPreset _openXrEyeResolutionPreset = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionPreset;
                private float _openXrEyeResolutionScale = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionScale;
                private uint _openXrCustomEyeResolutionWidth = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionWidth;
                private uint _openXrCustomEyeResolutionHeight = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionHeight;
                private bool _openXrCullWithFrustum = true;
                private bool _openXrDebugGl = false;
                private bool _openXrDebugClearOnly = false;
                private bool _openXrDebugLifecycle = false;
                private bool _openXrDebugRenderRightThenLeft = false;
                private bool _openXrPrepareFrameAfterDesktopRender = true;
                private float _openXrDeadlineSafetyMarginMs = 1.0f;
                private float _openXrPoseTimeOffsetMs = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.OpenXrPoseTimeOffsetMs;
                private OpenXRAPI.OpenXrCollectVisiblePosePolicy _openXrCollectVisiblePosePolicy = OpenXRAPI.OpenXrCollectVisiblePosePolicy.Predicted;
                private float _openXrCollectVisibleFrustumPaddingDegrees = 2.0f;
                private OpenXRAPI.OpenXrTrackingLossPolicy _openXrTrackingLossPolicy = OpenXRAPI.OpenXrTrackingLossPolicy.FreezeLastValid;
                private OpenXRAPI.OpenXrActionSyncPolicy _openXrActionSyncPolicy = OpenXRAPI.OpenXrActionSyncPolicy.PredictedOnly;
                private OpenXRAPI.OpenXrRenderPacingMode _openXrRenderPacingMode = XREngine.Rendering.RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode;
                private Vector2 _vrFoveationCenterUv = new(0.5f, 0.5f);
                private float _vrFoveationInnerRadius = 0.35f;
                private float _vrFoveationOuterRadius = 0.85f;
                private Vector3 _vrFoveationShadingRates = new(1.0f, 0.7f, 0.5f);
                private float _vrFoveationVisibilityMargin = 0.05f;
                private bool _vrFoveationForceFullResForUiAndNearField = true;
                private float _vrFoveationFullResNearDistanceMeters = 1.5f;
                private bool _populateVertexDataInParallel = true;
                private bool _processMeshImportsAsynchronously = true;
                private bool _useInterleavedMeshBuffer = false;
                private bool _enableSecondaryGpuCompute = false;
                private bool _allowSecondaryContextSharingFallback = false;
                private bool _transformCullingIsAxisAligned = true;
                private bool _logMissingShaderSamplers = false;
                private bool _logMaterialTextureBindings = false;
                private bool _enableVramBudget = true;
                private int _vramBudgetMB = 20 * 1024;

                private bool _cullShadowCollectionByCameraFrusta = true;
                private bool _useSpotShadowAtlas = true;
                private bool _useDirectionalShadowAtlas = true;
                private bool _usePointShadowAtlas = true;
                private uint _shadowAtlasPageSize = 4096u;
                private int _maxShadowAtlasPages = 1;
                private long _maxShadowAtlasMemoryBytes = 0L;
                private int _maxShadowTilesRenderedPerFrame = 16;
                private float _maxShadowRenderMilliseconds = 2.0f;
            private int _maxDirectionalCascadeAtlasStaleFrames = 2;
                private uint _minShadowAtlasTileResolution = 128u;
                private uint _maxShadowAtlasTileResolution = 4096u;

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

                /// <summary>logs every material texture uniform binding (material name, sampler index, texture identity,
                /// GL binding id, texture unit). Use to diagnose cross-material texture bleed (e.g. wrong texture rendered
                /// on a mesh because the material's texture reference or the GL unit state is incorrect). Noisy: enable only
                /// while reproducing.
                /// </summary>
                [Category("Debug")]
                [Description("If true, logs every material texture uniform binding. Use to diagnose cross-material texture bleed. Very noisy — enable only while reproducing.")]
                public bool LogMaterialTextureBindings
                {
                    get => _logMaterialTextureBindings;
                    set => SetField(ref _logMaterialTextureBindings, value);
                }

                /// <summary>
                /// If true, 
                /// If true, tracked GPU allocations are blocked once the configured VRAM budget is exceeded.
                /// </summary>
                [Category("Performance")]
                [Description("If true, tracked GPU allocations are blocked once the configured VRAM budget is exceeded.")]
                public bool EnableVramBudget
                {
                    get => _enableVramBudget;
                    set => SetField(ref _enableVramBudget, value);
                }

                /// <summary>
                /// Maximum tracked GPU memory budget in megabytes used by the VRAM budget gate.
                /// </summary>
                [Category("Performance")]
                [Description("Maximum tracked GPU memory budget in megabytes used by the VRAM budget gate.")]
                public int VramBudgetMB
                {
                    get => _vramBudgetMB;
                    set => SetField(ref _vramBudgetMB, Math.Clamp(value, 256, 256 * 1024));
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

                [Category("Shadows")]
                [Description("If true, dynamic spot lights render and sample through the dynamic shadow atlas. Disable to use per-light spot shadow maps for debugging.")]
                public bool UseSpotShadowAtlas
                {
                    get => Volatile.Read(ref _useSpotShadowAtlas);
                    set
                    {
                        if (!SetField(ref _useSpotShadowAtlas, value))
                            return;

                        Volatile.Write(ref _useSpotShadowAtlas, value);
                        MarkShadowAtlasRenderResourcesChanged(nameof(UseSpotShadowAtlas));
                    }
                }

                [Category("Shadows")]
                [Description("If true, directional cascades render and sample through the dynamic shadow atlas. Disable to use the legacy cascade texture array for debugging.")]
                public bool UseDirectionalShadowAtlas
                {
                    get => Volatile.Read(ref _useDirectionalShadowAtlas);
                    set
                    {
                        if (!SetField(ref _useDirectionalShadowAtlas, value))
                            return;

                        Volatile.Write(ref _useDirectionalShadowAtlas, value);
                        if (XREngine.Rendering.RenderDiagnosticsFlags.DirectionalShadowAudit)
                        {
                            XREngine.Debug.Lighting(
                                EOutputVerbosity.Normal,
                                false,
                                "[DirectionalShadowAudit][Setting] frame={0} UseDirectionalShadowAtlas={1}",
                                Engine.Rendering.State.RenderFrameId,
                                value);
                        }

                        MarkShadowAtlasRenderResourcesChanged(nameof(UseDirectionalShadowAtlas));
                    }
                }

                [Category("Shadows")]
                [Description("If true, point light faces render and sample through the dynamic shadow atlas. Disable to use legacy per-light cubemap shadows for debugging.")]
                public bool UsePointShadowAtlas
                {
                    get => Volatile.Read(ref _usePointShadowAtlas);
                    set
                    {
                        if (!SetField(ref _usePointShadowAtlas, value))
                            return;

                        Volatile.Write(ref _usePointShadowAtlas, value);
                        MarkShadowAtlasRenderResourcesChanged(nameof(UsePointShadowAtlas));
                    }
                }

                private static void MarkShadowAtlasRenderResourcesChanged(string settingName)
                    => global::XREngine.Rendering.AbstractRenderer.Current?.NotifyRenderResourcesChanged(settingName);

                [Category("Shadows")]
                [Description("Width and height of each dynamic shadow atlas page in texels.")]
                public uint ShadowAtlasPageSize
                {
                    get => _shadowAtlasPageSize;
                    set => SetField(ref _shadowAtlasPageSize, ClampShadowAtlasPowerOfTwo(value, 128u, 16384u));
                }

                [Category("Shadows")]
                [Description("Maximum number of dynamic shadow atlas pages per light-family atlas.")]
                public int MaxShadowAtlasPages
                {
                    get => _maxShadowAtlasPages;
                    set => SetField(ref _maxShadowAtlasPages, Math.Clamp(value, 1, 64));
                }

                [Category("Shadows")]
                [Description("Hard memory cap for dynamic shadow atlas pages. Zero derives the cap from page settings.")]
                public long MaxShadowAtlasMemoryBytes
                {
                    get => _maxShadowAtlasMemoryBytes;
                    set => SetField(ref _maxShadowAtlasMemoryBytes, Math.Max(0L, value));
                }

                [Category("Shadows")]
                [Description("Soft cap for dynamic shadow atlas tile renders per frame.")]
                public int MaxShadowTilesRenderedPerFrame
                {
                    get => _maxShadowTilesRenderedPerFrame;
                    set => SetField(ref _maxShadowTilesRenderedPerFrame, Math.Max(0, value));
                }

                [Category("Shadows")]
                [Description("Soft frame-time budget for dynamic shadow atlas tile rendering in milliseconds.")]
                public float MaxShadowRenderMilliseconds
                {
                    get => _maxShadowRenderMilliseconds;
                    set => SetField(ref _maxShadowRenderMilliseconds, MathF.Max(0.0f, value));
                }

                [Category("Shadows")]
                [Description("Maximum rendered-frame age for directional cascade atlas stale reprojection. Older atlas samples fall back to lit or legacy sampling.")]
                public int MaxDirectionalCascadeAtlasStaleFrames
                {
                    get => Volatile.Read(ref _maxDirectionalCascadeAtlasStaleFrames);
                    set
                    {
                        int clamped = Math.Clamp(value, 0, 120);
                        if (!SetField(ref _maxDirectionalCascadeAtlasStaleFrames, clamped))
                            return;

                        Volatile.Write(ref _maxDirectionalCascadeAtlasStaleFrames, clamped);
                    }
                }

                [Category("Shadows")]
                [Description("Minimum dynamic shadow atlas tile resolution in texels.")]
                public uint MinShadowAtlasTileResolution
                {
                    get => _minShadowAtlasTileResolution;
                    set => SetField(ref _minShadowAtlasTileResolution, ClampShadowAtlasPowerOfTwo(value, 16u, ShadowAtlasPageSize));
                }

                [Category("Shadows")]
                [Description("Maximum dynamic shadow atlas tile resolution in texels.")]
                public uint MaxShadowAtlasTileResolution
                {
                    get => _maxShadowAtlasTileResolution;
                    set => SetField(ref _maxShadowAtlasTileResolution, ClampShadowAtlasPowerOfTwo(value, MinShadowAtlasTileResolution, ShadowAtlasPageSize));
                }

                private static uint ClampShadowAtlasPowerOfTwo(uint value, uint min, uint max)
                {
                    min = Math.Max(1u, min);
                    max = Math.Max(min, max);
                    value = Math.Clamp(value, min, max);

                    uint result = 1u;
                    while (result < value && result < max)
                        result <<= 1;

                    return Math.Clamp(result, min, max);
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
                /// Selects which anti-aliasing technique to use, including temporal AA (TAA) and temporal super resolution (TSR).
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
                /// Enables NVIDIA DLSS frame generation when the Vulkan renderer can provide the required Streamline present path.
                /// </summary>
                [Category("Upscaling")]
                [Description("Enables NVIDIA DLSS frame generation when the Vulkan renderer can provide the required Streamline present path.")]
                public bool EnableNvidiaDlssFrameGeneration
                {
                    get => _enableNvidiaDlssFrameGeneration;
                    set => SetField(ref _enableNvidiaDlssFrameGeneration, value);
                }

                /// <summary>
                /// NVIDIA DLSS frame generation multiplier request.
                /// </summary>
                [Category("Upscaling")]
                [Description("NVIDIA DLSS frame generation multiplier request. Off disables generated frames; OneX through ThreeX request 1x-3x frame generation.")]
                public ENvidiaDlssFrameGenerationMode NvidiaDlssFrameGenerationMode
                {
                    get => _nvidiaDlssFrameGenerationMode;
                    set => SetField(ref _nvidiaDlssFrameGenerationMode, value);
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
                    get => OpenGL.AllowProgramPipelines;
                    set => OpenGL.AllowProgramPipelines = value;
                }

                /// <summary>
                /// When true, the engine will use integers in shaders instead of floats when needed.
                /// </summary>
                [Category("Performance")]
                [Description("When true, the engine will use integers in shaders instead of floats when needed.")]
                public bool UseIntegerUniformsInShaders
                {
                    get => _useIntegerUniformsInShaders;
                    set => SetField(ref _useIntegerUniformsInShaders, value, null, _ => BumpShaderConfigVersion());
                }

                /// <summary>
                /// The logical clip-space Y direction used by renderer backends.
                /// </summary>
                [Category("Rendering")]
                [Description("The logical clip-space Y direction used by renderer backends.")]
                public global::XREngine.Rendering.ERenderClipSpaceYDirection ClipSpaceYDirection
                {
                    get => _clipSpaceYDirection;
                    set => SetField(ref _clipSpaceYDirection, value, null, _ => BumpShaderConfigVersion());
                }

                /// <summary>
                /// The logical clip-space depth range used by camera matrices, depth reconstruction, and renderer backend state.
                /// </summary>
                [Category("Rendering")]
                [Description("The logical clip-space depth range used by camera matrices, depth reconstruction, and renderer backend state.")]
                public global::XREngine.Rendering.ERenderClipDepthRange ClipDepthRange
                {
                    get => _clipDepthRange;
                    set => SetField(ref _clipDepthRange, value, null, _ => BumpShaderConfigVersion());
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
                /// Global override for how transforms publish their render matrix.
                /// Default lets each call decide via its setRenderMatrixNow argument.
                /// ForceDeferred routes every update through the render-matrix queue.
                /// ForceSynchronous publishes every update immediately on the calling tick (diagnostic;
                /// may fire RenderMatrixChanged off the render thread when child recalculation runs in parallel).
                /// </summary>
                [Category("Performance")]
                [Description("Global override for how transforms publish their render matrix. Default lets each call decide via its setRenderMatrixNow argument; ForceDeferred routes every update through the render-matrix queue; ForceSynchronous publishes every update immediately on the calling tick (diagnostic; may fire RenderMatrixChanged off the render thread when child recalculation runs in parallel).")]
                public ERenderMatrixUpdateMode RenderMatrixUpdateMode
                {
                    get => _renderMatrixUpdateMode;
                    set => SetField(ref _renderMatrixUpdateMode, value);
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
                    get => OpenGL.ShaderLinking.AllowBinaryProgramCaching;
                    set => OpenGL.ShaderLinking.AllowBinaryProgramCaching = value;
                }

                /// <summary>
                /// If true, cached program binaries are uploaded via <c>glProgramBinary</c> on a
                /// shared background GL context thread instead of the main render thread.
                /// This prevents rendering stalls during startup and scene transitions.
                /// Requires <see cref="AllowBinaryProgramCaching"/> to be enabled.
                /// </summary>
                [Category("Performance")]
                [Description("If true, cached program binaries are uploaded asynchronously on a shared GL context thread to avoid main-thread stalls. Requires AllowBinaryProgramCaching.")]
                public bool AsyncProgramBinaryUpload
                {
                    get => OpenGL.ShaderLinking.AsyncProgramBinaryUpload;
                    set => OpenGL.ShaderLinking.AsyncProgramBinaryUpload = value;
                }

                /// <summary>
                /// If true, new (uncached) shader programs are compiled and linked on a
                /// shared background GL context thread instead of the main render thread.
                /// This avoids main-thread stalls on drivers that lack
                /// <c>GL_ARB_parallel_shader_compile</c>.
                /// </summary>
                [Category("Performance")]
                [Description("If true, uncached shader programs are compiled and linked on a shared GL context thread to avoid main-thread stalls on drivers without GL_ARB_parallel_shader_compile.")]
                public bool AsyncProgramCompilation
                {
                    get => OpenGL.ShaderLinking.AsyncProgramCompilation;
                    set => OpenGL.ShaderLinking.AsyncProgramCompilation = value;
                }

                /// <summary>
                /// Number of shared-context worker threads used to compile and link
                /// uncached OpenGL shader programs. The runtime uses one worker by
                /// default for driver startup stability; values above one require
                /// XRE_ENABLE_OPENGL_COMPILE_LINK_WORKER_POOL=1. Clamped to [1, 16].
                /// </summary>
                [Category("Performance")]
                [Description("Number of shared-context worker threads used to compile and link uncached OpenGL shader programs. Values above one require XRE_ENABLE_OPENGL_COMPILE_LINK_WORKER_POOL=1. Clamped to [1, 16].")]
                public int OpenGLProgramCompileLinkWorkerCount
                {
                    get => OpenGL.ShaderLinking.ProgramCompileLinkWorkerCount;
                    set => OpenGL.ShaderLinking.ProgramCompileLinkWorkerCount = value;
                }

                /// <summary>
                /// Caps how many pending async OpenGL shader programs the render thread will advance per frame.
                /// Lower values keep camera/input responsiveness higher during large shader warmups.
                /// </summary>
                [Category("Performance")]
                [Description("Maximum number of pending async OpenGL shader programs to poll/finalize per render frame.")]
                public int MaxAsyncShaderProgramsPerFrame
                {
                    get => OpenGL.ShaderLinking.MaxAsyncShaderProgramsPerFrame;
                    set => OpenGL.ShaderLinking.MaxAsyncShaderProgramsPerFrame = value;
                }

                /// <summary>
                /// Selects how uncached OpenGL shader programs are compiled and linked.
                /// Auto prefers driver-parallel compile/link after the startup probe passes,
                /// then falls back to the shared-context source queue and finally synchronous
                /// linking. Known hazard shapes bypass async source lanes. Synchronous only
                /// controls source compile/link; binary cache uploads still obey
                /// <see cref="AsyncProgramBinaryUpload"/>.
                /// </summary>
                [Category("Performance")]
                [Description("Selects how uncached OpenGL shader programs are compiled and linked. Auto prefers driver-parallel after the startup probe, then shared-context, then synchronous. Synchronous only affects source compile/link; async binary upload has its own toggle.")]
                public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy
                {
                    get => OpenGL.ShaderLinking.Strategy;
                    set => OpenGL.ShaderLinking.Strategy = value;
                }

                /// <summary>
                /// Requested worker-thread count for GL_ARB/KHR_parallel_shader_compile.
                /// The default is conservative to avoid flooding the Windows OpenGL driver
                /// during cold uber-shader startup. Use -1 to request the driver maximum,
                /// 0 to request no driver worker threads, or a positive explicit count.
                /// </summary>
                [Category("Performance")]
                [Description("Requested worker-thread count for GL_ARB/KHR_parallel_shader_compile. Default is conservative; use -1 for the driver maximum, 0 for no driver worker threads, or a positive explicit count.")]
                public int OpenGLShaderCompilerThreadCount
                {
                    get => OpenGL.ShaderLinking.DriverCompilerThreadCount;
                    set => OpenGL.ShaderLinking.DriverCompilerThreadCount = value;
                }

                /// <summary>
                /// If true, startup performs a tiny GL_ARB/KHR_parallel_shader_compile
                /// smoke test before using the explicit DriverParallel link path.
                /// </summary>
                [Category("Performance")]
                [Description("If true, startup performs a tiny GL_ARB/KHR_parallel_shader_compile smoke test before using the explicit DriverParallel link path.")]
                public bool OpenGLParallelShaderCompileProbeEnabled
                {
                    get => OpenGL.ShaderLinking.DriverParallelProbeEnabled;
                    set => OpenGL.ShaderLinking.DriverParallelProbeEnabled = value;
                }

                /// <summary>
                /// Maximum time spent polling the startup driver-parallel shader-link probe.
                /// </summary>
                [Category("Performance")]
                [Description("Maximum time in milliseconds spent polling the startup driver-parallel shader-link probe.")]
                public int OpenGLParallelShaderCompileProbeTimeoutMs
                {
                    get => OpenGL.ShaderLinking.DriverParallelProbeTimeoutMs;
                    set => OpenGL.ShaderLinking.DriverParallelProbeTimeoutMs = value;
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
                /// If true, eligible blendshape renderers can precombine active shape deltas into one per-vertex buffer before final skinning or direct vertex evaluation.
                /// </summary>
                [Category("Performance")]
                [Description("If true, eligible blendshape renderers can precombine active shape deltas into one per-vertex buffer before final skinning or direct vertex evaluation.")]
                public bool EnableBlendshapePrecombinePass
                {
                    get => _enableBlendshapePrecombinePass;
                    set => SetField(ref _enableBlendshapePrecombinePass, value, null, _ => BumpShaderConfigVersion());
                }

                /// <summary>
                /// If true, the precombined blendshape buffer may be used by direct vertex-shader blendshape evaluation.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the precombined blendshape buffer may be used by direct vertex-shader blendshape evaluation.")]
                public bool EnableBlendshapePrecombineForDirectVertexPath
                {
                    get => _enableBlendshapePrecombineForDirectVertexPath;
                    set => SetField(ref _enableBlendshapePrecombineForDirectVertexPath, value, null, _ => BumpShaderConfigVersion());
                }

                /// <summary>
                /// If true, cooked blendshape basis payloads may use PCA/SVD basis compression for non-protected shape groups.
                /// </summary>
                [Category("Performance")]
                [Description("If true, cooked blendshape basis payloads may use PCA/SVD basis compression for non-protected shape groups. Disabled unless the mesh has basis data.")]
                public bool EnableBlendshapePcaBasisCompression
                {
                    get => _enableBlendshapePcaBasisCompression;
                    set => SetField(ref _enableBlendshapePcaBasisCompression, value, null, _ => BumpShaderConfigVersion());
                }

                /// <summary>
                /// Minimum active shape count before the compute deformation path considers the precombine pass.
                /// </summary>
                [Category("Performance")]
                [Description("Minimum active shape count before the compute deformation path considers the precombine pass.")]
                public int BlendshapePrecombineComputeMinActiveShapes
                {
                    get => _blendshapePrecombineComputeMinActiveShapes;
                    set => SetField(ref _blendshapePrecombineComputeMinActiveShapes, Math.Max(1, value));
                }

                /// <summary>
                /// Minimum active shape count before the direct vertex path considers the precombine pass.
                /// </summary>
                [Category("Performance")]
                [Description("Minimum active shape count before the direct vertex path considers the precombine pass.")]
                public int BlendshapePrecombineDirectMinActiveShapes
                {
                    get => _blendshapePrecombineDirectMinActiveShapes;
                    set => SetField(ref _blendshapePrecombineDirectMinActiveShapes, Math.Max(1, value));
                }

                /// <summary>
                /// Minimum total affected vertex count before the precombine pass is considered.
                /// </summary>
                [Category("Performance")]
                [Description("Minimum total affected vertex count before the precombine pass is considered.")]
                public int BlendshapePrecombineMinAffectedVertices
                {
                    get => _blendshapePrecombineMinAffectedVertices;
                    set => SetField(ref _blendshapePrecombineMinAffectedVertices, Math.Max(1, value));
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

                /// <summary>
                /// If true, eligible 2D OpenGL textures generate mipmaps with the detail-preserving
                /// compute downscale shader instead of <c>glGenerateTextureMipmap</c>.
                /// Unsupported formats and non-2D paths fall back to standard GL mip generation.
                /// </summary>
                [Category("Performance")]
                [Description("If true, eligible 2D OpenGL textures generate mipmaps with a detail-preserving compute shader instead of glGenerateTextureMipmap. Unsupported formats and non-2D paths fall back to standard GL mip generation.")]
                public bool UseDetailPreservingComputeMipmaps
                {
                    get => OpenGL.TextureUpload.UseDetailPreservingComputeMipmaps;
                    set => OpenGL.TextureUpload.UseDetailPreservingComputeMipmaps = value;
                }

                /// <summary>
                /// If true (and compute skinning is enabled), skin palettes will be packed into a single global SSBO for all visible renderers.
                /// This reduces per-renderer SSBO binding and upload churn at the cost of building a packed buffer each render.
                /// </summary>
                [Category("Performance")]
                [Description("If true (and compute skinning is enabled), packs skin palettes into a single global SSBO for all visible renderers.")]
                public bool UseGlobalSkinPaletteBufferForComputeSkinning
                {
                    get => _useGlobalSkinPaletteBufferForComputeSkinning;
                    set => SetField(ref _useGlobalSkinPaletteBufferForComputeSkinning, value);
                }

                /// <summary>
                /// If true (and compute blendshapes are enabled), blendshape weights will be packed into a single global SSBO for all visible renderers.
                /// This reduces per-renderer SSBO binding and upload churn at the cost of building a packed buffer each render.
                /// </summary>
                [Category("Performance")]
                [Description("If true (and compute blendshapes are enabled), packs blendshape weights into a single global SSBO for all visible renderers.")]
                public bool UseGlobalBlendshapeWeightsBufferForComputeSkinning
                {
                    get => _useGlobalBlendshapeWeightsBufferForComputeSkinning;
                    set => SetField(ref _useGlobalBlendshapeWeightsBufferForComputeSkinning, value);
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
                /// When true (and <see cref="CalculateSkinnedBoundsInComputeShader"/> is also true),
                /// the engine writes skinned mesh world-space AABBs directly into the GPU command
                /// AABB buffer (BVH leaf bounds) via the reduce shader, bypassing the CPU 8-corner
                /// transform performed by GPUScene.WriteTightCommandAabb. Requires the internal BVH.
                /// </summary>
                [Category("Performance")]
                [Description("When true (and CalculateSkinnedBoundsInComputeShader is also true), the engine writes skinned mesh world-space AABBs directly into the GPU command AABB buffer via the reduce shader, bypassing the CPU 8-corner transform.")]
                public bool SkinnedBoundsGpuDirectAabbWrite
                {
                    get => _skinnedBoundsGpuDirectAabbWrite;
                    set => SetField(ref _skinnedBoundsGpuDirectAabbWrite, value);
                }

                /// <summary>
                /// Controls when skinned mesh bounds are recomputed at runtime.
                /// Never uses bind-pose or cached bounds only, Selective refreshes on a throttled cadence,
                /// and Always refreshes whenever skinned data changes.
                /// </summary>
                [Category("Performance")]
                [Description("Controls when skinned mesh bounds are recomputed at runtime. Never uses bind-pose or cached bounds only, Selective refreshes on a throttled cadence, and Always refreshes whenever skinned data changes.")]
                public ESkinnedBoundsRecomputePolicy SkinnedBoundsRecomputePolicy
                {
                    get => _skinnedBoundsRecomputePolicy;
                    set => SetField(ref _skinnedBoundsRecomputePolicy, value);
                }

                /// <summary>
                /// When true, the Never skinned-bounds policy still allows one initial runtime build
                /// for meshes that do not have cached bounds yet.
                /// </summary>
                [Category("Performance")]
                [Description("When true, the Never skinned-bounds policy still allows one initial runtime build for meshes that do not have cached bounds yet.")]
                public bool AllowInitialSkinnedBoundsBuildWhenNever
                {
                    get => _allowInitialSkinnedBoundsBuildWhenNever;
                    set => SetField(ref _allowInitialSkinnedBoundsBuildWhenNever, value);
                }

                /// <summary>
                /// Selects the CPU spatial structure used for render visibility when GPU dispatch is disabled.
                /// Can be overridden with XRE_CPU_SCENE_CULLING_STRUCTURE=Octree|Bvh.
                /// </summary>
                [Category("BVH")]
                [Description("Selects the CPU spatial structure used for render visibility when GPU dispatch is disabled. Can be overridden with XRE_CPU_SCENE_CULLING_STRUCTURE=Octree|Bvh.")]
                public ECpuSceneCullingStructure CpuSceneCullingStructure
                {
                    get => _cpuSceneCullingStructure;
                    set => SetField(ref _cpuSceneCullingStructure, value, null, _ => Rendering.ApplyCpuSceneCullingStructurePreference());
                }

                /// <summary>
                /// Selects the Vulkan GPU-driven runtime profile used to gate feature policy.
                /// Auto maps Debug builds to DevParity and non-Debug builds to ShippingFast.
                /// </summary>
                [Category("Vulkan")]
                [Description("Selects the Vulkan GPU-driven runtime profile used to gate feature policy. Auto maps Debug builds to DevParity and non-Debug builds to ShippingFast.")]
                public EVulkanGpuDrivenProfile VulkanGpuDrivenProfile
                {
                    get => Vulkan.GpuDriven.Profile;
                    set => Vulkan.GpuDriven.Profile = value;
                }

                /// <summary>
                /// Selects Vulkan queue overlap policy for queue-family ownership transitions.
                /// Auto resolves from active profile and runtime metrics.
                /// </summary>
                [Category("Vulkan")]
                [Description("Selects Vulkan queue overlap policy for queue-family ownership transitions. Auto resolves from active profile and runtime metrics.")]
                public EVulkanQueueOverlapMode VulkanQueueOverlapMode
                {
                    get => Vulkan.Synchronization.QueueOverlapMode;
                    set => Vulkan.Synchronization.QueueOverlapMode = value;
                }

                /// <summary>
                /// Enables correctness-validated Vulkan primary and stable secondary command-buffer reuse.
                /// </summary>
                [Category("Vulkan")]
                [Description("Enables correctness-validated Vulkan primary and stable secondary command-buffer reuse. XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0 disables reuse for diagnostics.")]
                public bool EnableVulkanPrimaryCommandBufferReuse
                {
                    get => Vulkan.CommandRecording.PrimaryCommandBufferReuseEnabled;
                    set => Vulkan.CommandRecording.PrimaryCommandBufferReuseEnabled = value;
                }

                /// <summary>
                /// Selects the named Vulkan diagnostics preset used during backend startup.
                /// Environment override: XRE_VULKAN_DIAGNOSTIC_PRESET=Off|StandardValidation|SyncValidation|GpuAssisted|BestPractices|CrashDiagnostics|RenderDocFriendly.
                /// </summary>
                [Category("Vulkan")]
                [Description("Selects the named Vulkan diagnostics preset used during backend startup. Environment override: XRE_VULKAN_DIAGNOSTIC_PRESET.")]
                public EVulkanDiagnosticPreset VulkanDiagnosticPreset
                {
                    get => Vulkan.Diagnostics.DiagnosticPreset;
                    set => Vulkan.Diagnostics.DiagnosticPreset = value;
                }

                /// <summary>
                /// Enables additional Vulkan diagnostics independently of the selected preset.
                /// Environment override: XRE_VULKAN_DIAGNOSTIC_FLAGS accepts comma/pipe-separated flag names.
                /// </summary>
                [Category("Vulkan")]
                [Description("Enables additional Vulkan diagnostics independently of the selected preset. Environment override: XRE_VULKAN_DIAGNOSTIC_FLAGS.")]
                public EVulkanDiagnosticFlags VulkanDiagnosticFlags
                {
                    get => Vulkan.Diagnostics.DiagnosticFlags;
                    set => Vulkan.Diagnostics.DiagnosticFlags = value;
                }

                /// <summary>
                /// Enables Vulkan descriptor indexing for large runtime descriptor arrays when supported.
                /// </summary>
                [Category("Vulkan")]
                [Description("Enables Vulkan descriptor indexing for large runtime descriptor arrays when supported.")]
                public bool EnableVulkanDescriptorIndexing
                {
                    get => Vulkan.Descriptors.EnableDescriptorIndexing;
                    set => Vulkan.Descriptors.EnableDescriptorIndexing = value;
                }

                /// <summary>
                /// Enables global material-table population path for GPU-driven rendering.
                /// </summary>
                [Category("Vulkan")]
                [Description("Enables global material-table population path for GPU-driven rendering.")]
                public bool EnableVulkanBindlessMaterialTable
                {
                    get => Vulkan.Descriptors.EnableBindlessMaterialTable;
                    set => Vulkan.Descriptors.EnableBindlessMaterialTable = value;
                }

                /// <summary>
                /// Selects Vulkan bindless material-table policy. Environment override:
                /// XRE_VULKAN_BINDLESS_MATERIAL_MODE=Auto|Disabled|Required|Diagnostics.
                /// </summary>
                [Category("Vulkan")]
                [Description("Selects Vulkan bindless material-table policy. Auto is conservative, Required fails visibly when unsupported, Diagnostics adds capability logging.")]
                public EVulkanBindlessMaterialMode VulkanBindlessMaterialMode
                {
                    get => Vulkan.Descriptors.BindlessMaterialMode;
                    set => Vulkan.Descriptors.BindlessMaterialMode = value;
                }

                /// <summary>
                /// Validates descriptor contract tiers against reflected shader bindings.
                /// </summary>
                [Category("Vulkan")]
                [Description("Validates descriptor contract tiers against reflected shader bindings.")]
                public bool ValidateVulkanDescriptorContracts
                {
                    get => Vulkan.Descriptors.ValidateContracts;
                    set => Vulkan.Descriptors.ValidateContracts = value;
                }

                /// <summary>
                /// Selects optional Vulkan geometry fetch strategy.
                /// </summary>
                [Category("Vulkan")]
                [Description("Selects optional Vulkan geometry fetch strategy. Prototype path must remain opt-in until validated.")]
                public EVulkanGeometryFetchMode VulkanGeometryFetchMode
                {
                    get => Vulkan.GpuDriven.GeometryFetchMode;
                    set => Vulkan.GpuDriven.GeometryFetchMode = value;
                }

                /// <summary>
                /// Selects whether Vulkan render targets use dynamic rendering or legacy render passes.
                /// </summary>
                [Category("Vulkan")]
                [Description("Selects whether Vulkan render targets use dynamic rendering or legacy render passes. XRE_VK_RENDER_TARGET_MODE overrides this at runtime.")]
                public EVulkanRenderTargetMode VulkanRenderTargetMode
                {
                    get => Vulkan.TargetMode.RenderTargetMode;
                    set => Vulkan.TargetMode.RenderTargetMode = value;
                }

                /// <summary>
                /// Selects the GPU command data layout policy used before culling.
                /// </summary>
                [Category("Culling")]
                [Description("Selects the GPU command data layout policy used before culling. SoA mode enables extraction for runtime experimentation/benchmarking.")]
                public EGpuCullingDataLayout GpuCullingDataLayout
                {
                    get => _gpuCullingDataLayout;
                    set => SetField(ref _gpuCullingDataLayout, value);
                }

                /// <summary>
                /// Selects GPU sorting domains for command ordering before indirect draw generation.
                /// </summary>
                [Category("Culling")]
                [Description("Selects GPU sorting domains for command ordering before indirect draw generation.")]
                public EGpuSortDomainPolicy GpuSortDomainPolicy
                {
                    get => _gpuSortDomainPolicy;
                    set => SetField(ref _gpuSortDomainPolicy, value,
                        null,
                        _ => Rendering.LogVulkanFeatureProfileFingerprint());
                }

                /// <summary>
                /// Selects which mesh occlusion culling path to run.
                /// </summary>
                [Category("Occlusion")]
                [Description("Selects which mesh occlusion culling path to run. CpuQueryAsync uses OpenGL or Vulkan hardware queries on CPU direct; DX12 forces visible. GpuHiZ requires GPU dispatch, while CPU direct uses CpuQueryAsync or CpuSoftwareOcclusion.")]
                public EOcclusionCullingMode GpuOcclusionCullingMode
                {
                    get => _gpuOcclusionCullingMode;
                    set => SetField(ref _gpuOcclusionCullingMode, value);
                }

                /// <summary>
                /// When true, the Hi-Z depth pyramid used by GPU_HiZ occlusion is built once per render frame
                /// and shared across GPU indirect render passes.
                /// When false, each GPU pass builds its own pyramid (safer when depth changes mid-frame).
                /// </summary>
                [Category("Occlusion")]
                [Description("When true, builds Hi-Z once per render frame and shares it across GPU passes. When false, builds per-pass.")]
                public bool CacheGpuHiZOcclusionOncePerFrame
                {
                    get => _cacheGpuHiZOcclusionOncePerFrame;
                    set => SetField(ref _cacheGpuHiZOcclusionOncePerFrame, value);
                }

                /// <summary>
                /// When true, non-essential mesh LOD levels stay out of the GPU mesh atlas until the
                /// GPU LOD-select pass requests them. LOD0 and each command's own mesh always stay resident,
                /// so selection clamps to the nearest resident level while finer/coarser data streams in.
                /// </summary>
                [Category("LOD Streaming")]
                [Description("When true, defers non-essential mesh LOD atlas uploads until the GPU LOD-select pass requests them. LOD0 and each command's own mesh always stay resident.")]
                public bool StreamMeshLodsOnDemand
                {
                    get => _streamMeshLodsOnDemand;
                    set => SetField(ref _streamMeshLodsOnDemand, value);
                }

                /// <summary>
                /// Render-frame interval between drains of the GPU mesh LOD request buffer.
                /// Lower values reduce LOD pop-in latency; higher values reduce map/readback pressure.
                /// </summary>
                [Category("LOD Streaming")]
                [Description("Frames between GPU mesh LOD request buffer drains (1..64). Lower = faster LOD loads, higher = less readback pressure.")]
                public int MeshLodStreamingDrainIntervalFrames
                {
                    get => _meshLodStreamingDrainIntervalFrames;
                    set => SetField(ref _meshLodStreamingDrainIntervalFrames, Math.Clamp(value, 1, 64));
                }

                /// <summary>
                /// Maximum mesh LOD atlas loads serviced per request-buffer drain. Bounds per-frame
                /// upload work; dropped requests are re-raised by the GPU while the LOD stays non-resident.
                /// </summary>
                [Category("LOD Streaming")]
                [Description("Maximum mesh LOD atlas loads serviced per drain (1..256). Dropped requests are re-raised by the GPU on later frames.")]
                public int MeshLodStreamingMaxLoadsPerDrain
                {
                    get => _meshLodStreamingMaxLoadsPerDrain;
                    set => SetField(ref _meshLodStreamingMaxLoadsPerDrain, Math.Clamp(value, 1, 256));
                }

                /// <summary>
                /// Period (in render frames) at which a fully-occluded CPU-query mesh is forced
                /// to redraw + requery so it can detect unocclusion. Lower values reduce visibility
                /// latency when an occluder moves; higher values reduce per-frame retest cost.
                /// Per-command stagger keeps the worst-case retest count bounded.
                /// Range: 1..64. Default 6.
                /// </summary>
                [Category("Occlusion")]
                [Description("CPU-query occlusion: frames between forced redraw+requery of fully-occluded meshes (1..64). Lower = faster unocclusion response, higher = lower retest cost.")]
                public int CpuQueryOcclusionRetestPeriodFrames
                {
                    get => _cpuQueryOcclusionRetestPeriodFrames;
                    set => SetField(ref _cpuQueryOcclusionRetestPeriodFrames, Math.Clamp(value, 1, 64));
                }

                /// <summary>
                /// Maximum unresolved CPU hardware occlusion queries plus current-frame
                /// proxy reservations for one pass/view scope. Shared stereo scopes
                /// consume this capacity once for the stereo frame.
                /// </summary>
                [Category("Occlusion")]
                [Description("CPU-query occlusion: per-pass/view cap on unresolved queries plus current-frame proxy reservations.")]
                public int CpuQueryOcclusionMaxQueriesPerFrame
                {
                    get => _cpuQueryOcclusionMaxQueriesPerFrame;
                    set => SetField(ref _cpuQueryOcclusionMaxQueriesPerFrame, Math.Clamp(value, 0, 4096));
                }

                /// <summary>
                /// Fraction of the CPU query budget reserved for visible-demotion probes.
                /// The remainder is reserved for occluded-recovery probes.
                /// </summary>
                [Category("Occlusion")]
                [Description("CPU-query occlusion: fraction of query budget reserved for visible-demotion probes. The rest is recovery.")]
                public float CpuQueryOcclusionVisibleDemotionBudgetFraction
                {
                    get => _cpuQueryOcclusionVisibleDemotionBudgetFraction;
                    set => SetField(ref _cpuQueryOcclusionVisibleDemotionBudgetFraction, Math.Clamp(value, 0.0f, 1.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: minimum frames between recovery probes for a predicted-occluded command.")]
                public int CpuQueryOcclusionRecoveryMinCadenceFrames
                {
                    get => _cpuQueryOcclusionRecoveryMinCadenceFrames;
                    set => SetField(ref _cpuQueryOcclusionRecoveryMinCadenceFrames, Math.Clamp(value, 1, 64));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: translation treated as Stable at a nominal 60 Hz, scaled by render delta.")]
                public float CpuQueryOcclusionSmallMotionMeters
                {
                    get => _cpuQueryOcclusionSmallMotionMeters;
                    set => SetField(ref _cpuQueryOcclusionSmallMotionMeters, Math.Clamp(value, 0.0f, 100.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: nominal 60 Hz translation threshold for MediumMotion, scaled by render delta.")]
                public float CpuQueryOcclusionMediumMotionMeters
                {
                    get => _cpuQueryOcclusionMediumMotionMeters;
                    set => SetField(ref _cpuQueryOcclusionMediumMotionMeters, Math.Clamp(value, 0.0f, 100.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: nominal 60 Hz translation threshold for LargeMotion, scaled by render delta.")]
                public float CpuQueryOcclusionLargeMotionMeters
                {
                    get => _cpuQueryOcclusionLargeMotionMeters;
                    set => SetField(ref _cpuQueryOcclusionLargeMotionMeters, Math.Clamp(value, 0.0f, 100.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: camera translation threshold in meters for CameraCut.")]
                public float CpuQueryOcclusionCameraCutMeters
                {
                    get => _cpuQueryOcclusionCameraCutMeters;
                    set => SetField(ref _cpuQueryOcclusionCameraCutMeters, Math.Clamp(value, 0.0f, 1000.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: rotation treated as Stable at a nominal 60 Hz, scaled by render delta.")]
                public float CpuQueryOcclusionSmallRotationDegrees
                {
                    get => _cpuQueryOcclusionSmallRotationDegrees;
                    set => SetField(ref _cpuQueryOcclusionSmallRotationDegrees, Math.Clamp(value, 0.0f, 180.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: nominal 60 Hz rotation threshold for MediumMotion, scaled by render delta.")]
                public float CpuQueryOcclusionMediumRotationDegrees
                {
                    get => _cpuQueryOcclusionMediumRotationDegrees;
                    set => SetField(ref _cpuQueryOcclusionMediumRotationDegrees, Math.Clamp(value, 0.0f, 180.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: nominal 60 Hz rotation threshold for LargeMotion, scaled by render delta.")]
                public float CpuQueryOcclusionLargeRotationDegrees
                {
                    get => _cpuQueryOcclusionLargeRotationDegrees;
                    set => SetField(ref _cpuQueryOcclusionLargeRotationDegrees, Math.Clamp(value, 0.0f, 180.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: camera rotation threshold in degrees for CameraCut.")]
                public float CpuQueryOcclusionCameraCutRotationDegrees
                {
                    get => _cpuQueryOcclusionCameraCutRotationDegrees;
                    set => SetField(ref _cpuQueryOcclusionCameraCutRotationDegrees, Math.Clamp(value, 0.0f, 180.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: normal VR head-pose translation threshold in meters; below this never becomes CameraCut.")]
                public float CpuQueryOcclusionVrHeadMotionMeters
                {
                    get => _cpuQueryOcclusionVrHeadMotionMeters;
                    set => SetField(ref _cpuQueryOcclusionVrHeadMotionMeters, Math.Clamp(value, 0.0f, 10.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: normal VR head-pose rotation threshold in degrees; below this never becomes CameraCut.")]
                public float CpuQueryOcclusionVrHeadRotationDegrees
                {
                    get => _cpuQueryOcclusionVrHeadRotationDegrees;
                    set => SetField(ref _cpuQueryOcclusionVrHeadRotationDegrees, Math.Clamp(value, 0.0f, 180.0f));
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: stereo query policy. Shared stereo defaults conservative-visible unless explicitly allowed.")]
                public ECpuQueryStereoMode CpuQueryOcclusionStereoMode
                {
                    get => _cpuQueryOcclusionStereoMode;
                    set => SetField(ref _cpuQueryOcclusionStereoMode, value);
                }

                [Category("Occlusion")]
                [Description("CPU-query occlusion: maximum age in frames for an unresolved pending query before it is discarded and forced visible.")]
                public int CpuQueryOcclusionMaxPendingFrames
                {
                    get => _cpuQueryOcclusionMaxPendingFrames;
                    set => SetField(ref _cpuQueryOcclusionMaxPendingFrames, Math.Clamp(value, 1, 120));
                }

                /// <summary>
                /// Legacy opt-in: enable the CPU software-rasterizer occluder pass (Masked SOC-style).
                /// Rasterizes a conservative low-resolution opaque depth buffer on the CPU.
                /// Prefer GpuOcclusionCullingMode=CpuSoftwareOcclusion for new captures.
                /// Env override: XRE_CPU_SOC_OCCLUSION=1. See C-CPU-3 in the perf-debug plan.
                /// </summary>
                [Category("Occlusion")]
                [Description("Legacy opt-in: enable CPU software-rasterizer occluder pass (SOC). Prefer CpuSoftwareOcclusion mode.")]
                public bool EnableCpuSoftwareOcclusionCulling
                {
                    get => _enableCpuSoftwareOcclusionCulling;
                    set => SetField(ref _enableCpuSoftwareOcclusionCulling, value);
                }

                [Category("Occlusion")]
                [Description("CPU SOC internal depth buffer width in pixels.")]
                public int CpuSocBufferWidth
                {
                    get => _cpuSocBufferWidth;
                    set => SetField(ref _cpuSocBufferWidth, Math.Clamp(value, 64, 4096));
                }

                [Category("Occlusion")]
                [Description("CPU SOC internal depth buffer height in pixels.")]
                public int CpuSocBufferHeight
                {
                    get => _cpuSocBufferHeight;
                    set => SetField(ref _cpuSocBufferHeight, Math.Clamp(value, 32, 4096));
                }

                [Category("Occlusion")]
                [Description("CPU SOC per-frame triangle budget for selected occluders.")]
                public int CpuSocOccluderTriangleBudget
                {
                    get => _cpuSocOccluderTriangleBudget;
                    set => SetField(ref _cpuSocOccluderTriangleBudget, Math.Clamp(value, 0, 1_000_000));
                }

                [Category("Occlusion")]
                [Description("CPU SOC maximum number of opaque mesh occluders selected per frame.")]
                public int CpuSocMaxOccluders
                {
                    get => _cpuSocMaxOccluders;
                    set => SetField(ref _cpuSocMaxOccluders, Math.Clamp(value, 0, 4096));
                }

                [Category("Occlusion")]
                [Description("CPU SOC minimum projected occluder screen area, normalized 0..1.")]
                public float CpuSocMinOccluderScreenArea
                {
                    get => _cpuSocMinOccluderScreenArea;
                    set => SetField(ref _cpuSocMinOccluderScreenArea, Math.Clamp(value, 0.0f, 1.0f));
                }

                [Category("Occlusion")]
                [Description("Allows the CPU SOC implementation to use AVX2 when a SIMD path is available.")]
                public bool CpuSocUseAvx2
                {
                    get => _cpuSocUseAvx2;
                    set => SetField(ref _cpuSocUseAvx2, value);
                }

                [Category("Occlusion")]
                [Description("Enables CPU SOC debug depth-buffer readback.")]
                public bool CpuSocDebugVisualization
                {
                    get => _cpuSocDebugVisualization;
                    set => SetField(ref _cpuSocDebugVisualization, value);
                }

                [Category("Occlusion")]
                [Description("Forces CPU SOC tests visible while still building telemetry and occluder buffers.")]
                public bool CpuSocDebugForceVisible
                {
                    get => _cpuSocDebugForceVisible;
                    set => SetField(ref _cpuSocDebugForceVisible, value);
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
                /// When enabled, CPU skinned-mesh BVH updates will attempt to refit and optimize existing trees
                /// instead of rebuilding from scratch. Falls back to full rebuild when refit is not possible.
                /// </summary>
                [Category("BVH")]
                [Description("When enabled, CPU skinned-mesh BVH updates will attempt to refit and optimize existing trees instead of rebuilding from scratch. Falls back to full rebuild when refit is not possible.")]
                public bool UseSkinnedBvhRefitOptimize
                {
                    get => _useSkinnedBvhRefitOptimize;
                    set => SetField(ref _useSkinnedBvhRefitOptimize, value);
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
                /// Enables GPU timestamp queries around BVH build/refit/cull/raycast compute dispatches for profiling.
                /// Disable to remove query overhead when not profiling.
                /// </summary>
                [Category("BVH")]
                [Description("Enables GPU timestamp queries around BVH compute dispatches for profiling. Disable to avoid query overhead when not inspecting timings.")]
                public bool EnableGpuBvhTimingQueries
                {
                    get => _enableGpuBvhTimingQueries;
                    set => SetField(ref _enableGpuBvhTimingQueries, value);
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
                /// If true, the engine will allow skinning.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will allow skinning.")]
                public bool AllowSkinning
                {
                    get => _allowSkinning;
                    set => SetField(ref _allowSkinning, value, _ => BumpShaderConfigVersion());
                }

                /// <summary>
                /// If true, the engine will allow blendshapes.
                /// </summary>
                [Category("Performance")]
                [Description("If true, the engine will allow blendshapes.")]
                public bool AllowBlendshapes
                {
                    get => _allowBlendshapes;
                    set => SetField(ref _allowBlendshapes, value, null, _ => BumpShaderConfigVersion());
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
                    set => SetField(ref _useAbsoluteBlendshapePositions, value, null, _ => BumpShaderConfigVersion());
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
                /// Selects the VR view rendering strategy.
                /// </summary>
                [Category("VR")]
                [Description("Selects sequential view rendering, single-pass stereo, or Vulkan-only parallel command-buffer recording.")]
                public EVrViewRenderMode VrViewRenderMode
                {
                    get => _vrViewRenderMode;
                    set => SetField(ref _vrViewRenderMode, value);
                }

                /// <summary>
                /// Legacy compatibility view of <see cref="VrViewRenderMode"/>.
                /// </summary>
                [Category("VR")]
                [Description("Legacy compatibility toggle. True maps to SinglePassStereo; false maps to SequentialViews.")]
                public bool RenderVRSinglePassStereo
                {
                    get => _vrViewRenderMode == EVrViewRenderMode.SinglePassStereo;
                    set => VrViewRenderMode = value
                        ? EVrViewRenderMode.SinglePassStereo
                        : EVrViewRenderMode.SequentialViews;
                }

                /// <summary>
                /// Explicitly selects the desktop output policy used while VR is active.
                /// </summary>
                [Category("VR")]
                [Description("Explicitly selects the desktop output policy while VR is active. BlitSubmittedEye is the standard profiling mode; FullIndependentRender is expensive and intended for diagnostics.")]
                public EVrMirrorMode VrMirrorMode
                {
                    get => _vrMirrorMode;
                    set => SetField(ref _vrMirrorMode, value);
                }

                /// <summary>
                /// If true, windows will be rendered while in VR mode.
                /// </summary>
                [Category("VR")]
                [Description("Legacy compatibility toggle for VR desktop output. Prefer VrMirrorMode; Off maps to false, other modes map to true.")]
                public bool RenderWindowsWhileInVR
                {
                    get => _vrMirrorMode != EVrMirrorMode.Off && _renderWindowsWhileInVR;
                    set
                    {
                        if (SetField(ref _renderWindowsWhileInVR, value) && !value)
                            VrMirrorMode = EVrMirrorMode.Off;
                    }
                }

                /// <summary>
                /// If true, desktop mirror output while in VR is composed from already rendered eye textures.
                /// When false, the desktop window follows the legacy full-scene viewport render path.
                /// </summary>
                [Category("VR")]
                [Description("Legacy compatibility toggle for cheap VR desktop mirror composition. Prefer VrMirrorMode=BlitSubmittedEye.")]
                public bool VrMirrorComposeFromEyeTextures
                {
                    get => _vrMirrorMode != EVrMirrorMode.Off &&
                        (_vrMirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct || _vrMirrorComposeFromEyeTextures);
                    set
                    {
                        if (!SetField(ref _vrMirrorComposeFromEyeTextures, value))
                            return;

                        if (value)
                            VrMirrorMode = EVrMirrorMode.BlitSubmittedEye;
                        else if (_vrMirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct)
                            VrMirrorMode = EVrMirrorMode.FullIndependentRender;
                    }
                }

                /// <summary>
                /// If true, OpenXR eye swapchain output is copied into preview textures for stereo preview UI or diagnostics.
                /// </summary>
                [Category("VR")]
                [Description("If true, OpenXR eye swapchain output is copied into preview textures for stereo preview UI or diagnostics.")]
                public bool VrCopyEyePreviewTextures
                {
                    get => _vrCopyEyePreviewTextures;
                    set => SetField(ref _vrCopyEyePreviewTextures, value);
                }

                [Category("VR")]
                [Description("Target presentation rate for the left eye output. 0 matches the XR runtime cadence.")]
                public float VrLeftEyeTargetRateHz
                {
                    get => _vrLeftEyeTargetRateHz;
                    set => SetField(ref _vrLeftEyeTargetRateHz, MathF.Max(0.0f, value));
                }

                [Category("VR")]
                [Description("Target presentation rate for the right eye output. 0 matches the XR runtime cadence.")]
                public float VrRightEyeTargetRateHz
                {
                    get => _vrRightEyeTargetRateHz;
                    set => SetField(ref _vrRightEyeTargetRateHz, MathF.Max(0.0f, value));
                }

                [Category("VR")]
                [Description("Target rate for the independent desktop editor output while VR is active. 0 matches the XR runtime cadence.")]
                public float VrDesktopEditorTargetRateHz
                {
                    get => _vrDesktopEditorTargetRateHz;
                    set => SetField(ref _vrDesktopEditorTargetRateHz, MathF.Max(0.0f, value));
                }

                [Category("VR")]
                [Description("Target rate for the cyclopean desktop preview while VR is active. 0 matches the XR runtime cadence.")]
                public float VrCyclopeanDesktopTargetRateHz
                {
                    get => _vrCyclopeanDesktopTargetRateHz;
                    set => SetField(ref _vrCyclopeanDesktopTargetRateHz, MathF.Max(0.0f, value));
                }

                [Category("VR")]
                [Description("When true, desktop-facing VR outputs are skipped for a frame if the previous whole-frame cost already exceeded the active XR budget band.")]
                public bool VrDesktopAutoSkipWhenOverBudget
                {
                    get => _vrDesktopAutoSkipWhenOverBudget;
                    set => SetField(ref _vrDesktopAutoSkipWhenOverBudget, value);
                }

                /// <summary>
                /// If true, ViewSet generation adds per-eye foveated views in addition to full-resolution stereo views.
                /// </summary>
                [Category("VR")]
                [Description("If true, ViewSet generation adds per-eye foveated views in addition to full-resolution stereo views.")]
                public bool EnableVrFoveatedViewSet
                {
                    get => _enableVrFoveatedViewSet;
                    set => SetField(ref _enableVrFoveatedViewSet, value);
                }

                [Category("VR")]
                [Description("Requested VR foveated rendering mode. Unsupported explicit requests must be reported rather than silently disabled.")]
                public EVrFoveationMode VrFoveationMode
                {
                    get => _vrFoveationMode;
                    set => SetField(ref _vrFoveationMode, value);
                }

                [Category("VR")]
                [Description("Quality preset used when VR foveated rendering is supported by the active backend/runtime.")]
                public EVrFoveationQualityPreset VrFoveationQualityPreset
                {
                    get => _vrFoveationQualityPreset;
                    set => SetField(ref _vrFoveationQualityPreset, value);
                }

                [Category("VR")]
                [Description("If true, explicitly requested VR foveation is treated as a visible configuration failure when unsupported.")]
                public bool VrFoveationRequireRequested
                {
                    get => _vrFoveationRequireRequested;
                    set => SetField(ref _vrFoveationRequireRequested, value);
                }

                /// <summary>
                /// Selects the base per-eye OpenXR swapchain resolution before scale is applied.
                /// </summary>
                [Category("VR")]
                [Description("Selects the base per-eye OpenXR swapchain resolution before OpenXrEyeResolutionScale is applied.")]
                public EOpenXrEyeResolutionPreset OpenXrEyeResolutionPreset
                {
                    get => _openXrEyeResolutionPreset;
                    set => SetField(ref _openXrEyeResolutionPreset, value);
                }

                /// <summary>
                /// Multiplies the selected OpenXR eye resolution. Valid range is 0.1x to 2.0x.
                /// </summary>
                [Category("VR")]
                [Description("Multiplies the selected OpenXR eye resolution. Valid range is 0.1x to 2.0x.")]
                public float OpenXrEyeResolutionScale
                {
                    get => _openXrEyeResolutionScale;
                    set => SetField(ref _openXrEyeResolutionScale, Math.Clamp(value, 0.1f, 2.0f));
                }

                /// <summary>
                /// Custom per-eye OpenXR swapchain width used when OpenXrEyeResolutionPreset is Custom.
                /// </summary>
                [Category("VR")]
                [Description("Custom per-eye OpenXR swapchain width used when OpenXrEyeResolutionPreset is Custom. A value of 0 falls back to the runtime recommendation.")]
                public uint OpenXrCustomEyeResolutionWidth
                {
                    get => _openXrCustomEyeResolutionWidth;
                    set => SetField(ref _openXrCustomEyeResolutionWidth, value);
                }

                /// <summary>
                /// Custom per-eye OpenXR swapchain height used when OpenXrEyeResolutionPreset is Custom.
                /// </summary>
                [Category("VR")]
                [Description("Custom per-eye OpenXR swapchain height used when OpenXrEyeResolutionPreset is Custom. A value of 0 falls back to the runtime recommendation.")]
                public uint OpenXrCustomEyeResolutionHeight
                {
                    get => _openXrCustomEyeResolutionHeight;
                    set => SetField(ref _openXrCustomEyeResolutionHeight, value);
                }

                /// <summary>
                /// If true, OpenXR eye viewports use frustum culling during CollectVisible.
                /// Disable only for debugging projection/pose issues.
                /// </summary>
                [Category("VR")]
                [Description("If true, OpenXR eye viewports use frustum culling during CollectVisible. Disable only for debugging projection/pose issues.")]
                public bool OpenXrCullWithFrustum
                {
                    get => _openXrCullWithFrustum;
                    set => SetField(ref _openXrCullWithFrustum, value);
                }

                /// <summary>
                /// If true, OpenXR waits/begins/locates the next frame after desktop viewport rendering completes.
                /// </summary>
                [Category("VR")]
                [Description("If true, OpenXR waits/begins/locates the next frame after desktop viewport rendering completes, reducing desktop stalls from xrWaitFrame.")]
                public bool OpenXrPrepareFrameAfterDesktopRender
                {
                    get => _openXrPrepareFrameAfterDesktopRender;
                    set => SetField(ref _openXrPrepareFrameAfterDesktopRender, value);
                }

                /// <summary>
                /// Safety margin, in milliseconds, used to flag predicted-display deadline misses after xrWaitFrame returns.
                /// </summary>
                [Category("VR")]
                [Description("Safety margin, in milliseconds, used to flag predicted-display deadline misses after xrWaitFrame returns.")]
                public float OpenXrDeadlineSafetyMarginMs
                {
                    get => _openXrDeadlineSafetyMarginMs;
                    set => SetField(ref _openXrDeadlineSafetyMarginMs, MathF.Max(0.0f, value));
                }

                /// <summary>
                /// Signed millisecond bias added to OpenXR pose location time.
                /// </summary>
                [Category("VR")]
                [Description("Signed millisecond bias added to OpenXR pose location time. Positive values ask the runtime to predict poses further ahead without changing xrEndFrame display time.")]
                public float OpenXrPoseTimeOffsetMs
                {
                    get => _openXrPoseTimeOffsetMs;
                    set => SetField(
                        ref _openXrPoseTimeOffsetMs,
                        Math.Clamp(value, OpenXRAPI.OpenXrMinPoseTimeOffsetMs, OpenXRAPI.OpenXrMaxPoseTimeOffsetMs));
                }

                /// <summary>
                /// Controls which OpenXR pose/frustum policy CollectVisible uses.
                /// </summary>
                [Category("VR")]
                [Description("Controls which OpenXR pose/frustum policy CollectVisible uses.")]
                public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy
                {
                    get => _openXrCollectVisiblePosePolicy;
                    set => SetField(ref _openXrCollectVisiblePosePolicy, value);
                }

                /// <summary>
                /// Extra asymmetric-FOV padding in degrees when OpenXrCollectVisiblePosePolicy is PaddedFrustum.
                /// </summary>
                [Category("VR")]
                [Description("Extra asymmetric-FOV padding in degrees when OpenXrCollectVisiblePosePolicy is PaddedFrustum.")]
                public float OpenXrCollectVisibleFrustumPaddingDegrees
                {
                    get => _openXrCollectVisibleFrustumPaddingDegrees;
                    set => SetField(ref _openXrCollectVisibleFrustumPaddingDegrees, Math.Clamp(value, 0.0f, 20.0f));
                }

                /// <summary>
                /// Controls how OpenXR handles frames whose xrLocateViews result lacks valid position/orientation flags.
                /// </summary>
                [Category("VR")]
                [Description("Controls how OpenXR handles frames whose xrLocateViews result lacks valid position/orientation flags.")]
                public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy
                {
                    get => _openXrTrackingLossPolicy;
                    set => SetField(ref _openXrTrackingLossPolicy, value);
                }

                /// <summary>
                /// Controls whether xrSyncActions runs only once for predicted poses or again during late update.
                /// </summary>
                [Category("VR")]
                [Description("Controls whether xrSyncActions runs only once for predicted poses or again during late update.")]
                public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy
                {
                    get => _openXrActionSyncPolicy;
                    set => SetField(ref _openXrActionSyncPolicy, value);
                }

                /// <summary>
                /// Controls where OpenXR's next-frame prep (xrWaitFrame/xrBeginFrame/LocateViews(Predicted)) runs.
                /// </summary>
                [Category("VR")]
                [Description("Controls where OpenXR's next-frame prep runs: inline at start of render callback, post-render, on the default dedicated pacing thread, or on the CollectVisible thread.")]
                public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode
                {
                    get => _openXrRenderPacingMode;
                    set => SetField(ref _openXrRenderPacingMode, value);
                }

                /// <summary>
                /// Enables extra OpenXR OpenGL diagnostics.
                /// </summary>
                [Category("Debug")]
                [Description("If true, emits additional OpenXR OpenGL diagnostic logging.")]
                public bool OpenXrDebugGl
                {
                    get => _openXrDebugGl;
                    set => SetField(ref _openXrDebugGl, value);
                }

                /// <summary>
                /// If true, OpenXR eye rendering clears swapchain images instead of rendering scene content.
                /// </summary>
                [Category("Debug")]
                [Description("If true, OpenXR eye rendering clears swapchain images instead of rendering scene content.")]
                public bool OpenXrDebugClearOnly
                {
                    get => _openXrDebugClearOnly;
                    set => SetField(ref _openXrDebugClearOnly, value);
                }

                /// <summary>
                /// Enables OpenXR frame lifecycle logging.
                /// </summary>
                [Category("Debug")]
                [Description("If true, emits OpenXR frame lifecycle diagnostics.")]
                public bool OpenXrDebugLifecycle
                {
                    get => _openXrDebugLifecycle;
                    set => SetField(ref _openXrDebugLifecycle, value);
                }

                /// <summary>
                /// If true, OpenXR renders right eye before left eye for debugging ordering issues.
                /// </summary>
                [Category("Debug")]
                [Description("If true, OpenXR renders right eye before left eye for debugging eye-order issues.")]
                public bool OpenXrDebugRenderRightThenLeft
                {
                    get => _openXrDebugRenderRightThenLeft;
                    set => SetField(ref _openXrDebugRenderRightThenLeft, value);
                }

                /// <summary>
                /// UV-space center for per-eye foveated shading policy.
                /// </summary>
                [Category("VR")]
                [Description("UV-space center for per-eye foveated shading policy.")]
                public Vector2 VrFoveationCenterUv
                {
                    get => _vrFoveationCenterUv;
                    set => SetField(ref _vrFoveationCenterUv, value);
                }

                /// <summary>
                /// Inner foveation radius in normalized UV space.
                /// </summary>
                [Category("VR")]
                [Description("Inner foveation radius in normalized UV space.")]
                public float VrFoveationInnerRadius
                {
                    get => _vrFoveationInnerRadius;
                    set => SetField(ref _vrFoveationInnerRadius, Math.Clamp(value, 0.0f, 1.0f));
                }

                /// <summary>
                /// Outer foveation radius in normalized UV space.
                /// </summary>
                [Category("VR")]
                [Description("Outer foveation radius in normalized UV space.")]
                public float VrFoveationOuterRadius
                {
                    get => _vrFoveationOuterRadius;
                    set => SetField(ref _vrFoveationOuterRadius, Math.Clamp(value, 0.0f, 1.5f));
                }

                /// <summary>
                /// Per-tier shading-rate policy for foveated views: X=inner, Y=mid, Z=outer.
                /// 1.0 means full rate; lower values indicate reduced shading rate.
                /// </summary>
                [Category("VR")]
                [Description("Per-tier shading-rate policy for foveated views: X=inner, Y=mid, Z=outer. 1.0 means full rate; lower values indicate reduced shading rate.")]
                public Vector3 VrFoveationShadingRates
                {
                    get => _vrFoveationShadingRates;
                    set => SetField(ref _vrFoveationShadingRates, value);
                }

                /// <summary>
                /// Conservative visibility margin applied to foveated outer radius to reduce edge popping.
                /// </summary>
                [Category("VR")]
                [Description("Conservative visibility margin applied to foveated outer radius to reduce edge popping.")]
                public float VrFoveationVisibilityMargin
                {
                    get => _vrFoveationVisibilityMargin;
                    set => SetField(ref _vrFoveationVisibilityMargin, Math.Clamp(value, 0.0f, 0.5f));
                }

                /// <summary>
                /// If true, near-field and UI-critical content is forced into full-resolution stereo views.
                /// </summary>
                [Category("VR")]
                [Description("If true, near-field and UI-critical content is forced into full-resolution stereo views.")]
                public bool VrFoveationForceFullResForUiAndNearField
                {
                    get => _vrFoveationForceFullResForUiAndNearField;
                    set => SetField(ref _vrFoveationForceFullResForUiAndNearField, value);
                }

                /// <summary>
                /// Near-distance threshold (meters) below which foveated views defer content to full-resolution views.
                /// </summary>
                [Category("VR")]
                [Description("Near-distance threshold (meters) below which foveated views defer content to full-resolution views.")]
                public float VrFoveationFullResNearDistanceMeters
                {
                    get => _vrFoveationFullResNearDistanceMeters;
                    set => SetField(ref _vrFoveationFullResNearDistanceMeters, Math.Clamp(value, 0.0f, 10.0f));
                }

                /// <summary>
                /// Requested Retinal Visibility Cache mode. Non-oracle modes require the RVC GPU pass stack and otherwise report a visible fallback reason.
                /// </summary>
                [Category("RVC")]
                [Description("Requested Retinal Visibility Cache mode. Non-oracle modes require the RVC GPU pass stack and otherwise report a visible fallback reason.")]
                public ERvcPipelineMode RvcPipelineMode
                {
                    get => _rvcPipelineMode;
                    set => SetField(ref _rvcPipelineMode, value);
                }

                /// <summary>
                /// If true, RVC requests quad-view rendering and requires an OpenXR quad-view runtime path.
                /// </summary>
                [Category("RVC")]
                [Description("If true, RVC requests quad-view rendering and requires an OpenXR quad-view runtime path.")]
                public bool RvcQuadViewEnabled
                {
                    get => _rvcQuadViewEnabled;
                    set => SetField(ref _rvcQuadViewEnabled, value);
                }

                /// <summary>
                /// Allows RVC shadelet reuse between stereo views after validation. Defaults off until the A/B harness is green.
                /// </summary>
                [Category("RVC")]
                [Description("Allows RVC shadelet reuse between stereo views after validation. Defaults off until the A/B harness is green.")]
                public bool RvcStereoReuseEnabled
                {
                    get => _rvcStereoReuseEnabled;
                    set => SetField(ref _rvcStereoReuseEnabled, value);
                }

                /// <summary>
                /// Allows validated reuse between wide and inset quad views.
                /// </summary>
                [Category("RVC")]
                [Description("Allows validated reuse between wide and inset quad views.")]
                public bool RvcInsetWideReuseEnabled
                {
                    get => _rvcInsetWideReuseEnabled;
                    set => SetField(ref _rvcInsetWideReuseEnabled, value);
                }

                /// <summary>
                /// Allows temporal shadelet reuse after validation. Defaults off for deterministic oracle comparison.
                /// </summary>
                [Category("RVC")]
                [Description("Allows temporal shadelet reuse after validation. Defaults off for deterministic oracle comparison.")]
                public bool RvcTemporalReuseEnabled
                {
                    get => _rvcTemporalReuseEnabled;
                    set => SetField(ref _rvcTemporalReuseEnabled, value);
                }

                /// <summary>
                /// Enables periphery-only shared-light aggregation through the RVC light-cluster contract.
                /// </summary>
                [Category("RVC")]
                [Description("Enables periphery-only shared-light aggregation through the RVC light-cluster contract.")]
                public bool RvcPeripheralLightAggregationEnabled
                {
                    get => _rvcPeripheralLightAggregationEnabled;
                    set => SetField(ref _rvcPeripheralLightAggregationEnabled, value);
                }

                /// <summary>
                /// Enables RVC diagnostic overlay publishing when a debug mode is selected.
                /// </summary>
                [Category("RVC")]
                [Description("Enables RVC diagnostic overlay publishing when a debug mode is selected.")]
                public bool RvcDiagnosticOverlayEnabled
                {
                    get => _rvcDiagnosticOverlayEnabled;
                    set => SetField(ref _rvcDiagnosticOverlayEnabled, value);
                }

                /// <summary>
                /// Selects the RVC debug view published to the mirror/debug output.
                /// </summary>
                [Category("RVC")]
                [Description("Selects the RVC debug view published to the mirror/debug output.")]
                public ERvcDebugViewMode RvcDebugViewMode
                {
                    get => _rvcDebugViewMode;
                    set => SetField(ref _rvcDebugViewMode, value);
                }

                /// <summary>
                /// Coordinate policy for RVC shared-lighting clusters.
                /// </summary>
                [Category("RVC")]
                [Description("Coordinate policy for RVC shared-lighting clusters.")]
                public ERvcLightGridSpace RvcLightGridSpace
                {
                    get => _rvcLightGridSpace;
                    set => SetField(ref _rvcLightGridSpace, value);
                }

                /// <summary>
                /// Foveal radius in degrees used by RVC density and quality policy.
                /// </summary>
                [Category("RVC")]
                [Description("Foveal radius in degrees used by RVC density and quality policy.")]
                public float RvcFovealRadiusDegrees
                {
                    get => _rvcFovealRadiusDegrees;
                    set => SetField(ref _rvcFovealRadiusDegrees, Math.Clamp(value, 0.1f, 45.0f));
                }

                /// <summary>
                /// Guard-band width in degrees around the foveal region.
                /// </summary>
                [Category("RVC")]
                [Description("Guard-band width in degrees around the foveal region.")]
                public float RvcGuardBandDegrees
                {
                    get => _rvcGuardBandDegrees;
                    set => SetField(ref _rvcGuardBandDegrees, Math.Clamp(value, 0.0f, 45.0f));
                }

                /// <summary>
                /// Mid-field radius in degrees before peripheral shadelet density is used.
                /// </summary>
                [Category("RVC")]
                [Description("Mid-field radius in degrees before peripheral shadelet density is used.")]
                public float RvcMidFieldRadiusDegrees
                {
                    get => _rvcMidFieldRadiusDegrees;
                    set => SetField(ref _rvcMidFieldRadiusDegrees, Math.Clamp(value, 0.1f, 90.0f));
                }

                /// <summary>
                /// Maximum shadelet density used in the peripheral region.
                /// </summary>
                [Category("RVC")]
                [Description("Maximum shadelet density used in the peripheral region.")]
                public ERvcShadeletDensity RvcPeripheralMaxRate
                {
                    get => _rvcPeripheralMaxRate;
                    set => SetField(ref _rvcPeripheralMaxRate, value);
                }

                /// <summary>
                /// Near-distance threshold below which RVC forces 1x1 shadelets for UI, hands, and near-field geometry.
                /// </summary>
                [Category("RVC")]
                [Description("Near-distance threshold below which RVC forces 1x1 shadelets for UI, hands, and near-field geometry.")]
                public float RvcForceFullResNearDistanceMeters
                {
                    get => _rvcForceFullResNearDistanceMeters;
                    set => SetField(ref _rvcForceFullResNearDistanceMeters, Math.Clamp(value, 0.0f, 10.0f));
                }

                /// <summary>
                /// Derivative strategy used for RVC material texture LOD and normal mapping.
                /// </summary>
                [Category("RVC")]
                [Description("Derivative strategy used for RVC material texture LOD and normal mapping.")]
                public ERvcDerivativeStrategy RvcDerivativeStrategy
                {
                    get => _rvcDerivativeStrategy;
                    set => SetField(ref _rvcDerivativeStrategy, value);
                }

                /// <summary>
                /// Foveal anti-aliasing path selected for visibility-buffer resolve.
                /// </summary>
                [Category("RVC")]
                [Description("Foveal anti-aliasing path selected for visibility-buffer resolve.")]
                public ERvcFovealAntiAliasingPath RvcFovealAntiAliasingPath
                {
                    get => _rvcFovealAntiAliasingPath;
                    set => SetField(ref _rvcFovealAntiAliasingPath, value);
                }

                /// <summary>
                /// Maximum normal-angle delta allowed when validating RVC reuse candidates.
                /// </summary>
                [Category("RVC")]
                [Description("Maximum normal-angle delta allowed when validating RVC reuse candidates.")]
                public float RvcReuseMaxNormalAngleDegrees
                {
                    get => _rvcReuseMaxNormalAngleDegrees;
                    set => SetField(ref _rvcReuseMaxNormalAngleDegrees, Math.Clamp(value, 0.0f, 45.0f));
                }

                /// <summary>
                /// Maximum depth delta in meters allowed when validating RVC reuse candidates.
                /// </summary>
                [Category("RVC")]
                [Description("Maximum depth delta in meters allowed when validating RVC reuse candidates.")]
                public float RvcReuseMaxDepthDeltaMeters
                {
                    get => _rvcReuseMaxDepthDeltaMeters;
                    set => SetField(ref _rvcReuseMaxDepthDeltaMeters, Math.Clamp(value, 0.0f, 10.0f));
                }

                /// <summary>
                /// Maximum roughness-bucket delta allowed when validating RVC reuse candidates.
                /// </summary>
                [Category("RVC")]
                [Description("Maximum roughness-bucket delta allowed when validating RVC reuse candidates.")]
                public byte RvcReuseMaxRoughnessBucketDelta
                {
                    get => _rvcReuseMaxRoughnessBucketDelta;
                    set => SetField(ref _rvcReuseMaxRoughnessBucketDelta, value);
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

                /// <summary>
                /// Settings that gate allocator, synchronization, and descriptor-update robustness migrations for Vulkan.
                /// </summary>
                [Category("Vulkan")]
                [Description("Controls staged Vulkan backend migrations for allocator, synchronization, and descriptor update paths.")]
                public VulkanRobustnessSettings VulkanRobustnessSettings
                {
                    get => Vulkan.Robustness;
                    set => Vulkan.Robustness = value ?? new VulkanRobustnessSettings();
                }
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

            }

            private static void ApplyEngineSettingChange(string? propertyName)
            {
                bool applyAll = string.IsNullOrEmpty(propertyName);

                if (applyAll || propertyName == nameof(EngineSettings.CpuSceneCullingStructure))
                    Engine.Rendering.ApplyCpuSceneCullingStructurePreference();

                if (applyAll || propertyName == nameof(EngineSettings.VulkanGpuDrivenProfile)
                    || propertyName == nameof(EngineSettings.EnableZeroReadbackMaterialScatter)
                    || propertyName == nameof(EngineSettings.ZeroReadbackMaterialDrawPath)
                    || propertyName == nameof(EngineSettings.EnableGpuIndirectDebugLogging)
                    || propertyName == nameof(EngineSettings.EnableGpuIndirectValidationLogging)
                    || propertyName == nameof(EngineSettings.EnableGpuIndirectCpuFallback)
                    || propertyName == nameof(EngineSettings.ForceMeshSubmissionStrategy))
                {
                    Engine.Rendering.ApplyGpuRenderDispatchPreference();
                    Engine.Rendering.LogVulkanFeatureProfileFingerprint();
                }

                if (applyAll || propertyName == nameof(EngineSettings.VulkanQueueOverlapMode))
                    Engine.Rendering.LogVulkanFeatureProfileFingerprint();

                if (applyAll || propertyName == nameof(EngineSettings.GpuSortDomainPolicy))
                    Engine.Rendering.LogVulkanFeatureProfileFingerprint();

                if (applyAll
                    || propertyName == nameof(EngineSettings.ClipSpaceYDirection)
                    || propertyName == nameof(EngineSettings.ClipDepthRange))
                {
                    foreach (var window in Engine.Windows)
                        window.RequestRenderStateRecheck(resetCircuitBreaker: true);
                }

                if (applyAll || propertyName == nameof(EngineSettings.EnableNvidiaDlss)
                    || propertyName == nameof(EngineSettings.DlssQuality)
                    || propertyName == nameof(EngineSettings.DlssCustomScale)
                    || propertyName == nameof(EngineSettings.DlssSharpness)
                    || propertyName == nameof(EngineSettings.EnableNvidiaDlssFrameGeneration)
                    || propertyName == nameof(EngineSettings.NvidiaDlssFrameGenerationMode))
                {
                    Engine.Rendering.ApplyNvidiaDlssPreference();
                }

                if (applyAll || propertyName == nameof(EngineSettings.EnableIntelXess)
                    || propertyName == nameof(EngineSettings.XessQuality)
                    || propertyName == nameof(EngineSettings.XessCustomScale))
                {
                    Engine.Rendering.ApplyIntelXessPreference();
                }

                if (applyAll || IsRvcPipelineSetting(propertyName))
                    Engine.Rendering.ApplyRenderPipelinePreference();
            }

            private static bool IsRvcPipelineSetting(string? propertyName)
                => propertyName is nameof(EngineSettings.RvcPipelineMode)
                    or nameof(EngineSettings.RvcQuadViewEnabled)
                    or nameof(EngineSettings.RvcStereoReuseEnabled)
                    or nameof(EngineSettings.RvcInsetWideReuseEnabled)
                    or nameof(EngineSettings.RvcTemporalReuseEnabled)
                    or nameof(EngineSettings.RvcPeripheralLightAggregationEnabled)
                    or nameof(EngineSettings.RvcDiagnosticOverlayEnabled)
                    or nameof(EngineSettings.RvcDebugViewMode)
                    or nameof(EngineSettings.RvcLightGridSpace)
                    or nameof(EngineSettings.RvcFovealRadiusDegrees)
                    or nameof(EngineSettings.RvcGuardBandDegrees)
                    or nameof(EngineSettings.RvcMidFieldRadiusDegrees)
                    or nameof(EngineSettings.RvcPeripheralMaxRate)
                    or nameof(EngineSettings.RvcForceFullResNearDistanceMeters)
                    or nameof(EngineSettings.RvcDerivativeStrategy)
                    or nameof(EngineSettings.RvcFovealAntiAliasingPath)
                    or nameof(EngineSettings.RvcReuseMaxNormalAngleDegrees)
                    or nameof(EngineSettings.RvcReuseMaxDepthDeltaMeters)
                    or nameof(EngineSettings.RvcReuseMaxRoughnessBucketDelta);

            public static void ApplyEditorPreferencesChange(string? propertyName)
            {
                bool applyAll = string.IsNullOrEmpty(propertyName);

                if (applyAll || propertyName == nameof(EditorDebugOptions.RenderMesh3DBounds))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll || propertyName == nameof(EditorDebugOptions.VisualizeTransparencyModeOverlay))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll || propertyName == nameof(EditorDebugOptions.VisualizeTransparencyClassificationOverlay))
                    ApplyRenderMeshBoundsSetting();

                if (applyAll ||
                    propertyName == nameof(EditorDebugOptions.VisualizeTransparencyAccumulation) ||
                    propertyName == nameof(EditorDebugOptions.VisualizeTransparencyRevealage) ||
                    propertyName == nameof(EditorDebugOptions.VisualizeTransparencyOverdrawHeatmap))
                    ApplyRenderPipelinePreference();

                if (applyAll || propertyName == nameof(EditorDebugOptions.RenderTransformDebugInfo))
                    ApplyTransformDebugSetting();

                if (applyAll || propertyName == nameof(EditorDebugOptions.UseDebugOpaquePipeline))
                    ApplyRenderPipelinePreference();

                if (applyAll ||
                    propertyName == nameof(EditorDebugOptions.EnableZeroReadbackMaterialScatter) ||
                    propertyName == nameof(EditorDebugOptions.ZeroReadbackMaterialDrawPath))
                {
                    ApplyGpuRenderDispatchPreference();
                    LogVulkanFeatureProfileFingerprint();
                }

                if (applyAll || propertyName == nameof(EditorPreferences.ViewportPresentationMode))
                {
                    foreach (var window in Engine.Windows)
                    {
                        window.InvalidateScenePanelResources();
                        window.RequestRenderStateRecheck(resetCircuitBreaker: true);
                    }
                }

                if (applyAll || propertyName == nameof(EditorPreferences.SceneDepthMode))
                    ApplySceneCameraDepthModePreference();

                if (applyAll || propertyName == nameof(EditorPreferences.InteractiveResizeStrategy))
                    Engine.ApplyInteractiveResizeStrategySettings();
            }

            public static global::XREngine.Rendering.XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
            {
                global::XREngine.Rendering.XRCamera.EDepthMode projectMode = Engine.GameSettings?.DepthModeOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : global::XREngine.Rendering.XRCamera.EDepthMode.Normal;

                return Engine.EditorPreferences.SceneDepthMode switch
                {
                    EditorPreferences.ESceneDepthModePreference.Normal => global::XREngine.Rendering.XRCamera.EDepthMode.Normal,
                    EditorPreferences.ESceneDepthModePreference.Reversed => global::XREngine.Rendering.XRCamera.EDepthMode.Reversed,
                    _ => projectMode,
                };
            }

            public static void ApplySceneCameraDepthModePreference()
            {
                global::XREngine.Rendering.XRCamera.EDepthMode depthMode = ResolveSceneCameraDepthModePreference();

                foreach (var worldInstance in Engine.WorldInstances)
                {
                    foreach (SceneNode root in worldInstance.RootNodes)
                    {
                        foreach (SceneNode node in Scene.Prefabs.SceneNodePrefabUtility.EnumerateHierarchy(root))
                        {
                            foreach (var component in node.Components)
                            {
                                if (component is CameraComponent cameraComponent)
                                    cameraComponent.Camera.DepthMode = depthMode;
                            }
                        }
                    }
                }

                foreach (var window in Engine.Windows)
                    window.RequestRenderStateRecheck(resetCircuitBreaker: true);
            }

            private static void ApplyRenderMeshBoundsSetting()
            {
                bool renderBounds =
                    Engine.EditorPreferences.Debug.RenderMesh3DBounds ||
                    Engine.EditorPreferences.Debug.VisualizeTransparencyModeOverlay ||
                    Engine.EditorPreferences.Debug.VisualizeTransparencyClassificationOverlay;

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

                EnqueueSwapTask(Apply);
            }

            private static void ApplyTransformDebugSetting()
            {
                bool enable = Engine.EditorPreferences.Debug.RenderTransformDebugInfo;

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

                EnqueueSwapTask(Apply);
            }
        }
    }
}
