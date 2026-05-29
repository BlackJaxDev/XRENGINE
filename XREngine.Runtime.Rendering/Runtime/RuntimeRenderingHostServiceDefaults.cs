using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Scalar fallback values used by <see cref="RuntimeRenderingHostServices"/> when no concrete host is installed.
/// </summary>
public static class RuntimeRenderingHostServiceDefaults
{
    public const bool AllowShaderPipelines = false;
    public const bool EnableExactTransparencyTechniques = false;
    public const bool UseInterleavedMeshBuffer = false;
    public const bool UseIntegerUniformsInShaders = false;
    public const bool RemapBlendshapeDeltas = false;
    public const bool AllowBlendshapes = true;
    public const bool PopulateVertexDataInParallel = true;
    public const bool ProcessMeshImportsAsynchronously = false;
    public const bool AllowSkinning = true;
    public const bool CalculateSkinningInComputeShader = false;
    public const bool CalculateBlendshapesInComputeShader = false;
    public const int ShaderConfigVersion = 0;
    public const bool AllowBinaryProgramCaching = true;
    public const bool AsyncProgramBinaryUpload = true;
    public const bool AsyncProgramCompilation = true;
    public const int OpenGLProgramCompileLinkWorkerCount = 1;
    public const int MaxAsyncShaderProgramsPerFrame = 16;
    public const EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy = EOpenGLShaderLinkStrategy.Auto;
    public const int OpenGLShaderCompilerThreadCount = 1;
    public const bool OpenGLParallelShaderCompileProbeEnabled = true;
    public const int OpenGLParallelShaderCompileProbeTimeoutMs = 25;

    public const bool IsRenderThread = true;
    public const bool IsRendererActive = false;
    public const bool IsShadowPass = false;
    public const bool IsStereoPass = false;
    public const bool IsSceneCapturePass = false;
    public const bool RenderCullingVolumesEnabled = false;
    public const bool IsNvidia = false;
    public const float DefaultLuminanceX = 0.2126f;
    public const float DefaultLuminanceY = 0.7152f;
    public const float DefaultLuminanceZ = 0.0722f;
    public const double DefaultDeltaSeconds = 0.0;
    public const long DefaultTimestampTicks = 0L;
    public const long DefaultTrackedVramBytes = 0L;
    public const long DefaultTrackedVramBudgetBytes = long.MaxValue;
    public const bool EnableGpuIndirectDebugLogging = false;
    public const EOcclusionCullingMode GpuOcclusionCullingMode = EOcclusionCullingMode.GpuHiZ;
    public const int CpuQueryOcclusionRetestPeriodFrames = 6;
    public const bool EnableCpuSoftwareOcclusionCulling = false;
    public const int CpuSocBufferWidth = 256;
    public const int CpuSocBufferHeight = 128;
    public const int CpuSocOccluderTriangleBudget = 5000;
    public const int CpuSocMaxOccluders = 64;
    public const float CpuSocMinOccluderScreenArea = 0.005f;
    public const bool CpuSocUseAvx2 = true;
    public const bool CpuSocDebugVisualization = false;
    public const bool CpuSocDebugForceVisible = false;
    public const ECpuSceneCullingStructure CpuSceneCullingStructure = ECpuSceneCullingStructure.Bvh;
    public const bool IsPlayModeTransitioning = false;
    public const string PlayModeStateName = "Stopped";
    public const uint DefaultMsaaSampleCount = 1u;
    public const bool DefaultOutputHDR = false;
    public const float DefaultTsrRenderScale = 1.0f;
    public const bool ForwardDepthPrePassEnabled = true;
    public const bool ForwardPrePassSharesGBufferTargets = true;
    public const bool EnableRenderStatisticsTracking = true;
    public const bool EnableGpuRenderPipelineProfiling = false;
    public const ulong CurrentRenderFrameId = 0UL;
    public const ETwoPlayerPreference TwoPlayerViewportPreference = ETwoPlayerPreference.SplitHorizontally;
    public const EThreePlayerPreference ThreePlayerViewportPreference = EThreePlayerPreference.PreferFirstPlayer;
    public const RuntimeGraphicsApiKind CurrentRenderBackend = RuntimeGraphicsApiKind.Unknown;
    public const EAntiAliasingMode DefaultAntiAliasingMode = EAntiAliasingMode.None;
    public const XRCamera.EDepthMode SceneCameraDepthModePreference = XRCamera.EDepthMode.Normal;

    public const bool ProvidesShadowAtlasSettings = false;
    public const bool UseSpotShadowAtlas = true;
    public const bool UseDirectionalShadowAtlas = true;
    public const bool UsePointShadowAtlas = true;
    public const uint ShadowAtlasPageSize = 4096u;
    public const int MaxShadowAtlasPages = 1;
    public const long MaxShadowAtlasMemoryBytes = 0L;
    public const int MaxShadowTilesRenderedPerFrame = 16;
    public const float MaxShadowRenderMilliseconds = 2.0f;
    public const uint MinShadowAtlasTileResolution = 128u;
    public const uint MaxShadowAtlasTileResolution = 4096u;

    public const string AssetFileExtension = "asset";
    public const TextureRuntimeLogMode TextureLogMode = TextureRuntimeLogMode.Summary;
    public const double TextureSlowCpuDecodeResizeMilliseconds = 5.0;
    public const double TextureSlowMipBuildMilliseconds = 5.0;
    public const double TextureSlowUploadChunkMilliseconds = 2.0;
    public const double TextureSlowTransitionMilliseconds = 8.0;
    public const double TextureSlowQueueWaitMilliseconds = 100.0;
    public const double TextureUploadFrameBudgetMilliseconds = 2.0;
    public const string SparseTextureStreamingUnsupportedReason = "No renderer-specific sparse texture capability service is configured.";
    public const string SparseTextureStreamingFinalizeNotConfiguredReason = "RuntimeRenderingHostServices.Current has not been configured for sparse texture finalization.";
    public const string JobSchedulingNotConfiguredMessage = "RuntimeRenderingHostServices.Current has not been configured for job scheduling.";

    public const string RendererCreationNotConfiguredMessage = "RuntimeRenderingHostServices.Current has not been configured to create renderers.";
    public const bool IsWindowScenePanelPresentationEnabled = false;
    public const int ScenePanelResizeDebounceMs = 100;
    public const bool ForceFullViewport = false;

    public const bool RenderWindowsWhileInVR = false;
    public const bool EnableVrFoveatedViewSet = false;
    public const bool IsInVR = false;
    public const bool IsOpenXRActive = false;
    public const bool VrMirrorComposeFromEyeTextures = false;
    public const float VrFoveationCenterU = 0.5f;
    public const float VrFoveationCenterV = 0.5f;
    public const float VrFoveationInnerRadius = 0.35f;
    public const float VrFoveationOuterRadius = 0.85f;
    public const float VrFoveationInnerShadingRate = 1.0f;
    public const float VrFoveationMiddleShadingRate = 0.7f;
    public const float VrFoveationOuterShadingRate = 0.5f;
    public const float VrFoveationVisibilityMargin = 0.05f;
    public const bool VrFoveationForceFullResForUiAndNearField = true;
    public const float VrFoveationFullResNearDistanceMeters = 1.5f;
    public const bool OpenXrCullWithFrustum = true;
    public const bool OpenXrDebugGl = false;
    public const bool OpenXrDebugClearOnly = false;
    public const bool OpenXrDebugLifecycle = false;
    public const bool OpenXrDebugRenderRightThenLeft = false;
    public const bool OpenXrPrepareFrameAfterDesktopRender = true;
    public const float OpenXrDeadlineSafetyMarginMs = 1.0f;
    public const float OpenXrCollectVisibleFrustumPaddingDegrees = 2.0f;

    public const bool IsViewportCurrentlyRendering = false;
    public const bool ShouldForceDebugOpaquePipeline = false;
    public const int FallbackBytesPerPixel = 4;
}
