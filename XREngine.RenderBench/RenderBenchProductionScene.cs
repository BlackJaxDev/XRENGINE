using System.Numerics;
using System.Diagnostics;
using System.Text;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Execution;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.RenderBench;

/// <summary>
/// A window-free, real-world scene used by the production Vulkan benchmark lanes.
/// It deliberately drives the ordinary world/viewport lifecycle inside an explicit target submission.
/// </summary>
public sealed class RenderBenchProductionScene : IDisposable
{
    private const int MaximumCandidates = 256;
    private const int FirstHeavyCandidateId = 7;
    private const int LastHeavyCandidateId = 70;
    private static readonly byte[] s_heavyColorLevels = [16, 64, 128, 192, 240];
    /// <summary>
    /// Fixed camera post-process profile used by the raw-albedo color oracle.
    /// This remains in the normal DefaultRenderPipeline; it merely removes
    /// adaptive and color-changing stages from the benchmark fixture.
    /// </summary>
    public const string ColorOraclePostProcessIdentity =
        "ManualExposure1-Gamma1-LinearTonemap-BloomAoAtmosphereOff-"
        + "VignetteChromaticLensDepthFogVolumetricFogMotionBlurDofOff";
    private readonly IDisposable _servicesLease;
    private readonly RenderBenchWorkSchedulerScope _workSchedulerScope;
    private readonly int _previousRenderThreadId;
    private readonly int _renderThreadOwnerId;
    private readonly uint _viewportWidth;
    private readonly uint _viewportHeight;
    private readonly Dictionary<int, SceneNode> _candidateNodes = new(MaximumCandidates);
    private readonly SceneNode _wallNode;
    private readonly XRMaterial _wallMaterial;
    private readonly HashSet<XRMaterial> _occluderMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly List<SceneNode> _fixtureOccluderNodes = [];
    // The masked fixture owns these tiny procedural textures. They are published through the
    // material table during the first real explicit production frame, before collection seals it.
    private readonly List<(XRTexture Texture, string Semantic)> _fixtureMaterialTextures = [];
    private SceneNode? _maskedCoverageNode;
    private SceneNode? _maskedOpaqueControlNode;
    private readonly Transform _cameraTransform;
    private readonly XRMesh _boxMesh;
    private readonly AABB _boxBounds = new(new Vector3(-0.5f), new Vector3(0.5f));
    private readonly XRMaterial[] _candidateMaterials;
    private readonly Dictionary<XRMaterial, int> _candidateIdsByMaterial = new(ReferenceEqualityComparer.Instance);
    private VulkanExplicitTargetRendererHost? _host;
    private IDisposable? _explicitBackendRegistration;
    private XRViewport? _viewport;
    private bool _fixtureMaterialTexturesPrepared;
    private bool _disposed;

    public RenderBenchProductionScene(uint width, uint height, bool reverseDepth, EOcclusionCullingMode occlusionMode)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        _servicesLease = RuntimeRenderingBootstrap.InstallEngineHostServices(new RuntimeApplicationProfile(
            "RenderBenchProduction",
            RuntimeAdapterProfile.All,
            AllowsWindows: false,
            AllowsVr: false,
            RegisterRendererBackends: false));
        _workSchedulerScope = RenderBenchWorkSchedulerScope.EnsureInstalled();
        World = new XRWorld("RenderBench Production Scene");
        XRScene scene = new("RenderBench Production Scene Root");
        SceneNode root = new("RenderBench Production Root");
        scene.RootNodes.Add(root);
        World.Scenes.Add(scene);

        _boxMesh = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));
        _candidateMaterials = CreateCandidateMaterials();
        _wallMaterial = CreateMaterial("Occluder Wall", new ColorF4(0.35f, 0.35f, 0.35f, 1.0f));
        _occluderMaterials.Add(_wallMaterial);
        // Leave a genuine covered interior across several 64-pixel Hi-Z tiles at
        // odd extents. A thin projected wall correctly cannot reject footprints
        // whose conservative tile coverage reaches the background.
        _wallNode = AddBox(root, "Occluder Wall", new Vector3(0.0f, 2.0f, 2.0f), new Vector3(8.0f, 8.0f, 0.3f),
            _wallMaterial);
        _fixtureOccluderNodes.Add(_wallNode);
        AddCandidate(1, new Vector3(6.0f, 2.0f, 4.0f), Vector3.One, _candidateMaterials[0]);
        AddCandidate(2, new Vector3(-3.0f, 1.0f, 6.0f), Vector3.One, _candidateMaterials[1]);
        AddCandidate(3, new Vector3(-1.5f, 3.0f, 6.0f), Vector3.One, _candidateMaterials[2]);
        AddCandidate(4, new Vector3(0.0f, 1.0f, 6.0f), Vector3.One, _candidateMaterials[3]);
        AddCandidate(5, new Vector3(1.5f, 3.0f, 6.0f), Vector3.One, _candidateMaterials[4]);
        AddCandidate(6, new Vector3(3.0f, 1.0f, 6.0f), Vector3.One, _candidateMaterials[5]);

        _cameraTransform = new Transform();
        Camera = new XRCamera(_cameraTransform)
        {
            DepthMode = reverseDepth ? XRCamera.EDepthMode.Reversed : XRCamera.EDepthMode.Normal,
            RenderPipeline = new DefaultRenderPipeline(stereo: false),
            AntiAliasingModeOverride = EAntiAliasingMode.None,
            OutputHDROverride = false,
        };
        ConfigureColorOraclePostProcessing();
        SetCamera(new Vector3(0.0f, 2.0f, -8.0f), new Vector3(0.0f, 2.0f, 3.0f));

        if (RuntimeWorldHostServices.Current is not EngineRuntimeWorldHostServices hosts)
            throw new InvalidOperationException("RenderBench requires the Bootstrap Engine world host.");
        WorldHost = hosts.GetOrCreateHost(World);
        WorldHost.BeginEditModeAsync().GetAwaiter().GetResult();
        GPUScene = WorldHost.RenderWorld.VisualScene.GPUCommands;
        RuntimeEngine.Rendering.Settings.GpuOcclusionCullingMode = occlusionMode;
        _renderThreadOwnerId = Environment.CurrentManagedThreadId;
        _previousRenderThreadId = RuntimeEngine.RenderThreadId;
        if (_previousRenderThreadId is not 0 && _previousRenderThreadId != _renderThreadOwnerId)
        {
            throw new InvalidOperationException(
                $"RenderBench production submission requires the caller to own the render lane; " +
                $"it is currently assigned to thread {_previousRenderThreadId}.");
        }
        RuntimeEngine.AssignRenderThread(_renderThreadOwnerId);
    }

    public RenderBenchProductionScene(RenderBenchOptions options, EOcclusionCullingMode occlusionMode)
        : this(options.Width, options.Height, options.ScenarioDepth.Equals("reversed", StringComparison.OrdinalIgnoreCase), occlusionMode)
    {
        try
        {
            // Register only the explicitly selected leaf. This installs its
            // streaming/compiler services, without enabling windows or XR.
            _explicitBackendRegistration = VulkanRendererBackendModule.Register(RuntimeRenderingHostServices.Factories.RendererBackends);
            _host = new VulkanExplicitTargetRendererHost(new PresentationlessRenderTarget(
                options.Width, options.Height, options.Layers, options.FrameSlots, options.Samples,
                options.ColorFormat, options.DepthFormat));
            ((DefaultRenderPipeline)Camera.RenderPipeline).DeferredDebugView = DefaultRenderPipeline.DeferredDebugViewMode.RawAlbedo;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public XRWorld World { get; }
    public RuntimeWorldHost WorldHost { get; }
    public GPUScene GPUScene { get; }
    /// <summary>
    /// The real production viewport, materialized only after the first explicit target output scope is active.
    /// </summary>
    public XRViewport Viewport => _viewport ?? throw new InvalidOperationException(
        "The production viewport is created by the first explicit target submission, not during scene construction.");
    public XRCamera Camera { get; }
    public VulkanExplicitTargetRendererHost Host => _host ?? throw new InvalidOperationException("The production scene has no explicit Vulkan target.");
    public IReadOnlyDictionary<int, SceneNode> CandidateRenderIdentity => _candidateNodes;
    public long SubmittedStepCount { get; private set; }
    public long LastCollectGeneration { get; private set; }
    /// <summary>Creates a real sampled deferred fixture draw before first collection.</summary>
    public XRMaterial AddMaterialScenarioFixture(XRTexture2D albedo)
    {
        ArgumentNullException.ThrowIfNull(albedo);
        if (SubmittedStepCount != 0)
            throw new InvalidOperationException("Material fixtures must be authored before the first production submission.");
        _fixtureMaterialTextures.Add((albedo, "Albedo"));
        XRMaterial material = XRMaterial.CreateLitTextureMaterial(albedo, deferred: true);
        material.Name = "Phase53 Material Fixture";
        material.RenderOptions.CullMode = ECullMode.None;
        AddCandidate(200, new Vector3(-5.0f, 2.0f, 5.0f), new Vector3(2.0f), material);
        return material;
    }
    /// <summary>Cold harness retries; an unsubmitted Pending plan is never reported as a frame.</summary>
    public int PipelineAdmissionRetryCount { get; private set; }
    public double PipelineAdmissionRetryMilliseconds { get; private set; }
    /// <summary>Actual coverage state selected for the masked fixture frame.</summary>
    public string MaskedCoverageMode { get; private set; } = "not-applicable";

    /// <summary>
    /// Returns the production opaque pass that owns the real deferred material-table rows.
    /// The caller may only use its cold diagnostic publication APIs after a completed submission.
    /// </summary>
    public GPURenderPassCollection GetMaterialScenarioOpaquePass()
    {
        XRViewport viewport = Viewport;
        if (!viewport.RenderPipelineInstance.MeshRenderCommands.TryGetGpuPass(
                (int)EDefaultRenderPass.OpaqueDeferred, out GPURenderPassCollection? pass))
        {
            throw new InvalidOperationException("The production pipeline has no opaque GPU pass for material diagnostics.");
        }

        return pass;
    }

    /// <summary>
    /// Adds a deterministic real-scene workload before its first collection.
    /// The original six palette candidates always remain available as image-oracle anchors.
    /// </summary>
    public void ConfigureScenarioWorkload(string workload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        if (!RenderBenchScenarioWorkloads.IsKnown(workload))
            throw new ArgumentOutOfRangeException(nameof(workload));

        SetWallActive(workload is not (RenderBenchScenarioWorkloads.OpenStatic or RenderBenchScenarioWorkloads.MaskedStatic or RenderBenchScenarioWorkloads.MaskedMoving));
        if (workload is RenderBenchScenarioWorkloads.HeavyStatic or RenderBenchScenarioWorkloads.HeavyMovingCut)
            AddHeavyOcclusionFixture();
        if (RenderBenchScenarioWorkloads.IsMasked(workload))
            AddMaskedFixture();
    }

    private void ConfigureColorOraclePostProcessing()
    {
        ColorGradingSettings colorGrading = RequirePostProcessSettings<ColorGradingSettings>();
        colorGrading.AutoExposure = false;
        colorGrading.Exposure = 1.0f;
        colorGrading.Gamma = 1.0f;

        // There is no explicit None member. Linear with manual unit exposure is identity.
        RequirePostProcessSettings<TonemappingSettings>().Tonemapping = ETonemappingType.Linear;
        RequirePostProcessSettings<BloomSettings>().Enabled = false;
        RequirePostProcessSettings<AmbientOcclusionSettings>().Enabled = false;
        RequirePostProcessSettings<AtmosphericScatteringSettings>().Enabled = false;

        // These are currently disabled by default, but pinning them keeps the fixture
        // deterministic if a DefaultRenderPipeline stage default changes.
        RequirePostProcessSettings<VignetteSettings>().Enabled = false;
        RequirePostProcessSettings<ChromaticAberrationSettings>().Enabled = false;
        RequirePostProcessSettings<LensDistortionSettings>().Mode = ELensDistortionMode.None;
        RequirePostProcessSettings<FogSettings>().DepthFogIntensity = 0.0f;
        RequirePostProcessSettings<VolumetricFogSettings>().Enabled = false;
        RequirePostProcessSettings<MotionBlurSettings>().Enabled = false;
        RequirePostProcessSettings<DepthOfFieldSettings>().Enabled = false;
    }

    private TSettings RequirePostProcessSettings<TSettings>() where TSettings : class
    {
        if (Camera.GetPostProcessStageState<TSettings>()?.TryGetBacking(out TSettings? settings) == true && settings is not null)
            return settings;

        throw new InvalidOperationException(
            $"The DefaultRenderPipeline color-oracle fixture is missing its {typeof(TSettings).Name} stage.");
    }

    public void SetWallActive(bool active) => _wallNode.IsActiveSelf = active;

    /// <summary>Enables or removes every real fixture occluder for the eligibility control.</summary>
    public void SetFixtureOccludersActive(bool active)
    {
        foreach (SceneNode node in _fixtureOccluderNodes)
            node.IsActiveSelf = active;
    }

    /// <summary>Switches between true cutout coverage and an otherwise-identical opaque control panel.</summary>
    public void SetMaskedCoverageOpaqueControl(bool opaqueControl)
    {
        if (_maskedCoverageNode is null || _maskedOpaqueControlNode is null)
        {
            MaskedCoverageMode = "not-applicable";
            return;
        }

        _maskedCoverageNode.IsActiveSelf = !opaqueControl;
        _maskedOpaqueControlNode.IsActiveSelf = opaqueControl;
        MaskedCoverageMode = opaqueControl ? "opaque-control" : "cutout";
    }

    /// <summary>Marks the eligibility lane after all fixture occluders have been removed.</summary>
    public void SetMaskedCoverageEligibilityControl()
    {
        if (_maskedCoverageNode is null || _maskedOpaqueControlNode is null)
            return;
        _maskedCoverageNode.IsActiveSelf = false;
        _maskedOpaqueControlNode.IsActiveSelf = false;
        MaskedCoverageMode = "eligibility-control";
    }

    public void SetCamera(in Vector3 position, in Vector3 target)
    {
        _cameraTransform.Translation = position;
        _cameraTransform.LookAt(target);
        _cameraTransform.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();
    }

    public void SetCandidate(int id, in Vector3 position, in Vector3 scale)
    {
        if (!_candidateNodes.TryGetValue(id, out SceneNode? node))
            throw new ArgumentOutOfRangeException(nameof(id), id, "The candidate has not been created.");
        Transform transform = node.GetTransformAs<Transform>(false)
            ?? throw new InvalidOperationException("Candidate node has no transform.");
        transform.Translation = position;
        transform.Scale = scale;
    }

    public void SetCandidates(ReadOnlySpan<RenderBenchProductionSceneCandidatePose> candidates)
    {
        foreach (ref readonly RenderBenchProductionSceneCandidatePose candidate in candidates)
            SetCandidate(candidate.Id, candidate.Position, candidate.Scale);
    }

    /// <summary>Adds a stable candidate identity for capacity lanes; the first six retain the oracle palette.</summary>
    public void AddCandidate(int id, in Vector3 position, in Vector3 scale)
    {
        if ((uint)id >= MaximumCandidates)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Candidate ids must be in [0, {MaximumCandidates - 1}].");
        if (_candidateNodes.ContainsKey(id))
            throw new InvalidOperationException($"Candidate {id} already exists.");
        AddCandidate(id, position, scale, CreateCandidateMaterial(id));
    }

    /// <summary>Runs one complete real collect/swap/render lifecycle within one production submission.</summary>
    public VulkanExplicitProductionSubmissionReceipt SubmitStep(
        double fixedDelta,
        VulkanExplicitProductionBufferStressProbeRequest? probeRequest = null)
    {
        long retryStart = 0;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                VulkanExplicitProductionSubmissionReceipt receipt = SubmitStepAttempt(fixedDelta, probeRequest);
                if (retryStart != 0)
                    PipelineAdmissionRetryMilliseconds += Stopwatch.GetElapsedTime(retryStart).TotalMilliseconds;
                return receipt;
            }
            catch (VulkanExplicitProductionAdmissionPendingException) when (attempt < 4096)
            {
                retryStart = retryStart == 0 ? Stopwatch.GetTimestamp() : retryStart;
                PipelineAdmissionRetryCount++;
                if (Stopwatch.GetElapsedTime(retryStart) > TimeSpan.FromSeconds(5))
                    throw;
                // This is the cold harness coordinator, outside production
                // admission. Give the real background compiler time to finish,
                // then rebuild a fresh plan instead of losing its dispatch.
                if (attempt < 16)
                    Thread.Yield();
                else
                    Thread.Sleep(1);
            }
        }
    }

    private VulkanExplicitProductionSubmissionReceipt SubmitStepAttempt(
        double fixedDelta,
        VulkanExplicitProductionBufferStressProbeRequest? probeRequest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!RuntimeEngine.IsRenderThread || Environment.CurrentManagedThreadId != _renderThreadOwnerId)
        {
            throw new InvalidOperationException(
                "RenderBench production submission must remain on the explicit render-lane owner thread.");
        }
        VulkanExplicitTargetRendererHost host = Host;
        EngineTimer.ExplicitFrameScope frame = Engine.Time.Timer.BeginExplicitFrame((float)fixedDelta);
        try
        {
            Action<RenderFrameOutputDescription> submitProductionFrame = _ =>
            {
                // A window normally drives this boundary. Explicit production
                // owns it here so worker-prepared uploads and other scheduled
                // render work advance before collection captures dependencies.
                RuntimeEngine.ProcessMainThreadTasks();
                XRViewport viewport = EnsureViewport();
                WorldHost.CoreWorld.Update();
                WorldHost.CoreWorld.ProcessDirtyTransforms(ELoopType.Sequential);
                if (!viewport.RenderPipelineInstance.TryPrepareExplicitFrameResources(
                    WorldHost.RenderWorld.VisualScene,
                    Camera,
                    viewport))
                {
                    throw new InvalidOperationException(
                        "The explicit production frame could not commit its resource generation before collection. " +
                        CreateRenderDeclinedDiagnostic(viewport));
                }
                PrepareFixtureMaterialTexturesForFirstProductionFrame();
                PrepareExplicitHiZCoarseTiles(viewport);
                LastCollectGeneration = frame.RequestCollect();
                WorldHost.RenderWorld.GlobalPreCollectVisible();
                WorldHost.RenderWorld.GlobalCollectVisible();
                viewport.CollectVisible();
                frame.CompleteCollect();
                WorldHost.RenderWorld.GlobalSwapBuffers();
                viewport.SwapBuffers();
                frame.PublishCollect();
                frame.ConsumePublishedCollect();
                PrepareOpaquePass(viewport);
                bool recorded = RenderViewportFrame(frame, viewport);
                if (!recorded)
                    throw new InvalidOperationException(CreateRenderDeclinedDiagnostic(viewport));
            };
            VulkanExplicitProductionSubmissionReceipt receipt = probeRequest is { } request
                ? host.SubmitProductionFrame(submitProductionFrame, request)
                : host.SubmitProductionFrame(submitProductionFrame);
            // Completion is intentionally outside the native submission callback: it must cover
            // the accepted frame's real production recording/submission lifetime.
            frame.CompleteRenderFrame();
            frame.MarkPresented();
            SubmittedStepCount++;
            return receipt;
        }
        catch
        {
            frame.AbortRenderFrame();
            throw;
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void PrepareOpaquePass(XRViewport viewport)
    {
        if (!viewport.RenderPipelineInstance.MeshRenderCommands.TryGetGpuPass(
                (int)EDefaultRenderPass.OpaqueDeferred, out GPURenderPassCollection? pass))
            throw new InvalidOperationException("The production pipeline has no opaque GPU pass.");
        pass.MeshSubmissionStrategy = EMeshSubmissionStrategy.GpuIndirectZeroReadback;
        pass.MeshPrimitivePathPreference = EMeshPrimitivePathPreference.TraditionalOnly;
        if (!pass.TryPrepareResources(GPUScene, allowAsyncBackendCompile: false))
            throw new InvalidOperationException("The opaque GPU pass could not prepare its production programs before recording.");
    }

    /// <summary>
    /// Publishes fixture-owned sampled textures before the first production collect can freeze a
    /// material-table row. This runs inside <see cref="VulkanExplicitTargetRendererHost.SubmitProductionFrame"/>,
    /// whose explicit cold-preparation scope permits synchronous first-use resource preparation.
    /// It does not submit a warm-up frame or discard any production provenance.
    /// </summary>
    private void PrepareFixtureMaterialTexturesForFirstProductionFrame()
    {
        if (_fixtureMaterialTexturesPrepared || _fixtureMaterialTextures.Count == 0)
            return;

        if (Host is not IMaterialTableBackendCapability materialTable)
        {
            throw new InvalidOperationException(
                "The explicit production host does not expose the material-table capability required by the fixture textures.");
        }

        if (!materialTable.TryEnsureMaterialTextureTable(out string tableReason))
        {
            throw new InvalidOperationException(
                $"The fixture material texture table could not be prepared: {tableReason}");
        }

        foreach ((XRTexture texture, string semantic) in _fixtureMaterialTextures)
        {
            // Descriptor-table resolution deliberately has lookup-only access to the Vulkan
            // wrapper registry. Create the fixture wrapper through the explicit host renderer
            // while this production cold-preparation scope owns the factory authority.
            if (Host.Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is null)
            {
                throw new InvalidOperationException(
                    $"Fixture texture '{texture.Name ?? "<unnamed>"}' ({semantic}) could not create its Vulkan wrapper.");
            }

            _ = materialTable.ResolveMaterialTextureReference(texture, semantic);
        }

        // Resolving a first-use texture dirties its descriptor slot. Publish those slots before
        // re-resolving: a Pending result after this point is a real readiness failure, not a
        // license to submit an incomplete first frame.
        materialTable.FlushMaterialTextureTableUpdates();
        foreach ((XRTexture texture, string semantic) in _fixtureMaterialTextures)
        {
            var resolution = materialTable.ResolveMaterialTextureReference(texture, semantic);
            if (!resolution.IsReady)
            {
                throw new InvalidOperationException(
                    $"Fixture texture '{texture.Name ?? "<unnamed>"}' ({semantic}) was not ready " +
                    $"for the first production frame: {resolution.Status}: {resolution.Reason}");
            }
        }

        _fixtureMaterialTexturesPrepared = true;
    }

    private static void PrepareExplicitHiZCoarseTiles(XRViewport viewport)
    {
        if (!viewport.RenderPipelineInstance.MeshRenderCommands.TryGetGpuPass(
                (int)EDefaultRenderPass.OpaqueDeferred, out GPURenderPassCollection? pass))
        {
            throw new InvalidOperationException("The production pipeline has no opaque GPU pass for explicit Hi-Z preparation.");
        }

        if (!pass.TryPrepareExplicitHiZCoarseTiles(viewport.RenderPipelineInstance))
        {
            throw new InvalidOperationException(
                "The explicit production frame could not prepare the bounded coarse Hi-Z capture before collection.");
        }
    }

    private bool RenderViewportFrame(EngineTimer.ExplicitFrameScope frame, XRViewport viewport)
    {
        frame.BeginRenderFrame();
        Exception? frameFailure = null;
        bool recorded = false;
        try
        {
            WorldHost.RenderWorld.GlobalPreRender();
            recorded = viewport.TryRender(null);
        }
        catch (Exception exception)
        {
            frameFailure = exception;
        }

        try
        {
            WorldHost.RenderWorld.GlobalPostRender();
        }
        catch when (frameFailure is not null)
        {
            // Preserve the production failure; post-render teardown is still attempted.
        }

        if (frameFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(frameFailure).Throw();
        return recorded;
    }

    private XRViewport EnsureViewport()
    {
        if (_viewport is not null)
            return _viewport;

        // VulkanExplicitTargetRendererHost has already pushed the presentation target at this point.
        // Creating the viewport earlier would prepare an external-window resource key and discard it on frame one.
        _viewport = new XRViewport(null, _viewportWidth, _viewportHeight)
        {
            WorldInstanceOverride = WorldHost.RenderWorld,
            Camera = Camera,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            MeshSubmissionStrategyOverride = EMeshSubmissionStrategy.GpuIndirectZeroReadback,
        };
        return _viewport;
    }

    private static string CreateRenderDeclinedDiagnostic(XRViewport viewport)
    {
        XRRenderPipelineInstance pipeline = viewport.RenderPipelineInstance;
        List<LogEntry> entries = Debug.GetConsoleEntries();
        int firstRecentEntry = Math.Max(0, entries.Count - 8);
        var summary = new StringBuilder(768)
            .Append("The production viewport did not record a render frame. LastResourceGenerationFailure=")
            .Append(pipeline.LastResourceGenerationFailure ?? "<none>")
            .Append("; ActiveGeneration=")
            .Append(pipeline.ActiveGeneration?.Key.ToString() ?? "<none>")
            .Append("; PendingGeneration=")
            .Append(pipeline.PendingGeneration?.Key.ToString() ?? "<none>")
            .Append("; recentDebug=[");
        for (int index = firstRecentEntry; index < entries.Count; index++)
        {
            if (index != firstRecentEntry)
                summary.Append(" | ");
            LogEntry entry = entries[index];
            summary.Append(entry.Category).Append(':').Append(entry.Message);
        }
        return summary.Append(']').ToString();
    }

    public bool TryResolveCandidateDrawId(uint drawId, out int candidateId)
    {
        candidateId = 0;
        if (!GPUScene.TryGetSourceCommand(drawId, out IRenderCommandMesh? sourceCommand) || sourceCommand is null)
            return false;
        IRenderCommandMesh command = sourceCommand;
        XRMaterial? material = command.MaterialOverride ?? command.Mesh?.Material;
        return material is not null && _candidateIdsByMaterial.TryGetValue(material, out candidateId);
    }

    /// <summary>Identifies only fixture occluders, never arbitrary unmapped scene draws.</summary>
    public bool TryIsOccluderDrawId(uint drawId)
        => GPUScene.TryGetSourceCommand(drawId, out IRenderCommandMesh? command) && command is not null &&
           (command.MaterialOverride ?? command.Mesh?.Material) is XRMaterial material && _occluderMaterials.Contains(material);

    private void AddHeavyOcclusionFixture()
    {
        if (_candidateNodes.ContainsKey(7))
            return;

        SceneNode root = World.Scenes[0].RootNodes[0];
        XRMaterial leftOccluder = CreateMaterial("Heavy Occluder Left", new ColorF4(0.30f, 0.30f, 0.30f, 1.0f));
        XRMaterial rightOccluder = CreateMaterial("Heavy Occluder Right", new ColorF4(0.40f, 0.40f, 0.40f, 1.0f));
        _occluderMaterials.Add(leftOccluder);
        _occluderMaterials.Add(rightOccluder);
        _fixtureOccluderNodes.Add(AddBox(root, "Heavy Occluder Left", new Vector3(-3.0f, 2.0f, 3.0f), new Vector3(2.0f, 4.0f, 0.3f), leftOccluder));
        _fixtureOccluderNodes.Add(AddBox(root, "Heavy Occluder Right", new Vector3(3.0f, 2.0f, 3.0f), new Vector3(2.0f, 4.0f, 0.3f), rightOccluder));

        for (int id = FirstHeavyCandidateId; id <= LastHeavyCandidateId; id++)
        {
            int index = id - 7;
            float x = -3.5f + index % 8;
            float y = 0.5f + (index / 8) % 4;
            float z = 5.5f + index / 32;
            AddCandidate(id, new Vector3(x, y, z), new Vector3(0.35f));
        }
    }

    private void AddMaskedFixture()
    {
        if (_maskedCoverageNode is not null)
            return;

        XRTexture2D albedo = CreateCutoutTexture("Masked Coverage Albedo");
        albedo.SamplerName = "Texture0";
        _fixtureMaterialTextures.Add((albedo, "Albedo"));
        XRMaterial masked = XRMaterial.CreateLitTextureMaterial(albedo, deferred: true);
        masked.Name = "Masked Coverage Panel";
        masked.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        masked.TransparencyMode = ETransparencyMode.Masked;
        masked.AlphaCutoff = 0.5f;
        masked.RenderOptions.CullMode = ECullMode.None;
        _occluderMaterials.Add(masked);

        XRMaterial opaque = CreateMaterial("Masked Coverage Opaque Control", ColorF4.White);
        _occluderMaterials.Add(opaque);
        SceneNode root = World.Scenes[0].RootNodes[0];
        Vector3 panelPosition = new(-2.0f, 1.0f, 3.0f);
        // Eight world units leave a genuinely covered interior over multiple 64-pixel
        // coarse tiles at the scenario resolution. Palette candidate 1 remains outside;
        // candidates 2, 4, and 5 are behind the opaque border/control.
        Vector3 panelScale = new(8.0f, 8.0f, 0.08f);
        _maskedCoverageNode = AddBox(root, "Masked Coverage Panel", panelPosition, panelScale, masked);
        _maskedOpaqueControlNode = AddBox(root, "Masked Coverage Opaque Control", panelPosition, panelScale, opaque);
        _fixtureOccluderNodes.Add(_maskedCoverageNode);
        _fixtureOccluderNodes.Add(_maskedOpaqueControlNode);
        SetMaskedCoverageOpaqueControl(false);
    }

    private static XRTexture2D CreateCutoutTexture(string name)
    {
        const int size = 16;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            // A two-texel centre cutout remains tightly aligned with candidate 2 while
            // the surrounding eight-unit panel still supplies coarse-tile coverage.
            bool hole = x is 7 or 8 && y is 7 or 8;
            // The generated material-table shader samples albedo alpha for the per-row
            // coverage discard. RGB remains white so its surviving border is unambiguous.
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = !hole ? (byte)255 : (byte)0;
        }

        return new XRTexture2D(size, size, pixels)
        {
            Name = name,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
        };
    }

    private void AddCandidate(int id, in Vector3 position, in Vector3 scale, XRMaterial material)
    {
        if ((uint)id >= MaximumCandidates)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Candidate ids must be in [0, {MaximumCandidates - 1}].");
        if (!_candidateNodes.TryAdd(id, AddBox(World.Scenes[0].RootNodes[0], $"Candidate {id}", position, scale, material)))
            throw new InvalidOperationException($"Candidate {id} already exists.");
        _candidateIdsByMaterial.Add(material, id);
    }

    private SceneNode AddBox(SceneNode parent, string name, in Vector3 position, in Vector3 scale, XRMaterial material)
    {
        SceneNode node = parent.NewChild(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = position;
        transform.Scale = scale;
        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Name = $"{name} Model";
        model.Model = new Model([new SubMesh(_boxMesh, material) { CullingBounds = _boxBounds }]);
        return node;
    }

    private static XRMaterial[] CreateCandidateMaterials()
        =>
        [
            CreateMaterial("Candidate Red", new ColorF4(1, 0, 0, 1)),
            CreateMaterial("Candidate Green", new ColorF4(0, 1, 0, 1)),
            CreateMaterial("Candidate Blue", new ColorF4(0, 0, 1, 1)),
            CreateMaterial("Candidate Yellow", new ColorF4(1, 1, 0, 1)),
            CreateMaterial("Candidate Cyan", new ColorF4(0, 1, 1, 1)),
            CreateMaterial("Candidate Magenta", new ColorF4(1, 0, 1, 1)),
        ];

    private static XRMaterial CreateCandidateMaterial(int id)
    {
        if (id >= FirstHeavyCandidateId && id <= LastHeavyCandidateId)
            return CreateMaterial($"Candidate {id}", GetHeavyCandidateColor(id));

        return (id % 6) switch
        {
            1 => CreateMaterial($"Candidate {id}", new ColorF4(1, 0, 0, 1)),
            2 => CreateMaterial($"Candidate {id}", new ColorF4(0, 1, 0, 1)),
            3 => CreateMaterial($"Candidate {id}", new ColorF4(0, 0, 1, 1)),
            4 => CreateMaterial($"Candidate {id}", new ColorF4(1, 1, 0, 1)),
            5 => CreateMaterial($"Candidate {id}", new ColorF4(0, 1, 1, 1)),
            _ => CreateMaterial($"Candidate {id}", new ColorF4(1, 0, 1, 1)),
        };
    }

    /// <summary>Returns one non-gray RGB code for every heavy candidate, separated from palette, white, and wall colors.</summary>
    public static ColorF4 GetHeavyCandidateColor(int id)
    {
        if (id < FirstHeavyCandidateId || id > LastHeavyCandidateId)
            throw new ArgumentOutOfRangeException(nameof(id));

        int targetOrdinal = id - FirstHeavyCandidateId;
        int ordinal = 0;
        for (int packed = 0; packed < 125; packed++)
        {
            int r = packed % 5;
            int g = packed / 5 % 5;
            int b = packed / 25;
            if (r == g && g == b)
                continue;
            if (ordinal++ != targetOrdinal)
                continue;
            return new ColorF4(s_heavyColorLevels[r] / 255.0f, s_heavyColorLevels[g] / 255.0f, s_heavyColorLevels[b] / 255.0f, 1.0f);
        }

        throw new InvalidOperationException("The heavy candidate palette has insufficient non-gray colors.");
    }

    private static XRMaterial CreateMaterial(string name, in ColorF4 color)
    {
        XRMaterial material = XRMaterial.CreateLitColorMaterial(color, deferred: true);
        material.Name = name;
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        material.RenderOptions.CullMode = ECullMode.None;
        return material;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (Environment.CurrentManagedThreadId != _renderThreadOwnerId)
        {
            throw new InvalidOperationException(
                "RenderBench production resources must be disposed by their explicit render-lane owner thread.");
        }
        _disposed = true;
        try
        {
            WorldHost.Dispose();
            _host?.Dispose();
            _explicitBackendRegistration?.Dispose();
            _servicesLease.Dispose();
        }
        finally
        {
            try
            {
                _workSchedulerScope.Dispose();
            }
            finally
            {
                RuntimeEngine.AssignRenderThread(_previousRenderThreadId);
            }
        }
    }

}
