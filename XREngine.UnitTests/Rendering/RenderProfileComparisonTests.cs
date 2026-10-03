using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Profiling;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class RenderProfileComparisonTests
{
    private const string DefaultOutputHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    [Test]
    public void CleanRecipe_RejectsTargetedSamplingAndValidation()
    {
        RenderProfileRecipe clean = new()
        {
            Name = "clean-control",
            Component = "HarnessSubmission",
            Fixture = "noop-control",
            ProfileMode = RenderProfileMode.CleanProfile,
            LabelPolicy = RenderProfileLabelPolicy.Disabled,
        };
        clean.Validate();
        Should.Throw<ArgumentException>(() => (clean with
        {
            CpuSamplingPolicy = RenderProfileCpuSamplingPolicy.TargetedSpans,
        }).Validate());
        Should.Throw<ArgumentException>(() => (clean with
        {
            EnableValidation = true,
        }).Validate());
        Should.Throw<ArgumentException>(() => (clean with
        {
            EnableSynchronizationValidation = true,
        }).Validate());
    }

    [Test]
    public void Comparison_ReportsCompatibleAbbaAndRejectsFixtureMismatch()
    {
        using Fixture fixture = new();
        ComparisonExecution passing = fixture.Run();
        passing.ExitCode.ShouldBe(0, passing.Output);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(fixture.ReportPath));
        report.RootElement.GetProperty("status").GetString().ShouldBe("pass");
        report.RootElement.GetProperty("repetitionsPerVariant").GetInt32().ShouldBe(4);
        report.RootElement.GetProperty("scoreboard").GetProperty("componentSavingsMilliseconds")
            .GetDouble().ShouldBeGreaterThan(0);

        using Fixture incompatible = new(candidateFixture: "different-control");
        ComparisonExecution rejected = incompatible.Run();
        rejected.ExitCode.ShouldNotBe(0);
        rejected.Output.ShouldContain("incompatible fixture manifest");
        File.Exists(incompatible.ReportPath).ShouldBeFalse();

        using Fixture changedPixels = new(candidateOutputHash: new string('B', 64));
        ComparisonExecution outputRejected = changedPixels.Run();
        outputRejected.ExitCode.ShouldNotBe(0);
        outputRejected.Output.ShouldContain("outputSha256");
        File.Exists(changedPixels.ReportPath).ShouldBeFalse();
    }

    [Test]
    public void FullFrameEvidence_RejectsUnrelatedSourceVariants()
    {
        using Fixture fixture = new();
        string reportPath = Path.Combine(fixture.RunRoot, "unrelated-full-frame.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            status = "pass", lane = "presentationless", evidenceKind = "cleanComparison",
            evidenceScope = "productionFullFrame",
            variantIdentity = new
            {
                baseline = new { source = new { commit = "unrelated", dirtyWorktree = false,
                    executableSha256 = "unrelated", buildConfiguration = "Release", backendModuleGeneration = "1" } },
                candidate = new { source = new { commit = "unrelated", dirtyWorktree = false,
                    executableSha256 = "unrelated", buildConfiguration = "Release", backendModuleGeneration = "1" } },
            },
            scoreboard = new { demonstratedFullFrameSavingsMilliseconds = 1.0 },
        }));
        ComparisonExecution rejected = fixture.Run("-FullFrameComparisonReportPath", reportPath);
        rejected.ExitCode.ShouldNotBe(0);
        rejected.Output.ShouldContain("does not match the component variant");
        File.Exists(fixture.ReportPath).ShouldBeFalse();
    }

    [Test]
    public void FullFrameEvidence_RejectsDifferentWorkerExperimentWithSameSources()
    {
        using Fixture fixture = new(candidateWorkerCount: 4);
        JsonElement baselineSource = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(fixture.BaselineFirstPath))
            .GetProperty("source");
        JsonElement candidateSource = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(fixture.CandidateFirstPath))
            .GetProperty("source");
        string reportPath = Path.Combine(fixture.RunRoot, "wrong-worker-full-frame.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            status = "pass", lane = "presentationless", evidenceKind = "cleanComparison",
            evidenceScope = "productionFullFrame",
            variantIdentity = new
            {
                baseline = new { source = baselineSource, workerCount = 1, mutationPolicy = "StableReuse" },
                candidate = new { source = candidateSource, workerCount = 2, mutationPolicy = "StableReuse" },
            },
            scoreboard = new { demonstratedFullFrameSavingsMilliseconds = 1.0 },
        }));
        ComparisonExecution rejected = fixture.Run("-AllowWorkerVariation",
            "-FullFrameComparisonReportPath", reportPath);
        rejected.ExitCode.ShouldNotBe(0);
        rejected.Output.ShouldContain("candidate workerCount does not match");
        File.Exists(fixture.ReportPath).ShouldBeFalse();
    }

    [Test]
    public void AcceptedBaseline_RequiresExplicitReplacementAndLeavesExistingBytesUntouched()
    {
        string acceptedPath;
        byte[] acceptedBytes;
        using (Fixture accepted = new())
        {
            acceptedPath = Path.Combine(accepted.RunRoot, "accepted-baseline.json");
            ComparisonExecution initial = accepted.Run("-AcceptBaseline", "-AcceptedBaselinePath", acceptedPath);
            initial.ExitCode.ShouldBe(0, initial.Output);
            acceptedBytes = File.ReadAllBytes(acceptedPath);

            using Fixture second = new();
            ComparisonExecution refused = second.Run("-AcceptBaseline", "-AcceptedBaselinePath", acceptedPath);
            refused.ExitCode.ShouldNotBe(0);
            refused.Output.ShouldContain("Accepted baseline already exists");
            File.ReadAllBytes(acceptedPath).SequenceEqual(acceptedBytes).ShouldBeTrue();
        }
    }

    [Test]
    public void SyntheticPresentationlessProxy_DoesNotClaimFullFrameSavings()
    {
        using Fixture proxy = new(presentationlessProxy: true);
        ComparisonExecution comparison = proxy.Run();
        comparison.ExitCode.ShouldBe(0, comparison.Output);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(proxy.ReportPath));
        report.RootElement.GetProperty("evidenceScope").GetString().ShouldBe("syntheticProxy");
        JsonElement scoreboard = report.RootElement.GetProperty("scoreboard");
        scoreboard.GetProperty("demonstratedFullFrameSavingsMilliseconds").ValueKind.ShouldBe(JsonValueKind.Null);
        scoreboard.GetProperty("broaderLaneResult").GetString().ShouldBe("unresolved");

        using Fixture component = new();
        string proxyReportPath = Path.Combine(component.RunRoot, "proxy-full-frame.json");
        File.Copy(proxy.ReportPath, proxyReportPath);
        ComparisonExecution rejected = component.Run("-FullFrameComparisonReportPath", proxyReportPath);
        rejected.ExitCode.ShouldNotBe(0);
        rejected.Output.ShouldContain("production full-frame");
    }

    private readonly record struct ComparisonExecution(int ExitCode, string Output);

    private sealed class Fixture : IDisposable
    {
        private readonly string _repoRoot;
        private readonly string[] _baselinePaths = new string[4];
        private readonly string[] _candidatePaths = new string[4];
        private readonly string _baselineRecipePath;
        private readonly string _candidateRecipePath;
        private readonly bool _presentationlessProxy;

        public Fixture(string candidateFixture = "noop-control", string candidateOutputHash = DefaultOutputHash,
            int candidateWorkerCount = 1, bool presentationlessProxy = false)
        {
            _presentationlessProxy = presentationlessProxy;
            _repoRoot = FindRepositoryRoot();
            RunRoot = Path.Combine(_repoRoot, "Build", "_AgentValidation", "00000000-000000-shared",
                "comparison-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RunRoot);
            _baselineRecipePath = Path.Combine(RunRoot, "baseline-recipe.json");
            _candidateRecipePath = Path.Combine(RunRoot, "candidate-recipe.json");
            string fixtureName = presentationlessProxy ? "presentationless-deferred" : "noop-control";
            File.WriteAllText(_baselineRecipePath,
                $"{{\"schema_version\":1,\"name\":\"before\",\"fixture\":\"{fixtureName}\",\"worker_counts\":[1]}}");
            File.WriteAllText(_candidateRecipePath,
                $"{{\"schema_version\":1,\"name\":\"after\",\"fixture\":\"{fixtureName}\",\"worker_counts\":[{candidateWorkerCount}]}}");

            DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            int[] baselineSlots = [0, 3, 4, 7];
            int[] candidateSlots = [1, 2, 5, 6];
            for (int index = 0; index < 4; index++)
            {
                _baselinePaths[index] = WriteResult("A", index, baselineSlots[index], 2.0, fixtureName, start);
                _candidatePaths[index] = WriteResult("B", index, candidateSlots[index], 1.8,
                    presentationlessProxy ? fixtureName : candidateFixture, start,
                    candidateOutputHash, candidateWorkerCount);
            }
        }

        public string RunRoot { get; }
        public string ReportPath => Path.Combine(RunRoot, "render-profile-comparison.json");
        public string BaselineFirstPath => _baselinePaths[0];
        public string CandidateFirstPath => _candidatePaths[0];

        public ComparisonExecution Run(params string[] extraArguments)
        {
            string script = Path.Combine(_repoRoot, "Tools", "Benchmarks", "Invoke-RenderProfileComparison.ps1");
            string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
            string ArrayLiteral(string[] paths) => "@(" + string.Join(",", paths.Select(Quote)) + ")";
            string command = $"& {Quote(script)} -RunRoot {Quote(RunRoot)} " +
                $"-BaselineRecipePath {Quote(_baselineRecipePath)} -CandidateRecipePath {Quote(_candidateRecipePath)} " +
                $"-BaselineResultPaths {ArrayLiteral(_baselinePaths)} -CandidateResultPaths {ArrayLiteral(_candidatePaths)} " +
                (_presentationlessProxy ? "-Lane presentationless " : string.Empty) +
                string.Join(" ", extraArguments.Select(value => value.StartsWith("-", StringComparison.Ordinal) ? value : Quote(value)));
            ProcessStartInfo info = new("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(command);
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Render profile comparison did not finish within 30 seconds.");
            }
            return new(process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }

        public void Dispose() => Directory.Delete(RunRoot, recursive: true);

        private string WriteResult(string variant, int index, int slot, double gpuP95, string fixture,
            DateTimeOffset origin, string outputHash = DefaultOutputHash, int workerCount = 1)
        {
            string path = Path.Combine(RunRoot, $"{variant}-{index}.json");
            string executablePath = Path.Combine(RunRoot, $"binary-{variant}.dat");
            if (!File.Exists(executablePath))
                File.WriteAllText(executablePath, variant);
            string executableHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executablePath)));
            string recipePath = variant == "A" ? _baselineRecipePath : _candidateRecipePath;
            string recipeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recipePath)));
            DateTimeOffset started = origin.AddMinutes(slot);
            object statistics = new
            {
                n = 1, p50 = gpuP95, p90 = gpuP95, p95 = gpuP95, p99 = gpuP95,
                worst = gpuP95, mean = gpuP95, standardDeviation = 0, medianAbsoluteDeviation = 0,
            };
            object result = new
            {
                schemaVersion = 2, runId = $"{variant}-{index}", startedUtc = started,
                completedUtc = started.AddSeconds(5), backend = "Vulkan",
                executionMode = _presentationlessProxy ? "Presentationless" : "Component",
                executablePath, executableSha256 = executableHash, recipeSha256 = recipeHash,
                fixture = _presentationlessProxy ? fixture : "noop-control",
                workloadSha256 = "same-workload", adapterName = "test-adapter",
                outputSha256 = outputHash,
                vendorId = 1, deviceId = 2, driverVersion = 3, presentationDescription = "component",
                output = new { width = 1, height = 1 }, layoutPolicy = "specialized", processId = slot + 1,
                captureFrames = 1, cpuFrameNanoseconds = new[] { (long)(gpuP95 * 1_000_000) },
                gpuFrameNanoseconds = new[] { gpuP95 * 1_000_000 },
                allocatedBytesOnCaptureThread = 0L,
                allocatedBytesOnFixtureWorkers = 0L,
                cpuFrameStatistics = statistics,
                gpuFrameStatistics = statistics,
                fixtureManifest = new { name = fixture, component = "HarnessSubmission",
                    kind = _presentationlessProxy ? "FullPresentationless" : "Control",
                    workerCount, mutationPolicy = "StableReuse", outputIdentity = "none" },
                targetManifest = new { presentationTarget = _presentationlessProxy ? "Presentationless" : "Component", outputCount = 1,
                    graphicsQueueFamily = 0, presentPolicy = "NoAcquireNoPresent", backendModuleSha256 = "module" },
                workCounters = new { submissions = 1 },
                stabilityGates = new[] { new { name = "valid", passed = true } },
                source = new { commit = variant, dirtyWorktree = false, executableSha256 = executableHash,
                    buildConfiguration = "Release", backendModuleGeneration = "1" },
                environment = new { operatingSystem = "Windows", processPriority = "Normal",
                    profileMode = "CleanProfile", instrumentationManifest = "none", extensionManifest = "ext",
                    powerPolicy = (string?)null, clockPolicy = (string?)null,
                    targetRefreshHertz = (double?)null, thermalNotes = (string?)null,
                    competingWorkloadWarnings = (string?)null },
                intervals = new { processStartUtc = started, warmupStartUtc = started,
                    warmupEndUtc = started, stabilityStartUtc = started, stabilityEndUtc = started,
                    captureStartUtc = started, captureEndUtc = started.AddSeconds(1),
                    drainStartUtc = started.AddSeconds(1), drainEndUtc = started.AddSeconds(2),
                    processEndUtc = started.AddSeconds(5) },
                artifactManifest = new { runRoot = RunRoot, recipePath,
                    effectiveConfigurationPath = path, workloadIdentityPath = path, resultPath = path },
                isIntrusive = false,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(result));
            return path;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Repository root was not found from test output.");
        }
    }
}
