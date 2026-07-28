using System.Text.Json;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Builds the editor and runs the existing game-loop profiler for a scenario matrix.
/// </summary>
public sealed class SelfIterationMeasurementRunner
{
    private static readonly string[] EvidenceFilePatterns =
    [
        "profiler-capture-manifest.json",
        "profiler-capture-summary.json",
        "profiler-render-stats.ndjson",
        "profiler-cpu-frame-*.log",
        "profiler-gpu-pipeline-*.log",
        "profiler-mcp-*.json",
        "profiler-fps-drops.log",
        "profiler-render-stalls.log",
        "log_rendering.log",
        "log_vulkan.log",
        "log_opengl.log",
    ];

    private readonly string _workspaceRoot;
    private readonly string _campaignId;
    private readonly SelfIterationConfiguration _configuration;
    private readonly SelfIterationProcessRunner _processRunner;

    public SelfIterationMeasurementRunner(
        string workspaceRoot,
        SelfIterationConfiguration configuration,
        SelfIterationProcessRunner processRunner)
    {
        _workspaceRoot = workspaceRoot;
        _campaignId = configuration.CampaignId;
        _configuration = configuration;
        _processRunner = processRunner;
    }

    public async Task<IReadOnlyList<SelfIterationScenarioMeasurement>> MeasureMatrixAsync(
        string phaseDirectory,
        string label,
        CancellationToken token)
    {
        Directory.CreateDirectory(phaseDirectory);
        await BuildEditorAsync(phaseDirectory, token);

        List<SelfIterationScenarioMeasurement> measurements = [];
        foreach (SelfIterationScenario scenario in _configuration.Scenarios)
        {
            SelfIterationScenarioMeasurement measurement =
                await MeasureScenarioWithRetriesAsync(scenario, phaseDirectory, label, token);
            measurements.Add(measurement);
        }
        return measurements;
    }

    private async Task BuildEditorAsync(string phaseDirectory, CancellationToken token)
    {
        SelfIterationMeasurementConfiguration measurement = _configuration.Measurement;
        List<string> arguments =
        [
            "build",
            Path.Combine(_workspaceRoot, "XREngine.Editor", "XREngine.Editor.csproj"),
            "--configuration",
            measurement.Configuration,
            "/property:GenerateFullPaths=true",
            "/consoleloggerparameters:NoSummary",
        ];
        if (measurement.NoRestore)
            arguments.Add("--no-restore");

        SelfIterationProcessResult result = await _processRunner.RunAsync(
            "dotnet",
            arguments,
            _workspaceRoot,
            TimeSpan.FromMinutes(20),
            Path.Combine(phaseDirectory, "build"),
            "editor-build",
            cancellationToken: token);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Editor build failed with exit code {result.ExitCode}. See {result.StandardErrorPath}.");
        }
    }

    private async Task<SelfIterationScenarioMeasurement> MeasureScenarioWithRetriesAsync(
        SelfIterationScenario scenario,
        string phaseDirectory,
        string label,
        CancellationToken token)
    {
        SelfIterationScenarioMeasurement? detailedDiagnostics = null;
        if (_configuration.Measurement.RunDetailedDiagnosticCapture)
        {
            detailedDiagnostics = await MeasureScenarioCaptureWithRetriesAsync(
                scenario,
                phaseDirectory,
                label,
                detailedCapture: true,
                detailedDiagnostics: null,
                token);
        }

        SelfIterationScenarioMeasurement formal = await MeasureScenarioCaptureWithRetriesAsync(
            scenario,
            phaseDirectory,
            label,
            detailedCapture: false,
            detailedDiagnostics,
            token);
        if (detailedDiagnostics is not null)
            formal.UseDetailedDiagnosticsFrom(detailedDiagnostics);
        SelfIterationDiagnosisWriter.Write(formal);
        return formal;
    }

    private async Task<SelfIterationScenarioMeasurement> MeasureScenarioCaptureWithRetriesAsync(
        SelfIterationScenario scenario,
        string phaseDirectory,
        string label,
        bool detailedCapture,
        SelfIterationScenarioMeasurement? detailedDiagnostics,
        CancellationToken token)
    {
        SelfIterationScenarioMeasurement? lastMeasurement = null;
        for (int attempt = 1; attempt <= _configuration.Measurement.MaxLaunchAttempts; attempt++)
        {
            string attemptDirectory = Path.Combine(
                phaseDirectory,
                SanitizePathSegment(scenario.Name),
                detailedCapture ? "detailed-diagnostics" : "formal",
                $"launch-{attempt}");
            string profileDirectory = Path.Combine(attemptDirectory, "profile");
            Directory.CreateDirectory(attemptDirectory);

            List<string> arguments = BuildMeasureArguments(
                scenario,
                profileDirectory,
                label,
                detailedCapture);
            TimeSpan timeout = CalculateTimeout(detailedCapture);
            SelfIterationProcessResult result = await _processRunner.RunAsync(
                "powershell.exe",
                arguments,
                _workspaceRoot,
                timeout,
                attemptDirectory,
                "measure",
                scenario.Environment,
                cancellationToken: token);

            string summaryPath = Path.Combine(profileDirectory, "summary.json");
            if (File.Exists(summaryPath))
            {
                lastMeasurement = SelfIterationScenarioMeasurement.Load(
                    scenario,
                    summaryPath,
                    profileDirectory,
                    detailedCapture
                        ? _configuration.Measurement.DiagnosticRepetitions
                        : _configuration.Measurement.Repetitions);
                if (detailedDiagnostics is not null)
                    lastMeasurement.UseDetailedDiagnosticsFrom(detailedDiagnostics);
                CopyEvidence(lastMeasurement);
                SelfIterationDiagnosisWriter.Write(lastMeasurement);
            }

            if (result.Succeeded && lastMeasurement is not null &&
                lastMeasurement.Validate(_configuration.Acceptance).Count == 0)
            {
                return lastMeasurement;
            }

            Console.Error.WriteLine(
                $"Scenario '{scenario.Name}' " +
                $"{(detailedCapture ? "detailed diagnostic" : "formal")} launch {attempt} " +
                "failed validation; " +
                (attempt < _configuration.Measurement.MaxLaunchAttempts ? "relaunching." : "no retries remain."));
        }

        return lastMeasurement
            ?? throw new InvalidOperationException(
                $"Scenario '{scenario.Name}' produced no {(detailedCapture ? "detailed diagnostic" : "formal")} " +
                $"summary after {_configuration.Measurement.MaxLaunchAttempts} launch attempts.");
    }

    private List<string> BuildMeasureArguments(
        SelfIterationScenario scenario,
        string outputDirectory,
        string label,
        bool detailedCapture)
    {
        SelfIterationMeasurementConfiguration measurement = _configuration.Measurement;
        SelfIterationScenarioMeasurementOverrides overrides = scenario.Overrides;
        string profileMode = detailedCapture
            ? measurement.DiagnosticProfileMode
            : measurement.ProfileMode;
        int warmupSeconds = detailedCapture
            ? measurement.DiagnosticWarmupSeconds
            : measurement.WarmupSeconds;
        int captureSeconds = detailedCapture
            ? measurement.DiagnosticCaptureSeconds
            : measurement.CaptureSeconds;
        int repetitions = detailedCapture
            ? measurement.DiagnosticRepetitions
            : measurement.Repetitions;
        List<string> arguments =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Path.Combine(_workspaceRoot, "Tools", "Measure-GameLoopRenderPipeline.ps1"),
            "-Configuration",
            measurement.Configuration,
            "-RenderBackend",
            scenario.RenderBackend,
            "-UnitTestingWorldSettingsPath",
            scenario.UnitTestingWorldSettingsPath,
            "-Strategies",
            scenario.MeshSubmissionStrategy,
            "-ProfileMode",
            profileMode,
            "-CacheMode",
            measurement.CacheMode,
            "-WarmupSec",
            warmupSeconds.ToString(),
            "-CaptureSec",
            captureSeconds.ToString(),
            "-Repetitions",
            repetitions.ToString(),
            "-StabilityWindowSec",
            measurement.StabilityWindowSeconds.ToString(),
            "-StabilityTimeoutSec",
            measurement.StabilityTimeoutSeconds.ToString(),
            "-ShutdownGraceSec",
            measurement.ShutdownGraceSeconds.ToString(),
            "-NoSampleHangSec",
            measurement.NoSampleHangSeconds.ToString(),
            "-ZeroReadbackMaterialDrawPath",
            overrides.ZeroReadbackMaterialDrawPath ?? measurement.ZeroReadbackMaterialDrawPath,
            "-UnitTestVrMode",
            overrides.UnitTestVrMode ?? measurement.UnitTestVrMode,
            "-VulkanRenderTargetMode",
            overrides.VulkanRenderTargetMode ?? measurement.VulkanRenderTargetMode,
            "-VulkanPrimaryReuse",
            overrides.VulkanPrimaryReuse ?? measurement.VulkanPrimaryReuse,
            "-VulkanCommandChains",
            overrides.VulkanCommandChains ?? measurement.VulkanCommandChains,
            "-VulkanParallelCommandChainRecording",
            overrides.VulkanParallelCommandChainRecording ??
                measurement.VulkanParallelCommandChainRecording,
            "-VulkanParallelSecondaryRecording",
            overrides.VulkanParallelSecondaryRecording ??
                measurement.VulkanParallelSecondaryRecording,
            "-OcclusionCullingMode",
            overrides.OcclusionCullingMode ?? measurement.OcclusionCullingMode,
            "-VulkanDiagnosticPreset",
            overrides.VulkanDiagnosticPreset ?? measurement.VulkanDiagnosticPreset,
            "-GpuClockPolicy",
            overrides.GpuClockPolicy ?? measurement.GpuClockPolicy,
            "-RunLabel",
            $"{_campaignId}-{label}-{SanitizePathSegment(scenario.Name)}-" +
                (detailedCapture ? "detailed" : "formal"),
            "-OutputDirectory",
            outputDirectory,
        ];
        AddOptional(arguments, "-ProfileScene", overrides.ProfileScene ?? measurement.ProfileScene);
        AddOptional(arguments, "-ProfileCamera", overrides.ProfileCamera ?? measurement.ProfileCamera);
        AddOptional(arguments, "-ProfileLights", overrides.ProfileLights ?? measurement.ProfileLights);
        AddOptional(
            arguments,
            "-ProfileViewport",
            overrides.ProfileViewport ?? measurement.ProfileViewport);
        AddOptional(arguments, "-RenderScale", overrides.RenderScale ?? measurement.RenderScale);
        double targetRefreshHz = overrides.TargetRefreshHz ?? measurement.TargetRefreshHz;
        if (targetRefreshHz > 0)
        {
            arguments.Add("-TargetRefreshHz");
            arguments.Add(targetRefreshHz.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }
        bool gpuTimestampDense = detailedCapture
            ? measurement.DiagnosticGpuTimestampDense
            : overrides.GpuTimestampDense ?? measurement.GpuTimestampDense;
        bool commandBufferLabels = detailedCapture
            ? measurement.DiagnosticVulkanCommandBufferLabels
            : overrides.VulkanCommandBufferLabels ?? measurement.VulkanCommandBufferLabels;
        if (gpuTimestampDense)
            arguments.Add("-GpuTimestampDense");
        if (commandBufferLabels)
            arguments.Add("-VulkanCommandBufferLabels");
        arguments.AddRange(measurement.AdditionalMeasureArguments);
        arguments.AddRange(overrides.AdditionalMeasureArguments);
        return arguments;
    }

    private TimeSpan CalculateTimeout(bool detailedCapture)
    {
        SelfIterationMeasurementConfiguration measurement = _configuration.Measurement;
        int warmupSeconds = detailedCapture
            ? measurement.DiagnosticWarmupSeconds
            : measurement.WarmupSeconds;
        int captureSeconds = detailedCapture
            ? measurement.DiagnosticCaptureSeconds
            : measurement.CaptureSeconds;
        int repetitions = detailedCapture
            ? measurement.DiagnosticRepetitions
            : measurement.Repetitions;
        int perRepetition = warmupSeconds +
            measurement.StabilityTimeoutSeconds +
            captureSeconds +
            measurement.ShutdownGraceSeconds +
            60;
        return TimeSpan.FromSeconds(Math.Max(120, perRepetition * repetitions));
    }

    private static void CopyEvidence(SelfIterationScenarioMeasurement measurement)
    {
        string destinationRoot = Path.Combine(measurement.EvidenceDirectory, "logs");
        for (int index = 0; index < measurement.LogDirectories.Length; index++)
        {
            string source = measurement.LogDirectories[index];
            if (!Directory.Exists(source))
                continue;
            string destination = Path.Combine(destinationRoot, $"repetition-{index + 1}");
            Directory.CreateDirectory(destination);
            foreach (string pattern in EvidenceFilePatterns)
            {
                foreach (string file in Directory.EnumerateFiles(source, pattern, SearchOption.TopDirectoryOnly))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
        }

        string indexPath = Path.Combine(measurement.EvidenceDirectory, "evidence-index.json");
        File.WriteAllText(
            indexPath,
            JsonSerializer.Serialize(
                new
                {
                    measurement.ScenarioName,
                    measurement.SummaryPath,
                    sourceLogDirectories = measurement.LogDirectories,
                    copiedLogDirectory = destinationRoot,
                    measurement.MinimumCpuTimingDumpFiles,
                    measurement.MinimumGpuTimingDumpFiles,
                    measurement.DetailedEvidenceDirectory,
                    measurement.DetailedCpuTimingDumpFiles,
                    measurement.DetailedGpuTimingDumpFiles,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AddOptional(List<string> arguments, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        arguments.Add(name);
        arguments.Add(value);
    }

    internal static string SanitizePathSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray())
            .Trim()
            .Replace(' ', '-');
    }
}
