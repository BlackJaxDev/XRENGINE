using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

public sealed partial class RenderBenchProfileExecutor
{
    private RenderBenchProfileEnvironmentScope? _environmentScope;
    private RenderBenchGpuDiagnostic? _gpuDiagnostic;
    private RenderBenchExternalCaptureArtifacts? _externalCaptureArtifacts;
    private string[] _selectedGpuTargets = [];
    private bool _gpuWorkDrained;
    private bool _cpuSpansEnabled;
    private string _recipeJson = string.Empty;
    private string _sourceCommit = "unavailable";
    private bool _dirtyWorktree = true;
    private DateTimeOffset _processStartUtc;
    private DateTimeOffset _warmupStartUtc;
    private DateTimeOffset _warmupEndUtc;
    private DateTimeOffset _stabilityStartUtc;
    private DateTimeOffset _stabilityEndUtc;
    private DateTimeOffset _captureStartUtc;
    private DateTimeOffset _captureEndUtc;
    private DateTimeOffset _drainStartUtc;
    private DateTimeOffset _drainEndUtc;
    private readonly Dictionary<string, string> _diagnosticArtifacts = new(StringComparer.Ordinal);

    private void PrepareDiagnostics(RenderProfileRecipe recipe)
    {
        _recipeJson = JsonSerializer.Serialize(recipe, RenderProfileRecipe.CreateSerializerOptions(writeIndented: true));
        _environmentScope = new(recipe);
        using Process process = Process.GetCurrentProcess();
        _processStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
        string? commit = RunMetadataCommand("git", ["rev-parse", "HEAD"]);
        if (!string.IsNullOrWhiteSpace(commit))
            _sourceCommit = commit.Trim();
        string? status = RunMetadataCommand("git", ["status", "--porcelain", "--untracked-files=normal"]);
        _dirtyWorktree = status is null || !string.IsNullOrWhiteSpace(status);
        _cpuSpansEnabled = recipe.Instrumentation.HasFlag(RenderProfileInstrumentation.TargetedCpuSpans) ||
            recipe.CpuSamplingPolicy == RenderProfileCpuSamplingPolicy.TargetedSpans;
        VulkanCpuSpanProfiler.Disarm();
        if (_cpuSpansEnabled)
        {
            string[] stages = recipe.CpuProfiling.Stages.Length == 0
                ? Enum.GetNames<EVulkanCpuStage>().Where(static name => name != nameof(EVulkanCpuStage.Count)).ToArray()
                : recipe.CpuProfiling.Stages;
            VulkanCpuSpanProfiler.Configure(stages, recipe.CpuProfiling.CapacityPerThread, recipe.CpuProfiling.EmitMarkers);
        }
    }

    private RenderBenchSourceIdentity CreateSourceIdentity(long generation)
        => new(_sourceCommit, _dirtyWorktree, ComputeFileHash(Assembly.GetExecutingAssembly().Location),
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unavailable",
            generation.ToString(CultureInfo.InvariantCulture));

    private static RenderBenchEnvironment CreateEnvironmentManifest(RenderProfileRecipe recipe, RenderProfilePreparation preparation)
    {
        using Process process = Process.GetCurrentProcess();
        string observers = JsonSerializer.Serialize(new
        {
            recipe.Instrumentation, recipe.CpuSamplingPolicy, recipe.CpuProfiling, recipe.GpuProfiling,
            recipe.ExternalCapture,
            recipe.LabelPolicy, recipe.HardwareCounterPolicy, recipe.EnableValidation, recipe.EnableSynchronizationValidation,
        }, s_jsonOptions);
        return new(RuntimeInformation.OSDescription, process.PriorityClass.ToString(), recipe.ProfileMode.ToString(),
            observers, string.Join(';', preparation.EnabledExtensions.Order(StringComparer.Ordinal)),
            RunMetadataCommand("powercfg", ["/getactivescheme"])?.Trim(), null, null, null,
            "Competing workloads and thermal/clock state were not measured.");
    }

    private void WriteDiagnosticArtifacts(RenderProfileRecipe recipe, VulkanValidationDiagnosticSnapshot validation)
    {
        WriteDiagnostic("validation", "render-profile-validation.json", validation);
        if ((recipe.EnableValidation && !validation.StandardValidationEnabled) ||
            (recipe.EnableSynchronizationValidation && !validation.SynchronizationValidationEnabled))
            throw new NotSupportedException("Requested Vulkan validation layers or synchronization validation were not enabled.");
        if (validation.ErrorCount != 0 || validation.OverflowCount != 0)
            throw new InvalidOperationException($"Vulkan validation reported {validation.ErrorCount} errors and {validation.OverflowCount} dropped messages.");
        if (_cpuSpansEnabled)
        {
            VulkanCpuSpanAnalysis analysis = VulkanCpuSpanProfiler.Analyze();
            WriteDiagnostic("cpu_spans", "render-profile-cpu-spans.json", VulkanCpuSpanProfiler.GetSnapshot());
            WriteDiagnostic("cpu_analysis", "render-profile-cpu-analysis.json", analysis);
            string path = Path.Combine(_runDirectory, "render-profile-cpu-trace.json");
            VulkanCpuSpanProfiler.WriteChromeTrace(path);
            _diagnosticArtifacts.Add("cpu_trace", path);
            if (!analysis.Complete || analysis.Stages.Length == 0)
                throw new InvalidOperationException("Targeted CPU capture was empty or lost span/nesting evidence; inspect the CPU analysis artifact.");
        }
        if (_gpuDiagnostic is not null)
        {
            RenderBenchGpuDiagnosticSnapshot snapshot = _gpuDiagnostic.Snapshot();
            WriteDiagnostic("gpu_queries", "render-profile-gpu-queries.json", snapshot);
            string tracePath = Path.Combine(_runDirectory, "render-profile-timeline.json");
            RenderBenchTraceExporter.Write(tracePath, _diagnosticArtifacts.GetValueOrDefault("cpu_trace"), snapshot);
            _diagnosticArtifacts.Add("timeline", tracePath);
        }
        if (recipe.ExternalCapture.IsRequested)
        {
            _externalCaptureArtifacts = RenderBenchExternalCaptureArtifacts.Attach(recipe.ExternalCapture, _runDirectory);
            WriteDiagnostic("external_capture_manifest", "render-profile-external-captures.json", _externalCaptureArtifacts);
            if (_externalCaptureArtifacts.Required && _externalCaptureArtifacts.HasMissingArtifacts)
                throw new FileNotFoundException("A required external capture artifact was missing at drain; inspect render-profile-external-captures.json.");
        }
        if (recipe.CpuSamplingPolicy is RenderProfileCpuSamplingPolicy.ExternalSamplerOptional or RenderProfileCpuSamplingPolicy.ExternalSamplerRequired)
            WriteDiagnostic("cpu_sampling", "render-profile-cpu-sampling.json", new
            {
                ProcessId = Environment.ProcessId, ProcessStartUtc = _processStartUtc,
                CaptureStartFrameId = _captureStartFrame, CapturedFrames = _submittedFrames - _captureStartFrame - (int)_options.FrameSlots,
                _captureStartUtc, _captureEndUtc, StopwatchFrequency = Stopwatch.Frequency,
                recipe.CpuProfiling.SamplerIdentity, RequestedPolicy = recipe.CpuSamplingPolicy,
                Intrusive = true, SamplerAttachmentVerified = false,
            });
    }

    private bool GpuDiagnosticComplete(int capturedFrames)
    {
        RenderBenchGpuDiagnosticSnapshot snapshot = _gpuDiagnostic!.Snapshot();
        if (snapshot.AbandonedQueries != 0 || snapshot.BudgetOverflowScopes != 0)
            return false;
        if (!_gpuDiagnostic.Enabled)
            return true;
        ulong firstSourceFrameId = _fixture is RenderBenchProductionProfileFixture production
            ? production.FirstCapturedEngineFrameId
            : unchecked((ulong)_captureStartFrame);
        foreach (string target in _selectedGpuTargets)
            for (int frame = 0; frame < capturedFrames; frame++)
                if (!snapshot.Samples.Any(sample => sample.SourceFrameId == firstSourceFrameId + unchecked((ulong)frame) &&
                    string.Equals(sample.Target, target, StringComparison.Ordinal)))
                    return false;
        return true;
    }

    private static void ValidateGpuTargetSelection(RenderProfileRecipe recipe, RenderBenchFixtureManifest fixture)
    {
        string[] targets = recipe.GpuProfiling.Targets;
        if (targets.Length == 0)
            return;
        if (fixture.Kind == RenderBenchFixtureKind.ProductionFullFrame)
            return;
        if (fixture.Kind is not (RenderBenchFixtureKind.GpuPass or RenderBenchFixtureKind.FullPresentationless))
            throw new NotSupportedException($"Fixture '{fixture.Name}' has no selectable GPU passes.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string target in targets)
        {
            if (!seen.Add(target))
                throw new ArgumentException($"GPU target '{target}' is selected more than once.");
            bool found = false;
            for (int pass = 0; pass < fixture.PassIterations; pass++)
                if (string.Equals(target, $"{fixture.Name}.Pass{pass}", StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            if (!found)
                throw new NotSupportedException(
                    $"GPU target '{target}' does not belong to fixture '{fixture.Name}' with {fixture.PassIterations} passes.");
        }
    }

    private void WriteDiagnostic<T>(string key, string fileName, T value)
    {
        string path = Path.Combine(_runDirectory, fileName);
        WriteAtomic(path, JsonSerializer.Serialize(value, s_jsonOptions));
        _diagnosticArtifacts.Add(key, path);
    }

    private RenderBenchArtifactManifest CreateArtifactManifest(string? imagePath)
    {
        string[] Paths(params string[] keys) => keys.Where(_diagnosticArtifacts.ContainsKey).Select(key => _diagnosticArtifacts[key]).ToArray();
        string result = Path.Combine(_runDirectory, "render-bench-result.json");
        return new RenderBenchArtifactManifest(_runDirectory, Path.Combine(_runDirectory, "render-profile-recipe.json"),
            _effectiveConfigurationPath, _workloadIdentityPath, result, Paths("frame_stream"),
            Paths("cpu_spans", "cpu_analysis", "cpu_sampling"), Paths("gpu_queries", "hardware_counters"), Paths("validation"),
            Paths("cpu_trace", "timeline"), imagePath is null ? [] : [imagePath])
        {
            OptionalCaptures = _externalCaptureArtifacts is null
                ? []
                : _externalCaptureArtifacts.Artifacts.Where(static artifact => artifact.Status == "attached")
                    .Select(static artifact => artifact.AttachedPath!).ToArray(),
        };
    }

    private static string? RunMetadataCommand(string executable, string[] arguments)
    {
        try
        {
            ProcessStartInfo start = new(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using Process? process = Process.Start(start);
            if (process is null)
                return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
