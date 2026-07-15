using System.Collections;
using System.IO;
using System.Numerics;
using System;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Shadows;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeRenderingHostServicesTests
{
    private IRuntimeRenderingHostServices? _previousServices;
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeRenderingHostServices.Current = _previousServices ?? new TestRuntimeRenderingHostServices();
        RuntimeShaderServices.Current = _previousShaderServices;
    }

    [Test]
    [NonParallelizable]
    public void EffectiveOcclusionSettings_UseRuntimeRenderingHostServices()
    {
        string? previousMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OcclusionCullingMode);
        string? previousRetest = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CpuQueryOcclusionRetestPeriodFrames);
        string? previousSoc = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CpuSoftwareOcclusion);

        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OcclusionCullingMode, null);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuQueryOcclusionRetestPeriodFrames, null);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSoftwareOcclusion, null);

            TestRuntimeRenderingHostServices services = new()
            {
                GpuOcclusionCullingMode = EOcclusionCullingMode.CpuQueryAsync,
                CpuQueryOcclusionRetestPeriodFrames = 9,
                CpuQueryOcclusionMaxQueriesPerFrame = 37,
                CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.4f,
                CpuQueryOcclusionRecoveryMinCadenceFrames = 3,
                CpuQueryOcclusionSmallMotionMeters = 0.03f,
                CpuQueryOcclusionMediumMotionMeters = 0.5f,
                CpuQueryOcclusionLargeMotionMeters = 3.0f,
                CpuQueryOcclusionCameraCutMeters = 14.0f,
                CpuQueryOcclusionSmallRotationDegrees = 2.0f,
                CpuQueryOcclusionMediumRotationDegrees = 6.0f,
                CpuQueryOcclusionLargeRotationDegrees = 18.0f,
                CpuQueryOcclusionCameraCutRotationDegrees = 65.0f,
                CpuQueryOcclusionVrHeadMotionMeters = 0.35f,
                CpuQueryOcclusionVrHeadRotationDegrees = 25.0f,
                CpuQueryOcclusionStereoMode = ECpuQueryStereoMode.StereoPairShared,
                CpuQueryOcclusionMaxPendingFrames = 8,
                EnableCpuSoftwareOcclusionCulling = true,
                CpuSocBufferWidth = 512,
                CpuSocBufferHeight = 256,
                CpuSocOccluderTriangleBudget = 12345,
                CpuSocMaxOccluders = 17,
                CpuSocMinOccluderScreenArea = 0.125f,
                CpuSocUseAvx2 = false,
                CpuSocDebugVisualization = true,
                CpuSocDebugForceVisible = true,
            };
            RuntimeRenderingHostServices.Current = services;

            RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode.ShouldBe(EOcclusionCullingMode.CpuQueryAsync);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames.ShouldBe(9);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMaxQueriesPerFrame.ShouldBe(37);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVisibleDemotionBudgetFraction.ShouldBe(0.4f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRecoveryMinCadenceFrames.ShouldBe(3);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionSmallMotionMeters.ShouldBe(0.03f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMediumMotionMeters.ShouldBe(0.5f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionLargeMotionMeters.ShouldBe(3.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutMeters.ShouldBe(14.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionSmallRotationDegrees.ShouldBe(2.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMediumRotationDegrees.ShouldBe(6.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionLargeRotationDegrees.ShouldBe(18.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutRotationDegrees.ShouldBe(65.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVrHeadMotionMeters.ShouldBe(0.35f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVrHeadRotationDegrees.ShouldBe(25.0f);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionStereoMode.ShouldBe(ECpuQueryStereoMode.StereoPairShared);
            RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMaxPendingFrames.ShouldBe(8);
            RuntimeEngine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling.ShouldBeTrue();
            RuntimeEngine.EffectiveSettings.CpuSocBufferWidth.ShouldBe(512);
            RuntimeEngine.EffectiveSettings.CpuSocBufferHeight.ShouldBe(256);
            RuntimeEngine.EffectiveSettings.CpuSocOccluderTriangleBudget.ShouldBe(12345);
            RuntimeEngine.EffectiveSettings.CpuSocMaxOccluders.ShouldBe(17);
            RuntimeEngine.EffectiveSettings.CpuSocMinOccluderScreenArea.ShouldBe(0.125f);
            RuntimeEngine.EffectiveSettings.CpuSocUseAvx2.ShouldBeFalse();
            RuntimeEngine.EffectiveSettings.CpuSocDebugVisualization.ShouldBeTrue();
            RuntimeEngine.EffectiveSettings.CpuSocDebugForceVisible.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OcclusionCullingMode, previousMode);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuQueryOcclusionRetestPeriodFrames, previousRetest);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSoftwareOcclusion, previousSoc);
        }
    }

    [Test]
    [NonParallelizable]
    public void EffectiveCpuSceneCullingStructure_UsesRuntimeRenderingHostServicesAndEnvOverride()
    {
        string? previousStructure = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure);

        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure, null);
            EffectiveSettingsEnvOverrides.ReloadForTests();

            RuntimeRenderingHostServices.Current = new TestRuntimeRenderingHostServices
            {
                CpuSceneCullingStructure = ECpuSceneCullingStructure.Bvh,
            };

            RuntimeEngine.EffectiveSettings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Bvh);

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure, "Octree");
            EffectiveSettingsEnvOverrides.ReloadForTests();
            RuntimeEngine.EffectiveSettings.CpuSceneCullingStructure.ShouldBe(ECpuSceneCullingStructure.Octree);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CpuSceneCullingStructure, previousStructure);
            EffectiveSettingsEnvOverrides.ReloadForTests();
        }
    }

    [Test]
    public void CameraRenderPipeline_UsesRuntimeRenderingHostServicesFactory()
    {
        TestRenderPipeline pipeline = new();
        TestRuntimeRenderingHostServices services = new()
        {
            DefaultPipeline = pipeline,
        };
        RuntimeRenderingHostServices.Current = services;

        XRCamera camera = new();

        camera.RenderPipeline.ShouldBeSameAs(pipeline);
        services.CreateDefaultRenderPipelineCallCount.ShouldBe(1);
    }

    [Test]
    public void BlendshapePrecombineSettings_UseRuntimeRenderingHostServices()
    {
        TestRuntimeRenderingHostServices services = new()
        {
            EnableBlendshapePrecombinePass = true,
            EnableBlendshapePrecombineForDirectVertexPath = false,
            EnableBlendshapePcaBasisCompression = true,
            BlendshapePrecombineComputeMinActiveShapes = 11,
            BlendshapePrecombineDirectMinActiveShapes = 13,
            BlendshapePrecombineMinAffectedVertices = 17,
        };
        RuntimeRenderingHostServices.Current = services;

        RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass.ShouldBeTrue();
        RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombineForDirectVertexPath.ShouldBeFalse();
        RuntimeEngine.Rendering.Settings.EnableBlendshapePcaBasisCompression.ShouldBeTrue();
        RuntimeEngine.Rendering.Settings.BlendshapePrecombineComputeMinActiveShapes.ShouldBe(11);
        RuntimeEngine.Rendering.Settings.BlendshapePrecombineDirectMinActiveShapes.ShouldBe(13);
        RuntimeEngine.Rendering.Settings.BlendshapePrecombineMinAffectedVertices.ShouldBe(17);
    }

    [Test]
    public void PipelineInstance_UsesRuntimeRenderingHostServicesFactory()
    {
        TestRenderPipeline pipeline = new();
        TestRuntimeRenderingHostServices services = new()
        {
            DefaultPipeline = pipeline,
        };
        RuntimeRenderingHostServices.Current = services;

        XRRenderPipelineInstance instance = new();

        instance.Pipeline.ShouldBeSameAs(pipeline);
        services.CreateDefaultRenderPipelineCallCount.ShouldBe(1);
    }

    [Test]
    public void OpenXrRuntimeServiceEnsurer_UsesRegisteredDelegate()
    {
        Func<string, bool>? previousEnsurer = RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer;
        try
        {
            int callCount = 0;
            RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer = reason =>
            {
                reason.ShouldBe("unit-test recovery");
                callCount++;
                return true;
            };

            RuntimeRenderingHostServices.Current.TryEnsureOpenXrRuntimeService("unit-test recovery").ShouldBeTrue();
            callCount.ShouldBe(1);
        }
        finally
        {
            RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer = previousEnsurer;
        }
    }

    [Test]
    public void CameraRenderPipelineReplacement_UpdatesConnectedViewportPipelineInstance()
    {
        RuntimeRenderingHostServices.Current = new TestRuntimeRenderingHostServices
        {
            DefaultPipeline = new TestRenderPipeline(),
        };

        XRCamera camera = new();
        XRViewport viewport = new(null);
        SinglePassRenderPipeline initialPipeline = new();
        TwoPassRenderPipeline replacementPipeline = new();

        camera.RenderPipeline = initialPipeline;
        viewport.Camera = camera;

        viewport.RenderPipelineInstance.Pipeline.ShouldBeSameAs(initialPipeline);
        viewport.RenderPipelineInstance.MeshRenderCommands.GetUpdatingPassCount().ShouldBe(1);

        camera.RenderPipeline = replacementPipeline;

        viewport.RenderPipelineInstance.Pipeline.ShouldBeSameAs(replacementPipeline);
        viewport.RenderPipelineInstance.MeshRenderCommands.GetUpdatingPassCount().ShouldBe(2);
        initialPipeline.Instances.ShouldNotContain(viewport.RenderPipelineInstance);
        replacementPipeline.Instances.ShouldContain(viewport.RenderPipelineInstance);
    }

    [Test]
    public void ViewportAutomaticCallbacks_UseRuntimeRenderingHostServicesSubscriptions()
    {
        TestRuntimeRenderingHostServices services = new()
        {
            DefaultPipeline = new TestRenderPipeline(),
        };
        RuntimeRenderingHostServices.Current = services;

        XRViewport viewport = new(null);
        XRCamera camera = new();

        viewport.Camera = camera;
        services.ViewportSwapSubscribeCount.ShouldBe(1);
        services.ViewportCollectSubscribeCount.ShouldBe(1);

        viewport.AutomaticallySwapBuffers = false;
        viewport.AutomaticallyCollectVisible = false;

        services.ViewportSwapUnsubscribeCount.ShouldBe(1);
        services.ViewportCollectUnsubscribeCount.ShouldBe(1);
    }

    [Test]
    public void TextureAuthorityPathResolution_UsesRuntimeRenderingHostServicesResolver()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), "TextureAuthoritySource.png");
        string cachePath = Path.Combine(Path.GetTempPath(), "TextureAuthorityCache.asset");

        TestRuntimeRenderingHostServices services = new()
        {
            ResolvedTextureStreamingAuthorityPath = cachePath,
        };
        RuntimeRenderingHostServices.Current = services;

        string resolvedPath = XRTexture2D.ResolveTextureStreamingAuthorityPathInternal(sourcePath, out string? originalSourcePath);

        resolvedPath.ShouldBe(Path.GetFullPath(cachePath));
        originalSourcePath.ShouldBe(Path.GetFullPath(sourcePath));
    }

    [Test]
    public void ImportedTextureStreamingScope_EnablesImportedTextureTimingDiagnostics()
    {
        RuntimeRenderingHostServices.Current = new TestRuntimeRenderingHostServices();

        XRTexture2D.ShouldLogImportedTextureTiming.ShouldBeFalse();

        using (XRTexture2D.EnterImportedTextureStreamingScope())
            XRTexture2D.ShouldLogImportedTextureTiming.ShouldBeTrue();

        XRTexture2D.ShouldLogImportedTextureTiming.ShouldBeFalse();
    }

    [Test]
    public void RegisterImportedTextureStreamingPlaceholder_TracksTextureWithoutPreview()
    {
        TestRuntimeRenderingHostServices services = new()
        {
            DefaultPipeline = new TestRenderPipeline(),
        };
        RuntimeRenderingHostServices.Current = services;

        string sourcePath = Path.Combine(Path.GetTempPath(), $"ImportedTexturePreview_{Guid.NewGuid():N}.png");
        string normalizedPath = Path.GetFullPath(sourcePath);
        XRTexture2D texture = new()
        {
            Name = "DeferredPreviewTexture",
        };

        XRTexture2D.RegisterImportedTextureStreamingPlaceholder(sourcePath, texture);

        bool found = false;
        foreach (ImportedTextureStreamingTextureTelemetry telemetry in XRTexture2D.GetImportedTextureStreamingTextureTelemetry())
        {
            if (!string.Equals(telemetry.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            telemetry.PreviewReady.ShouldBeFalse();
            found = true;
            break;
        }

        found.ShouldBeTrue();
    }

    private sealed class TestRuntimeRenderingHostServices : IRuntimeRenderingHostServices
    {
        public RenderPipeline? DefaultPipeline { get; set; }

        public string? ResolvedTextureStreamingAuthorityPath { get; set; }

        public SparseTextureStreamingSupport SparseTextureStreamingSupport { get; set; } = SparseTextureStreamingSupport.Unsupported();

        public int CreateDefaultRenderPipelineCallCount { get; private set; }

        public int ViewportSwapSubscribeCount { get; private set; }

        public int ViewportSwapUnsubscribeCount { get; private set; }

        public int ViewportCollectSubscribeCount { get; private set; }

        public int ViewportCollectUnsubscribeCount { get; private set; }

        public int ViewportPostCollectSubscribeCount { get; private set; }

        public int ViewportPostCollectUnsubscribeCount { get; private set; }

        public IDisposable? StartProfileScope(string? scopeName)
            => null;

        public bool AllowShaderPipelines => false;
        public bool AllowBinaryProgramCaching => RuntimeRenderingHostServiceDefaults.AllowBinaryProgramCaching;
        public bool AsyncProgramBinaryUpload => RuntimeRenderingHostServiceDefaults.AsyncProgramBinaryUpload;
        public bool AsyncProgramCompilation => RuntimeRenderingHostServiceDefaults.AsyncProgramCompilation;
        public int OpenGLProgramCompileLinkWorkerCount => RuntimeRenderingHostServiceDefaults.OpenGLProgramCompileLinkWorkerCount;
        public int MaxAsyncShaderProgramsPerFrame => RuntimeRenderingHostServiceDefaults.MaxAsyncShaderProgramsPerFrame;
        public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy => RuntimeRenderingHostServiceDefaults.OpenGLShaderLinkStrategy;
        public int OpenGLShaderCompilerThreadCount => RuntimeRenderingHostServiceDefaults.OpenGLShaderCompilerThreadCount;
        public bool OpenGLParallelShaderCompileProbeEnabled => RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeEnabled;
        public int OpenGLParallelShaderCompileProbeTimeoutMs => RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeTimeoutMs;
        public EVulkanAllocatorBackend VulkanAllocatorBackend => RuntimeRenderingHostServiceDefaults.VulkanAllocatorBackend;
        public EVulkanSynchronizationBackend VulkanSynchronizationBackend => RuntimeRenderingHostServiceDefaults.VulkanSynchronizationBackend;
        public EVulkanDescriptorUpdateBackend VulkanDescriptorUpdateBackend => RuntimeRenderingHostServiceDefaults.VulkanDescriptorUpdateBackend;
        public bool VulkanDynamicUniformBufferEnabled => RuntimeRenderingHostServiceDefaults.VulkanDynamicUniformBufferEnabled;
        public bool EnableExactTransparencyTechniques => false;
        public bool UseInterleavedMeshBuffer => false;
        public bool UseIntegerUniformsInShaders => false;
        public bool RemapBlendshapeDeltas => false;
        public bool AllowBlendshapes => true;
        public bool PopulateVertexDataInParallel => true;
        public bool ProcessMeshImportsAsynchronously => false;
        public bool AllowSkinning => true;
        public bool CalculateSkinningInComputeShader => false;
        public bool CalculateBlendshapesInComputeShader => false;
        public bool CalculateSkinnedBoundsInComputeShader => false;
        public bool SkinnedBoundsGpuDirectAabbWrite => false;
        public bool EnableBlendshapePrecombinePass { get; set; } = RuntimeRenderingHostServiceDefaults.EnableBlendshapePrecombinePass;
        public bool EnableBlendshapePrecombineForDirectVertexPath { get; set; } = RuntimeRenderingHostServiceDefaults.EnableBlendshapePrecombineForDirectVertexPath;
        public bool EnableBlendshapePcaBasisCompression { get; set; } = RuntimeRenderingHostServiceDefaults.EnableBlendshapePcaBasisCompression;
        public int BlendshapePrecombineComputeMinActiveShapes { get; set; } = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineComputeMinActiveShapes;
        public int BlendshapePrecombineDirectMinActiveShapes { get; set; } = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineDirectMinActiveShapes;
        public int BlendshapePrecombineMinAffectedVertices { get; set; } = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineMinAffectedVertices;
        public bool StreamMeshLodsOnDemand { get; set; } = RuntimeRenderingHostServiceDefaults.StreamMeshLodsOnDemand;
        public int MeshLodStreamingDrainIntervalFrames { get; set; } = RuntimeRenderingHostServiceDefaults.MeshLodStreamingDrainIntervalFrames;
        public int MeshLodStreamingMaxLoadsPerDrain { get; set; } = RuntimeRenderingHostServiceDefaults.MeshLodStreamingMaxLoadsPerDrain;
        public int ShaderConfigVersion => 0;
        public ERenderClipSpaceYDirection ClipSpaceYDirection { get; set; } = RuntimeRenderingHostServiceDefaults.ClipSpaceYDirection;
        public ERenderClipDepthRange ClipDepthRange { get; set; } = RuntimeRenderingHostServiceDefaults.ClipDepthRange;
        public bool IsRenderThread => true;
        public bool IsRendererActive => false;
        public bool IsOpenXrRuntimeRequested => RuntimeRenderingHostServiceDefaults.IsOpenXrRuntimeRequested;
        public bool IsShadowPass => false;
        public bool IsStereoPass => false;
        public bool IsSceneCapturePass => false;
        public bool RenderCullingVolumesEnabled => false;
        public bool IsNvidia => false;
        public string AssetFileExtension => "asset";
        public string? TextureFallbackPath => null;
        public XRMaterial? InvalidMaterial => null;
        public Vector3 DefaultLuminance => Vector3.One;
        public long ElapsedTicks => 0L;
        public float ElapsedTime => 0.0f;
        public double UpdateDeltaSeconds => 0.0;
        public long LastUpdateTimestampTicks => 0L;
        public double RenderDeltaSeconds => 0.0;
        public long LastRenderTimestampTicks => 0L;
        public long TrackedVramBytes => 0L;
        public long TrackedVramBudgetBytes => long.MaxValue;
        public bool EnableGpuIndirectDebugLogging => false;
        public EOcclusionCullingMode GpuOcclusionCullingMode { get; set; } = RuntimeRenderingHostServiceDefaults.GpuOcclusionCullingMode;
        public int CpuQueryOcclusionRetestPeriodFrames { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionRetestPeriodFrames;
        public int CpuQueryOcclusionMaxQueriesPerFrame { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMaxQueriesPerFrame;
        public float CpuQueryOcclusionVisibleDemotionBudgetFraction { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionVisibleDemotionBudgetFraction;
        public int CpuQueryOcclusionRecoveryMinCadenceFrames { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionRecoveryMinCadenceFrames;
        public float CpuQueryOcclusionSmallMotionMeters { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionSmallMotionMeters;
        public float CpuQueryOcclusionMediumMotionMeters { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMediumMotionMeters;
        public float CpuQueryOcclusionLargeMotionMeters { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionLargeMotionMeters;
        public float CpuQueryOcclusionCameraCutMeters { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionCameraCutMeters;
        public float CpuQueryOcclusionSmallRotationDegrees { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionSmallRotationDegrees;
        public float CpuQueryOcclusionMediumRotationDegrees { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMediumRotationDegrees;
        public float CpuQueryOcclusionLargeRotationDegrees { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionLargeRotationDegrees;
        public float CpuQueryOcclusionCameraCutRotationDegrees { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionCameraCutRotationDegrees;
        public float CpuQueryOcclusionVrHeadMotionMeters { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionVrHeadMotionMeters;
        public float CpuQueryOcclusionVrHeadRotationDegrees { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionVrHeadRotationDegrees;
        public ECpuQueryStereoMode CpuQueryOcclusionStereoMode { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionStereoMode;
        public int CpuQueryOcclusionMaxPendingFrames { get; set; } = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMaxPendingFrames;
        public bool EnableCpuSoftwareOcclusionCulling { get; set; } = RuntimeRenderingHostServiceDefaults.EnableCpuSoftwareOcclusionCulling;
        public int CpuSocBufferWidth { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocBufferWidth;
        public int CpuSocBufferHeight { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocBufferHeight;
        public int CpuSocOccluderTriangleBudget { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocOccluderTriangleBudget;
        public int CpuSocMaxOccluders { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocMaxOccluders;
        public float CpuSocMinOccluderScreenArea { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocMinOccluderScreenArea;
        public bool CpuSocUseAvx2 { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocUseAvx2;
        public bool CpuSocDebugVisualization { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocDebugVisualization;
        public bool CpuSocDebugForceVisible { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSocDebugForceVisible;
        public ECpuSceneCullingStructure CpuSceneCullingStructure { get; set; } = RuntimeRenderingHostServiceDefaults.CpuSceneCullingStructure;
        public TextureRuntimeLogMode TextureLogMode => TextureRuntimeLogMode.Disabled;
        public double TextureSlowCpuDecodeResizeMilliseconds => 5.0;
        public double TextureSlowMipBuildMilliseconds => 5.0;
        public double TextureSlowUploadChunkMilliseconds => 2.0;
        public double TextureSlowTransitionMilliseconds => 8.0;
        public double TextureSlowQueueWaitMilliseconds => 100.0;
        public double TextureUploadFrameBudgetMilliseconds => 2.0;
        public ETwoPlayerPreference TwoPlayerViewportPreference => ETwoPlayerPreference.SplitHorizontally;
        public EThreePlayerPreference ThreePlayerViewportPreference => EThreePlayerPreference.PreferFirstPlayer;
        public bool EnableOpenXrVulkanParallelRendering => RuntimeRenderingHostServiceDefaults.EnableOpenXrVulkanParallelRendering;
        public RuntimeGraphicsApiKind CurrentRenderBackend => RuntimeGraphicsApiKind.Unknown;
        public IRuntimeRendererHost? CurrentRenderer => null;
        public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => null;
        public IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext => null;
        public bool IsPlayModeTransitioning => false;
        public string PlayModeStateName => "Stopped";
        public EAntiAliasingMode DefaultAntiAliasingMode => EAntiAliasingMode.None;
        public uint DefaultMsaaSampleCount => 1u;
        public bool DefaultOutputHDR => false;
        public float DefaultTsrRenderScale => 1.0f;
        public bool EnableRenderStatisticsTracking => true;
        public bool EnableGpuRenderPipelineProfiling => true;
        public ulong CurrentRenderFrameId => 0UL;
        public bool ProvidesShadowAtlasSettings => false;
        public bool UseSpotShadowAtlas => true;
        public bool UseDirectionalShadowAtlas => true;
        public bool UsePointShadowAtlas => true;
        public uint ShadowAtlasPageSize => 4096u;
        public int MaxShadowAtlasPages => 1;
        public long MaxShadowAtlasMemoryBytes => 0L;
        public int MaxShadowTilesRenderedPerFrame => 16;
        public float MaxShadowRenderMilliseconds => 2.0f;
        public int MaxDirectionalCascadeAtlasStaleFrames => RuntimeRenderingHostServiceDefaults.MaxDirectionalCascadeAtlasStaleFrames;
        public uint MinShadowAtlasTileResolution => 128u;
        public uint MaxShadowAtlasTileResolution => 4096u;
        public bool IsWindowScenePanelPresentationEnabled => false;
        public EInteractiveWindowResizeStrategy InteractiveResizeStrategy => EInteractiveWindowResizeStrategy.Default;
        public int ScenePanelResizeDebounceMs => 100;
        public bool ForceFullViewport => false;
        public bool RenderWindowsWhileInVR => false;
        public bool EnableVrFoveatedViewSet => false;
        public EVrViewRenderMode VrViewRenderMode => RuntimeRenderingHostServiceDefaults.VrViewRenderMode;
        public EVrFoveationMode VrFoveationMode => RuntimeRenderingHostServiceDefaults.VrFoveationMode;
        public EVrFoveationQualityPreset VrFoveationQualityPreset => RuntimeRenderingHostServiceDefaults.VrFoveationQualityPreset;
        public bool VrFoveationRequireRequested => RuntimeRenderingHostServiceDefaults.VrFoveationRequireRequested;
        public EOpenXrEyeResolutionPreset OpenXrEyeResolutionPreset => RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionPreset;
        public float OpenXrEyeResolutionScale => RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionScale;
        public uint OpenXrCustomEyeResolutionWidth => RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionWidth;
        public uint OpenXrCustomEyeResolutionHeight => RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionHeight;
        public bool IsInVR => false;
        public bool IsOpenXRActive => false;
        public bool VrMirrorComposeFromEyeTextures => false;
        public bool VrCopyEyePreviewTextures => false;
        public Vector2 VrFoveationCenterUv => new(0.5f, 0.5f);
        public float VrFoveationInnerRadius => 0.35f;
        public float VrFoveationOuterRadius => 0.85f;
        public Vector3 VrFoveationShadingRates => new(1.0f, 0.7f, 0.5f);
        public float VrFoveationVisibilityMargin => 0.05f;
        public bool VrFoveationForceFullResForUiAndNearField => true;
        public float VrFoveationFullResNearDistanceMeters => 1.5f;
        public bool OpenXrCullWithFrustum => true;
        public bool OpenXrDebugGl => false;
        public bool OpenXrDebugClearOnly => false;
        public bool OpenXrDebugLifecycle => false;
        public bool OpenXrDebugRenderRightThenLeft => false;
        public bool OpenXrPrepareFrameAfterDesktopRender => true;
        public float OpenXrDeadlineSafetyMarginMs => 1.0f;
        public float OpenXrPoseTimeOffsetMs => RuntimeRenderingHostServiceDefaults.OpenXrPoseTimeOffsetMs;
        public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy => OpenXRAPI.OpenXrCollectVisiblePosePolicy.Predicted;
        public float OpenXrCollectVisibleFrustumPaddingDegrees => 2.0f;
        public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy => OpenXRAPI.OpenXrTrackingLossPolicy.FreezeLastValid;
        public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy => OpenXRAPI.OpenXrActionSyncPolicy.PredictedOnly;
        public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode => RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode;
        public bool ShouldForceDebugOpaquePipeline => false;

        public RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window)
            => RuntimeGraphicsApiKind.Unknown;

        public IEnumerable<IRuntimeViewportHost> EnumerateActiveViewports()
            => [];

        public IEnumerable<IPawnController> EnumerateLocalPlayers()
            => [];

        public XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
            => XRCamera.EDepthMode.Normal;

        public IRuntimeInputControllablePawn? EnsurePawnForCamera(SceneNode sceneNode, CameraComponent camera, ELocalPlayerIndex playerIndex, Type? pawnType = null)
            => null;

        public void PickViewportPhysicsAsync(
            XRViewport viewport,
            CameraComponent camera,
            Vector2 normalizedViewportPosition,
            LayerMask layerMask,
            object? filter,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedPhysicsResults,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>?> physicsFinishedCallback,
            bool useUnjitteredProjection)
        {
            physicsFinishedCallback(null);
        }

        public IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline)
            => null;

        public void LogOutput(string message)
        {
        }

        public void LogWarning(string message)
        {
        }

        public void LogException(Exception ex, string? context = null)
        {
        }

        public void RecordMissingAsset(string assetPath, string category, string? context = null)
        {
        }

        public void RecordRenderResourceChurn(string resourceKind, string resourceName, string eventName, string? reason = null)
        {
        }

        public byte[] ReadAllBytes(string filePath)
            => Array.Empty<byte>();

        public string ResolveTextureStreamingAuthorityPath(string filePath)
            => ResolvedTextureStreamingAuthorityPath ?? filePath;

        public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
            => SparseTextureStreamingSupport;

        public bool TryScheduleSparseTextureStreamingTransitionAsync(
            XRTexture2D texture,
            SparseTextureStreamingTransitionRequest request,
            CancellationToken cancellationToken,
            Action<SparseTextureStreamingTransitionResult> onCompleted,
            Action<Exception>? onError = null)
            => false;

        public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
            XRTexture2D texture,
            SparseTextureStreamingTransitionRequest request,
            SparseTextureStreamingTransitionResult transitionResult)
            => SparseTextureStreamingFinalizeResult.Failed();

        public EnumeratorJob ScheduleEnumeratorJob(
            Func<IEnumerable> routineFactory,
            JobPriority priority = JobPriority.Normal,
            Action? completed = null,
            Action<Exception>? error = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();

        public void SubscribeViewportSwapBuffers(Action swapBuffers)
            => ViewportSwapSubscribeCount++;

        public void UnsubscribeViewportSwapBuffers(Action swapBuffers)
            => ViewportSwapUnsubscribeCount++;

        public void SubscribeViewportCollectVisible(Action collectVisible)
            => ViewportCollectSubscribeCount++;

        public void UnsubscribeViewportCollectVisible(Action collectVisible)
            => ViewportCollectUnsubscribeCount++;

        public void SubscribeViewportPostCollectVisible(Action postCollectVisible)
            => ViewportPostCollectSubscribeCount++;

        public void UnsubscribeViewportPostCollectVisible(Action postCollectVisible)
            => ViewportPostCollectUnsubscribeCount++;

        public void SubscribeRenderingSettingsChanged(Action callback)
        {
        }

        public void UnsubscribeRenderingSettingsChanged(Action callback)
        {
        }

        public void SubscribeAntiAliasingSettingsChanged(Action callback)
        {
        }

        public void UnsubscribeAntiAliasingSettingsChanged(Action callback)
        {
        }

        public void SubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame)
        {
        }

        public void UnsubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame)
        {
        }

        public void SubscribePlayModeTransitions(Action callback)
        {
        }

        public void UnsubscribePlayModeTransitions(Action callback)
        {
        }

        public void EnqueueRenderThreadTask(Action task)
            => task();

        public void EnqueueRenderThreadTask(Action task, RenderThreadJobKind renderThreadKind)
            => task();

        public void EnqueueRenderThreadTask(Action task, string reason)
            => task();

        public void EnqueueRenderThreadTask(Action task, string reason, RenderThreadJobKind renderThreadKind)
            => task();

        public T InvokeRenderThreadTask<T>(
            Func<T> task,
            string reason,
            RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
            => task();

        public void EnqueueAppThreadTask(Action task)
            => task();

        public void EnqueueAppThreadTask(Action task, string reason)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task, RenderThreadJobKind renderThreadKind)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason, RenderThreadJobKind renderThreadKind)
            => task();

        public void ProcessRenderThreadTasks()
        {
        }

        public void MarkRenderFrameReadyForCollect(IRuntimeRenderWindowHost window)
        {
        }

        public bool Preview3DWorldOctree => false;
        public bool Preview2DWorldQuadtree => false;
        public bool HoverOutlineEnabled => true;
        public bool SelectionOutlineEnabled => true;
        public ColorF4 OctreeIntersectedBoundsColor => ColorF4.LightGray;
        public ColorF4 OctreeContainedBoundsColor => ColorF4.Yellow;
        public ColorF4 QuadtreeIntersectedBoundsColor => ColorF4.LightGray;
        public ColorF4 QuadtreeContainedBoundsColor => ColorF4.Yellow;

        public IDisposable? PushTransformId(uint transformId)
            => null;

        public void RecordOctreeSkippedMove()
        {
        }

        public void ProcessGpuPhysicsChainDispatches()
        {
        }

        public void ProcessGpuPhysicsChainCompletions()
        {
        }

        public void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugLine(Vector3 start, Vector3 end, ColorF4 color)
        {
        }

        public void RenderDebugSphere(Vector3 center, float radius, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugQuad(Vector3 center, XREngine.Data.Transforms.Rotations.Rotator rotation, Vector2 extents, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugPoint(Vector3 position, ColorF4 color)
        {
        }

        public void RenderDebugText(Vector3 position, string text, ColorF4 color)
        {
        }

        public void RenderDebugShapes()
        {
        }

        public TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new()
            => null;

        public IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline()
        {
            CreateDefaultRenderPipelineCallCount++;
            return DefaultPipeline;
        }

        public IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind)
            => throw new InvalidOperationException();

        public IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter()
            => new NullWindowScenePanelAdapter();

        public BoundingRectangle? GetScenePanelRenderRegion(IRuntimeRenderWindowHost window)
            => null;

        public bool AllowWindowClose(IRuntimeRenderWindowHost window)
            => true;

        public void RemoveWindow(IRuntimeRenderWindowHost window)
        {
        }

        public void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window)
        {
        }

        public void BeginRenderStatsFrame()
        {
        }

        public void IncrementRenderDrawCalls(int count)
        {
        }

        public void IncrementRenderMultiDrawCalls(int count)
        {
        }

        public void AddRenderTrianglesRendered(int count)
        {
        }

        public void AddRenderGpuBufferAllocation(long bytes)
        {
        }

        public void RemoveRenderGpuBufferAllocation(long bytes)
        {
        }

        public void AddRenderGpuTextureAllocation(long bytes)
        {
        }

        public void RemoveRenderGpuTextureAllocation(long bytes)
        {
        }

        public void AddRenderGpuRenderBufferAllocation(long bytes)
        {
        }

        public void RemoveRenderGpuRenderBufferAllocation(long bytes)
        {
        }

        public bool CanAllocateRenderVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes)
        {
            budgetBytes = long.MaxValue;
            projectedBytes = Math.Max(0L, requestedBytes - Math.Max(0L, existingAllocationBytes));
            return true;
        }

        public void RecordRenderGpuBufferMapped(int count = 1)
        {
        }

        public void RecordRenderGpuReadbackBytes(long bytes)
        {
        }

        public void RecordRenderGpuCpuFallback(int eventCount, int recoveredCommands)
        {
        }

        public void RecordRenderForbiddenGpuFallback(int eventCount = 1)
        {
        }

        public void RecordRenderShadowAtlasSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics)
        {
        }

        public void RecordRenderGpuTransparencyDomainCounts(uint opaqueOrOtherVisible, uint maskedVisible, uint approximateVisible, uint exactVisible)
        {
        }

        public void RecordRenderRendererStateCounter(ERendererProfilerCounter counter, long count = 1)
        {
        }

        public void RecordRenderMemoryBarrier(EMemoryBarrierMask mask)
        {
        }

        public void RecordRenderSceneAssetVisible(
            string? assetType,
            string? assetName,
            string? rendererType,
            string? meshName,
            int materialCount,
            int vertexCount,
            long triangleCount,
            bool gpuEligible,
            string? exclusionReason)
        {
        }

        public void RecordRenderTextureUpload(long bytes, TimeSpan elapsed)
        {
        }

        public void RecordRenderSkinningUpload(
            long matrixBytes,
            long weightBytes,
            int matrices = 0,
            int vertices = 0,
            long coreInfluenceBytes = 0,
            long spillHeaderBytes = 0,
            long spillEntryBytes = 0,
            long skinPaletteBytes = 0,
            int skippedSkinningDispatches = 0,
            int reusedSkinnedOutputBuffers = 0,
            int liveSkinningShaderPermutations = 0,
            long blendshapeActiveListUploadBytes = 0,
            long blendshapeDeltaBytes = 0,
            int blendshapeAuthoredShapeCount = 0,
            int blendshapeActiveShapeCount = 0,
            int blendshapeAffectedVertexCount = 0,
            int skippedBlendshapeDispatches = 0,
            int compactedActiveBlendshapeCount = 0,
            int liveBlendshapeShaderPermutations = 0)
        {
        }

        public void RecordRenderShaderVariant(bool hasSkinning, bool hasBlendshapes, bool hasMorphNormals, bool hasTangents, bool hasInstancing, bool hasStereo)
        {
        }

        public void RecordRenderGpuDrivenBucketWork(int bucketCount, int activeBucketCount, int emptyBucketCount, int materialCount)
        {
        }

        public void RecordRenderGpuDrivenCommandCompaction(long inputCommands, long outputCommands, long culledCommands, long inputBytes, long outputBytes, long overflowCount)
        {
        }

        public void RecordRenderGpuDrivenStageTiming(TimeSpan cull, TimeSpan scatter, TimeSpan compact)
        {
        }

        public void RecordRenderGpuDrivenDelayedDiagnosticReadback(long bytes)
        {
        }

        public void RecordRenderGpuDrivenHiZMode(string? mode)
        {
        }

        public void RecordRenderGpuDrivenHiZPhase(bool enabled, long candidates, long culled)
        {
        }

        public void RecordRenderVisibilityBuffer(int drawCount, long pixelCount, int materialCount, int overflowCount, TimeSpan buildTime, TimeSpan shadeTime)
        {
        }

        public void RecordRenderGpuMeshletStrategyRequested(int eventCount = 1)
        {
        }

        public void RecordRenderGpuMeshletProductionFrame(int eventCount = 1)
        {
        }

        public void RecordRenderGpuMeshletFallback(int eventCount = 1)
        {
        }

        public void RecordRenderGpuMeshletDispatchSkipped(int eventCount = 1)
        {
        }

        public void RecordRenderGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled)
        {
        }

        public void RecordRenderGpuMeshletExpansionOverflow(uint overflowCount)
        {
        }

        public void RecordRenderGpuMeshletBufferBytesResident(long bytes)
        {
        }

        public void RecordRenderGpuMeshletInstrumentation(
            uint visibleMeshletCount,
            uint dispatchedMeshletCount,
            uint taskRecordOverflowCount,
            TimeSpan dispatchTime,
            uint readbackBytes)
        {
        }

        public void RecordRenderGpuMeshletCacheHit(int eventCount = 1)
        {
        }

        public void RecordRenderGpuMeshletCacheMiss(int eventCount = 1)
        {
        }

        public void RecordRenderGpuMeshletCacheStale(int eventCount = 1)
        {
        }

        public void RecordRenderOctreeCollect(int visibleRenderables, int emittedCommands)
        {
        }

        public void RecordRenderCpuSpatialTreeStats(string mode, SpatialTreeOccupancyStats occupancy, long collectTicks)
        {
        }

        public void RecordRenderRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime)
        {
        }

        public void RecordRenderRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime)
        {
        }

        public void RecordRenderSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
        {
        }

        public void RecordRenderSkinnedBoundsRefreshDeferredScheduled()
        {
        }

        public void RecordRenderSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks)
        {
        }

        public void RecordRenderVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
        {
        }

        public void RecordRenderVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
        {
        }

        public void RecordRenderVrRenderSubmitTime(TimeSpan submitTime)
        {
        }

        public void RecordRenderVrXrWaitFrameBlockTime(TimeSpan waitTime)
        {
        }

        public void RecordRenderVrXrEndFrameSubmitTime(TimeSpan submitTime, ulong renderFrameId = 0UL)
        {
        }

        public void RecordRenderVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
        {
        }

        public void RecordRenderVrXrPredictedDisplayLeadTime(double leadTimeMs)
        {
        }

        public void RecordRenderVrXrMissedDeadlineFrame()
        {
        }

        public void RecordRenderVrXrTrackingLossFrame()
        {
        }

        public void RecordRenderVrXrRelocatePredictedTime(TimeSpan elapsed)
        {
        }

        public void RecordRenderVrXrCollectFrustumExpansionDegrees(double degrees)
        {
        }

        public void RecordRenderVrXrPacingThreadIdleTime(TimeSpan elapsed)
        {
        }

        public void RecordRenderVrXrPacingHandoffStall()
        {
        }

        public void RecordRenderVulkanAdhocBarrier(int emittedCount, int redundantCount)
        {
        }

        public void RecordRenderVulkanAllocation(int allocationClass, long bytes)
        {
        }

        public void RecordRenderVulkanBarrierPlannerPass(int imageBarrierCount, int bufferBarrierCount, int queueOwnershipTransfers, int stageFlushes)
        {
        }

        public void RecordRenderVulkanBindChurn(
            int pipelineBinds = 0,
            int descriptorBinds = 0,
            int pushConstantWrites = 0,
            int vertexBufferBinds = 0,
            int indexBufferBinds = 0,
            int pipelineBindSkips = 0,
            int descriptorBindSkips = 0,
            int vertexBufferBindSkips = 0,
            int indexBufferBindSkips = 0)
        {
        }

        public void RecordRenderVulkanDescriptorBindingFailure(
            string? programName,
            string? bindingClass,
            string? bindingName,
            uint set,
            uint binding,
            bool skippedDraw,
            bool skippedDispatch,
            string? message)
        {
        }

        public void RecordRenderVulkanDescriptorFallback(
            string? programName,
            string? bindingClass,
            string? bindingName,
            uint set,
            uint binding,
            int count = 1)
        {
        }

        public void RecordRenderVulkanDescriptorPoolCreate()
        {
        }

        public void RecordRenderVulkanDescriptorPoolDestroy()
        {
        }

        public void RecordRenderVulkanDescriptorPoolReset()
        {
        }

        public void RecordRenderVulkanResourceLifetimeGauges(int liveResourceCount, int trackedDescriptorSetCount, int pendingRetirementCount, long oldestPendingRetirementAgeMilliseconds)
        {
        }

        public void RecordRenderVulkanMeshFrameDataGauges(int arenaChunkCount, long mappedBytes, long reservedBytes, int reservationCount, ulong generation, int recordingLeases, int cachedLeases, int submittedLeases, int activeGenerationCount, int leaseRetainedGenerationCount)
        {
        }

        public void AdjustRenderVulkanMeshDescriptorOwnership(int allocationVariants, int pools, int allocatedSets, int reservedSets)
        {
        }

        public void RecordRenderVulkanDynamicUniformAllocation(long bytes)
        {
        }

        public void RecordRenderVulkanDynamicUniformExhaustion()
        {
        }

        public void RecordRenderVulkanRecordCommandBufferAllocation(long bytes)
        {
        }

        public void RecordRenderVulkanFrameDiagnostics(
            int droppedFrameOps,
            int droppedDrawOps,
            int droppedComputeOps,
            int sceneSwapchainWriters,
            int overlaySwapchainWriters,
            int forcedDiagnosticSwapchainWriters,
            int fboOnlyDrawOps,
            int fboOnlyBlitOps,
            bool missingSceneSwapchainWriters,
            string? firstFailedOpType,
            int firstFailedPassIndex,
            int firstFailedPipelineIdentity,
            int firstFailedViewportIdentity,
            string? firstFailedTargetName,
            string? firstFailedMaterialName,
            string? firstFailedShaderName,
            string? firstFailedMessage,
            string? diagnosticSummary)
        {
        }

        public void RecordRenderVulkanFrameGpuCommandBufferTime(TimeSpan elapsed)
        {
        }

        public void RecordRenderVulkanFrameLifecycleTiming(
            TimeSpan waitFence,
            TimeSpan acquireImage,
            TimeSpan recordCommandBuffer,
            TimeSpan submit,
            TimeSpan trim,
            TimeSpan present,
            TimeSpan total)
        {
        }

        public void RecordRenderVulkanFrameLifecycleDetailTiming(
            TimeSpan sampleTimingQueries,
            TimeSpan drainRetiredResources,
            TimeSpan acquireBridgeSubmit,
            TimeSpan waitSwapchainImage,
            TimeSpan resetDynamicUniformRing)
        {
        }

        public void RecordRenderVulkanFrameOpCensus(
            int totalCount,
            int clearCount,
            int meshDrawCount,
            int indirectDrawCount,
            int meshTaskDispatchCount,
            int blitCount,
            int computeCount,
            int swapchainWriteCount,
            int fboWriteCount,
            int uniquePassCount,
            int uniqueContextCount,
            int uniqueTargetCount)
        {
        }

        public void RecordRenderVulkanCommandBufferCacheOutcome(
            bool reusedClean,
            bool recorded,
            bool forcedDirty,
            bool frameOpSignatureDirty,
            bool plannerDirty,
            bool profilerDirty,
            string? dirtyReason)
        {
        }

        public void RecordRenderVulkanCommandBuffersDirty(string? reason)
        {
        }

        public void RecordRenderVulkanCommandChainMetrics(
            int chainsScheduled,
            int chainsRecorded,
            int chainsReused,
            int chainsFrameDataRefreshed,
            int volatileChainsRecorded,
            int primaryCommandBuffersReused,
            int primaryCommandBuffersRecorded,
            int visibilityPackets,
            int renderPackets,
            int secondaryCommandBuffers,
            TimeSpan chainWorkerRecordTime,
            TimeSpan renderThreadWaitForWorkersTime,
            string? firstStructuralDirtyReason,
            string? firstDescriptorGenerationMismatch,
            string? firstResourcePlanRevisionMismatch)
        {
        }

        public void RecordRenderVulkanGpuDrivenStageTiming(int stage, TimeSpan elapsed)
        {
        }

        public void RecordRenderVulkanIndirectBatchMerge(int requestedBatchCount, int mergedBatchCount)
        {
        }

        public void RecordRenderVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0)
        {
        }

        public void RecordRenderVulkanIndirectRecordingMode(bool usedSecondary, bool usedParallel, int opCount)
        {
        }

        public void RecordRenderVulkanIndirectSubmission(bool usedCountPath, bool usedLoopFallback, int apiCalls, uint submittedDraws)
        {
        }

        public void RecordRenderVulkanOomFallback()
        {
        }

        public void RecordRenderVulkanPipelineCacheLookup(bool cacheHit)
        {
        }

        public void RecordRenderVulkanPipelineCacheMiss(string? summary)
        {
        }

        public void RecordRenderVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode)
        {
        }

        public void RecordRenderVulkanQueueSubmit()
        {
        }

        public void RecordRenderVulkanPresentResult(int result, bool accepted)
        {
        }

        public void RecordRenderVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount)
        {
        }

        public void RecordRenderVulkanRetiredResourceDrain(
            int descriptorPools,
            int descriptorSets,
            int commandBuffers,
            int queryPools,
            int bufferViews,
            int pipelines,
            int framebuffers,
            int buffers,
            int bufferMemories,
            int images,
            int imageViews,
            int samplers,
            int imageMemories,
            long imageBytes)
        {
        }

        public void RecordRenderVulkanValidationMessage(bool isError, string? message)
        {
        }

        public bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        {
            return false;
        }

        public void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
        {
        }

        public void DestroyObjectsForRenderer(IRuntimeRendererHost renderer)
        {
        }

        public bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport)
            => false;

        public IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride()
            => null;

        public void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline)
        {
        }

        public void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
        {
        }

        public int GetBytesPerPixel(ESizedInternalFormat format)
            => 4;

        public int GetBytesPerPixel(ERenderBufferStorage storage)
            => 4;

        public void AddFrameBufferBandwidth(long totalBytes)
        {
        }

        public void DispatchCompute(XRRenderProgram program, uint groupCountX, uint groupCountY, uint groupCountZ)
        {
        }

        public bool TryBlitFrameBufferToFrameBuffer(
            XRFrameBuffer sourceFrameBuffer,
            XRFrameBuffer destinationFrameBuffer,
            EReadBufferMode readBuffer,
            bool colorBit,
            bool depthBit,
            bool stencilBit,
            bool linearFilter)
            => false;

        public bool TryBlitViewportToFrameBuffer(
            IRuntimeViewportGrabSource viewport,
            XRFrameBuffer framebuffer,
            EReadBufferMode readBuffer,
            bool colorBit,
            bool depthBit,
            bool stencilBit,
            bool linearFilter)
            => false;
    }

    private sealed class NullWindowScenePanelAdapter : IRuntimeWindowScenePanelAdapter
    {
        public XRTexture2D? Texture => null;
        public XRFrameBuffer? FrameBuffer => null;

        public void Dispose()
        {
        }

        public void InvalidateResources()
        {
        }

        public void InvalidateResourcesImmediate()
        {
        }

        public void OnFramebufferResized(IRuntimeRenderWindowHost window, int framebufferWidth, int framebufferHeight)
        {
        }

        public bool TryRenderScenePanelMode(IRuntimeRenderWindowHost window)
            => false;

        public void EndScenePanelMode(IRuntimeRenderWindowHost window)
        {
        }
    }

    private sealed class TestRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain()
            => new(this);

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => [];
    }

    private sealed class SinglePassRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain()
            => new(this);

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => new()
            {
                [10] = null,
            };
    }

    private sealed class TwoPassRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain()
            => new(this);

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => new()
            {
                [20] = null,
                [30] = null,
            };
    }
}
