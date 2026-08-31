using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Fresh-process proof for Vulkan pipeline persistence. The parent launches a
/// cold child then a warm child with the same explicit cache root; each child
/// retains production submissions and treats only frames after preparation as
/// steady-state evidence.
/// </summary>
internal static class RenderBenchPipelineScenario
{
    internal static async Task<int> RunAsync(RenderBenchOptions options)
    {
        if (options.Scenario != "phase53-pipelines")
            throw new ArgumentException("The pipeline scenario requires phase53-pipelines.");
        if (options.ScenarioLane is not null)
            return RunLane(options);

        List<string> failures = [];
        List<string> children = [];
        List<RenderBenchScenarioResult> childResults = [];
        string[] depths = options.ScenarioDepth == "both" ? ["normal", "reversed"] : [options.ScenarioDepth];
        for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
        foreach (string depth in depths)
        {
            string cacheRoot = Path.Combine(options.ScenarioCacheRoot!, $"{depth}-repeat-{repeat}");
            if (Directory.Exists(cacheRoot) && Directory.EnumerateFileSystemEntries(cacheRoot).Any())
                throw new InvalidOperationException($"Pipeline cache root must be empty for a cold cohort: {cacheRoot}");
            Directory.CreateDirectory(cacheRoot);
            RenderBenchPhase53ChildResult coldChild = await RenderBenchPhase53ProcessRunner.RunChildAsync(options, "cold", depth, repeat, cacheRoot).ConfigureAwait(false);
            RenderBenchPhase53ChildResult warmChild = await RenderBenchPhase53ProcessRunner.RunChildAsync(options, "warm", depth, repeat, cacheRoot).ConfigureAwait(false);
            children.Add(coldChild.ResultPath);
            children.Add(warmChild.ResultPath);
            RenderBenchScenarioResult cold = ReadChild(coldChild);
            RenderBenchScenarioResult warm = ReadChild(warmChild);
            childResults.Add(cold);
            childResults.Add(warm);
            ValidatePair(depth, repeat, cold, warm, failures);
        }

        RenderBenchScenarioResult summary = new()
        {
            Scenario = options.Scenario,
            Lane = "matrix",
            Depth = options.ScenarioDepth,
            Workload = "production-default-hiz",
            Width = options.Width,
            Height = options.Height,
            Status = failures.Count == 0 ? "passed" : "failed",
            Failure = failures.FirstOrDefault(),
            Failures = [.. failures],
            ChildResults = [.. children],
            DiagnosticReadbacks = false,
            PipelineScenario = childResults.LastOrDefault()?.PipelineScenario,
        };
        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), summary);
        foreach (string failure in failures)
            Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static int RunLane(RenderBenchOptions options)
    {
        List<RenderBenchScenarioFrame> frames = [];
        RenderBenchScenarioResult result = CreateIdentity(options);
        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPipelineCacheRoot, options.ScenarioCacheRoot);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPipelinePrewarmCapture, "1");
            Environment.SetEnvironmentVariable("XRE_FORCE_MESH_SUBMISSION_STRATEGY", "GpuIndirectZeroReadback");
            Environment.SetEnvironmentVariable("XRE_ENGINE_ASSETS_PATH", Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets"));
            Environment.SetEnvironmentVariable("XRE_GPU_HIZ_COARSE_TILES", "1");
            Environment.SetEnvironmentVariable("XRE_GPU_HIZ_COARSE_TILES_CULL", "1");
            using RenderBenchProductionScene scene = new(options, EOcclusionCullingMode.GpuHiZ);
            if (RuntimeEngine.Windows.Count != 0 || scene.Host.PresentationUsesDesktopCompositor)
                throw new InvalidOperationException("Pipeline cohorts must remain presentationless.");
            scene.ConfigureScenarioWorkload(RenderBenchScenarioWorkloads.HeavyStatic);
            scene.SetFixtureOccludersActive(true);
            int preparationFrames = Math.Min(8, options.ScenarioFrames - 2);
            VulkanPipelineCacheDiagnostic? preparation = null;
            for (int step = 0; step < options.ScenarioFrames; step++)
            {
                VulkanExplicitProductionSubmissionReceipt receipt = scene.SubmitStep(options.FixedStepSeconds);
                RenderBenchScenarioLane.WaitForCompletion(scene.Host, in receipt);
                frames.Add(new RenderBenchScenarioFrame
                {
                    Step = step,
                    Workload = "production-default-hiz",
                    Mutation = step < preparationFrames ? "explicit-startup-preparation" : "steady-production",
                    EngineFrameId = receipt.EngineFrameId,
                    CollectGeneration = scene.LastCollectGeneration,
                    Submission = receipt,
                });
                if (step == preparationFrames - 1)
                    preparation = scene.Host.CapturePipelineCacheDiagnostic();
            }

            VulkanPipelineCacheDiagnostic completion = scene.Host.CapturePipelineCacheDiagnostic();
            VulkanPipelineCacheDiagnostic baseline = preparation ?? completion;
            VulkanPipelineTelemetrySnapshot steady = completion.Telemetry - baseline.Telemetry;
            ValidatePreparation(baseline.Telemetry);
            ValidateSteadyState(steady);
            if (string.Equals(options.ScenarioLane, "cold", StringComparison.OrdinalIgnoreCase) &&
                scene.PipelineAdmissionRetryCount == 0)
            {
                throw new InvalidOperationException(
                    "The cold pipeline cohort did not observe the required late compute admission retry.");
            }
            VulkanValidationDiagnosticSnapshot validation = scene.Host.CaptureValidationDiagnostics();
            if (!validation.StandardValidationEnabled || !validation.SynchronizationValidationEnabled)
            {
                throw new InvalidOperationException(
                    "Pipeline evidence requires standard and synchronization Vulkan validation.");
            }
            if (validation.ErrorCount != 0)
                throw new InvalidOperationException($"Native Vulkan validation reported {validation.ErrorCount} errors.");
            result = result with
            {
                Status = "passed",
                Frames = [.. frames],
                Adapter = scene.Host.AdapterName,
                Driver = scene.Host.DriverVersion,
                VendorId = scene.Host.VendorId,
                DeviceId = scene.Host.DeviceId,
                NativeValidation = validation,
                PipelineScenario = new()
                {
                    PreparationFrameCount = preparationFrames,
                    SteadyFrameCount = options.ScenarioFrames - preparationFrames,
                    PipelineAdmissionRetryCount = scene.PipelineAdmissionRetryCount,
                    PipelineAdmissionRetryMilliseconds = scene.PipelineAdmissionRetryMilliseconds,
                    Preparation = baseline,
                    Completion = completion,
                    SteadyStateTelemetry = steady,
                },
            };
        }
        catch (Exception exception)
        {
            result = result with { Status = "failed", Failure = exception.ToString(), Frames = [.. frames] };
            Console.Error.WriteLine(exception);
        }

        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), result);
        return result.Status == "passed" ? 0 : 1;
    }

    private static RenderBenchScenarioResult CreateIdentity(RenderBenchOptions options)
    {
        string inputs = JsonSerializer.Serialize(new
        {
            Version = 1,
            options.Width,
            options.Height,
            options.FrameSlots,
            options.FixedStepSeconds,
            options.ScenarioDepth,
            options.ScenarioFrames,
            options.RandomSeed,
            options.ScenarioLane,
            Pipeline = "production-default-hiz;explicit-startup-preparation;steady-no-native-create",
        }, RenderBenchScenarioRunner.JsonOptions);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "scenario-input.json"), inputs);
        return new()
        {
            Scenario = options.Scenario!,
            Lane = options.ScenarioLane!,
            Depth = options.ScenarioDepth,
            Workload = "production-default-hiz",
            Width = options.Width,
            Height = options.Height,
            DiagnosticReadbacks = false,
            InputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs))),
        };
    }

    private static void ValidateSteadyState(VulkanPipelineTelemetrySnapshot telemetry)
    {
        if (telemetry.PendingGraphicsPipelineCount != 0)
            throw new InvalidOperationException($"Steady production retained {telemetry.PendingGraphicsPipelineCount} pending graphics pipeline(s).");
        if (telemetry.PendingComputePipelineCount != 0)
            throw new InvalidOperationException($"Steady production retained {telemetry.PendingComputePipelineCount} pending compute pipeline(s).");
        if (telemetry.GraphicsPipelineCreateCount != 0 || telemetry.ComputePipelineCreateCount != 0)
            throw new InvalidOperationException($"Steady production created native pipelines: graphics={telemetry.GraphicsPipelineCreateCount}, compute={telemetry.ComputePipelineCreateCount}.");
        if (telemetry.RenderThreadShaderCompileCount != 0)
            throw new InvalidOperationException($"Steady production compiled {telemetry.RenderThreadShaderCompileCount} shader(s) on the render thread.");
        if (telemetry.ForegroundPipelineWaitCount != 0)
            throw new InvalidOperationException($"Steady production waited for {telemetry.ForegroundPipelineWaitCount} foreground pipeline compile(s).");
    }

    private static void ValidatePreparation(VulkanPipelineTelemetrySnapshot telemetry)
    {
        if (telemetry.AsyncQueueCount == 0 ||
            telemetry.WorkerPipelineCreateCount == 0 ||
            telemetry.ComputePipelineCreateCount == 0)
        {
            throw new InvalidOperationException(
                "Pipeline preparation did not prove queued worker-native compute pipeline creation.");
        }
    }

    private static RenderBenchScenarioResult ReadChild(RenderBenchPhase53ChildResult child)
    {
        RenderBenchScenarioResult result = JsonSerializer.Deserialize<RenderBenchScenarioResult>(
            File.ReadAllText(child.ResultPath), RenderBenchScenarioRunner.JsonOptions)
            ?? throw new InvalidOperationException($"Invalid pipeline child evidence: {child.ResultPath}");
        return child.ExitCode == 0 || result.Status != "passed"
            ? result
            : result with { Status = "failed", Failure = $"Child exited {child.ExitCode} despite a passing payload." };
    }

    private static void ValidatePair(
        string depth,
        int repeat,
        RenderBenchScenarioResult cold,
        RenderBenchScenarioResult warm,
        List<string> failures)
    {
        string prefix = $"{depth}/repeat-{repeat}";
        if (cold.Status != "passed")
            failures.Add($"{prefix}/cold: {cold.Failure ?? "pipeline scenario failed"}");
        if (warm.Status != "passed")
            failures.Add($"{prefix}/warm: {warm.Failure ?? "pipeline scenario failed"}");
        VulkanPipelineCacheDiagnostic? coldCache = cold.PipelineScenario?.Completion;
        VulkanPipelineCacheDiagnostic? warmCache = warm.PipelineScenario?.Preparation;
        if (coldCache is null || warmCache is null)
        {
            failures.Add($"{prefix}: missing pipeline cache diagnostics.");
            return;
        }
        if (warmCache.NativePipelineCacheInitialBytes <= 0)
            failures.Add($"{prefix}/warm: no persisted native pipeline cache bytes were loaded.");
        if (!string.Equals(coldCache.Identity.EngineAssemblySha256, warmCache.Identity.EngineAssemblySha256, StringComparison.Ordinal) ||
            !string.Equals(coldCache.Identity.DriverIdentity, warmCache.Identity.DriverIdentity, StringComparison.Ordinal) ||
            !string.Equals(coldCache.Identity.EffectiveTargetMode, warmCache.Identity.EffectiveTargetMode, StringComparison.Ordinal))
            failures.Add($"{prefix}: cold/warm engine, driver, or target-mode cache identities differ.");
        if (coldCache.Identity.ShaderArtifactFingerprints.Length == 0 || warmCache.Identity.ShaderArtifactFingerprints.Length == 0)
            failures.Add($"{prefix}: pipeline cache identity has no observed shader artifact fingerprints.");
    }
}
