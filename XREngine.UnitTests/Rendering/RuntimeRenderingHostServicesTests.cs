using System.Collections;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeRenderingHostServicesTests
{
    private IRuntimeRenderingHostServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeRenderingHostServices.Current;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeRenderingHostServices.Current = _previousServices ?? new TestRuntimeRenderingHostServices();
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
    public void ImportedTextureStreamingScope_SuppressesTextureCacheWarmup()
    {
        XRTexture2D.ShouldSuppressTextureStreamingCacheWarmup.ShouldBeFalse();

        using (XRTexture2D.EnterImportedTextureStreamingScope())
            XRTexture2D.ShouldSuppressTextureStreamingCacheWarmup.ShouldBeTrue();

        XRTexture2D.ShouldSuppressTextureStreamingCacheWarmup.ShouldBeFalse();
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

        public IDisposable? StartProfileScope(string? scopeName)
            => null;

        public bool AllowShaderPipelines => false;
        public bool EnableExactTransparencyTechniques => false;
        public bool UseInterleavedMeshBuffer => false;
        public bool UseIntegerUniformsInShaders => false;
        public bool RemapBlendshapeDeltas => false;
        public bool AllowBlendshapes => true;
        public bool PopulateVertexDataInParallel => true;
        public bool ProcessMeshImportsAsynchronously => false;
        public bool AllowSkinning => true;
        public bool OptimizeSkinningTo4Weights => false;
        public bool OptimizeSkinningWeightsIfPossible => false;
        public bool IsRenderThread => true;
        public bool IsRendererActive => false;
        public bool IsShadowPass => false;
        public bool IsStereoPass => false;
        public bool IsSceneCapturePass => false;
        public bool RenderCullingVolumesEnabled => false;
        public bool IsNvidia => false;
        public string AssetFileExtension => "asset";
        public string? TextureFallbackPath => null;
        public XRMaterial? InvalidMaterial => null;
        public Vector3 DefaultLuminance => Vector3.One;
        public double RenderDeltaSeconds => 0.0;
        public long LastRenderTimestampTicks => 0L;
        public long TrackedVramBytes => 0L;
        public long TrackedVramBudgetBytes => long.MaxValue;
        public bool EnableGpuIndirectDebugLogging => false;
        public ETwoPlayerPreference TwoPlayerViewportPreference => ETwoPlayerPreference.SplitHorizontally;
        public EThreePlayerPreference ThreePlayerViewportPreference => EThreePlayerPreference.PreferFirstPlayer;
        public RuntimeGraphicsApiKind CurrentRenderBackend => RuntimeGraphicsApiKind.Unknown;
        public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => null;
        public IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext => null;
        public bool IsPlayModeTransitioning => false;
        public string PlayModeStateName => "Stopped";
        public EAntiAliasingMode DefaultAntiAliasingMode => EAntiAliasingMode.None;
        public uint DefaultMsaaSampleCount => 1u;
        public bool DefaultOutputHDR => false;
        public float DefaultTsrRenderScale => 1.0f;
        public bool IsWindowScenePanelPresentationEnabled => false;
        public bool ForceFullViewport => false;
        public bool RenderWindowsWhileInVR => false;
        public bool EnableVrFoveatedViewSet => false;
        public bool IsInVR => false;
        public bool IsOpenXRActive => false;
        public bool VrMirrorComposeFromEyeTextures => false;
        public Vector2 VrFoveationCenterUv => new(0.5f, 0.5f);
        public float VrFoveationInnerRadius => 0.35f;
        public float VrFoveationOuterRadius => 0.85f;
        public Vector3 VrFoveationShadingRates => new(1.0f, 0.7f, 0.5f);
        public float VrFoveationVisibilityMargin => 0.05f;
        public bool VrFoveationForceFullResForUiAndNearField => true;
        public float VrFoveationFullResNearDistanceMeters => 1.5f;
        public bool ShouldForceDebugOpaquePipeline => false;

        public RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window)
            => RuntimeGraphicsApiKind.Unknown;

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

        public void EnqueueRenderThreadTask(Action task, string reason)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task)
            => task();

        public IDisposable? PushTransformId(uint transformId)
            => null;

        public void RecordOctreeSkippedMove()
        {
        }

        public void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
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

        public void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        {
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
}
