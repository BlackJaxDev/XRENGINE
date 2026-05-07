using System.Collections;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Scene;

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
        public long ElapsedTicks => 0L;
        public float ElapsedTime => 0.0f;
        public double UpdateDeltaSeconds => 0.0;
        public long LastUpdateTimestampTicks => 0L;
        public double RenderDeltaSeconds => 0.0;
        public long LastRenderTimestampTicks => 0L;
        public long TrackedVramBytes => 0L;
        public long TrackedVramBudgetBytes => long.MaxValue;
        public bool EnableGpuIndirectDebugLogging => false;
        public TextureRuntimeLogMode TextureLogMode => TextureRuntimeLogMode.Disabled;
        public double TextureSlowCpuDecodeResizeMilliseconds => 5.0;
        public double TextureSlowMipBuildMilliseconds => 5.0;
        public double TextureSlowUploadChunkMilliseconds => 2.0;
        public double TextureSlowTransitionMilliseconds => 8.0;
        public double TextureSlowQueueWaitMilliseconds => 100.0;
        public double TextureUploadFrameBudgetMilliseconds => 2.0;
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
        public bool ForwardDepthPrePassEnabled => true;
        public bool ForwardPrePassSharesGBufferTargets => true;
        public bool ProvidesShadowAtlasSettings => false;
        public bool UseSpotShadowAtlas => true;
        public bool UseDirectionalShadowAtlas => true;
        public bool UsePointShadowAtlas => true;
        public uint ShadowAtlasPageSize => 4096u;
        public int MaxShadowAtlasPages => 1;
        public long MaxShadowAtlasMemoryBytes => 0L;
        public int MaxShadowTilesRenderedPerFrame => 16;
        public float MaxShadowRenderMilliseconds => 2.0f;
        public uint MinShadowAtlasTileResolution => 128u;
        public uint MaxShadowAtlasTileResolution => 4096u;
        public bool IsWindowScenePanelPresentationEnabled => false;
        public int ScenePanelResizeDebounceMs => 100;
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

        public void EnqueueAppThreadTask(Action task)
            => task();

        public void EnqueueAppThreadTask(Action task, string reason)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason)
            => task();

        public void ProcessRenderThreadTasks()
        {
        }

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
