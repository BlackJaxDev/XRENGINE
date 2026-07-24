using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

internal sealed class RuntimeRenderSettings
{
    public string DefaultFontFolder => RuntimeRenderingHostServices.FrameTiming.DefaultFontFolder;
    public string DefaultFontFileName => RuntimeRenderingHostServices.FrameTiming.DefaultFontFileName;

    private bool _allowBlendshapes = RuntimeRenderingHostServiceDefaults.AllowBlendshapes;
    private bool _allowShaderPipelines = RuntimeRenderingHostServiceDefaults.AllowShaderPipelines;
    private bool _allowSkinning = RuntimeRenderingHostServiceDefaults.AllowSkinning;
    private bool _calculateBlendshapesInComputeShader = RuntimeRenderingHostServiceDefaults.CalculateBlendshapesInComputeShader;
    private bool _calculateSkinningInComputeShader = RuntimeRenderingHostServiceDefaults.CalculateSkinningInComputeShader;
    private bool _calculateSkinnedBoundsInComputeShader;
    private bool _skinnedBoundsGpuDirectAabbWrite;
    private bool _enableBlendshapePrecombinePass = RuntimeRenderingHostServiceDefaults.EnableBlendshapePrecombinePass;
    private bool _enableBlendshapePrecombineForDirectVertexPath = RuntimeRenderingHostServiceDefaults.EnableBlendshapePrecombineForDirectVertexPath;
    private bool _enableBlendshapePcaBasisCompression = RuntimeRenderingHostServiceDefaults.EnableBlendshapePcaBasisCompression;
    private int _blendshapePrecombineComputeMinActiveShapes = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineComputeMinActiveShapes;
    private int _blendshapePrecombineDirectMinActiveShapes = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineDirectMinActiveShapes;
    private int _blendshapePrecombineMinAffectedVertices = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineMinAffectedVertices;
    private bool _useIntegerUniformsInShaders = RuntimeRenderingHostServiceDefaults.UseIntegerUniformsInShaders;
    private bool _useSpotShadowAtlas = true;
    private bool _useDirectionalShadowAtlas = true;
    private bool _usePointShadowAtlas = true;
    private uint _shadowAtlasPageSize = 4096u;
    private int _maxShadowAtlasPages = 1;
    private long _maxShadowAtlasMemoryBytes;
    private int _maxShadowTilesRenderedPerFrame = 16;
    private float _maxShadowRenderMilliseconds = 2.0f;
    private int _maxDirectionalCascadeAtlasStaleFrames = RuntimeRenderingHostServiceDefaults.MaxDirectionalCascadeAtlasStaleFrames;
    private uint _minShadowAtlasTileResolution = 128u;
    private uint _maxShadowAtlasTileResolution = 4096u;
    private int _shaderConfigVersion = RuntimeRenderingHostServiceDefaults.ShaderConfigVersion;
    private ERenderClipSpaceYDirection _clipSpaceYDirection = RuntimeRenderingHostServiceDefaults.ClipSpaceYDirection;
    private ERenderClipDepthRange _clipDepthRange = RuntimeRenderingHostServiceDefaults.ClipDepthRange;
    private bool _openXrCullWithFrustum = RuntimeRenderingHostServiceDefaults.OpenXrCullWithFrustum;
    private bool _openXrDebugClearOnly = RuntimeRenderingHostServiceDefaults.OpenXrDebugClearOnly;
    private bool _openXrDebugGl = RuntimeRenderingHostServiceDefaults.OpenXrDebugGl;
    private bool _openXrDebugLifecycle = RuntimeRenderingHostServiceDefaults.OpenXrDebugLifecycle;
    private bool _openXrDebugRenderRightThenLeft = RuntimeRenderingHostServiceDefaults.OpenXrDebugRenderRightThenLeft;
    private bool _openXrPrepareFrameAfterDesktopRender = RuntimeRenderingHostServiceDefaults.OpenXrPrepareFrameAfterDesktopRender;
    private float _openXrDeadlineSafetyMarginMs = RuntimeRenderingHostServiceDefaults.OpenXrDeadlineSafetyMarginMs;
    private float _openXrPoseTimeOffsetMs = RuntimeRenderingHostServiceDefaults.OpenXrPoseTimeOffsetMs;
    private OpenXRAPI.OpenXrCollectVisiblePosePolicy _openXrCollectVisiblePosePolicy = OpenXRAPI.OpenXrCollectVisiblePosePolicy.Predicted;
    private float _openXrCollectVisibleFrustumPaddingDegrees = RuntimeRenderingHostServiceDefaults.OpenXrCollectVisibleFrustumPaddingDegrees;
    private OpenXRAPI.OpenXrTrackingLossPolicy _openXrTrackingLossPolicy = OpenXRAPI.OpenXrTrackingLossPolicy.FreezeLastValid;
    private OpenXRAPI.OpenXrActionSyncPolicy _openXrActionSyncPolicy = OpenXRAPI.OpenXrActionSyncPolicy.PredictedOnly;
    private OpenXRAPI.OpenXrRenderPacingMode _openXrRenderPacingMode = RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode;
    private EVrViewRenderMode _vrViewRenderMode = RuntimeRenderingHostServiceDefaults.VrViewRenderMode;
    private EVrMirrorMode _vrMirrorMode = RuntimeRenderingHostServiceDefaults.VrMirrorMode;
    private EVrFoveationMode _vrFoveationMode = RuntimeRenderingHostServiceDefaults.VrFoveationMode;
    private EVrFoveationQualityPreset _vrFoveationQualityPreset = RuntimeRenderingHostServiceDefaults.VrFoveationQualityPreset;
    private bool _vrFoveationRequireRequested = RuntimeRenderingHostServiceDefaults.VrFoveationRequireRequested;
    private EOpenXrEyeResolutionPreset _openXrEyeResolutionPreset = RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionPreset;
    private float _openXrEyeResolutionScale = RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionScale;
    private uint _openXrCustomEyeResolutionWidth = RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionWidth;
    private uint _openXrCustomEyeResolutionHeight = RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionHeight;

    private bool _allowBinaryProgramCaching = RuntimeRenderingHostServiceDefaults.AllowBinaryProgramCaching;
    public bool AllowBinaryProgramCaching
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.AllowBinaryProgramCaching
            : _allowBinaryProgramCaching;
        set => _allowBinaryProgramCaching = value;
    }
    public bool AllowBlendshapes
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.AllowBlendshapes
            : _allowBlendshapes;
        set => SetShaderSetting(ref _allowBlendshapes, value);
    }
    public bool AllowShaderPipelines
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.AllowShaderPipelines
            : _allowShaderPipelines;
        set
        {
            bool previous = AllowShaderPipelines;
            SetShaderSetting(ref _allowShaderPipelines, value);
            if (previous != AllowShaderPipelines)
            {
                IRuntimeRendererHost? renderer = RuntimeRenderingHostServices.FrameTiming.CurrentRenderer;
                if (renderer is not null &&
                    renderer.TryGetBackendCapability<IShaderPipelineModeBackendCapability>(out var capability))
                {
                    capability?.HandleShaderPipelineModeChanged(AllowShaderPipelines);
                }
                XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();
            }
        }
    }
    public bool AllowSkinning
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.AllowSkinning
            : _allowSkinning;
        set => SetShaderSetting(ref _allowSkinning, value);
    }
    private bool _asyncProgramBinaryUpload = RuntimeRenderingHostServiceDefaults.AsyncProgramBinaryUpload;
    public bool AsyncProgramBinaryUpload
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.AsyncProgramBinaryUpload
            : _asyncProgramBinaryUpload;
        set => _asyncProgramBinaryUpload = value;
    }
    private bool _asyncProgramCompilation = RuntimeRenderingHostServiceDefaults.AsyncProgramCompilation;
    public bool AsyncProgramCompilation
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.AsyncProgramCompilation
            : _asyncProgramCompilation;
        set => _asyncProgramCompilation = value;
    }
    private int _openGLProgramCompileLinkWorkerCount = RuntimeRenderingHostServiceDefaults.OpenGLProgramCompileLinkWorkerCount;
    public int OpenGLProgramCompileLinkWorkerCount
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.OpenGLProgramCompileLinkWorkerCount
            : _openGLProgramCompileLinkWorkerCount;
        set => _openGLProgramCompileLinkWorkerCount = Math.Clamp(value, 1, 16);
    }
    public bool CacheGpuHiZOcclusionOncePerFrame { get; set; } = true;
    public bool CalculateBlendshapesInComputeShader
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.CalculateBlendshapesInComputeShader
            : _calculateBlendshapesInComputeShader;
        set => SetShaderSetting(ref _calculateBlendshapesInComputeShader, value);
    }
    public bool EnableBlendshapePrecombinePass
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.EnableBlendshapePrecombinePass
            : _enableBlendshapePrecombinePass;
        set => SetShaderSetting(ref _enableBlendshapePrecombinePass, value);
    }
    public bool EnableBlendshapePrecombineForDirectVertexPath
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.EnableBlendshapePrecombineForDirectVertexPath
            : _enableBlendshapePrecombineForDirectVertexPath;
        set => SetShaderSetting(ref _enableBlendshapePrecombineForDirectVertexPath, value);
    }
    public bool EnableBlendshapePcaBasisCompression
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.EnableBlendshapePcaBasisCompression
            : _enableBlendshapePcaBasisCompression;
        set => SetShaderSetting(ref _enableBlendshapePcaBasisCompression, value);
    }
    public int BlendshapePrecombineComputeMinActiveShapes
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.BlendshapePrecombineComputeMinActiveShapes
            : _blendshapePrecombineComputeMinActiveShapes;
        set => _blendshapePrecombineComputeMinActiveShapes = Math.Max(1, value);
    }
    public int BlendshapePrecombineDirectMinActiveShapes
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.BlendshapePrecombineDirectMinActiveShapes
            : _blendshapePrecombineDirectMinActiveShapes;
        set => _blendshapePrecombineDirectMinActiveShapes = Math.Max(1, value);
    }
    public int BlendshapePrecombineMinAffectedVertices
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.BlendshapePrecombineMinAffectedVertices
            : _blendshapePrecombineMinAffectedVertices;
        set => _blendshapePrecombineMinAffectedVertices = Math.Max(1, value);
    }
    private bool _streamMeshLodsOnDemand = RuntimeRenderingHostServiceDefaults.StreamMeshLodsOnDemand;
    public bool StreamMeshLodsOnDemand
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.StreamMeshLodsOnDemand
            : _streamMeshLodsOnDemand;
        set => _streamMeshLodsOnDemand = value;
    }
    private int _meshLodStreamingDrainIntervalFrames = RuntimeRenderingHostServiceDefaults.MeshLodStreamingDrainIntervalFrames;
    public int MeshLodStreamingDrainIntervalFrames
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.MeshLodStreamingDrainIntervalFrames
            : _meshLodStreamingDrainIntervalFrames;
        set => _meshLodStreamingDrainIntervalFrames = Math.Clamp(value, 1, 64);
    }
    private int _meshLodStreamingMaxLoadsPerDrain = RuntimeRenderingHostServiceDefaults.MeshLodStreamingMaxLoadsPerDrain;
    public int MeshLodStreamingMaxLoadsPerDrain
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.MeshLodStreamingMaxLoadsPerDrain
            : _meshLodStreamingMaxLoadsPerDrain;
        set => _meshLodStreamingMaxLoadsPerDrain = Math.Clamp(value, 1, 256);
    }
    public bool CalculateSkinnedBoundsInComputeShader
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.CalculateSkinnedBoundsInComputeShader
            : _calculateSkinnedBoundsInComputeShader;
        set => _calculateSkinnedBoundsInComputeShader = value;
    }
    public bool CalculateSkinningInComputeShader
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.CalculateSkinningInComputeShader
            : _calculateSkinningInComputeShader;
        set => SetShaderSetting(ref _calculateSkinningInComputeShader, value);
    }
    public bool SkinnedBoundsGpuDirectAabbWrite
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.SkinnedBoundsGpuDirectAabbWrite
            : _skinnedBoundsGpuDirectAabbWrite;
        set => _skinnedBoundsGpuDirectAabbWrite = value;
    }
    public bool CullShadowCollectionByCameraFrusta { get; set; } = true;
    public Vector3 DefaultLuminance { get; set; } = new(0.299f, 0.587f, 0.114f);
    public float DlssCustomScale { get; set; } = 1.0f;
    public EDlssQualityMode DlssQuality { get; set; } = EDlssQualityMode.Quality;
    public float DlssSharpness { get; set; }
    public bool EnableIntelXessFrameGeneration { get; set; }
    public bool EnableNvidiaDlss { get; set; }
    public bool EnableNvidiaDlssFrameGeneration { get; set; }
    public ENvidiaDlssFrameGenerationMode NvidiaDlssFrameGenerationMode { get; set; } = ENvidiaDlssFrameGenerationMode.Off;
    public EGpuSortDomainPolicy GpuSortDomainPolicy { get; set; } = EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront;
    public uint LightProbeResolution { get; set; } = 128u;
    public bool LightProbesCaptureDepth { get; set; } = true;
    public bool LogMaterialTextureBindings { get; set; }
    public bool LogMissingShaderSamplers { get; set; }
    private int _maxAsyncShaderProgramsPerFrame = RuntimeRenderingHostServiceDefaults.MaxAsyncShaderProgramsPerFrame;
    public int MaxAsyncShaderProgramsPerFrame
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxAsyncShaderProgramsPerFrame
            : _maxAsyncShaderProgramsPerFrame;
        set => _maxAsyncShaderProgramsPerFrame = value;
    }
    private EOpenGLShaderLinkStrategy _openGLShaderLinkStrategy = RuntimeRenderingHostServiceDefaults.OpenGLShaderLinkStrategy;
    public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.OpenGLShaderLinkStrategy
            : _openGLShaderLinkStrategy;
        set => _openGLShaderLinkStrategy = value;
    }
    private int _openGLShaderCompilerThreadCount = RuntimeRenderingHostServiceDefaults.OpenGLShaderCompilerThreadCount;
    public int OpenGLShaderCompilerThreadCount
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.OpenGLShaderCompilerThreadCount
            : _openGLShaderCompilerThreadCount;
        set => _openGLShaderCompilerThreadCount = value;
    }
    private bool _openGLParallelShaderCompileProbeEnabled = RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeEnabled;
    public bool OpenGLParallelShaderCompileProbeEnabled
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.OpenGLParallelShaderCompileProbeEnabled
            : _openGLParallelShaderCompileProbeEnabled;
        set => _openGLParallelShaderCompileProbeEnabled = value;
    }
    private int _openGLParallelShaderCompileProbeTimeoutMs = RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeTimeoutMs;
    public int OpenGLParallelShaderCompileProbeTimeoutMs
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.OpenGLParallelShaderCompileProbeTimeoutMs
            : _openGLParallelShaderCompileProbeTimeoutMs;
        set => _openGLParallelShaderCompileProbeTimeoutMs = value;
    }
    public long MaxShadowAtlasMemoryBytes
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxShadowAtlasMemoryBytes
            : _maxShadowAtlasMemoryBytes;
        set => _maxShadowAtlasMemoryBytes = Math.Max(0L, value);
    }

    public int MaxShadowAtlasPages
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxShadowAtlasPages
            : _maxShadowAtlasPages;
        set => _maxShadowAtlasPages = Math.Clamp(value, 1, 64);
    }

    public uint MaxShadowAtlasTileResolution
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxShadowAtlasTileResolution
            : _maxShadowAtlasTileResolution;
        set => _maxShadowAtlasTileResolution = value;
    }

    public float MaxShadowRenderMilliseconds
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxShadowRenderMilliseconds
            : _maxShadowRenderMilliseconds;
        set => _maxShadowRenderMilliseconds = MathF.Max(0.0f, value);
    }

    public int MaxDirectionalCascadeAtlasStaleFrames
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxDirectionalCascadeAtlasStaleFrames
            : _maxDirectionalCascadeAtlasStaleFrames;
        set => _maxDirectionalCascadeAtlasStaleFrames = Math.Clamp(value, 0, 120);
    }

    public int MaxShadowTilesRenderedPerFrame
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MaxShadowTilesRenderedPerFrame
            : _maxShadowTilesRenderedPerFrame;
        set => _maxShadowTilesRenderedPerFrame = Math.Max(0, value);
    }

    public uint MinShadowAtlasTileResolution
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.MinShadowAtlasTileResolution
            : _minShadowAtlasTileResolution;
        set => _minShadowAtlasTileResolution = value;
    }

    public bool OpenXrCullWithFrustum
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrCullWithFrustum
            : _openXrCullWithFrustum;
        set => _openXrCullWithFrustum = value;
    }
    public bool OpenXrDebugClearOnly
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrDebugClearOnly
            : _openXrDebugClearOnly;
        set => _openXrDebugClearOnly = value;
    }
    public bool OpenXrDebugGl
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrDebugGl
            : _openXrDebugGl;
        set => _openXrDebugGl = value;
    }
    public bool OpenXrDebugLifecycle
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrDebugLifecycle
            : _openXrDebugLifecycle;
        set => _openXrDebugLifecycle = value;
    }
    public bool OpenXrDebugRenderRightThenLeft
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrDebugRenderRightThenLeft
            : _openXrDebugRenderRightThenLeft;
        set => _openXrDebugRenderRightThenLeft = value;
    }
    public bool OpenXrPrepareFrameAfterDesktopRender
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrPrepareFrameAfterDesktopRender
            : _openXrPrepareFrameAfterDesktopRender;
        set => _openXrPrepareFrameAfterDesktopRender = value;
    }
    public float OpenXrDeadlineSafetyMarginMs
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrDeadlineSafetyMarginMs
            : _openXrDeadlineSafetyMarginMs;
        set => _openXrDeadlineSafetyMarginMs = MathF.Max(0.0f, value);
    }
    public float OpenXrPoseTimeOffsetMs
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrPoseTimeOffsetMs
            : _openXrPoseTimeOffsetMs;
        set => _openXrPoseTimeOffsetMs = Math.Clamp(
            value,
            OpenXRAPI.OpenXrMinPoseTimeOffsetMs,
            OpenXRAPI.OpenXrMaxPoseTimeOffsetMs);
    }
    public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrCollectVisiblePosePolicy
            : _openXrCollectVisiblePosePolicy;
        set => _openXrCollectVisiblePosePolicy = value;
    }
    public float OpenXrCollectVisibleFrustumPaddingDegrees
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrCollectVisibleFrustumPaddingDegrees
            : _openXrCollectVisibleFrustumPaddingDegrees;
        set => _openXrCollectVisibleFrustumPaddingDegrees = Math.Clamp(value, 0.0f, 20.0f);
    }
    public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrTrackingLossPolicy
            : _openXrTrackingLossPolicy;
        set => _openXrTrackingLossPolicy = value;
    }
    public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrActionSyncPolicy
            : _openXrActionSyncPolicy;
        set => _openXrActionSyncPolicy = value;
    }
    public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrRenderPacingMode
            : _openXrRenderPacingMode;
        set => _openXrRenderPacingMode = value;
    }
    public bool OutputHDR { get; set; } = true;
    public bool PreferNVStereo { get; set; }
    public bool ProcessMeshImportsAsynchronously { get; set; } = true;
    public EVrViewRenderMode VrViewRenderMode
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.VrViewRenderMode
            : _vrViewRenderMode;
        set => _vrViewRenderMode = value;
    }
    public bool RenderVRSinglePassStereo
    {
        get => VrViewRenderMode == EVrViewRenderMode.SinglePassStereo;
        set => _vrViewRenderMode = value
            ? EVrViewRenderMode.SinglePassStereo
            : EVrViewRenderMode.SequentialViews;
    }
    public EVrMirrorMode VrMirrorMode
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.VrMirrorMode
            : _vrMirrorMode;
        set => _vrMirrorMode = value;
    }
    public EVrFoveationMode VrFoveationMode
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.VrFoveationMode
            : _vrFoveationMode;
        set => _vrFoveationMode = value;
    }
    public EVrFoveationQualityPreset VrFoveationQualityPreset
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.VrFoveationQualityPreset
            : _vrFoveationQualityPreset;
        set => _vrFoveationQualityPreset = value;
    }
    public bool VrFoveationRequireRequested
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.VrFoveationRequireRequested
            : _vrFoveationRequireRequested;
        set => _vrFoveationRequireRequested = value;
    }
    public EOpenXrEyeResolutionPreset OpenXrEyeResolutionPreset
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrEyeResolutionPreset
            : _openXrEyeResolutionPreset;
        set => _openXrEyeResolutionPreset = value;
    }
    public float OpenXrEyeResolutionScale
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrEyeResolutionScale
            : _openXrEyeResolutionScale;
        set => _openXrEyeResolutionScale = Math.Clamp(value, 0.1f, 2.0f);
    }
    public uint OpenXrCustomEyeResolutionWidth
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrCustomEyeResolutionWidth
            : _openXrCustomEyeResolutionWidth;
        set => _openXrCustomEyeResolutionWidth = value;
    }
    public uint OpenXrCustomEyeResolutionHeight
    {
        get => TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
            ? services.OpenXrCustomEyeResolutionHeight
            : _openXrCustomEyeResolutionHeight;
        set => _openXrCustomEyeResolutionHeight = value;
    }
    public int ShaderConfigVersion
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.ShaderConfigVersion
            : _shaderConfigVersion;
        set
        {
            if (_shaderConfigVersion == value)
                return;

            _shaderConfigVersion = value;
            RuntimeEngine.Rendering.RaiseSettingsChanged();
        }
    }
    public ERenderClipSpaceYDirection ClipSpaceYDirection
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.ClipSpaceYDirection
            : _clipSpaceYDirection;
        set => SetShaderSetting(ref _clipSpaceYDirection, value);
    }
    public ERenderClipDepthRange ClipDepthRange
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.ClipDepthRange
            : _clipDepthRange;
        set => SetShaderSetting(ref _clipDepthRange, value);
    }
    public uint ShadowAtlasPageSize
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.ShadowAtlasPageSize
            : _shadowAtlasPageSize;
        set => _shadowAtlasPageSize = value;
    }

    public float TsrRenderScale { get; set; } = 1.0f;
    public bool UseAbsoluteBlendshapePositions { get; set; }
    public bool UseDetailPreservingComputeMipmaps { get; set; } = true;
    public bool UseDirectionalShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.UseDirectionalShadowAtlas
            : _useDirectionalShadowAtlas;
        set => _useDirectionalShadowAtlas = value;
    }

    public bool UseGlobalBlendshapeWeightsBufferForComputeSkinning { get; set; } = true;
    public bool UseGlobalSkinPaletteBufferForComputeSkinning { get; set; } = true;
    public bool UseIntegerUniformsInShaders
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.UseIntegerUniformsInShaders
            : _useIntegerUniformsInShaders;
        set => SetShaderSetting(ref _useIntegerUniformsInShaders, value);
    }
    public bool UsePointShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.UsePointShadowAtlas
            : _usePointShadowAtlas;
        set => _usePointShadowAtlas = value;
    }

    public bool UseSkinnedBvhRefitOptimize { get; set; } = true;
    public bool UseSpotShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
            ? services.UseSpotShadowAtlas
            : _useSpotShadowAtlas;
        set => _useSpotShadowAtlas = value;
    }

    public RuntimeVulkanRobustnessSettings VulkanRobustnessSettings { get; } = new();
    public float XessCustomScale { get; set; } = 1.0f;
    public float XessSharpness { get; set; }

    private static bool TryGetHostShadowAtlasSettings(out IRuntimeRenderSettingsServices services)
    {
        services = RuntimeRenderingHostServices.Settings;
        return services.ProvidesShadowAtlasSettings;
    }

    private static bool TryGetHostSettings(out IRuntimeRenderSettingsServices services)
    {
        services = RuntimeRenderingHostServices.Settings;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }

    private static bool TryGetHostPresentationSettings(out IRuntimeRenderPresentationServices services)
    {
        services = RuntimeRenderingHostServices.Presentation;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }

    private void SetShaderSetting(ref bool field, bool value)
    {
        if (field == value)
            return;

        field = value;
        BumpShaderConfigVersion();
    }

    private void SetShaderSetting<T>(ref T field, T value)
        where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        BumpShaderConfigVersion();
    }

    private void BumpShaderConfigVersion()
    {
        unchecked
        {
            _shaderConfigVersion++;
        }
        RuntimeEngine.Rendering.RaiseSettingsChanged();
    }
}
