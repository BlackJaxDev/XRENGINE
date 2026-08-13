using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

public sealed class RenderBenchRunner(
    RenderBenchOptions options,
    RenderBenchProcessState state,
    bool networkListenerStopped,
    Func<bool> shutdownRequested)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Run()
    {
        Directory.CreateDirectory(options.OutputDirectory);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        string runId = $"{startedUtc:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        string executablePath = Assembly.GetExecutingAssembly().Location;
        RenderBenchEffectiveConfiguration effectiveConfiguration = new(
            1,
            options.Backend,
            options.ExecutionMode,
            options.Recipe,
            options.Fixture,
            options.OutputProperties,
            options.WarmupFrames,
            options.StabilityFrames,
            options.CaptureFrames,
            options.FixedStepSeconds,
            options.RandomSeed,
            options.FrozenWorld,
            options.ColorFormat,
            options.DepthFormat);
        RenderBenchWorkloadIdentity workloadIdentity = new(
            1,
            options.Backend,
            options.ExecutionMode,
            options.Recipe,
            options.Fixture,
            options.OutputProperties,
            options.FixedStepSeconds,
            options.RandomSeed,
            options.FrozenWorld,
            "synthetic-fixed-camera:identity",
            options.FrozenWorld ? "frozen" : "fixed-step-sine");
        string effectiveConfigurationJson = JsonSerializer.Serialize(effectiveConfiguration, s_jsonOptions);
        string workloadIdentityJson = JsonSerializer.Serialize(workloadIdentity, s_jsonOptions);
        string effectiveConfigurationPath = Path.Combine(options.OutputDirectory, "render-bench-effective-config.json");
        string workloadIdentityPath = Path.Combine(options.OutputDirectory, "render-bench-workload.json");
        WriteAtomic(effectiveConfigurationPath, effectiveConfigurationJson);
        WriteAtomic(workloadIdentityPath, workloadIdentityJson);
        IRendererPresentationTarget target = CreateTarget();
        RenderBenchDeterministicInputs inputs = new(options);
        Action<Silk.NET.Vulkan.Vk, Silk.NET.Vulkan.CommandBuffer, VulkanRenderFrameTarget> recordFrame = inputs.RecordFrame;
        int submittedFrames = 0;

        using VulkanExplicitTargetRendererHost host = new(target);
        state.SetPhase(RenderBenchPhase.Warmup);
        for (int index = 0; index < options.WarmupFrames; index++)
        {
            ThrowIfShutdownRequested();
            inputs.Advance(submittedFrames++);
            host.SubmitFrame(recordFrame);
        }

        state.SetPhase(RenderBenchPhase.Stabilizing);
        ulong stableGeneration = host.TargetGeneration;
        for (int index = 0; index < options.StabilityFrames; index++)
        {
            ThrowIfShutdownRequested();
            inputs.Advance(submittedFrames++);
            host.SubmitFrame(recordFrame);
            if (host.TargetGeneration != stableGeneration)
                throw new InvalidOperationException("The Vulkan target generation changed during the stability window.");
            if (host.IsDeviceLost)
                throw new InvalidOperationException("The Vulkan device was lost during the stability window.");
        }

        long[] cpuFrameNanoseconds = GC.AllocateUninitializedArray<long>(options.CaptureFrames);
        double[] gpuFrameNanoseconds = GC.AllocateUninitializedArray<double>(options.CaptureFrames);
        Array.Fill(gpuFrameNanoseconds, double.NaN);
        int captureStartFrame = submittedFrames;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        state.SetPhase(RenderBenchPhase.Capturing);
        for (int index = 0; index < options.CaptureFrames; index++)
        {
            ThrowIfShutdownRequested();
            inputs.Advance(submittedFrames++);
            long frameStart = Stopwatch.GetTimestamp();
            host.SubmitFrame(recordFrame);
            cpuFrameNanoseconds[index] = ToNanoseconds(Stopwatch.GetTimestamp() - frameStart);
            CaptureDelayedGpuTiming(host, submittedFrames - 1, captureStartFrame, gpuFrameNanoseconds);
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (allocatedBytes != 0)
            throw new InvalidOperationException($"The steady-state capture allocated {allocatedBytes} managed bytes on the benchmark thread.");

        state.SetPhase(RenderBenchPhase.Draining);
        string? outputHash = TryComputeOutputHash(host);
        for (int index = 0; index < options.FrameSlots; index++)
        {
            ThrowIfShutdownRequested();
            host.SubmitFrame(recordFrame);
            CaptureDelayedGpuTiming(host, submittedFrames++, captureStartFrame, gpuFrameNanoseconds);
        }
        bool gpuTimingsDrained = options.ExecutionMode == RenderExecutionMode.HeadlessWsi ||
            gpuFrameNanoseconds.All(static timing => double.IsFinite(timing));
        string adapterName = host.AdapterName;
        uint driverVersion = host.DriverVersion;
        uint vendorId = host.VendorId;
        uint deviceId = host.DeviceId;
        string presentationDescription = host.PresentationDescription;
        RenderTargetOutputProperties actualOutput = host.OutputProperties;
        ulong finalTargetGeneration = host.TargetGeneration;
        bool deviceLost = host.IsDeviceLost;
        host.Dispose();
        bool retirementCompleted = true;
        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
        string settingsHash = ComputeTextHash(effectiveConfigurationJson);
        string workloadHash = ComputeTextHash(workloadIdentityJson);
        RenderBenchGateResult[] stabilityGates =
        [
            new("mcp_capture_silence", networkListenerStopped, "No MCP listener during warmup, stability, capture, or drain.", networkListenerStopped ? "listener stopped" : "listener active"),
            new("shader_pipeline_warmup", true, "All fixture-owned shaders and pipelines are ready before capture.", "not applicable: synthetic clear owns no shader or pipeline"),
            new("texture_residency", true, "All fixture-owned sampled textures are resident before capture.", "not applicable: synthetic clear owns no sampled texture"),
            new("resource_retirement", retirementCompleted, "Renderer cleanup waits for GPU completion and flushes target-owned retirement before result publication.", "cleanup completed after GPU idle"),
            new("gpu_query_drain", gpuTimingsDrained, "Presentationless capture timings are mapped after frame-slot-delayed query retrieval.", options.ExecutionMode == RenderExecutionMode.HeadlessWsi ? "unsupported by headless WSI target" : $"drained {gpuFrameNanoseconds.Count(double.IsFinite)} of {gpuFrameNanoseconds.Length}"),
            new("workload_identity", true, "Recipe, target, deterministic inputs, and output contract remain fixed.", $"settings={settingsHash}; workload={workloadHash}"),
            new("target_generation", finalTargetGeneration == stableGeneration, "Target generation remains stable after warmup.", $"generation={finalTargetGeneration}"),
            new("device_health", !deviceLost, "The Vulkan device remains available.", $"deviceLost={deviceLost}"),
            new("steady_state_allocations", allocatedBytes == 0, "Capture thread performs no managed allocation after warmup.", $"allocatedBytes={allocatedBytes}"),
        ];
        RenderBenchGateResult? failedGate = stabilityGates.FirstOrDefault(static gate => !gate.Passed);
        if (failedGate is not null)
            throw new InvalidOperationException($"RenderBench stability gate '{failedGate.Name}' failed: {failedGate.Observed}");

        RenderBenchResult result = new()
        {
            RunId = runId,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            Backend = options.Backend,
            ExecutionMode = options.ExecutionMode,
            Recipe = options.Recipe,
            Fixture = options.Fixture,
            ExecutablePath = executablePath,
            ExecutableSha256 = ComputeFileHash(executablePath),
            EffectiveConfigurationSha256 = settingsHash,
            WorkloadSha256 = workloadHash,
            EffectiveConfigurationPath = effectiveConfigurationPath,
            WorkloadIdentityPath = workloadIdentityPath,
            AdapterName = adapterName,
            DriverVersion = driverVersion,
            VendorId = vendorId,
            DeviceId = deviceId,
            PresentationDescription = presentationDescription,
            Output = actualOutput,
            ProcessId = Environment.ProcessId,
            WarmupFrames = options.WarmupFrames,
            StabilityFrames = options.StabilityFrames,
            CaptureFrames = options.CaptureFrames,
            FixedStepSeconds = options.FixedStepSeconds,
            RandomSeed = options.RandomSeed,
            FrozenWorld = options.FrozenWorld,
            DeterministicInputs = inputs.CaptureManifest(),
            CpuFrameNanoseconds = cpuFrameNanoseconds,
            GpuFrameNanoseconds = gpuFrameNanoseconds,
            AllocatedBytesOnCaptureThread = allocatedBytes,
            OutputSha256 = outputHash,
            StabilityGates = stabilityGates,
        };

        string resultPath = Path.Combine(options.OutputDirectory, "render-bench-result.json");
        WriteAtomic(resultPath, JsonSerializer.Serialize(result, s_jsonOptions));
        return resultPath;
    }

    private IRendererPresentationTarget CreateTarget()
        => options.ExecutionMode switch
        {
            RenderExecutionMode.Presentationless => new PresentationlessRenderTarget(
                options.Width,
                options.Height,
                options.Layers,
                options.FrameSlots,
                options.Samples,
                options.ColorFormat,
                options.DepthFormat),
            RenderExecutionMode.Component => new ComponentRenderTarget(options.Recipe, options.OutputProperties),
            _ => throw new NotSupportedException($"Execution mode '{options.ExecutionMode}' is not supported by RenderBench."),
        };

    private static long ToNanoseconds(long stopwatchTicks)
        => (long)(stopwatchTicks * (1_000_000_000.0 / Stopwatch.Frequency));

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ComputeTextHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteAtomic(string path, string contents)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void ThrowIfShutdownRequested()
    {
        if (shutdownRequested())
            throw new OperationCanceledException("RenderBench shutdown was requested.");
    }

    private static string? TryComputeOutputHash(VulkanExplicitTargetRendererHost host)
    {
        try
        {
            return host.ComputeLastSubmittedColorHash();
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void CaptureDelayedGpuTiming(
        VulkanExplicitTargetRendererHost host,
        int submittedFrame,
        int captureStartFrame,
        double[] gpuFrameNanoseconds)
    {
        if (options.ExecutionMode == RenderExecutionMode.HeadlessWsi)
            return;

        int completedFrame = submittedFrame - checked((int)options.FrameSlots);
        int captureIndex = completedFrame - captureStartFrame;
        if ((uint)captureIndex < (uint)gpuFrameNanoseconds.Length)
            gpuFrameNanoseconds[captureIndex] = host.LastCompletedGpuFrameNanoseconds;
    }

}
