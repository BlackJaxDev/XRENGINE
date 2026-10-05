using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Data.Rendering;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Owns a fixed production world and its scene-owned Vulkan host for full-frame profiling.
/// The ordinary viewport and DefaultRenderPipeline submit the frame; no synthetic record callback is used.
/// </summary>
internal sealed class RenderBenchProductionProfileFixture : IRenderBenchFixture
{
    internal const string SceneIdentity = "renderbench:production-moderate-static";
    internal const string CameraIdentity = "renderbench:production-fixed-camera";
    internal const string RenderFeature = "default-render-pipeline:raw-albedo:gpu-indirect-zero-readback";
    internal const string OutputIdentity = "production-color";

    private readonly RenderProfileRecipe _recipe;
    private readonly RenderBenchOptions _options;
    private RenderBenchProductionScene? _scene;
    private RenderBenchWorkCounters _counters;
    private ulong _captureTargetGeneration;
    private long _captureAdmissionRetries;
    private ulong _lastEngineFrameId;
    private VulkanExplicitProductionSubmissionReceipt _lastCapturedReceipt;
    private bool _measuring;
    private EVulkanQueueOverlapMode? _previousQueueMode;
    private bool? _previousStatisticsTracking;
    private CancellationToken _cancellationToken;

    internal RenderBenchProductionProfileFixture(
        RenderBenchFixtureDefinition definition,
        RenderProfileRecipe recipe,
        RenderBenchOptions options)
    {
        Definition = definition;
        _recipe = recipe;
        _options = options with { Layers = 1, ScenarioDepth = "normal" };
        ValidateInputs(recipe);
        Manifest = new RenderBenchFixtureManifest(
            1, definition.Name, definition.Component, definition.Kind,
            definition.Inclusions, definition.Exclusions,
            0, 0, 0, 0, 0, 0, 1, recipe.Mutation.Policy.ToString(), OutputIdentity)
        {
            EvidenceScope = "productionFullFrame",
            ObservableWorkCounters = ["Submissions", "CommandBuffers"],
        };
    }

    public RenderBenchFixtureDefinition Definition { get; }
    public RenderBenchFixtureManifest Manifest { get; }
    public RenderBenchWorkCounters Counters => _counters;
    public RenderBenchCommandBufferActivity CommandBufferActivity
        => new(_counters.CommandBuffers, 0, 0, 0, _recipe.GpuProfiling.Targets.Length != 0);
    public long WorkerAllocatedBytes => throw new NotSupportedException(
        "Production renderer worker allocations are not measured by the fixture worker counter.");
    internal bool WorkerAllocationsMeasured => false;
    internal VulkanExplicitTargetRendererHost Host => RequireScene().Host;
    internal long CurrentFrameOrdinal => RequireScene().SubmittedStepCount;
    internal long AdmissionRetryCount => RequireScene().PipelineAdmissionRetryCount;
    /// <summary>Reports the explicit world clock, including cold admission attempts and drainage.</summary>
    internal double SimulationTimeSeconds
    {
        get
        {
            _ = RequireScene();
            return Engine.Time.Timer.Render.LastTimestampTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        }
    }
    internal IReadOnlyCollection<RenderPassMetadata> PassMetadata
        => RequireScene().Camera.RenderPipeline?.PassMetadata
            ?? throw new InvalidOperationException("The production camera has no render-pass metadata.");
    internal ulong FirstCapturedEngineFrameId { get; private set; }

    internal void PrepareProduction(CancellationToken cancellationToken)
    {
        if (_scene is not null)
            throw new InvalidOperationException("The production profile scene is already prepared.");
        _cancellationToken = cancellationToken;
        _previousQueueMode = RuntimeEngine.Rendering.Settings.VulkanQueueOverlapMode;
        _previousStatisticsTracking = RuntimeEngine.Rendering.Stats.EnableTracking;
        RuntimeEngine.Rendering.Stats.EnableTracking = true;
        RuntimeEngine.Rendering.Settings.VulkanQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
        RenderBenchProductionScene? scene = null;
        try
        {
            scene = new RenderBenchProductionScene(_options, EOcclusionCullingMode.Disabled);
            scene.ConfigureScenarioWorkload(RenderBenchScenarioWorkloads.ModerateStatic);
            _scene = scene;
        }
        catch
        {
            try
            {
                scene?.Dispose();
            }
            finally
            {
                RestoreQueueMode();
            }
            throw;
        }
    }

    internal VulkanExplicitProductionSubmissionReceipt SubmitStep()
    {
        RenderBenchProductionScene scene = RequireScene();
        long priorSteps = scene.SubmittedStepCount;
        VulkanExplicitProductionSubmissionReceipt receipt = scene.SubmitStep(
            _recipe.Scene.FixedTimeStepSeconds, allowAdmissionRetry: !_measuring,
            cancellationToken: _cancellationToken, admissionRetryTimeoutSeconds: _recipe.TimeoutSeconds);
        if (!receipt.IsValid || receipt.CommandBufferHandle == 0 ||
            scene.SubmittedStepCount != priorSteps + 1 ||
            receipt.ExecutedQueueMode != EVulkanQueueOverlapMode.GraphicsOnly ||
            receipt.NativeFrameSubmissionCount != 1)
        {
            throw new InvalidOperationException("The production frame did not produce one valid graphics submission and command buffer.");
        }

        if (_measuring)
        {
            if (_counters.Submissions == 0)
                FirstCapturedEngineFrameId = receipt.EngineFrameId;
            if (scene.PipelineAdmissionRetryCount != _captureAdmissionRetries)
                throw new InvalidOperationException("Production resource admission retried during measured capture.");
            if (receipt.TargetGeneration != _captureTargetGeneration ||
                scene.Host.TargetGeneration != _captureTargetGeneration)
                throw new InvalidOperationException("The production target generation changed during measured capture.");
            if (_counters.Submissions != 0 && receipt.EngineFrameId != _lastEngineFrameId + 1)
                throw new InvalidOperationException("Production engine frame IDs were not consecutive during measured capture.");
            _lastEngineFrameId = receipt.EngineFrameId;
            _lastCapturedReceipt = receipt;
            _counters += new RenderBenchWorkCounters(0, 0, 1, 1, 0, 0, 0, 0, 0);
        }

        return receipt;
    }

    public void BeginCapture()
    {
        RenderBenchProductionScene scene = RequireScene();
        if (EffectiveSettingsEnvOverrides.OcclusionCullingMode != nameof(EOcclusionCullingMode.Disabled) ||
            EffectiveSettingsEnvOverrides.ZeroReadbackMaterialDrawPath != nameof(EZeroReadbackMaterialDrawPath.BindlessMaterialTable))
            throw new InvalidOperationException("Effective production occlusion or material draw settings differ from the fixed recipe.");
        _counters = default;
        _captureTargetGeneration = scene.Host.TargetGeneration;
        _captureAdmissionRetries = scene.PipelineAdmissionRetryCount;
        _lastEngineFrameId = 0;
        _lastCapturedReceipt = default;
        FirstCapturedEngineFrameId = 0;
        _measuring = true;
    }

    public void EndCapture() => _measuring = false;

    /// <summary>Captures passive scene and opaque-pass admission state after measured work.</summary>
    internal object CaptureOutputDiagnostics()
    {
        RenderBenchProductionScene scene = RequireScene();
        GPURenderPassCollection pass = scene.GetMaterialScenarioOpaquePass();
        bool hasVisibility = pass.TryGetVisibilityDiagnostic(_lastCapturedReceipt.EngineFrameId, out GpuHiZTwoPassDiagnosticDescriptor visibility);
        uint? visibleDrawCount = null;
        string visibilityReadback = "no current visibility descriptor";
        if (hasVisibility)
        {
            Span<byte> countBytes = stackalloc byte[sizeof(uint)];
            if (Host.TryReadbackProductionBuffer(in _lastCapturedReceipt, visibility.LateCount, 0,
                    countBytes, out visibilityReadback))
                visibleDrawCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
        }
        uint[]? materialBucketCounts = null;
        string materialBucketReadback = "no material count buffer";
        ulong materialCountElements = pass.MaterialTierDrawCountBuffer?.ElementCount ?? 0;
        if (hasVisibility && pass.MaterialTierDrawCountBuffer is { } materialCounts && materialCountElements > 0)
        {
            int count = checked((int)Math.Min(materialCountElements, 256ul));
            Span<byte> bytes = stackalloc byte[count * sizeof(uint)];
            if (Host.TryReadbackProductionBuffer(in _lastCapturedReceipt, materialCounts, 0, bytes, out materialBucketReadback))
            {
                materialBucketCounts = new uint[count];
                for (int index = 0; index < count; index++)
                    materialBucketCounts[index] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * sizeof(uint), sizeof(uint)));
            }
        }
        return new
        {
            EngineFrameId = _lastCapturedReceipt.EngineFrameId,
            CurrentEngineFrameId = RuntimeEngine.Rendering.State.RenderFrameId,
            scene.GPUScene.TotalCommandCount,
            CameraPosition = scene.Camera.Transform.RenderTranslation.ToString(),
            DeferredDebugView = RenderDiagnosticsFlags.DeferredDebugView,
            pass.GpuProgramsPendingThisFrame,
            pass.ZeroReadbackProgramPendingCountThisFrame,
            HasVisibilityDescriptor = hasVisibility,
            VisibleDrawCount = visibleDrawCount,
            VisibilityReadback = visibilityReadback,
            MaterialCountElements = materialCountElements,
            MaterialBucketCounts = materialBucketCounts,
            MaterialBucketReadback = materialBucketReadback,
            VisibilityStrategy = hasVisibility ? visibility.Strategy.ToString() : "unavailable",
            HasPreviousVisibility = _lastCapturedReceipt.EngineFrameId > 0 && pass.TryGetVisibilityDiagnostic(_lastCapturedReceipt.EngineFrameId - 1, out _),
            HasNextVisibility = pass.TryGetVisibilityDiagnostic(_lastCapturedReceipt.EngineFrameId + 1, out _),
            pass.MeshSubmissionStrategy,
            NativeSubmission = new
            {
                PublishedEngineFrameId = _lastCapturedReceipt.EngineFrameId - 1,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpIndirectDrawCount,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectApiCalls,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectCountPathCalls,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanRequiredPipelinePendingCount,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorSkippedDraws,
                RuntimeEngine.Rendering.Stats.GpuDriven.RequiredMaterialRows,
                RuntimeEngine.Rendering.Stats.GpuDriven.ReadyMaterialRows,
                RuntimeEngine.Rendering.Stats.GpuDriven.InvalidMaterialIds,
                RuntimeEngine.Rendering.Stats.GpuDriven.MaterialBindingRung,
                RuntimeEngine.Rendering.Stats.GpuDriven.MaterialBindingRungReason,
            },
            MaterialTable = HybridRenderingManager.GetZeroReadbackMaterialTableDiagnosticsSnapshot(),
            Console = Debug.GetConsoleEntries().TakeLast(12).Select(static entry => new { entry.Category, entry.Message }).ToArray(),
        };
    }

    /// <summary>Reads the exact last measured production output after its submission completes.</summary>
    internal byte[] ReadCapturedColor(int expectedBytes)
    {
        if (_measuring || !_lastCapturedReceipt.IsValid)
            throw new InvalidOperationException("A completed measured production frame is required for color readback.");
        if (expectedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes));

        VulkanExplicitTargetRendererHost host = Host;
        RenderBenchScenarioLane.WaitForCompletion(host, in _lastCapturedReceipt);
        if (!host.TryReadbackProductionColor(in _lastCapturedReceipt, expectedBytes, out byte[]? color) ||
            color is null || color.Length != expectedBytes)
        {
            throw new InvalidOperationException(
                "The exact measured production receipt did not return the expected color bytes.");
        }
        return color;
    }

    public void Prepare(VulkanExplicitTargetRendererHost host, RenderProfileRecipe recipe)
        => throw new NotSupportedException("Production frames require their scene-owned host; use PrepareProduction.");

    public void RecordFrame(Vk api, CommandBuffer commandBuffer, VulkanRenderFrameTarget target)
        => throw new NotSupportedException("Production frames are recorded by the real viewport and render pipeline.");

    public void Dispose()
    {
        _measuring = false;
        try
        {
            _scene?.Dispose();
            _scene = null;
        }
        finally
        {
            RestoreQueueMode();
        }
    }

    private void RestoreQueueMode()
    {
        if (_previousStatisticsTracking is { } tracking)
        {
            RuntimeEngine.Rendering.Stats.EnableTracking = tracking;
            _previousStatisticsTracking = null;
        }
        if (_previousQueueMode is not { } previous)
            return;
        RuntimeEngine.Rendering.Settings.VulkanQueueOverlapMode = previous;
        _previousQueueMode = null;
    }

    private RenderBenchProductionScene RequireScene()
        => _scene ?? throw new InvalidOperationException("The production profile scene is not prepared.");

    private static void ValidateInputs(RenderProfileRecipe recipe)
    {
        if (recipe.ExecutionMode != RenderExecutionMode.Presentationless ||
            !recipe.Scene.SceneIdentity.Equals(SceneIdentity, StringComparison.Ordinal) ||
            !recipe.Scene.CameraIdentity.Equals(CameraIdentity, StringComparison.Ordinal) ||
            !recipe.Scene.AnimationIdentity.Equals("frozen", StringComparison.Ordinal) ||
            recipe.Scene.RandomSeed != 0x585245 ||
            recipe.Scene.MeshStrategy != RenderProfileMeshStrategy.MultiDrawIndirect ||
            recipe.Scene.StereoMode != RenderProfileStereoMode.Mono ||
            recipe.Scene.LightIdentities.Length != 0 ||
            recipe.Scene.RenderFeatures.Length != 1 ||
            !recipe.Scene.RenderFeatures[0].Equals(RenderFeature, StringComparison.Ordinal) ||
            recipe.Scene.OutputIdentities.Length != 1 ||
            !recipe.Scene.OutputIdentities[0].Equals(OutputIdentity, StringComparison.Ordinal))
        {
            throw new NotSupportedException("The production full-frame fixture requires its fixed world, camera, indirect path, frozen animation, and mono color output identities.");
        }

        if (recipe.Mutation.Policy != RenderProfileMutationPolicy.StableReuse ||
            recipe.Mutation.DirtyEveryNFrames != 1 ||
            recipe.WorkerCounts.Length != 1 || recipe.WorkerCounts[0] != 1 ||
            recipe.SampleCount != 1 ||
            !recipe.ColorFormat.Equals("Rgba8", StringComparison.OrdinalIgnoreCase) ||
            !recipe.DepthFormat.Equals("DepthComponent32f", StringComparison.OrdinalIgnoreCase) ||
            recipe.Workload.ChainCount.HasValue || recipe.Workload.DrawCount.HasValue ||
            recipe.Workload.DescriptorCount.HasValue || recipe.Workload.BarrierCount.HasValue ||
            recipe.Workload.UploadBytes.HasValue || recipe.Workload.PassIterations.HasValue ||
            recipe.Workload.TargetInputs.Count != 0 ||
            recipe.Contract.Inclusions.Length != 0 || recipe.Contract.Exclusions.Length != 0 ||
            recipe.Contract.ValidityRequirements.Length != 0)
        {
            throw new NotSupportedException("The production full-frame fixture accepts only its fixed static workload, mono Rgba8/DepthComponent32f output, and one owner lane.");
        }

        if (recipe.Expected.Draws.HasValue || recipe.Expected.Dispatches.HasValue ||
            recipe.Expected.Descriptors.HasValue || recipe.Expected.Barriers.HasValue ||
            recipe.Expected.UploadBytes.HasValue || recipe.Expected.PassIterations.HasValue ||
            recipe.Expected.CommandBufferDecisions.HasValue ||
            recipe.Expected.Submissions is not (null or 1) ||
            recipe.Expected.CommandBuffers is not (null or 1))
        {
            throw new NotSupportedException("The production receipt measures submissions and primary command buffers; other exact native work counters are unavailable.");
        }

        if (recipe.GpuProfiling.HardwareCounterIndices.Length != 0 ||
            recipe.HardwareCounterPolicy != RenderProfileHardwareCounterPolicy.Disabled ||
            recipe.Instrumentation.HasFlag(RenderProfileInstrumentation.HardwareCounters))
            throw new NotSupportedException("Production hardware-counter replay is unavailable for this fixture.");
    }
}
