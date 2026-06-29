using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

internal sealed class RuntimeRenderSettings
{
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
    private OpenXRAPI.OpenXrCollectVisiblePosePolicy _openXrCollectVisiblePosePolicy = OpenXRAPI.OpenXrCollectVisiblePosePolicy.Predicted;
    private float _openXrCollectVisibleFrustumPaddingDegrees = RuntimeRenderingHostServiceDefaults.OpenXrCollectVisibleFrustumPaddingDegrees;
    private OpenXRAPI.OpenXrTrackingLossPolicy _openXrTrackingLossPolicy = OpenXRAPI.OpenXrTrackingLossPolicy.FreezeLastValid;
    private OpenXRAPI.OpenXrActionSyncPolicy _openXrActionSyncPolicy = OpenXRAPI.OpenXrActionSyncPolicy.PredictedOnly;
    private OpenXRAPI.OpenXrRenderPacingMode _openXrRenderPacingMode = RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode;
    private EVrViewRenderMode _vrViewRenderMode = RuntimeRenderingHostServiceDefaults.VrViewRenderMode;
    private EVrFoveationMode _vrFoveationMode = RuntimeRenderingHostServiceDefaults.VrFoveationMode;
    private EVrFoveationQualityPreset _vrFoveationQualityPreset = RuntimeRenderingHostServiceDefaults.VrFoveationQualityPreset;
    private bool _vrFoveationRequireRequested = RuntimeRenderingHostServiceDefaults.VrFoveationRequireRequested;

    private bool _allowBinaryProgramCaching = RuntimeRenderingHostServiceDefaults.AllowBinaryProgramCaching;
    public bool AllowBinaryProgramCaching
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowBinaryProgramCaching
            : _allowBinaryProgramCaching;
        set => _allowBinaryProgramCaching = value;
    }
    public bool AllowBlendshapes
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowBlendshapes
            : _allowBlendshapes;
        set => SetShaderSetting(ref _allowBlendshapes, value);
    }
    public bool AllowShaderPipelines
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowShaderPipelines
            : _allowShaderPipelines;
        set
        {
            bool previous = AllowShaderPipelines;
            SetShaderSetting(ref _allowShaderPipelines, value);
            if (previous != AllowShaderPipelines)
            {
                global::XREngine.Rendering.OpenGL.OpenGLRenderer.HandleShaderPipelineModeChanged(AllowShaderPipelines);
                XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();
            }
        }
    }
    public bool AllowSkinning
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowSkinning
            : _allowSkinning;
        set => SetShaderSetting(ref _allowSkinning, value);
    }
    private bool _asyncProgramBinaryUpload = RuntimeRenderingHostServiceDefaults.AsyncProgramBinaryUpload;
    public bool AsyncProgramBinaryUpload
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AsyncProgramBinaryUpload
            : _asyncProgramBinaryUpload;
        set => _asyncProgramBinaryUpload = value;
    }
    private bool _asyncProgramCompilation = RuntimeRenderingHostServiceDefaults.AsyncProgramCompilation;
    public bool AsyncProgramCompilation
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AsyncProgramCompilation
            : _asyncProgramCompilation;
        set => _asyncProgramCompilation = value;
    }
    private int _openGLProgramCompileLinkWorkerCount = RuntimeRenderingHostServiceDefaults.OpenGLProgramCompileLinkWorkerCount;
    public int OpenGLProgramCompileLinkWorkerCount
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLProgramCompileLinkWorkerCount
            : _openGLProgramCompileLinkWorkerCount;
        set => _openGLProgramCompileLinkWorkerCount = Math.Clamp(value, 1, 16);
    }
    public bool CacheGpuHiZOcclusionOncePerFrame { get; set; } = true;
    public bool CalculateBlendshapesInComputeShader
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CalculateBlendshapesInComputeShader
            : _calculateBlendshapesInComputeShader;
        set => SetShaderSetting(ref _calculateBlendshapesInComputeShader, value);
    }
    public bool EnableBlendshapePrecombinePass
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.EnableBlendshapePrecombinePass
            : _enableBlendshapePrecombinePass;
        set => SetShaderSetting(ref _enableBlendshapePrecombinePass, value);
    }
    public bool EnableBlendshapePrecombineForDirectVertexPath
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.EnableBlendshapePrecombineForDirectVertexPath
            : _enableBlendshapePrecombineForDirectVertexPath;
        set => SetShaderSetting(ref _enableBlendshapePrecombineForDirectVertexPath, value);
    }
    public bool EnableBlendshapePcaBasisCompression
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.EnableBlendshapePcaBasisCompression
            : _enableBlendshapePcaBasisCompression;
        set => SetShaderSetting(ref _enableBlendshapePcaBasisCompression, value);
    }
    public int BlendshapePrecombineComputeMinActiveShapes
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.BlendshapePrecombineComputeMinActiveShapes
            : _blendshapePrecombineComputeMinActiveShapes;
        set => _blendshapePrecombineComputeMinActiveShapes = Math.Max(1, value);
    }
    public int BlendshapePrecombineDirectMinActiveShapes
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.BlendshapePrecombineDirectMinActiveShapes
            : _blendshapePrecombineDirectMinActiveShapes;
        set => _blendshapePrecombineDirectMinActiveShapes = Math.Max(1, value);
    }
    public int BlendshapePrecombineMinAffectedVertices
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.BlendshapePrecombineMinAffectedVertices
            : _blendshapePrecombineMinAffectedVertices;
        set => _blendshapePrecombineMinAffectedVertices = Math.Max(1, value);
    }
    public bool CalculateSkinnedBoundsInComputeShader
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CalculateSkinnedBoundsInComputeShader
            : _calculateSkinnedBoundsInComputeShader;
        set => _calculateSkinnedBoundsInComputeShader = value;
    }
    public bool CalculateSkinningInComputeShader
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CalculateSkinningInComputeShader
            : _calculateSkinningInComputeShader;
        set => SetShaderSetting(ref _calculateSkinningInComputeShader, value);
    }
    public bool SkinnedBoundsGpuDirectAabbWrite
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
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
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxAsyncShaderProgramsPerFrame
            : _maxAsyncShaderProgramsPerFrame;
        set => _maxAsyncShaderProgramsPerFrame = value;
    }
    private EOpenGLShaderLinkStrategy _openGLShaderLinkStrategy = RuntimeRenderingHostServiceDefaults.OpenGLShaderLinkStrategy;
    public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLShaderLinkStrategy
            : _openGLShaderLinkStrategy;
        set => _openGLShaderLinkStrategy = value;
    }
    private int _openGLShaderCompilerThreadCount = RuntimeRenderingHostServiceDefaults.OpenGLShaderCompilerThreadCount;
    public int OpenGLShaderCompilerThreadCount
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLShaderCompilerThreadCount
            : _openGLShaderCompilerThreadCount;
        set => _openGLShaderCompilerThreadCount = value;
    }
    private bool _openGLParallelShaderCompileProbeEnabled = RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeEnabled;
    public bool OpenGLParallelShaderCompileProbeEnabled
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLParallelShaderCompileProbeEnabled
            : _openGLParallelShaderCompileProbeEnabled;
        set => _openGLParallelShaderCompileProbeEnabled = value;
    }
    private int _openGLParallelShaderCompileProbeTimeoutMs = RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeTimeoutMs;
    public int OpenGLParallelShaderCompileProbeTimeoutMs
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLParallelShaderCompileProbeTimeoutMs
            : _openGLParallelShaderCompileProbeTimeoutMs;
        set => _openGLParallelShaderCompileProbeTimeoutMs = value;
    }
    public long MaxShadowAtlasMemoryBytes
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowAtlasMemoryBytes
            : _maxShadowAtlasMemoryBytes;
        set => _maxShadowAtlasMemoryBytes = Math.Max(0L, value);
    }

    public int MaxShadowAtlasPages
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowAtlasPages
            : _maxShadowAtlasPages;
        set => _maxShadowAtlasPages = Math.Clamp(value, 1, 64);
    }

    public uint MaxShadowAtlasTileResolution
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowAtlasTileResolution
            : _maxShadowAtlasTileResolution;
        set => _maxShadowAtlasTileResolution = value;
    }

    public float MaxShadowRenderMilliseconds
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowRenderMilliseconds
            : _maxShadowRenderMilliseconds;
        set => _maxShadowRenderMilliseconds = MathF.Max(0.0f, value);
    }

    public int MaxShadowTilesRenderedPerFrame
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowTilesRenderedPerFrame
            : _maxShadowTilesRenderedPerFrame;
        set => _maxShadowTilesRenderedPerFrame = Math.Max(0, value);
    }

    public uint MinShadowAtlasTileResolution
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MinShadowAtlasTileResolution
            : _minShadowAtlasTileResolution;
        set => _minShadowAtlasTileResolution = value;
    }

    public bool OpenXrCullWithFrustum
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrCullWithFrustum
            : _openXrCullWithFrustum;
        set => _openXrCullWithFrustum = value;
    }
    public bool OpenXrDebugClearOnly
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugClearOnly
            : _openXrDebugClearOnly;
        set => _openXrDebugClearOnly = value;
    }
    public bool OpenXrDebugGl
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugGl
            : _openXrDebugGl;
        set => _openXrDebugGl = value;
    }
    public bool OpenXrDebugLifecycle
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugLifecycle
            : _openXrDebugLifecycle;
        set => _openXrDebugLifecycle = value;
    }
    public bool OpenXrDebugRenderRightThenLeft
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugRenderRightThenLeft
            : _openXrDebugRenderRightThenLeft;
        set => _openXrDebugRenderRightThenLeft = value;
    }
    public bool OpenXrPrepareFrameAfterDesktopRender
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrPrepareFrameAfterDesktopRender
            : _openXrPrepareFrameAfterDesktopRender;
        set => _openXrPrepareFrameAfterDesktopRender = value;
    }
    public float OpenXrDeadlineSafetyMarginMs
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDeadlineSafetyMarginMs
            : _openXrDeadlineSafetyMarginMs;
        set => _openXrDeadlineSafetyMarginMs = MathF.Max(0.0f, value);
    }
    public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrCollectVisiblePosePolicy
            : _openXrCollectVisiblePosePolicy;
        set => _openXrCollectVisiblePosePolicy = value;
    }
    public float OpenXrCollectVisibleFrustumPaddingDegrees
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrCollectVisibleFrustumPaddingDegrees
            : _openXrCollectVisibleFrustumPaddingDegrees;
        set => _openXrCollectVisibleFrustumPaddingDegrees = Math.Clamp(value, 0.0f, 20.0f);
    }
    public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrTrackingLossPolicy
            : _openXrTrackingLossPolicy;
        set => _openXrTrackingLossPolicy = value;
    }
    public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrActionSyncPolicy
            : _openXrActionSyncPolicy;
        set => _openXrActionSyncPolicy = value;
    }
    public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrRenderPacingMode
            : _openXrRenderPacingMode;
        set => _openXrRenderPacingMode = value;
    }
    public bool OutputHDR { get; set; } = true;
    public bool PreferNVStereo { get; set; }
    public bool ProcessMeshImportsAsynchronously { get; set; } = true;
    public EVrViewRenderMode VrViewRenderMode
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
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
    public EVrFoveationMode VrFoveationMode
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VrFoveationMode
            : _vrFoveationMode;
        set => _vrFoveationMode = value;
    }
    public EVrFoveationQualityPreset VrFoveationQualityPreset
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VrFoveationQualityPreset
            : _vrFoveationQualityPreset;
        set => _vrFoveationQualityPreset = value;
    }
    public bool VrFoveationRequireRequested
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VrFoveationRequireRequested
            : _vrFoveationRequireRequested;
        set => _vrFoveationRequireRequested = value;
    }
    public int ShaderConfigVersion
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
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
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.ClipSpaceYDirection
            : _clipSpaceYDirection;
        set => SetShaderSetting(ref _clipSpaceYDirection, value);
    }
    public ERenderClipDepthRange ClipDepthRange
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.ClipDepthRange
            : _clipDepthRange;
        set => SetShaderSetting(ref _clipDepthRange, value);
    }
    public uint ShadowAtlasPageSize
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.ShadowAtlasPageSize
            : _shadowAtlasPageSize;
        set => _shadowAtlasPageSize = value;
    }

    public float TsrRenderScale { get; set; } = 1.0f;
    public bool UseAbsoluteBlendshapePositions { get; set; }
    public bool UseDetailPreservingComputeMipmaps { get; set; } = true;
    public bool UseDirectionalShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.UseDirectionalShadowAtlas
            : _useDirectionalShadowAtlas;
        set => _useDirectionalShadowAtlas = value;
    }

    public bool UseGlobalBlendshapeWeightsBufferForComputeSkinning { get; set; } = true;
    public bool UseGlobalSkinPaletteBufferForComputeSkinning { get; set; } = true;
    public bool UseIntegerUniformsInShaders
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.UseIntegerUniformsInShaders
            : _useIntegerUniformsInShaders;
        set => SetShaderSetting(ref _useIntegerUniformsInShaders, value);
    }
    public bool UsePointShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.UsePointShadowAtlas
            : _usePointShadowAtlas;
        set => _usePointShadowAtlas = value;
    }

    public bool UseSkinnedBvhRefitOptimize { get; set; } = true;
    public bool UseSpotShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.UseSpotShadowAtlas
            : _useSpotShadowAtlas;
        set => _useSpotShadowAtlas = value;
    }

    public RuntimeVulkanRobustnessSettings VulkanRobustnessSettings { get; } = new();
    public float XessCustomScale { get; set; } = 1.0f;
    public float XessSharpness { get; set; }

    private static bool TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
    {
        services = RuntimeRenderingHostServices.Current;
        return services.ProvidesShadowAtlasSettings;
    }

    private static bool TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
    {
        services = RuntimeRenderingHostServices.Current;
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
