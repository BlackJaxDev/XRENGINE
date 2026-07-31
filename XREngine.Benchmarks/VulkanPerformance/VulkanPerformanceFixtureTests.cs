using System.Text.Json;

namespace XREngine.Benchmarks;

/// <summary>
/// GPU-free fixture tests for the Vulkan performance evidence evaluator.
/// </summary>
public static class VulkanPerformanceFixtureTests
{
    private static readonly JsonSerializerOptions s_fixtureJson = new()
    {
        WriteIndented = true,
    };

    public static int Run(string workspaceRoot)
    {
        string validationRoot = Path.Combine(
            workspaceRoot,
            "Build",
            "_AgentValidation");
        Directory.CreateDirectory(validationRoot);
        string fixtureRoot = Path.Combine(
            validationRoot,
            $"vulkan-perf-fixtures-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);

        try
        {
            TestPercentileAndVariance();
            TestQuickStatusAndCommandExitCodes(fixtureRoot);
            TestPromotionBaselineAndSeededRegression(fixtureRoot);
            TestAdvisoryAbsoluteBudgetIsRecordedWithoutBlocking(fixtureRoot);
            TestFullScanDiagnosticDoesNotUseCompactOnlyGates(fixtureRoot);
            TestManifestMismatchReportsExactField(fixtureRoot);
            TestInvalidCaptureRejection(fixtureRoot);
            TestMultiRateRequiredOutput(fixtureRoot);
            TestPrimaryReuseRatioGate(fixtureRoot);
            TestStructurallyIneligiblePrimaryRecordsAreExcluded(fixtureRoot);
            TestSelectedGateScope(fixtureRoot);
            TestProfileContractEnforcement(fixtureRoot);
            Console.WriteLine("Vulkan performance fixture tests: 12 passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Vulkan performance fixture test failed: {exception.Message}");
            return 1;
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static void TestPercentileAndVariance()
    {
        double[] values = [1.0, 2.0, 3.0, 4.0, 100.0];
        AssertEqual(
            3.0,
            VulkanPerformanceEvaluator.Percentile(values, 0.50),
            "nearest-rank p50");
        AssertEqual(
            100.0,
            VulkanPerformanceEvaluator.Percentile(values, 0.95),
            "nearest-rank p95");
        AssertEqual(
            9.0,
            VulkanPerformanceEvaluator.Percentile(
                [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0],
                0.90),
            "nearest-rank p90");
        AssertEqual(
            20.0,
            VulkanPerformanceEvaluator.CalculateRelativeRangePercent(
                [4.5, 5.0, 5.5],
                5.0),
            "relative range");
    }

    private static void TestQuickStatusAndCommandExitCodes(
        string fixtureRoot)
    {
        string root = Path.Combine(fixtureRoot, "quick");
        Fixture fixture = CreateFixture(
            root,
            "Quick",
            promotionEligible: false,
            repetitions: 1,
            budgetValues: [4.0]);
        VulkanPerformanceEvaluationReport report =
            Evaluate(fixture, baseline: null, acceptingBaseline: false);
        Assert(
            report.Status == "NonPromotableQuickRun",
            "Quick must report NonPromotableQuickRun.");
        Assert(
            report.PromotionStatus == "NonPromotableQuickRun",
            "Quick promotion status must be NonPromotableQuickRun.");
        Assert(
            report.ProfileMode == "CleanProfile",
            "Quick report must identify its profile mode.");
        Assert(
            report.CleanComparisonSuitable,
            "Quick report must identify clean-comparison suitability.");
        Assert(
            !string.IsNullOrWhiteSpace(report.ExpectedObserverOverhead),
            "Quick report must describe expected observer overhead.");
        Assert(report.Issues.Count == 0, "Valid Quick fixture must pass.");

        string outputPath = Path.Combine(root, "command-output.json");
        int successExitCode = VulkanPerformanceCommand.Run(
        [
            "--vulkan-perf",
            "--contract", fixture.ContractPath,
            "--run-manifest", fixture.RunManifestPath,
            "--out", outputPath,
        ]);
        AssertEqual(0, successExitCode, "valid command exit code");

        int malformedExitCode = VulkanPerformanceCommand.Run(
            ["--vulkan-perf"]);
        AssertEqual(2, malformedExitCode, "malformed command exit code");
    }

    private static void TestPromotionBaselineAndSeededRegression(
        string fixtureRoot)
    {
        Fixture baselineFixture = CreateFixture(
            Path.Combine(fixtureRoot, "baseline"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.0, 4.0, 4.0]);
        VulkanPerformanceEvaluationReport baseline = Evaluate(
            baselineFixture,
            baseline: null,
            acceptingBaseline: true);
        Assert(
            baseline.Status == "PromotionPass",
            "Accepted valid baseline must pass.");

        Fixture regressedFixture = CreateFixture(
            Path.Combine(fixtureRoot, "regressed"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.8, 4.8, 4.8]);
        VulkanPerformanceEvaluationReport regressed = Evaluate(
            regressedFixture,
            baseline,
            acceptingBaseline: false);
        AssertIssue(regressed, "BaselineRegression");
        Assert(
            regressed.Status == "Fail",
            "Seeded regression must fail promotion.");

        Fixture budgetFixture = CreateFixture(
            Path.Combine(fixtureRoot, "budget"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [5.5, 5.5, 5.5]);
        VulkanPerformanceEvaluationReport overBudget = Evaluate(
            budgetFixture,
            baseline: null,
            acceptingBaseline: true);
        AssertIssue(overBudget, "AbsoluteBudgetExceeded");
    }

    private static void TestAdvisoryAbsoluteBudgetIsRecordedWithoutBlocking(
        string fixtureRoot)
    {
        Fixture fixture = CreateFixture(
            Path.Combine(fixtureRoot, "advisory-budget"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [5.5, 5.5, 5.5],
            enforceAbsoluteBudget: false);
        VulkanPerformanceEvaluationReport report = Evaluate(
            fixture,
            baseline: null,
            acceptingBaseline: true);

        Assert(
            !report.Cohorts.Single().WithinAbsoluteBudget,
            "Advisory budget result must still be recorded.");
        Assert(
            report.Issues.All(static issue =>
                issue.Code != "AbsoluteBudgetExceeded"),
            "Advisory absolute budget must not block the owning workstream.");
        Assert(
            report.Status == "PromotionPass",
            "A valid run with only an advisory absolute miss must pass.");
    }

    private static void TestFullScanDiagnosticDoesNotUseCompactOnlyGates(
        string fixtureRoot)
    {
        Fixture diagnostic = CreateFixture(
            Path.Combine(fixtureRoot, "full-scan-diagnostic"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.0, 4.0, 4.0],
            submissionOwnedAllocatedBytes: 4096,
            unsupportedCompactPasses: 1);
        VulkanPerformanceEvaluationReport diagnosticReport = Evaluate(
            diagnostic,
            baseline: null,
            acceptingBaseline: true);
        Assert(
            diagnosticReport.Issues.All(static issue =>
                issue.Code is not (
                    "ZeroReadbackSubmissionAllocation" or
                    "UnsupportedCompactVariant")),
            "Full-scan diagnostics must not be classified as compact-path failures.");

        Fixture compact = CreateFixture(
            Path.Combine(fixtureRoot, "compact-owned-allocation"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.0, 4.0, 4.0],
            zeroReadbackMaterialDrawPath: "BindlessMaterialTable",
            submissionOwnedAllocatedBytes: 4096,
            unsupportedCompactPasses: 1);
        VulkanPerformanceEvaluationReport compactReport = Evaluate(
            compact,
            baseline: null,
            acceptingBaseline: true);
        AssertIssue(compactReport, "ZeroReadbackSubmissionAllocation");
        AssertIssue(compactReport, "UnsupportedCompactVariant");
    }
    private static void TestManifestMismatchReportsExactField(
        string fixtureRoot)
    {
        Fixture baselineFixture = CreateFixture(
            Path.Combine(fixtureRoot, "compatible"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.0, 4.0, 4.0]);
        VulkanPerformanceEvaluationReport baseline = Evaluate(
            baselineFixture,
            baseline: null,
            acceptingBaseline: true);
        baseline.Cohorts[0].ComparisonIdentity["settings_sha256"] =
            "different-settings";

        VulkanPerformanceEvaluationReport mismatched = Evaluate(
            baselineFixture,
            baseline,
            acceptingBaseline: false);
        VulkanPerformanceIssue issue = mismatched.Issues.Single(
            static candidate => candidate.Code == "ManifestMismatch");
        Assert(
            issue.Message.Contains(
                "field 'settings_sha256'",
                StringComparison.Ordinal),
            "Manifest mismatch must name the exact field.");
    }

    private static void TestInvalidCaptureRejection(string fixtureRoot)
    {
        Fixture fixture = CreateFixture(
            Path.Combine(fixtureRoot, "invalid"),
            "Quick",
            promotionEligible: false,
            repetitions: 1,
            budgetValues: [4.0],
            readbackBytes: 64,
            fallbackEvents: 1,
            includeRequiredOutput: false);
        VulkanPerformanceEvaluationReport invalid = Evaluate(
            fixture,
            baseline: null,
            acceptingBaseline: false);
        AssertIssue(invalid, "ZeroReadbackViolation");
        AssertIssue(invalid, "ForbiddenFallback");
        AssertIssue(invalid, "RequiredOutputMissing");
        Assert(invalid.Status == "Fail", "Invalid capture must fail.");
        Assert(
            invalid.PromotionStatus == "NonPromotableQuickRun",
            "Invalid Quick capture must remain explicitly non-promotable.");

        string rejectedBaselinePath = Path.Combine(
            fixtureRoot,
            "invalid-candidate-must-not-be-accepted.json");
        int acceptExitCode = VulkanPerformanceCommand.Run(
        [
            "--vulkan-perf",
            "--contract", fixture.ContractPath,
            "--run-manifest", fixture.RunManifestPath,
            "--baseline", rejectedBaselinePath,
            "--accept-baseline",
            "--out", Path.Combine(
                fixtureRoot,
                "invalid-accept-evaluation.json"),
        ]);
        AssertEqual(
            1,
            acceptExitCode,
            "invalid baseline acceptance exit code");
        Assert(
            !File.Exists(rejectedBaselinePath),
            "A failing candidate must not create or replace a baseline.");
    }

    private static void TestMultiRateRequiredOutput(string fixtureRoot)
    {
        Fixture fixture = CreateFixture(
            Path.Combine(fixtureRoot, "multi-rate-output"),
            "Quick",
            promotionEligible: false,
            repetitions: 1,
            budgetValues: [4.0],
            includeRequiredOutputEveryFrame: false);
        VulkanPerformanceEvaluationReport report = Evaluate(
            fixture,
            baseline: null,
            acceptingBaseline: false);

        Assert(
            report.Issues.All(static issue => issue.Code != "RequiredOutputMissing"),
            "A fresh required output observed at its own cadence must satisfy the capture-level requirement.");
    }

    private static void TestPrimaryReuseRatioGate(string fixtureRoot)
    {
        Fixture fixture = CreateFixture(
            Path.Combine(fixtureRoot, "primary-reuse"),
            "Quick",
            promotionEligible: false,
            repetitions: 1,
            budgetValues: [4.0],
            minimumPrimaryReuseRatio: 0.99,
            primaryRecorded: 2,
            primaryReused: 98);

        VulkanPerformanceEvaluationReport report = Evaluate(
            fixture,
            baseline: null,
            acceptingBaseline: false);

        AssertIssue(report, "PrimaryReuseRatioBelowMinimum");
    }

    private static void TestStructurallyIneligiblePrimaryRecordsAreExcluded(
        string fixtureRoot)
    {
        Fixture fixture = CreateFixture(
            Path.Combine(fixtureRoot, "eligible-primary-reuse"),
            "Quick",
            promotionEligible: false,
            repetitions: 1,
            budgetValues: [4.0],
            minimumPrimaryReuseRatio: 0.99,
            primaryRecorded: 2,
            primaryReused: 98,
            eligiblePrimaryRecorded: 0);

        VulkanPerformanceEvaluationReport report = Evaluate(
            fixture,
            baseline: null,
            acceptingBaseline: false);

        Assert(
            report.Issues.All(static issue =>
                issue.Code != "PrimaryReuseRatioBelowMinimum"),
            "Structurally ineligible primary records must not lower the eligible-frame reuse ratio.");
    }

    private static void TestSelectedGateScope(string fixtureRoot)
    {
        Fixture selectedFixture = CreateFixture(
            Path.Combine(fixtureRoot, "selected-gate"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.0, 4.0, 4.0],
            gateScope: "Selected",
            includeUnselectedGateCohort: true);
        VulkanPerformanceEvaluationReport selectedReport = Evaluate(
            selectedFixture,
            baseline: null,
            acceptingBaseline: true);
        Assert(
            selectedReport.Issues.All(static issue => issue.Code != "MissingGateCohort"),
            "An explicit selected-Gate run must evaluate only the requested cohorts.");
        Assert(
            selectedReport.Status == "PromotionPass",
            "A valid selected-Gate capture must remain promotion-eligible.");

        Fixture fullFixture = CreateFixture(
            Path.Combine(fixtureRoot, "full-gate"),
            "Gate",
            promotionEligible: true,
            repetitions: 3,
            budgetValues: [4.0, 4.0, 4.0],
            gateScope: "Full",
            includeUnselectedGateCohort: true);
        VulkanPerformanceEvaluationReport fullReport = Evaluate(
            fullFixture,
            baseline: null,
            acceptingBaseline: true);
        AssertIssue(fullReport, "MissingGateCohort");
    }

    private static void TestProfileContractEnforcement(string fixtureRoot)
    {
        Fixture fixture = CreateFixture(
            Path.Combine(fixtureRoot, "profile-contract-violation"),
            "Quick",
            promotionEligible: false,
            repetitions: 1,
            budgetValues: [4.0],
            profileContractViolation: true);
        VulkanPerformanceEvaluationReport report = Evaluate(
            fixture,
            baseline: null,
            acceptingBaseline: false);

        AssertIssue(report, "IntrusiveCommandLabels");
        AssertIssue(report, "IntrusiveDiagnosticTraceFlags");
        AssertIssue(report, "ProfileNotCleanComparisonSuitable");
        AssertIssue(report, "IntrusiveLogVerbosity");
        AssertIssue(report, "ManifestNotCleanComparisonSuitable");
        AssertIssue(report, "ManifestIntrusiveDiagnosticTraceFlags");
        Assert(
            !report.CleanComparisonSuitable,
            "A profile-contract violation must not be reported as clean-comparison suitable.");
    }

    private static VulkanPerformanceEvaluationReport Evaluate(
        Fixture fixture,
        VulkanPerformanceEvaluationReport? baseline,
        bool acceptingBaseline)
        => new VulkanPerformanceEvaluator(
            VulkanPerformanceContract.Load(fixture.ContractPath),
            VulkanPerformanceRunManifest.Load(fixture.RunManifestPath),
            baseline).Evaluate(acceptingBaseline);

    private static Fixture CreateFixture(
        string root,
        string preset,
        bool promotionEligible,
        int repetitions,
        IReadOnlyList<double> budgetValues,
        int readbackBytes = 0,
        int fallbackEvents = 0,
        bool includeRequiredOutput = true,
        double minimumPrimaryReuseRatio = 0.0,
        int primaryRecorded = 0,
        int primaryReused = 0,
        int? eligiblePrimaryRecorded = null,
        bool includeRequiredOutputEveryFrame = true,
        bool enforceAbsoluteBudget = true,
        string zeroReadbackMaterialDrawPath = "FullBucketScanDiagnostic",
        int submissionOwnedAllocatedBytes = 0,
        int unsupportedCompactPasses = 0,
        string gateScope = "Full",
        bool includeUnselectedGateCohort = false,
        bool profileContractViolation = false)
    {
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.jsonc");
        File.WriteAllText(settingsPath, "{}");

        VulkanPerformanceContract contract = new()
        {
            SchemaVersion = 1,
            DefaultVarianceThresholdPercent = 7.5,
            DefaultRegressionThresholdPercent = 5.0,
            PrimaryGateEnvironment =
                new VulkanPerformanceGateEnvironment
                {
                    GpuName = "fixture-gpu",
                    GpuDriver = "fixture-driver",
                    OperatingSystem = "fixture-os",
                    DisplayMode = "fixture-display",
                },
            ProfileModes = CreateProfileModes(),
            Presets = new Dictionary<
                string,
                VulkanPerformancePresetDefinition>(
                StringComparer.OrdinalIgnoreCase)
            {
                [preset] = new VulkanPerformancePresetDefinition
                {
                    WarmupSeconds = 1,
                    CaptureSeconds = 1,
                    Repetitions = repetitions,
                    PromotionEligible = promotionEligible,
                    ProfileMode = promotionEligible
                        ? "ReleaseBenchmark"
                        : "CleanProfile",
                },
            },
            Cohorts =
            [
                new VulkanPerformanceCohort
                {
                    Id = "fixture-desktop",
                    Lane = "Desktop",
                    SettingsPath = settingsPath,
                    Scene = "FixtureScene",
                    Camera = "Static",
                    Lights = "CanonicalDirectional",
                    Viewport = "1280x720",
                    RenderScale = "1.0",
                    Strategy = "GpuIndirectZeroReadback",
                    ZeroReadbackMaterialDrawPath = zeroReadbackMaterialDrawPath,
                    VrMode = "Desktop",
                    FoveationMode = "Off",
                    BudgetMetric = "render_dispatch_ms",
                    BudgetMilliseconds = 5.0,
                    EnforceAbsoluteBudget = enforceAbsoluteBudget,
                    MinimumPrimaryReuseRatio = minimumPrimaryReuseRatio,
                    RequiredOutputs =
                    [
                        new VulkanPerformanceOutputRequirement
                        {
                            Kind = "DesktopScene",
                            MinimumRenderedViews = 1,
                        },
                    ],
                    Gate = true,
                },
            ],
        };
        if (includeUnselectedGateCohort)
        {
            contract.Cohorts.Add(new VulkanPerformanceCohort
            {
                Id = "fixture-unselected-gate",
                Gate = true,
            });
        }

        string contractPath = Path.Combine(root, "contract.json");
        WriteJson(contractPath, contract);

        List<object> summaries = [];
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            string logDirectory = Path.Combine(
                root,
                $"logs-{repetition + 1}");
            Directory.CreateDirectory(logDirectory);
            DateTimeOffset start = new(
                2026,
                7,
                28,
                12,
                repetition,
                0,
                TimeSpan.Zero);
            DateTimeOffset end = start.AddSeconds(1);
            WriteFrameStream(
                logDirectory,
                start,
                budgetValues[repetition],
                readbackBytes,
                fallbackEvents,
                includeRequiredOutput,
                includeRequiredOutputEveryFrame,
                zeroReadbackMaterialDrawPath,
                submissionOwnedAllocatedBytes,
                unsupportedCompactPasses,
                promotionEligible
                    ? "ReleaseBenchmark"
                    : "CleanProfile",
                profileContractViolation);
            WriteJson(
                Path.Combine(
                    logDirectory,
                    "profiler-capture-manifest.json"),
                new
                {
                    schema = "xrengine.profile_capture.render_stats.v5",
                    run = new
                    {
                        ProfileMode = promotionEligible
                            ? "ReleaseBenchmark"
                            : "CleanProfile",
                        ProfileComparisonSuitable =
                            !profileContractViolation,
                        ProfilePromotionEligible =
                            promotionEligible &&
                            !profileContractViolation,
                        VulkanCommandBufferLabelsEnabled =
                            profileContractViolation,
                        P3LoggingEnabled = profileContractViolation,
                        DiagnosticTraceFlagsEnabled =
                            profileContractViolation,
                        ActiveDiagnosticTraceFlags =
                            profileContractViolation
                                ? "XRE_VULKAN_RECORDING_DIAG"
                                : string.Empty,
                        EditorUiState = profileContractViolation
                            ? "Enabled"
                            : "Disabled",
                        ProfilerUiState = profileContractViolation
                            ? "Active"
                            : "Disabled",
                        DynamicTextOverlayEnabled =
                            profileContractViolation,
                        DebugOverlayEnabled = profileContractViolation,
                        LogVerbosity = profileContractViolation
                            ? "Verbose"
                            : "Normal",
                        LogSessionPath = logDirectory,
                        SceneIdentityHash = "fixture-scene",
                        SettingsIdentityHash = "fixture-settings",
                        ValidationLayersEnabled = false,
                        DebugOutputEnabled = false,
                        GpuTimestampDenseMode = false,
                    },
                });
            summaries.Add(new
            {
                StabilityReady = true,
                CaptureWorkloadIdentityCount = 1,
                Configuration = "Release",
                CacheMode = "Warm",
                UnapprovedOutputPolicyEventsTotal = 0,
                VulkanSubmissionRejectionsTotal = 0,
                VulkanPrimaryCommandBuffersRecordedTotal = primaryRecorded,
                VulkanPrimaryCommandBuffersReusedTotal = primaryReused,
                VulkanEligiblePrimaryCommandBufferRecordsTotal =
                    eligiblePrimaryRecorded ?? primaryRecorded,
                VulkanEligiblePrimaryCommandBuffersReusedTotal = primaryReused,
                VulkanEligiblePrimaryCommandBufferReuseDecisionsTotal =
                    (eligiblePrimaryRecorded ?? primaryRecorded) + primaryReused,
                LogDir = logDirectory,
                CaptureStartUtc = start,
                CaptureEndUtc = end,
            });
        }

        string summaryPath = Path.Combine(root, "summary.json");
        WriteJson(summaryPath, summaries);
        VulkanPerformanceRunManifest run = new()
        {
            SchemaVersion = 1,
            Preset = preset,
            GateScope = gateScope,
            PromotionEligible = promotionEligible,
            ProfileMode = promotionEligible
                ? "ReleaseBenchmark"
                : "CleanProfile",
            ContractPath = contractPath,
            SourceCommit = "fixture-commit",
            DirtyWorktree = false,
            ExecutableSha256 = "fixture-executable",
            OperatingSystem = "fixture-os",
            MachineName = "fixture-machine",
            GpuName = "fixture-gpu",
            GpuDriver = "fixture-driver",
            DisplayMode = "fixture-display",
            CreatedUtc = DateTime.UtcNow,
            Cohorts =
            [
                new VulkanPerformanceRunCohort
                {
                    Id = "fixture-desktop",
                    SummaryPath = summaryPath,
                    SettingsPath = settingsPath,
                    SettingsSha256 = "fixture-settings-sha",
                },
            ],
        };
        string runManifestPath = Path.Combine(root, "run-manifest.json");
        WriteJson(runManifestPath, run);
        return new Fixture(contractPath, runManifestPath);
    }

    private static Dictionary<
        string,
        VulkanPerformanceProfileDefinition> CreateProfileModes()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Diagnostics"] = new VulkanPerformanceProfileDefinition
            {
                ValidationAllowed = true,
                CommandLabelsAllowed = true,
                DenseGpuTimestampsAllowed = true,
                P3LoggingAllowed = true,
                ImGuiAllowed = true,
                DynamicTextAllowed = true,
                MaximumLogVerbosity = "Verbose",
                ExpectedOverhead = "Fixture diagnostics.",
            },
            ["DevelopmentProfile"] = new VulkanPerformanceProfileDefinition
            {
                ValidationAllowed = true,
                CommandLabelsAllowed = true,
                DenseGpuTimestampsAllowed = true,
                P3LoggingAllowed = true,
                ImGuiAllowed = true,
                DynamicTextAllowed = true,
                MaximumLogVerbosity = "Verbose",
                ExpectedOverhead = "Fixture development profile.",
            },
            ["CleanProfile"] = CreateCleanProfileDefinition(
                promotionEligible: false),
            ["ReleaseBenchmark"] = CreateCleanProfileDefinition(
                promotionEligible: true),
        };

    private static VulkanPerformanceProfileDefinition
        CreateCleanProfileDefinition(bool promotionEligible)
        => new()
        {
            CleanComparisonSuitable = true,
            PromotionEligible = promotionEligible,
            MaximumLogVerbosity = "Normal",
            ExpectedOverhead = "Fixture clean profile.",
        };

    private static void WriteFrameStream(
        string logDirectory,
        DateTimeOffset start,
        double budgetValue,
        int readbackBytes,
        int fallbackEvents,
        bool includeRequiredOutput,
        bool includeRequiredOutputEveryFrame,
        string zeroReadbackMaterialDrawPath,
        int submissionOwnedAllocatedBytes,
        int unsupportedCompactPasses,
        string profileMode,
        bool profileContractViolation)
    {
        string path = Path.Combine(
            logDirectory,
            "profiler-render-stats.ndjson");
        using StreamWriter writer = File.CreateText(path);
        for (int index = 0; index < 20; index++)
        {
            Dictionary<string, object> sample = new()
            {
                ["ts_utc"] = start.AddMilliseconds(25 + index * 40),
                ["active_render_backend"] = "Vulkan",
                ["effective_strategy"] = "GpuIndirectZeroReadback",
                ["zero_readback_material_draw_path"] =
                    zeroReadbackMaterialDrawPath,
                ["vr_foveation_mode"] = "Off",
                ["profile_mode"] = profileMode,
                ["profile_suitability"] = profileContractViolation
                    ? "IntrusiveConfiguration"
                    : profileMode == "ReleaseBenchmark"
                        ? "PromotionEligible"
                        : "CleanComparison",
                ["profile_comparison_suitable"] =
                    !profileContractViolation,
                ["profile_promotion_eligible"] =
                    profileMode == "ReleaseBenchmark" &&
                    !profileContractViolation,
                ["profile_intrusive"] = profileContractViolation,
                ["gpu_timestamps_dense_mode"] = false,
                ["validation_layers_enabled"] = false,
                ["debug_output_enabled"] = false,
                ["vulkan_command_buffer_labels_enabled"] =
                    profileContractViolation,
                ["p3_logging_enabled"] = profileContractViolation,
                ["diagnostic_trace_flags_enabled"] =
                    profileContractViolation,
                ["active_diagnostic_trace_flags"] =
                    profileContractViolation
                        ? "XRE_VULKAN_RECORDING_DIAG"
                        : string.Empty,
                ["profiler_ui_state"] = profileContractViolation
                    ? "Active"
                    : "Disabled",
                ["editor_ui_state"] = profileContractViolation
                    ? "Enabled"
                    : "Disabled",
                ["dynamic_text_overlay_enabled"] =
                    profileContractViolation,
                ["debug_overlay_enabled"] = profileContractViolation,
                ["log_verbosity"] = profileContractViolation
                    ? "Verbose"
                    : "Normal",
                ["shader_cache_state"] = "Warm",
                ["texture_cache_state"] = "Warm",
                ["xr_runtime"] = "None",
                ["anti_aliasing_mode"] = "Tsr",
                ["msaa_sample_count"] = 1,
                ["tsr_render_scale"] = 1.0,
                ["ambient_occlusion_enabled"] = true,
                ["ambient_occlusion_mode"] =
                    "GroundTruthAmbientOcclusion",
                ["auto_exposure_enabled"] = true,
                ["bloom_enabled"] = true,
                ["motion_vectors_requested"] = true,
                ["gpu_readback_bytes"] = readbackBytes,
                ["gpu_mapped_buffers"] = readbackBytes == 0 ? 0 : 1,
                ["forbidden_gpu_fallback_events"] = fallbackEvents,
                ["gpu_driven_submission_owned_managed_allocated_bytes"] =
                    submissionOwnedAllocatedBytes,
                ["gpu_driven_unsupported_compact_passes"] =
                    unsupportedCompactPasses,
                ["frame_output_unapproved_policy_event_count"] = 0,
                ["render_dispatch_ms"] = budgetValue,
                ["vulkan_frame_total_ms"] = budgetValue * 0.75,
                ["vulkan_frame_record_command_buffer_ms"] =
                    budgetValue * 0.75,
                ["frame_outputs"] = new
                {
                    outputs = includeRequiredOutput &&
                        (includeRequiredOutputEveryFrame || index % 2 == 0)
                        ? new[]
                        {
                            new
                            {
                                output_kind = "DesktopScene",
                                active = true,
                                rendered = true,
                                display_width = 1280,
                                display_height = 720,
                                internal_width = 1280,
                                internal_height = 720,
                                sample_count = 1,
                                view_mask = 0,
                            },
                        }
                        : Array.Empty<object>(),
                },
            };
            writer.WriteLine(JsonSerializer.Serialize(sample));
        }
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, s_fixtureJson));

    private static void AssertIssue(
        VulkanPerformanceEvaluationReport report,
        string code)
        => Assert(
            report.Issues.Any(issue => issue.Code == code),
            $"Expected issue '{code}'.");

    private static void AssertEqual(
        double expected,
        double actual,
        string name)
    {
        if (Math.Abs(expected - actual) > 0.000_001)
            throw new InvalidOperationException(
                $"{name}: expected {expected}, actual {actual}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct Fixture(
        string ContractPath,
        string RunManifestPath);
}
