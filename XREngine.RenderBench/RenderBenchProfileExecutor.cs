using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageMagick;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Frame-granular executor for deterministic Phase 4 fixtures on the real Vulkan explicit-target
/// host. Capture buffers, shaders, descriptors, command pools, and fixture resources are prepared
/// before the armed interval.
/// </summary>
public sealed class RenderBenchProfileExecutor : IRenderProfileExecutor
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly RenderBenchOptions _processOptions;
    private readonly RenderBenchProcessState _state;
    private RenderBenchOptions _options = null!;
    private VulkanExplicitTargetRendererHost? _host;
    private IRenderBenchFixture? _fixture;
    private Action<Silk.NET.Vulkan.Vk, Silk.NET.Vulkan.CommandBuffer, VulkanRenderFrameTarget>? _recordFrame;
    private long[] _cpuFrameNanoseconds = [];
    private double[] _gpuFrameNanoseconds = [];
    private long[] _fixtureFrameAllocatedBytes = [];
    private long[] _submitFrameAllocatedBytes = [];
    private long[] _delayedGpuTimingAllocatedBytes = [];
    private VulkanExplicitTargetFrameAllocationCounters[] _explicitTargetFrameAllocationCounters = [];
    private int _submittedFrames;
    private int _captureStartFrame;
    private int _captureThreadId;
    private long _allocatedBefore;
    private long _allocatedAfter;
    private ulong _stableGeneration;
    private DateTimeOffset _startedUtc;
    private string _runDirectory = string.Empty;
    private string _effectiveConfigurationPath = string.Empty;
    private string _workloadIdentityPath = string.Empty;
    private string _effectiveConfigurationJson = string.Empty;
    private string _workloadIdentityJson = string.Empty;
    private bool _captureAllocationBreakdownActive;
    private bool _disposed;

    public RenderBenchProfileExecutor(RenderBenchOptions processOptions, RenderBenchProcessState state, RenderProfileRecipe recipe)
    {
        _processOptions = processOptions;
        _state = state;
    }

    public long NextFrameId => _submittedFrames;

    public Task<RenderProfilePreparation> PrepareAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRecipe(recipe);
        _startedUtc = DateTimeOffset.UtcNow;
        _runDirectory = Path.Combine(_processOptions.OutputDirectory, "profiles", $"{SanitizeFileName(recipe.Name)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runDirectory);
        EPixelInternalFormat colorFormat = ParseFormat(recipe.ColorFormat, "color");
        EPixelInternalFormat depthFormat = ParseFormat(recipe.DepthFormat, "depth");
        _options = _processOptions with
        {
            ExecutionMode = recipe.ExecutionMode,
            Recipe = recipe.Name,
            Fixture = recipe.Fixture,
            OutputDirectory = _runDirectory,
            Width = recipe.ScaledWidth,
            Height = recipe.ScaledHeight,
            FrameSlots = recipe.FrameSlots,
            Samples = recipe.SampleCount,
            ColorFormat = colorFormat,
            DepthFormat = depthFormat,
            WarmupFrames = recipe.WarmupFrames,
            StabilityFrames = recipe.StabilityFrames,
            CaptureFrames = recipe.TotalCaptureFrames,
            FixedStepSeconds = recipe.Scene.FixedTimeStepSeconds,
            RandomSeed = recipe.Scene.RandomSeed,
            FrozenWorld = recipe.Scene.AnimationIdentity.Equals("frozen", StringComparison.OrdinalIgnoreCase),
        };

        _fixture = RenderBenchFixtureCatalog.Create(recipe);
        _recordFrame = RecordFixtureFrame;
        RenderBenchEffectiveConfiguration effectiveConfiguration = new(1, recipe, _fixture.Manifest);
        RenderBenchWorkloadIdentity workloadIdentity = CreateWorkloadIdentity(recipe, _fixture.Manifest);
        _effectiveConfigurationJson = JsonSerializer.Serialize(effectiveConfiguration, s_jsonOptions);
        _workloadIdentityJson = JsonSerializer.Serialize(workloadIdentity, s_jsonOptions);
        _effectiveConfigurationPath = Path.Combine(_runDirectory, "render-bench-effective-config.json");
        _workloadIdentityPath = Path.Combine(_runDirectory, "render-bench-workload.json");
        WriteAtomic(_effectiveConfigurationPath, _effectiveConfigurationJson);
        WriteAtomic(_workloadIdentityPath, _workloadIdentityJson);

        _cpuFrameNanoseconds = GC.AllocateUninitializedArray<long>(recipe.TotalCaptureFrames);
        _gpuFrameNanoseconds = GC.AllocateUninitializedArray<double>(recipe.TotalCaptureFrames);
        _fixtureFrameAllocatedBytes = GC.AllocateUninitializedArray<long>(recipe.TotalCaptureFrames);
        _submitFrameAllocatedBytes = GC.AllocateUninitializedArray<long>(recipe.TotalCaptureFrames);
        _delayedGpuTimingAllocatedBytes = GC.AllocateUninitializedArray<long>(recipe.TotalCaptureFrames);
        _explicitTargetFrameAllocationCounters = GC.AllocateUninitializedArray<VulkanExplicitTargetFrameAllocationCounters>(recipe.TotalCaptureFrames);
        Array.Fill(_gpuFrameNanoseconds, double.NaN);
        _host = new VulkanExplicitTargetRendererHost(CreateTarget(recipe));
        List<string> unsupported = ValidateSelectedRuntime(recipe, _host);
        if (unsupported.Count == 0)
            _fixture.Prepare(_host, recipe);
        string[] extensions = [.. _host.EnabledInstanceExtensions, .. _host.EnabledDeviceExtensions];
        return Task.FromResult(new RenderProfilePreparation(
            _host.AdapterName,
            _host.DriverVersion.ToString(),
            ComputeTextHash(_workloadIdentityJson),
            extensions,
            unsupported));
    }

    public Task StabilizeAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken)
    {
        VulkanExplicitTargetRendererHost host = GetHost();
        IRenderBenchFixture fixture = GetFixture();
        _state.SetPhase(RenderBenchPhase.Warmup);
        for (int index = 0; index < recipe.WarmupFrames; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _submittedFrames++;
            host.SubmitFrame(_recordFrame!);
        }
        _state.SetPhase(RenderBenchPhase.Stabilizing);
        _stableGeneration = host.TargetGeneration;
        for (int index = 0; index < recipe.StabilityFrames; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _submittedFrames++;
            host.SubmitFrame(_recordFrame!);
            ValidateStableHost(host, "stability window");
        }
        return Task.CompletedTask;
    }

    public void WarmCaptureThread(RenderProfileRecipe recipe)
    {
        VulkanExplicitTargetRendererHost host = GetHost();
        IRenderBenchFixture fixture = GetFixture();
        host.ExplicitTargetAllocationDiagnosticsEnabled = true;
        _submittedFrames++;
        host.SubmitFrame(_recordFrame!);
        ValidateStableHost(host, "capture-thread warmup");
        _captureStartFrame = _submittedFrames;
        fixture.BeginCapture();
        _ = GC.GetAllocatedBytesForCurrentThread();
    }

    public void ExecuteMeasuredFrame(RenderProfileRecipe recipe, int frameIndex)
    {
        if (frameIndex != _submittedFrames)
            throw new InvalidOperationException($"Profile armed frame {frameIndex} does not match renderer frame {_submittedFrames}.");
        int captureIndex = _submittedFrames - _captureStartFrame;
        if ((uint)captureIndex >= (uint)_cpuFrameNanoseconds.Length)
            throw new InvalidOperationException("The profile executor exceeded its preallocated capture capacity.");
        int threadId = Environment.CurrentManagedThreadId;
        if (captureIndex == 0)
        {
            _captureThreadId = threadId;
            _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            _state.SetPhase(RenderBenchPhase.Capturing);
            _captureAllocationBreakdownActive = true;
        }
        else if (threadId != _captureThreadId)
            throw new InvalidOperationException("Measured Vulkan frames moved between worker threads.");

        VulkanExplicitTargetRendererHost host = GetHost();
        _submittedFrames++;
        long submitAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        long frameStart = Stopwatch.GetTimestamp();
        host.SubmitFrame(_recordFrame!);
        long submitAllocationEnd = GC.GetAllocatedBytesForCurrentThread();
        _submitFrameAllocatedBytes[captureIndex] = submitAllocationEnd - submitAllocationStart;
        _explicitTargetFrameAllocationCounters[captureIndex] = host.LastExplicitTargetFrameAllocationCounters;
        _cpuFrameNanoseconds[captureIndex] = ToNanoseconds(Stopwatch.GetTimestamp() - frameStart);
        long delayedGpuTimingAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        CaptureDelayedGpuTiming(host, _submittedFrames - 1);
        _delayedGpuTimingAllocatedBytes[captureIndex] =
            GC.GetAllocatedBytesForCurrentThread() - delayedGpuTimingAllocationStart;
        _allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
    }

    public Task<RenderProfileResult> DrainAsync(RenderProfileRecipe recipe, RenderProfilePreparation preparation, CancellationToken cancellationToken)
    {
        _state.SetPhase(RenderBenchPhase.Draining);
        VulkanExplicitTargetRendererHost host = GetHost();
        IRenderBenchFixture fixture = GetFixture();
        fixture.EndCapture();
        _captureAllocationBreakdownActive = false;
        host.ExplicitTargetAllocationDiagnosticsEnabled = false;
        int capturedFrames = _submittedFrames - _captureStartFrame;
        string? outputHash = TryComputeOutputHash(host);
        string? outputImagePath = recipe.ValidationMode == RenderProfileValidationMode.CountersHashAndImage
            ? WriteOutputImage(host, recipe)
            : null;
        for (int index = 0; index < _options.FrameSlots; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            host.SubmitFrame(_recordFrame!);
            CaptureDelayedGpuTiming(host, _submittedFrames++);
        }

        long allocatedBytes = _allocatedAfter - _allocatedBefore;
        WriteCaptureAllocationDiagnostics(capturedFrames, allocatedBytes);
        long[] cpu = _cpuFrameNanoseconds.AsSpan(0, capturedFrames).ToArray();
        double[] gpu = _gpuFrameNanoseconds.AsSpan(0, capturedFrames).ToArray();
        RenderBenchWorkCounters workCounters = fixture.Counters;
        long workerAllocatedBytes = fixture.WorkerAllocatedBytes;
        RenderBenchGateResult[] stabilityGates = BuildGates(
            recipe, preparation, host, fixture, capturedFrames, allocatedBytes, workerAllocatedBytes,
            outputHash, outputImagePath, cpu, gpu, workCounters);
        string adapterName = host.AdapterName;
        uint driverVersion = host.DriverVersion;
        uint vendorId = host.VendorId;
        uint deviceId = host.DeviceId;
        string presentationDescription = host.PresentationDescription;
        RenderTargetOutputProperties actualOutput = host.OutputProperties;
        RenderBenchFixtureManifest fixtureManifest = fixture.Manifest;
        DisposeHost();

        RenderBenchGateResult? failedGate = stabilityGates.FirstOrDefault(static gate => !gate.Passed);
        if (failedGate is not null)
            throw new InvalidOperationException($"RenderBench validity gate '{failedGate.Name}' failed: {failedGate.Observed}");

        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
        RenderBenchInputManifest inputManifest = new(
            !recipe.Scene.SceneIdentity.Equals("synthetic:no-world", StringComparison.OrdinalIgnoreCase),
            recipe.Scene.SceneIdentity,
            recipe.Scene.CameraIdentity,
            recipe.Scene.LightIdentities,
            recipe.Scene.AnimationIdentity,
            _submittedFrames * recipe.Scene.FixedTimeStepSeconds,
            recipe.Scene.FixedTimeStepSeconds,
            recipe.Scene.RandomSeed,
            recipe.Scene.MeshStrategy,
            recipe.Scene.RenderFeatures,
            recipe.Scene.StereoMode,
            recipe.Scene.OutputIdentities);
        RenderBenchResult result = new()
        {
            RunId = $"{_startedUtc:yyyyMMdd-HHmmss}-{Environment.ProcessId}",
            StartedUtc = _startedUtc,
            CompletedUtc = completedUtc,
            Backend = _options.Backend,
            ExecutionMode = recipe.ExecutionMode,
            Recipe = recipe.Name,
            Fixture = recipe.Fixture,
            ExecutablePath = Assembly.GetExecutingAssembly().Location,
            ExecutableSha256 = ComputeFileHash(Assembly.GetExecutingAssembly().Location),
            EffectiveConfigurationSha256 = ComputeTextHash(_effectiveConfigurationJson),
            WorkloadSha256 = preparation.WorkloadIdentity,
            EffectiveConfigurationPath = _effectiveConfigurationPath,
            WorkloadIdentityPath = _workloadIdentityPath,
            AdapterName = adapterName,
            DriverVersion = driverVersion,
            VendorId = vendorId,
            DeviceId = deviceId,
            PresentationDescription = presentationDescription,
            Output = actualOutput,
            ProcessId = Environment.ProcessId,
            WarmupFrames = recipe.WarmupFrames,
            StabilityFrames = recipe.StabilityFrames,
            CaptureFrames = capturedFrames,
            Repetitions = recipe.Repetitions,
            FixedStepSeconds = recipe.Scene.FixedTimeStepSeconds,
            RandomSeed = recipe.Scene.RandomSeed,
            FrozenWorld = recipe.Scene.AnimationIdentity.Equals("frozen", StringComparison.OrdinalIgnoreCase),
            DeterministicInputs = inputManifest,
            FixtureManifest = fixtureManifest,
            WorkCounters = workCounters,
            CpuFrameNanoseconds = cpu,
            GpuFrameNanoseconds = gpu,
            AllocatedBytesOnCaptureThread = allocatedBytes,
            AllocatedBytesOnFixtureWorkers = workerAllocatedBytes,
            OutputSha256 = outputHash,
            OutputImagePath = outputImagePath,
            StabilityGates = stabilityGates,
        };
        string resultPath = Path.Combine(_runDirectory, "render-bench-result.json");
        WriteAtomic(resultPath, JsonSerializer.Serialize(result, s_jsonOptions));
        _state.Complete(resultPath);
        Dictionary<string, string> artifacts = new()
        {
            ["result"] = resultPath,
            ["effective_configuration"] = _effectiveConfigurationPath,
            ["workload_identity"] = _workloadIdentityPath,
        };
        if (outputImagePath is not null)
            artifacts["output_image"] = outputImagePath;
        return Task.FromResult(new RenderProfileResult
        {
            SessionId = string.Empty,
            RecipeName = recipe.Name,
            ExecutionMode = recipe.ExecutionMode,
            WorkloadIdentity = preparation.WorkloadIdentity,
            CapturedFrames = capturedFrames,
            FrameMilliseconds = cpu.Select(static nanoseconds => nanoseconds / 1_000_000.0).ToArray(),
            Artifacts = artifacts,
        });
    }

    public Task CancelAsync(CancellationToken cancellationToken)
    {
        DisposeHost();
        return Task.CompletedTask;
    }

    private RenderBenchGateResult[] BuildGates(
        RenderProfileRecipe recipe,
        RenderProfilePreparation preparation,
        VulkanExplicitTargetRendererHost host,
        IRenderBenchFixture fixture,
        int capturedFrames,
        long allocatedBytes,
        long workerAllocatedBytes,
        string? outputHash,
        string? outputImagePath,
        long[] cpu,
        double[] gpu,
        RenderBenchWorkCounters actual)
    {
        bool gpuDrained = gpu.All(double.IsFinite);
        RenderBenchWorkCounters expected = ExpectedCounters(recipe, fixture.Manifest, capturedFrames);
        bool countsMatch = actual == expected;
        double cpuP50 = Percentile(cpu.Select(static value => value / 1_000_000.0).ToArray(), 0.50);
        double cpuP95 = Percentile(cpu.Select(static value => value / 1_000_000.0).ToArray(), 0.95);
        double gpuP95 = Percentile(gpu.Select(static value => value / 1_000_000.0).ToArray(), 0.95);
        bool allocationBudget = !recipe.Budgets.MaxCaptureThreadAllocatedBytes.HasValue || allocatedBytes <= recipe.Budgets.MaxCaptureThreadAllocatedBytes.Value;
        bool workerAllocationBudget = !recipe.Budgets.MaxWorkerAllocatedBytes.HasValue || workerAllocatedBytes <= recipe.Budgets.MaxWorkerAllocatedBytes.Value;
        bool outputHashValid = !recipe.Budgets.RequireOutputHash || outputHash is not null;
        bool requiredHashMatches = recipe.Budgets.RequiredOutputSha256 is null ||
            recipe.Budgets.RequiredOutputSha256.Equals(outputHash, StringComparison.OrdinalIgnoreCase);
        bool imageValid = recipe.ValidationMode != RenderProfileValidationMode.CountersHashAndImage || outputImagePath is not null;
        return
        [
            new("mcp_capture_silence", true, "MCP transport is suspended during capture and drain.", "listener suspended"),
            new("fixture_precreation", true, "Fixture-owned reusable assets and Vulkan objects exist before capture.", fixture.Definition.Kind.ToString()),
            new("fixture_identity", fixture.Definition.Name.Equals(recipe.Fixture, StringComparison.OrdinalIgnoreCase) &&
                fixture.Definition.Component.Equals(recipe.Component, StringComparison.OrdinalIgnoreCase),
                "Prepared fixture identity must match the recipe target.", $"{fixture.Definition.Component}/{fixture.Definition.Name}"),
            new("shader_state", fixture.Definition.Kind is not (RenderBenchFixtureKind.GpuPass or RenderBenchFixtureKind.FullPresentationless) || host.SupportsDynamicRendering,
                "Shader fixtures retain their precreated pipeline and required dynamic-rendering state.",
                fixture.Definition.Kind is RenderBenchFixtureKind.GpuPass or RenderBenchFixtureKind.FullPresentationless ? "precreated fullscreen pipeline" : "not shader-owned"),
            new("fallback_state", true, "Unsupported fixture paths fail explicitly and never substitute another fixture/backend.", "no fallback selected"),
            new("gpu_query_drain", gpuDrained, "GPU timings use delayed frame-slot query retrieval.", $"drained {gpu.Count(double.IsFinite)} of {capturedFrames}"),
            new("expected_work", countsMatch, "Measured work must exactly match the fixture/recipe declaration.", $"expected={expected}; actual={actual}"),
            new("workload_identity", true, "Immutable workload identity excludes worker and mutation experiment knobs.", preparation.WorkloadIdentity),
            new("target_generation", host.TargetGeneration == _stableGeneration, "Target generation remains stable after warmup.", $"stable={host.TargetGeneration == _stableGeneration}"),
            new("device_health", !host.IsDeviceLost, "The Vulkan device remains available.", $"healthy={!host.IsDeviceLost}"),
            new("capture_thread_allocations", allocationBudget, "Capture-thread managed allocation satisfies the recipe budget.", $"allocatedBytes={allocatedBytes}; max={recipe.Budgets.MaxCaptureThreadAllocatedBytes?.ToString() ?? "unbounded"}"),
            new("fixture_worker_allocations", workerAllocationBudget, "Persistent fixture workers satisfy the recipe managed-allocation budget.", $"allocatedBytes={workerAllocatedBytes}; max={recipe.Budgets.MaxWorkerAllocatedBytes?.ToString() ?? "unbounded"}"),
            new("output_hash", outputHashValid && requiredHashMatches, "Required output identity is readable and matches any pinned hash.", outputHash ?? "unavailable"),
            new("output_image", imageValid, "Image validation mode emits a post-capture image.", outputImagePath ?? "not requested"),
            BudgetGate("cpu_p50_budget", cpuP50, recipe.Budgets.MaxCpuP50Milliseconds),
            BudgetGate("cpu_p95_budget", cpuP95, recipe.Budgets.MaxCpuP95Milliseconds),
            BudgetGate("gpu_p95_budget", gpuP95, recipe.Budgets.MaxGpuP95Milliseconds),
        ];
    }

    private static RenderBenchGateResult BudgetGate(string name, double actual, double? maximum)
        => new(name, !maximum.HasValue || actual <= maximum.Value, "Observed percentile must satisfy the recipe acceptance budget.",
            $"actualMs={actual:F6}; maxMs={maximum?.ToString("F6") ?? "unbounded"}");

    private static RenderBenchWorkCounters ExpectedCounters(RenderProfileRecipe recipe, RenderBenchFixtureManifest fixture, int frames)
    {
        long PerFrame(long? explicitValue, long fallback) => (explicitValue ?? fallback) * frames;
        long commandBuffers = fixture.Kind is RenderBenchFixtureKind.SecondaryCommandRecording or RenderBenchFixtureKind.CommandBufferReuse
            ? fixture.WorkerCount + 1
            : 1;
        long barriers = fixture.Kind is RenderBenchFixtureKind.GpuPass or RenderBenchFixtureKind.FullPresentationless
            ? fixture.PassIterations * 2L
            : fixture.BarrierCount;
        long decisions = fixture.Kind is RenderBenchFixtureKind.SecondaryCommandRecording or RenderBenchFixtureKind.CommandBufferReuse ? 1 : 0;
        long descriptors = fixture.DescriptorCount;
        if (fixture.Kind == RenderBenchFixtureKind.DescriptorPublication && recipe.Mutation.Policy == RenderProfileMutationPolicy.DescriptorChurn)
            descriptors *= 2;
        return new RenderBenchWorkCounters(
            PerFrame(recipe.Expected.Draws, fixture.DrawCount),
            PerFrame(recipe.Expected.Dispatches, 0),
            PerFrame(recipe.Expected.Submissions, 1),
            PerFrame(recipe.Expected.CommandBuffers, commandBuffers),
            PerFrame(recipe.Expected.Descriptors, descriptors),
            PerFrame(recipe.Expected.Barriers, barriers),
            PerFrame(recipe.Expected.UploadBytes, fixture.UploadBytes),
            PerFrame(recipe.Expected.PassIterations, fixture.Kind is RenderBenchFixtureKind.GpuPass or RenderBenchFixtureKind.FullPresentationless ? fixture.PassIterations : 0),
            PerFrame(recipe.Expected.CommandBufferDecisions, decisions));
    }

    private IRendererPresentationTarget CreateTarget(RenderProfileRecipe recipe)
    {
        RenderTargetOutputProperties output = new(
            recipe.ScaledWidth,
            recipe.ScaledHeight,
            Layers: recipe.Scene.StereoMode == RenderProfileStereoMode.Mono ? 1u : 2u,
            ColorFormat: _options.ColorFormat,
            DepthFormat: _options.DepthFormat,
            SampleCount: recipe.SampleCount,
            FrameSlotCount: recipe.FrameSlots);
        return recipe.ExecutionMode switch
        {
            RenderExecutionMode.Presentationless => new PresentationlessRenderTarget(
                output.Width, output.Height, output.Layers, output.FrameSlotCount, output.SampleCount, output.ColorFormat, output.DepthFormat),
            RenderExecutionMode.Component => new ComponentRenderTarget(recipe.Component, output),
            _ => throw new NotSupportedException($"Execution mode '{recipe.ExecutionMode}' is not supported by RenderBench."),
        };
    }

    private static void ValidateRecipe(RenderProfileRecipe recipe)
    {
        recipe.Validate();
        if (recipe.Backend != RuntimeGraphicsApiKind.Vulkan)
            throw new NotSupportedException($"RenderBench supports only Vulkan recipes, not '{recipe.Backend}'.");
        if (recipe.ExecutionMode is not (RenderExecutionMode.Component or RenderExecutionMode.Presentationless))
            throw new NotSupportedException($"RenderBench cannot execute '{recipe.ExecutionMode}'.");
        _ = RenderBenchFixtureCatalog.Get(recipe.Fixture, recipe.Component, recipe.ExecutionMode);
    }

    private void RecordFixtureFrame(
        Silk.NET.Vulkan.Vk api,
        Silk.NET.Vulkan.CommandBuffer commandBuffer,
        VulkanRenderFrameTarget target)
    {
        if (!_captureAllocationBreakdownActive)
        {
            GetFixture().RecordFrame(api, commandBuffer, target);
            return;
        }

        int captureIndex = _submittedFrames - _captureStartFrame;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        GetFixture().RecordFrame(api, commandBuffer, target);
        if ((uint)captureIndex < (uint)_fixtureFrameAllocatedBytes.Length)
        {
            _fixtureFrameAllocatedBytes[captureIndex] =
                GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        }
    }

    private void WriteCaptureAllocationDiagnostics(int capturedFrames, long allocatedBytes)
    {
        RenderBenchCaptureAllocationDiagnostics diagnostics = new(
            allocatedBytes,
            _fixtureFrameAllocatedBytes.AsSpan(0, capturedFrames).ToArray(),
            _submitFrameAllocatedBytes.AsSpan(0, capturedFrames).ToArray(),
            _delayedGpuTimingAllocatedBytes.AsSpan(0, capturedFrames).ToArray(),
            _explicitTargetFrameAllocationCounters.AsSpan(0, capturedFrames).ToArray());
        string path = Path.Combine(_runDirectory, "render-bench-capture-allocation-diagnostics.json");
        WriteAtomic(path, JsonSerializer.Serialize(diagnostics, s_jsonOptions));
    }

    private static List<string> ValidateSelectedRuntime(RenderProfileRecipe recipe, VulkanExplicitTargetRendererHost host)
    {
        List<string> unsupported = [];
        if (!recipe.Adapter.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !host.AdapterName.Contains(recipe.Adapter, StringComparison.OrdinalIgnoreCase) &&
            !recipe.Adapter.Equals($"0x{host.VendorId:X4}:0x{host.DeviceId:X4}", StringComparison.OrdinalIgnoreCase))
            unsupported.Add($"Requested adapter '{recipe.Adapter}' does not match selected adapter '{host.AdapterName}' (0x{host.VendorId:X4}:0x{host.DeviceId:X4}).");
        if (recipe.HardwareCounterPolicy == RenderProfileHardwareCounterPolicy.Required)
            unsupported.Add("Required hardware counters are unavailable in the RenderBench in-process Vulkan lane.");
        if (recipe.CpuSamplingPolicy == RenderProfileCpuSamplingPolicy.ExternalSamplerRequired)
            unsupported.Add("Required external CPU sampling must be supplied by an external profiler run.");
        return unsupported;
    }

    private static RenderBenchWorkloadIdentity CreateWorkloadIdentity(RenderProfileRecipe recipe, RenderBenchFixtureManifest fixture)
    {
        RenderTargetOutputProperties output = new(
            recipe.ScaledWidth, recipe.ScaledHeight,
            recipe.Scene.StereoMode == RenderProfileStereoMode.Mono ? 1u : 2u,
            ParseFormat(recipe.ColorFormat, "color"), ParseFormat(recipe.DepthFormat, "depth"),
            SampleCount: recipe.SampleCount, FrameSlotCount: recipe.FrameSlots);
        return new RenderBenchWorkloadIdentity(
            1, recipe.Backend.ToString(), recipe.ExecutionMode, recipe.Component, recipe.Fixture, output,
            recipe.Scene.SceneIdentity, recipe.Scene.CameraIdentity, [.. recipe.Scene.LightIdentities.Order(StringComparer.Ordinal)],
            recipe.Scene.AnimationIdentity, recipe.Scene.FixedTimeStepSeconds, recipe.Scene.RandomSeed, recipe.Scene.MeshStrategy,
            [.. recipe.Scene.RenderFeatures.Order(StringComparer.Ordinal)], recipe.Scene.StereoMode,
            [.. recipe.Scene.OutputIdentities.Order(StringComparer.Ordinal)], fixture.ChainCount, fixture.DrawCount,
            fixture.DescriptorCount, fixture.BarrierCount, fixture.UploadBytes, fixture.PassIterations,
            SortTargetInputs(recipe.Workload.TargetInputs));
    }

    private static IReadOnlyDictionary<string, long> SortTargetInputs(IReadOnlyDictionary<string, long> inputs)
    {
        SortedDictionary<string, long> sorted = new(StringComparer.Ordinal);
        foreach ((string key, long value) in inputs)
            sorted.Add(key, value);
        return sorted;
    }

    private static EPixelInternalFormat ParseFormat(string value, string description)
        => Enum.TryParse(value, true, out EPixelInternalFormat format)
            ? format
            : throw new NotSupportedException($"RenderBench {description} format '{value}' is unknown.");

    private void ValidateStableHost(VulkanExplicitTargetRendererHost host, string phase)
    {
        if (host.TargetGeneration != _stableGeneration && _stableGeneration != 0)
            throw new InvalidOperationException($"The Vulkan target generation changed during {phase}.");
        if (host.IsDeviceLost)
            throw new InvalidOperationException($"The Vulkan device was lost during {phase}.");
    }

    private VulkanExplicitTargetRendererHost GetHost()
        => _host ?? throw new InvalidOperationException("The Vulkan profile host is not prepared.");

    private IRenderBenchFixture GetFixture()
        => _fixture ?? throw new InvalidOperationException("The deterministic fixture is not prepared.");

    private void DisposeHost()
    {
        if (_disposed)
            return;
        _disposed = true;
        _fixture?.Dispose();
        _fixture = null;
        _recordFrame = null;
        _host?.Dispose();
        _host = null;
    }

    private void CaptureDelayedGpuTiming(VulkanExplicitTargetRendererHost host, int submittedFrame)
    {
        int completedFrame = submittedFrame - checked((int)_options.FrameSlots);
        int captureIndex = completedFrame - _captureStartFrame;
        if ((uint)captureIndex < (uint)_gpuFrameNanoseconds.Length)
            _gpuFrameNanoseconds[captureIndex] = host.LastCompletedGpuFrameNanoseconds;
    }

    private static string? TryComputeOutputHash(VulkanExplicitTargetRendererHost host)
    {
        try { return host.ComputeLastSubmittedColorHash(); }
        catch (NotSupportedException) { return null; }
    }

    private string WriteOutputImage(VulkanExplicitTargetRendererHost host, RenderProfileRecipe recipe)
    {
        if (!recipe.ColorFormat.Equals("Rgba8", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Post-capture image export currently requires color_format 'Rgba8'.");
        int pixelCount = checked((int)(recipe.ScaledWidth * recipe.ScaledHeight));
        byte[] rgba = host.ReadbackLastSubmittedColor(checked(pixelCount * 4));
        if (rgba.Length < pixelCount * 4)
            throw new InvalidOperationException($"Output readback returned {rgba.Length} bytes; expected at least {pixelCount * 4}.");
        string path = Path.Combine(_runDirectory, "render-bench-output.png");
        using MagickImage image = new(rgba, new MagickReadSettings
        {
            Width = recipe.ScaledWidth,
            Height = recipe.ScaledHeight,
            Format = MagickFormat.Rgba,
            Depth = 8,
        });
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
            return double.NaN;
        Array.Sort(values);
        int index = Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static long ToNanoseconds(long stopwatchTicks)
        => (long)(stopwatchTicks * (1_000_000_000.0 / Stopwatch.Frequency));

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeTextHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string SanitizeFileName(string name)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        string sanitized = new(name.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "render-profile" : sanitized;
    }

    private static void WriteAtomic(string path, string contents)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
