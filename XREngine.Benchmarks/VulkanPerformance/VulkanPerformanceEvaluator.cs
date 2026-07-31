using System.Numerics;
using System.Text.Json;

namespace XREngine.Benchmarks;

/// <summary>
/// Evaluates captured Vulkan frame streams against reproducibility, variance,
/// zero-readback, output-coverage, absolute-budget, and baseline contracts.
/// </summary>
public sealed class VulkanPerformanceEvaluator
{
    private readonly VulkanPerformanceContract _contract;
    private readonly VulkanPerformanceRunManifest _run;
    private readonly VulkanPerformanceEvaluationReport? _baseline;
    private readonly List<VulkanPerformanceIssue> _issues = [];
    private readonly HashSet<string> _issueKeys = new(StringComparer.Ordinal);
    private VulkanPerformanceProfileDefinition _profileDefinition = new();

    public VulkanPerformanceEvaluator(
        VulkanPerformanceContract contract,
        VulkanPerformanceRunManifest run,
        VulkanPerformanceEvaluationReport? baseline)
    {
        _contract = contract;
        _run = run;
        _baseline = baseline;
    }

    public VulkanPerformanceEvaluationReport Evaluate(bool acceptingBaseline)
    {
        if (!_contract.Presets.TryGetValue(
                _run.Preset,
                out VulkanPerformancePresetDefinition? preset))
        {
            AddIssue(
                "UnknownPreset",
                string.Empty,
                $"Preset '{_run.Preset}' is not present in the contract.");
            preset = new VulkanPerformancePresetDefinition();
        }

        if (!_contract.ProfileModes.TryGetValue(
                preset.ProfileMode,
                out VulkanPerformanceProfileDefinition? profileDefinition))
        {
            AddIssue(
                "UnknownProfileDefinition",
                string.Empty,
                $"Profile mode '{preset.ProfileMode}' is not present in the contract.");
            profileDefinition = new VulkanPerformanceProfileDefinition();
        }
        _profileDefinition = profileDefinition;

        if (_run.PromotionEligible != preset.PromotionEligible)
        {
            AddIssue(
                "PromotionPolicyMismatch",
                string.Empty,
                $"Run promotion eligibility {_run.PromotionEligible} does not match preset '{_run.Preset}' ({preset.PromotionEligible}).");
        }
        if (!_run.ProfileMode.Equals(
                preset.ProfileMode,
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "ProfileModeMismatch",
                string.Empty,
                $"Run profile mode '{_run.ProfileMode}' does not match preset '{_run.Preset}' mode '{preset.ProfileMode}'.");
        }
        if (profileDefinition.PromotionEligible != preset.PromotionEligible)
        {
            AddIssue(
                "ProfilePromotionPolicyMismatch",
                string.Empty,
                $"Profile mode '{preset.ProfileMode}' promotion eligibility " +
                $"{profileDefinition.PromotionEligible} does not match preset " +
                $"'{_run.Preset}' ({preset.PromotionEligible}).");
        }

        if (preset.PromotionEligible && _baseline is null && !acceptingBaseline)
        {
            AddIssue(
                "BaselineRequired",
                string.Empty,
                $"Preset '{_run.Preset}' requires an explicit baseline or AcceptBaseline action.");
        }
        ValidatePrimaryGateEnvironment();

        Dictionary<string, VulkanPerformanceCohortReport> baselineCohorts =
            _baseline?.Cohorts.ToDictionary(
                static cohort => cohort.Id,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, VulkanPerformanceCohortReport>(
                StringComparer.OrdinalIgnoreCase);

        List<VulkanPerformanceCohortReport> reports = [];
        foreach (VulkanPerformanceRunCohort runCohort in _run.Cohorts)
        {
            VulkanPerformanceCohort? cohort = _contract.Cohorts.FirstOrDefault(
                candidate => candidate.Id.Equals(
                    runCohort.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (cohort is null)
            {
                AddIssue(
                    "UnknownCohort",
                    runCohort.Id,
                    $"Cohort '{runCohort.Id}' is not present in the contract.");
                continue;
            }

            baselineCohorts.TryGetValue(
                cohort.Id,
                out VulkanPerformanceCohortReport? baseline);
            reports.Add(EvaluateCohort(
                cohort,
                runCohort,
                preset,
                baseline,
                acceptingBaseline));
        }

        if (_run.Preset.Equals("Gate", StringComparison.OrdinalIgnoreCase) &&
            !_run.GateScope.Equals("Selected", StringComparison.OrdinalIgnoreCase))
        {
            foreach (VulkanPerformanceCohort required in _contract.Cohorts.Where(
                         static cohort => cohort.Gate))
            {
                if (!_run.Cohorts.Any(candidate => candidate.Id.Equals(
                        required.Id,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    AddIssue(
                        "MissingGateCohort",
                        required.Id,
                        $"Gate run did not include required cohort '{required.Id}'.");
                }
            }
        }

        string status = _issues.Count != 0
            ? "Fail"
            : preset.PromotionEligible
                ? "PromotionPass"
                : "NonPromotableQuickRun";
        string promotionStatus = preset.PromotionEligible
            ? _issues.Count == 0
                ? "PromotionPass"
                : "PromotionFail"
            : "NonPromotableQuickRun";

        return new VulkanPerformanceEvaluationReport
        {
            GeneratedUtc = DateTime.UtcNow,
            Status = status,
            PromotionStatus = promotionStatus,
            Preset = _run.Preset,
            ProfileMode = preset.ProfileMode,
            CleanComparisonSuitable =
                profileDefinition.CleanComparisonSuitable &&
                !_issues.Any(
                    static issue => IsProfileContractIssue(issue.Code)),
            ExpectedObserverOverhead =
                profileDefinition.ExpectedOverhead,
            PromotionEligible = preset.PromotionEligible,
            SourceCommit = _run.SourceCommit,
            DirtyWorktree = _run.DirtyWorktree,
            ExecutableSha256 = _run.ExecutableSha256,
            OperatingSystem = _run.OperatingSystem,
            MachineName = _run.MachineName,
            GpuName = _run.GpuName,
            GpuDriver = _run.GpuDriver,
            DisplayMode = _run.DisplayMode,
            VarianceThresholdPercent =
                _contract.DefaultVarianceThresholdPercent,
            RegressionThresholdPercent =
                _contract.DefaultRegressionThresholdPercent,
            Cohorts = reports,
            Issues = _issues,
        };
    }

    private void ValidatePrimaryGateEnvironment()
    {
        if (!_run.Preset.Equals("Gate", StringComparison.OrdinalIgnoreCase))
            return;

        VulkanPerformanceGateEnvironment expected =
            _contract.PrimaryGateEnvironment;
        ValidateGateEnvironmentField(
            "gpuName",
            expected.GpuName,
            _run.GpuName);
        ValidateGateEnvironmentField(
            "gpuDriver",
            expected.GpuDriver,
            _run.GpuDriver);
        ValidateGateEnvironmentField(
            "operatingSystem",
            expected.OperatingSystem,
            _run.OperatingSystem);
        ValidateGateEnvironmentField(
            "displayMode",
            expected.DisplayMode,
            _run.DisplayMode);
    }

    private void ValidateGateEnvironmentField(
        string field,
        string expected,
        string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) ||
            string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        AddIssue(
            "PrimaryGateEnvironmentMismatch",
            string.Empty,
            $"Primary gate field '{field}' differs: expected '{expected}', actual '{actual}'.");
    }

    private VulkanPerformanceCohortReport EvaluateCohort(
        VulkanPerformanceCohort cohort,
        VulkanPerformanceRunCohort runCohort,
        VulkanPerformancePresetDefinition preset,
        VulkanPerformanceCohortReport? baseline,
        bool acceptingBaseline)
    {
        JsonElement[] repetitions = LoadSummaryRepetitions(
            runCohort.SummaryPath);
        if (repetitions.Length < preset.Repetitions)
        {
            AddIssue(
                "InsufficientRepetitions",
                cohort.Id,
                $"Expected at least {preset.Repetitions} repetitions but found {repetitions.Length}.");
        }

        Dictionary<string, List<double>> allMetricValues =
            new(StringComparer.Ordinal);
        List<double> runP95Values = [];
        int missedBudgetFrames = 0;
        int totalFrames = 0;
        Dictionary<string, string>? comparisonIdentity = null;

        for (int repetitionIndex = 0;
             repetitionIndex < repetitions.Length;
             repetitionIndex++)
        {
            JsonElement summary = repetitions[repetitionIndex];
            ValidateSummary(cohort, preset, summary, repetitionIndex + 1);

            string logDirectory = GetString(summary, "LogDir");
            DateTimeOffset captureStart = GetDateTimeOffset(
                summary,
                "CaptureStartUtc");
            DateTimeOffset captureEnd = GetDateTimeOffset(
                summary,
                "CaptureEndUtc");
            List<JsonDocument> samples = LoadCaptureSamples(
                cohort.Id,
                logDirectory,
                captureStart,
                captureEnd);

            try
            {
                if (samples.Count == 0)
                {
                    AddIssue(
                        "NoFrameSamples",
                        cohort.Id,
                        $"Repetition {repetitionIndex + 1} has no frame samples in its capture window.");
                    continue;
                }

                Dictionary<string, string> identity = BuildComparisonIdentity(
                    cohort,
                    runCohort,
                    preset,
                    summary,
                    SelectRepresentativeOutputSample(samples),
                    logDirectory);
                if (comparisonIdentity is null)
                {
                    comparisonIdentity = identity;
                }
                else
                {
                    ValidateIdentityCompatibility(
                        cohort.Id,
                        comparisonIdentity,
                        identity,
                        $"repetition {repetitionIndex + 1}");
                }

                List<double> repetitionBudgetValues = [];
                foreach (JsonDocument sampleDocument in samples)
                {
                    JsonElement sample = sampleDocument.RootElement;
                    totalFrames++;
                    CollectNumericMetrics(sample, allMetricValues);
                    ValidateFrame(cohort, preset, sample);

                    if (!TryGetDouble(
                            sample,
                            cohort.BudgetMetric,
                            out double budgetValue))
                    {
                        AddIssue(
                            "MissingBudgetMetric",
                            cohort.Id,
                            $"Frame stream does not contain numeric metric '{cohort.BudgetMetric}'.");
                        continue;
                    }

                    repetitionBudgetValues.Add(budgetValue);
                    if (budgetValue > cohort.BudgetMilliseconds)
                        missedBudgetFrames++;
                }

                ValidateRequiredOutputs(cohort, samples);

                if (repetitionBudgetValues.Count != 0)
                {
                    repetitionBudgetValues.Sort();
                    runP95Values.Add(Percentile(
                        repetitionBudgetValues,
                        0.95));
                }
            }
            finally
            {
                foreach (JsonDocument sample in samples)
                    sample.Dispose();
            }
        }

        runP95Values.Sort();
        bool hasBudgetSamples = runP95Values.Count != 0;
        double p95Median = hasBudgetSamples
            ? Percentile(runP95Values, 0.50)
            : 0.0;
        double variancePercent = hasBudgetSamples
            ? CalculateRelativeRangePercent(runP95Values, p95Median)
            : 0.0;
        bool withinVariance = runP95Values.Count <= 1 ||
            variancePercent <= _contract.DefaultVarianceThresholdPercent;
        bool withinBudget = hasBudgetSamples &&
            p95Median <= cohort.BudgetMilliseconds;

        if (preset.PromotionEligible && !withinVariance)
        {
            AddIssue(
                "RunVarianceExceeded",
                cohort.Id,
                $"Run-to-run p95 variance {variancePercent:F2}% exceeds {_contract.DefaultVarianceThresholdPercent:F2}%.");
        }

        if (preset.PromotionEligible &&
            cohort.EnforceAbsoluteBudget &&
            !withinBudget)
        {
            AddIssue(
                "AbsoluteBudgetExceeded",
                cohort.Id,
                $"{cohort.BudgetMetric} p95 {p95Median:F3} ms exceeds {cohort.BudgetMilliseconds:F3} ms.");
        }

        bool baselineCompatible = true;
        double? baselineDeltaPercent = null;
        if (baseline is not null)
        {
            baselineCompatible = ValidateIdentityCompatibility(
                cohort.Id,
                baseline.ComparisonIdentity,
                comparisonIdentity ??
                    new Dictionary<string, string>(StringComparer.Ordinal),
                "baseline");

            if (baseline.BudgetMetricP95Median > 0.0 &&
                hasBudgetSamples)
            {
                baselineDeltaPercent =
                    ((p95Median - baseline.BudgetMetricP95Median) /
                        baseline.BudgetMetricP95Median) *
                    100.0;

                double toleratedRegression = Math.Max(
                    _contract.DefaultRegressionThresholdPercent,
                    baseline.BudgetMetricRunVariancePercent);
                if (preset.PromotionEligible &&
                    baselineDeltaPercent > toleratedRegression)
                {
                    AddIssue(
                        "BaselineRegression",
                        cohort.Id,
                        $"{cohort.BudgetMetric} p95 regressed {baselineDeltaPercent:F2}% (allowed {toleratedRegression:F2}%).");
                }
            }
        }
        else if (preset.PromotionEligible && !acceptingBaseline)
        {
            AddIssue(
                "MissingBaselineCohort",
                cohort.Id,
                $"No baseline cohort exists for '{cohort.Id}'.");
        }

        Dictionary<string, VulkanPerformanceMetricStatistics> metrics =
            new(StringComparer.Ordinal);
        foreach ((string name, List<double> values) in allMetricValues)
        {
            values.Sort();
            metrics[name] = CreateStatistics(values);
        }

        return new VulkanPerformanceCohortReport
        {
            Id = cohort.Id,
            Lane = cohort.Lane,
            Repetitions = repetitions.Length,
            BudgetMetric = cohort.BudgetMetric,
            BudgetMilliseconds = cohort.BudgetMilliseconds,
            BudgetMetricP95Median = p95Median,
            BudgetMetricRunVariancePercent = variancePercent,
            MissedBudgetFrameCount = missedBudgetFrames,
            FrameSampleCount = totalFrames,
            WithinAbsoluteBudget = withinBudget,
            WithinVarianceThreshold = withinVariance,
            BaselineCompatible = baselineCompatible,
            BaselineDeltaPercent = baselineDeltaPercent,
            FailureClassification = ClassifyFailure(
                withinBudget,
                metrics,
                cohort.BudgetMilliseconds),
            ComparisonIdentity = comparisonIdentity ??
                new Dictionary<string, string>(StringComparer.Ordinal),
            Metrics = metrics,
        };
    }

    private void ValidateSummary(
        VulkanPerformanceCohort cohort,
        VulkanPerformancePresetDefinition preset,
        JsonElement summary,
        int repetition)
    {
        if (!GetBoolean(summary, "StabilityReady"))
            AddIssue(
                "UnstableCapture",
                cohort.Id,
                $"Repetition {repetition} did not pass workload stability.");
        if (GetInt32(summary, "CaptureWorkloadIdentityCount") != 1)
            AddIssue(
                "WorkloadIdentityChanged",
                cohort.Id,
                $"Repetition {repetition} contains multiple workload identities.");
        if (!GetString(summary, "Configuration").Equals(
                "Release",
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "NonReleaseBuild",
                cohort.Id,
                $"Repetition {repetition} was not captured from a Release build.");
        }
        if (preset.PromotionEligible &&
            !GetString(summary, "CacheMode").Equals(
                "Warm",
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "NonWarmPromotionCapture",
                cohort.Id,
                $"Repetition {repetition} did not use the warm-cache promotion contract.");
        }
        if (GetDouble(summary, "UnapprovedOutputPolicyEventsTotal") > 0.0)
            AddIssue(
                "UnapprovedOutputPolicy",
                cohort.Id,
                $"Repetition {repetition} used an unapproved output fallback.");
        if (GetDouble(summary, "VulkanSubmissionRejectionsTotal") > 0.0)
            AddIssue(
                "SubmissionRejected",
                cohort.Id,
                $"Repetition {repetition} rejected one or more Vulkan submissions.");

        if (cohort.MinimumPrimaryReuseRatio > 0.0)
        {
            double recorded = 0.0;
            double reused = 0.0;
            double decisions = 0.0;
            bool hasEligibleReuse = TryGetDouble(
                    summary,
                    "VulkanEligiblePrimaryCommandBufferRecordsTotal",
                    out recorded) &&
                TryGetDouble(
                    summary,
                    "VulkanEligiblePrimaryCommandBuffersReusedTotal",
                    out reused) &&
                TryGetDouble(
                    summary,
                    "VulkanEligiblePrimaryCommandBufferReuseDecisionsTotal",
                    out decisions);
            if (!hasEligibleReuse)
            {
                recorded = GetDouble(
                    summary,
                    "VulkanPrimaryCommandBuffersRecordedTotal");
                reused = GetDouble(
                    summary,
                    "VulkanPrimaryCommandBuffersReusedTotal");
                decisions = recorded + reused;
            }

            double ratio = decisions > 0.0 ? reused / decisions : 0.0;
            if (decisions <= 0.0)
            {
                AddIssue(
                    "MissingPrimaryReuseDecisions",
                    cohort.Id,
                    $"Repetition {repetition} did not report any primary record/reuse decisions.");
            }
            else if (ratio < cohort.MinimumPrimaryReuseRatio)
            {
                AddIssue(
                    "PrimaryReuseRatioBelowMinimum",
                    cohort.Id,
                    $"Repetition {repetition} reused {reused:F0} of {decisions:F0} " +
                    $"{(hasEligibleReuse ? "eligible " : string.Empty)}primary decisions ({ratio:P2}); " +
                    $"required at least {cohort.MinimumPrimaryReuseRatio:P2}.");
            }
        }
    }

    private void ValidateFrame(
        VulkanPerformanceCohort cohort,
        VulkanPerformancePresetDefinition preset,
        JsonElement sample)
    {
        if (!GetString(sample, "active_render_backend").Equals(
                "Vulkan",
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "WrongBackend",
                cohort.Id,
                $"Captured backend '{GetString(sample, "active_render_backend")}' is not Vulkan.");
        }

        string effectiveStrategy = GetString(sample, "effective_strategy");
        if (!effectiveStrategy.Equals(
                cohort.Strategy,
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "StrategyFallback",
                cohort.Id,
                $"Requested strategy '{cohort.Strategy}' resolved to '{effectiveStrategy}'.");
        }

        if (!_profileDefinition.DenseGpuTimestampsAllowed &&
            GetBoolean(sample, "gpu_timestamps_dense_mode"))
        {
            AddIssue(
                "IntrusiveGpuTimestamps",
                cohort.Id,
                $"Dense GPU timestamps are prohibited by profile mode '{preset.ProfileMode}'.");
        }
        if (!_profileDefinition.ValidationAllowed &&
            (GetBoolean(sample, "validation_layers_enabled") ||
             GetBoolean(sample, "debug_output_enabled")))
        {
            AddIssue(
                "IntrusiveValidationOrDebugOutput",
                cohort.Id,
                $"Validation layers or synchronous debug output are prohibited by profile mode '{preset.ProfileMode}'.");
        }
        if (!_profileDefinition.CommandLabelsAllowed &&
            GetBoolean(sample, "vulkan_command_buffer_labels_enabled"))
        {
            AddIssue(
                "IntrusiveCommandLabels",
                cohort.Id,
                $"Vulkan command-buffer labels are prohibited by profile mode '{preset.ProfileMode}'.");
        }
        if (!_profileDefinition.P3LoggingAllowed &&
            GetBooleanFlag(sample, "p3_logging_enabled", "p3_logging"))
        {
            AddIssue(
                "IntrusiveP3Logging",
                cohort.Id,
                $"P3 diagnostic logging is prohibited by profile mode '{preset.ProfileMode}'.");
        }
        if (_profileDefinition.CleanComparisonSuitable &&
            GetBoolean(sample, "diagnostic_trace_flags_enabled"))
        {
            AddIssue(
                "IntrusiveDiagnosticTraceFlags",
                cohort.Id,
                $"Diagnostic trace flags '{GetString(sample, "active_diagnostic_trace_flags")}' " +
                $"are prohibited by profile mode '{preset.ProfileMode}'.");
        }

        string profileMode = GetString(sample, "profile_mode");
        if (!profileMode.Equals(
                preset.ProfileMode,
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "FrameProfileModeMismatch",
                cohort.Id,
                $"Frame profile mode '{profileMode}' does not match required mode '{preset.ProfileMode}'.");
        }
        if (_profileDefinition.CleanComparisonSuitable &&
            !GetBoolean(sample, "profile_comparison_suitable"))
        {
            AddIssue(
                "ProfileNotCleanComparisonSuitable",
                cohort.Id,
                $"Profile mode '{preset.ProfileMode}' reported intrusive or non-warm capture state.");
        }
        if (GetBoolean(sample, "profile_promotion_eligible") !=
            _profileDefinition.PromotionEligible)
        {
            AddIssue(
                "FramePromotionPolicyMismatch",
                cohort.Id,
                $"Frame promotion eligibility does not match profile mode '{preset.ProfileMode}'.");
        }
        if (!_profileDefinition.ImGuiAllowed &&
            (!GetString(sample, "editor_ui_state").Equals(
                 "Disabled",
                 StringComparison.OrdinalIgnoreCase) ||
             !GetString(sample, "profiler_ui_state").Equals(
                 "Disabled",
                 StringComparison.OrdinalIgnoreCase)))
        {
            AddIssue(
                "IntrusiveEditorUi",
                cohort.Id,
                $"Editor or profiler UI remained enabled in profile mode '{preset.ProfileMode}'.");
        }
        if (!_profileDefinition.DynamicTextAllowed &&
            (GetBoolean(sample, "dynamic_text_overlay_enabled") ||
             GetBoolean(sample, "debug_overlay_enabled")))
        {
            AddIssue(
                "IntrusiveDebugOverlay",
                cohort.Id,
                $"Dynamic text or debug overlays remained enabled in profile mode '{preset.ProfileMode}'.");
        }
        if (GetVerbosityRank(GetString(sample, "log_verbosity")) >
            GetVerbosityRank(_profileDefinition.MaximumLogVerbosity))
        {
            AddIssue(
                "IntrusiveLogVerbosity",
                cohort.Id,
                $"Log verbosity '{GetString(sample, "log_verbosity")}' exceeds profile mode " +
                $"'{preset.ProfileMode}' maximum '{_profileDefinition.MaximumLogVerbosity}'.");
        }
        if (_profileDefinition.CleanComparisonSuitable &&
            (!GetString(sample, "shader_cache_state").Equals(
                 "Warm",
                 StringComparison.OrdinalIgnoreCase) ||
             !GetString(sample, "texture_cache_state").Equals(
                 "Warm",
                 StringComparison.OrdinalIgnoreCase)))
        {
            AddIssue(
                "ColdCacheState",
                cohort.Id,
                $"Profile mode '{preset.ProfileMode}' requires warm shader and texture caches.");
        }

        string foveationMode = GetString(sample, "vr_foveation_mode");
        if (!foveationMode.Equals(
                cohort.FoveationMode,
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                cohort.RequireFoveation
                    ? "UnsupportedRequiredFoveation"
                    : "FoveationModeMismatch",
                cohort.Id,
                $"Requested foveation '{cohort.FoveationMode}' resolved to '{foveationMode}'.");
        }

        bool zeroReadbackStrategy = cohort.Strategy.Contains(
            "ZeroReadback",
            StringComparison.OrdinalIgnoreCase);
        bool compactZeroReadbackPath =
            zeroReadbackStrategy &&
            !cohort.ZeroReadbackMaterialDrawPath.Equals(
                "FullBucketScanDiagnostic",
                StringComparison.OrdinalIgnoreCase);
        if (zeroReadbackStrategy)
        {
            double bytes = GetDouble(sample, "gpu_readback_bytes");
            double mappings = GetDouble(sample, "gpu_mapped_buffers");
            if (bytes > 0.0 || mappings > 0.0)
            {
                AddIssue(
                    "ZeroReadbackViolation",
                    cohort.Id,
                    $"Current-frame GPU readback observed (bytes={bytes}, mappings={mappings}).");
            }

            if (compactZeroReadbackPath)
            {
                double allocatedBytes = GetDouble(
                    sample,
                    "gpu_driven_submission_owned_managed_allocated_bytes");
                if (allocatedBytes > 0.0)
                {
                    AddIssue(
                        "ZeroReadbackSubmissionAllocation",
                        cohort.Id,
                        $"Compact zero-readback preparation/selection allocated {allocatedBytes:F0} managed byte(s) on the render thread.");
                }

                double unsupportedPasses = GetDouble(
                    sample,
                    "gpu_driven_unsupported_compact_passes");
                if (unsupportedPasses > 0.0)
                {
                    AddIssue(
                        "UnsupportedCompactVariant",
                        cohort.Id,
                        $"Compact zero-readback submission rejected {unsupportedPasses:F0} required variant(s).");
                }
            }
        }

        if (GetDouble(sample, "forbidden_gpu_fallback_events") > 0.0 ||
            GetDouble(sample, "frame_output_unapproved_policy_event_count") > 0.0)
        {
            AddIssue(
                "ForbiddenFallback",
                cohort.Id,
                "A forbidden GPU or output-policy fallback occurred.");
        }

        ValidateCpuStageReconciliation(cohort, sample);
        ValidateCommandBufferTruth(cohort, sample);
        ValidateOverlayWork(cohort, preset, sample);
    }

    private void ValidateCpuStageReconciliation(
        VulkanPerformanceCohort cohort,
        JsonElement sample)
    {
        double total = GetDouble(sample, "vulkan_frame_total_ms");
        if (total <= 0.0)
            return;

        double lifecycleStages =
            GetDouble(sample, "vulkan_frame_wait_fence_ms") +
            GetDouble(sample, "vulkan_frame_sample_timing_queries_ms") +
            GetDouble(sample, "vulkan_frame_drain_retired_resources_ms") +
            GetDouble(sample, "vulkan_frame_acquire_image_ms") +
            GetDouble(sample, "vulkan_frame_acquire_bridge_submit_ms") +
            GetDouble(sample, "vulkan_frame_wait_swapchain_image_ms") +
            GetDouble(sample, "vulkan_frame_reset_dynamic_uniform_ring_ms") +
            GetDouble(sample, "vulkan_frame_record_command_buffer_ms") +
            GetDouble(sample, "vulkan_frame_submit_ms") +
            GetDouble(sample, "vulkan_frame_trim_ms") +
            GetDouble(sample, "vulkan_frame_present_ms");
        double tolerance = Math.Max(1.0, total * 0.05);
        if (Math.Abs(total - lifecycleStages) <= tolerance)
            return;

        AddIssue(
            "CpuStageReconciliationFailed",
            cohort.Id,
            $"Vulkan lifecycle stages total {lifecycleStages:F3} ms but Vulkan frame total is {total:F3} ms (tolerance {tolerance:F3} ms).");
    }

    private void ValidateCommandBufferTruth(
        VulkanPerformanceCohort cohort,
        JsonElement sample)
    {
        double cleanReuse = GetDouble(
            sample,
            "vulkan_primary_command_buffers_reused");
        double primaryRecords = GetDouble(
            sample,
            "vulkan_primary_command_buffers_recorded");
        double primaryEncoding = GetDouble(
            sample,
            "vulkan_cpu_primary_command_encoding_ms");
        if (cleanReuse > 0.0 &&
            primaryRecords == 0.0 &&
            primaryEncoding > 0.05)
        {
            AddIssue(
                "ReuseEncodedCommands",
                cohort.Id,
                $"Clean primary reuse reported {primaryEncoding:F3} ms of primary command encoding.");
        }

        if (!GetBoolean(sample, "gpu_timestamps_dense_mode") &&
            GetDouble(
                sample,
                "vulkan_command_buffer_profiler_dirty_count") > 0.0)
        {
            AddIssue(
                "GpuTimingForcedDirty",
                cohort.Id,
                "Normal GPU timing forced a command buffer dirty.");
        }

        if (GetDouble(sample, "vulkan_command_buffer_record_count") > 0.0 &&
            GetDouble(
                sample,
                "vulkan_command_buffer_decision_reason_mask") == 0.0)
        {
            AddIssue(
                "MissingDirtyReason",
                cohort.Id,
                "A command buffer was recorded without an exact decision-reason mask.");
        }

    }

    private void ValidateOverlayWork(
        VulkanPerformanceCohort cohort,
        VulkanPerformancePresetDefinition preset,
        JsonElement sample)
    {
        if (_profileDefinition.ImGuiAllowed &&
            _profileDefinition.DynamicTextAllowed)
        {
            return;
        }
        if (!sample.TryGetProperty("frame_outputs", out JsonElement frameOutputs) ||
            !frameOutputs.TryGetProperty("outputs", out JsonElement outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement output in outputs.EnumerateArray())
        {
            string kind = GetString(output, "output_kind");
            if (kind is not ("ImGuiOverlay" or "DynamicTextOverlay"))
                continue;
            if (GetInt32(output, "command_count") == 0)
                continue;
            if (kind == "ImGuiOverlay" &&
                _profileDefinition.ImGuiAllowed)
            {
                continue;
            }
            if (kind == "DynamicTextOverlay" &&
                _profileDefinition.DynamicTextAllowed)
            {
                continue;
            }

            AddIssue(
                "IntrusiveOverlayWork",
                cohort.Id,
                $"Profile mode '{preset.ProfileMode}' recorded " +
                $"{GetInt32(output, "command_count")} commands for {kind}.");
        }
    }

    private void ValidateRequiredOutputs(
        VulkanPerformanceCohort cohort,
        IReadOnlyList<JsonDocument> samples)
    {
        if (cohort.RequiredOutputs.Count == 0)
            return;

        bool manifestObserved = samples.Any(static document =>
            document.RootElement.TryGetProperty("frame_outputs", out JsonElement frameOutputs) &&
            frameOutputs.TryGetProperty("outputs", out JsonElement outputs) &&
            outputs.ValueKind == JsonValueKind.Array);
        if (!manifestObserved)
        {
            AddIssue(
                "MissingOutputManifest",
                cohort.Id,
                "Capture has no structured output manifest.");
            return;
        }

        foreach (VulkanPerformanceOutputRequirement requirement in cohort.RequiredOutputs)
        {
            int maximumRenderedViews = 0;
            foreach (JsonDocument sampleDocument in samples)
            {
                JsonElement sample = sampleDocument.RootElement;
                if (!sample.TryGetProperty("frame_outputs", out JsonElement frameOutputs) ||
                    !frameOutputs.TryGetProperty("outputs", out JsonElement outputs) ||
                    outputs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                int renderedViews = 0;
                foreach (JsonElement output in outputs.EnumerateArray())
                {
                    if (!GetString(output, "output_kind").Equals(
                            requirement.Kind,
                            StringComparison.OrdinalIgnoreCase) ||
                        !IsFreshlyRenderedOutput(output))
                    {
                        continue;
                    }

                    int viewMask = GetInt32(output, "view_mask");
                    renderedViews += viewMask == 0
                        ? 1
                        : BitOperations.PopCount((uint)viewMask);
                }

                maximumRenderedViews = Math.Max(maximumRenderedViews, renderedViews);
            }

            if (maximumRenderedViews < requirement.MinimumRenderedViews)
            {
                AddIssue(
                    "RequiredOutputMissing",
                    cohort.Id,
                    $"Capture rendered at most {maximumRenderedViews} fresh {requirement.Kind} views in one output frame; {requirement.MinimumRenderedViews} required.");
            }
        }
    }

    private static bool IsFreshlyRenderedOutput(JsonElement output)
    {
        if (!GetBoolean(output, "rendered") || GetBoolean(output, "skipped"))
            return false;
        if (GetInt32(output, "content_age_frames") > 0)
            return false;

        string disposition = GetString(output, "work_disposition");
        return string.IsNullOrWhiteSpace(disposition) ||
            disposition.Equals("FreshRender", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement SelectRepresentativeOutputSample(IReadOnlyList<JsonDocument> samples)
    {
        JsonElement representative = samples[^1].RootElement;
        int maximumOutputCount = GetOutputCount(representative);
        for (int index = samples.Count - 2; index >= 0; index--)
        {
            JsonElement candidate = samples[index].RootElement;
            int outputCount = GetOutputCount(candidate);
            if (outputCount <= maximumOutputCount)
                continue;

            representative = candidate;
            maximumOutputCount = outputCount;
        }

        return representative;
    }

    private static int GetOutputCount(JsonElement sample)
    {
        if (!sample.TryGetProperty("frame_outputs", out JsonElement frameOutputs) ||
            !frameOutputs.TryGetProperty("outputs", out JsonElement outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return outputs.GetArrayLength();
    }

    private Dictionary<string, string> BuildComparisonIdentity(
        VulkanPerformanceCohort cohort,
        VulkanPerformanceRunCohort runCohort,
        VulkanPerformancePresetDefinition preset,
        JsonElement summary,
        JsonElement sample,
        string logDirectory)
    {
        Dictionary<string, string> identity = new(StringComparer.Ordinal)
        {
            ["cohort"] = cohort.Id,
            ["settings_sha256"] = runCohort.SettingsSha256,
            ["backend"] = GetString(sample, "active_render_backend"),
            ["configuration"] = GetString(summary, "Configuration"),
            ["cache_mode"] = GetString(summary, "CacheMode"),
            ["strategy"] = GetString(sample, "effective_strategy"),
            ["zero_readback_path"] =
                GetString(sample, "zero_readback_material_draw_path"),
            ["scene"] = cohort.Scene,
            ["camera"] = cohort.Camera,
            ["lights"] = cohort.Lights,
            ["viewport"] = cohort.Viewport,
            ["render_scale"] = cohort.RenderScale,
            ["vr_mode"] = cohort.VrMode,
            ["foveation_mode"] = cohort.FoveationMode,
            ["profile_mode"] = preset.ProfileMode,
            ["profile_suitability"] =
                GetString(sample, "profile_suitability"),
            ["profile_comparison_suitable"] =
                GetString(sample, "profile_comparison_suitable"),
            ["profile_intrusive"] =
                GetString(sample, "profile_intrusive"),
            ["validation_layers"] =
                GetString(sample, "validation_layers_enabled"),
            ["debug_output"] =
                GetString(sample, "debug_output_enabled"),
            ["command_buffer_labels"] =
                GetString(sample, "vulkan_command_buffer_labels_enabled"),
            ["gpu_timestamp_dense"] =
                GetString(sample, "gpu_timestamps_dense_mode"),
            ["p3_logging"] =
                GetString(sample, "p3_logging_enabled"),
            ["diagnostic_trace_flags"] =
                GetString(sample, "active_diagnostic_trace_flags"),
            ["log_verbosity"] =
                GetString(sample, "log_verbosity"),
            ["profiler_ui_state"] =
                GetString(sample, "profiler_ui_state"),
            ["editor_ui_state"] =
                GetString(sample, "editor_ui_state"),
            ["dynamic_text_overlay"] =
                GetString(sample, "dynamic_text_overlay_enabled"),
            ["debug_overlay"] =
                GetString(sample, "debug_overlay_enabled"),
            ["shader_cache_state"] =
                GetString(sample, "shader_cache_state"),
            ["texture_cache_state"] =
                GetString(sample, "texture_cache_state"),
            ["xr_runtime"] =
                GetString(sample, "xr_runtime"),
            ["anti_aliasing_mode"] =
                GetString(sample, "anti_aliasing_mode"),
            ["msaa_sample_count"] =
                GetString(sample, "msaa_sample_count"),
            ["tsr_render_scale"] =
                GetString(sample, "tsr_render_scale"),
            ["ambient_occlusion_enabled"] =
                GetString(sample, "ambient_occlusion_enabled"),
            ["ambient_occlusion_mode"] =
                GetString(sample, "ambient_occlusion_mode"),
            ["auto_exposure_enabled"] =
                GetString(sample, "auto_exposure_enabled"),
            ["bloom_enabled"] =
                GetString(sample, "bloom_enabled"),
            ["motion_vectors_requested"] =
                GetString(sample, "motion_vectors_requested"),
            ["gpu_name"] = _run.GpuName,
            ["gpu_driver"] = _run.GpuDriver,
            ["operating_system"] = _run.OperatingSystem,
            ["display_mode"] = _run.DisplayMode,
            ["target_extents"] = BuildTargetExtentIdentity(sample),
        };

        string manifestPath = Path.Combine(
            logDirectory,
            "profiler-capture-manifest.json");
        if (!File.Exists(manifestPath))
        {
            AddIssue(
                "MissingCaptureManifest",
                cohort.Id,
                $"Capture manifest not found at '{manifestPath}'.");
            return identity;
        }

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        identity["capture_schema"] = GetString(root, "schema");
        if (root.TryGetProperty("run", out JsonElement run))
        {
            identity["scene_identity_hash"] =
                GetString(run, "SceneIdentityHash");
            identity["settings_identity_hash"] =
                GetString(run, "SettingsIdentityHash");
            identity["validation_layers"] =
                GetString(run, "ValidationLayersEnabled");
            identity["debug_output"] =
                GetString(run, "DebugOutputEnabled");
            identity["gpu_timestamp_dense"] =
                GetString(run, "GpuTimestampDenseMode");
            ValidateManifestProfileContract(
                cohort,
                preset,
                run);
        }

        return identity;
    }

    private void ValidateManifestProfileContract(
        VulkanPerformanceCohort cohort,
        VulkanPerformancePresetDefinition preset,
        JsonElement run)
    {
        string mode = GetString(run, "ProfileMode");
        if (!mode.Equals(
                preset.ProfileMode,
                StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                "ManifestProfileModeMismatch",
                cohort.Id,
                $"Capture manifest profile mode '{mode}' does not match required mode '{preset.ProfileMode}'.");
        }
        if (_profileDefinition.CleanComparisonSuitable &&
            !GetBoolean(run, "ProfileComparisonSuitable"))
        {
            AddIssue(
                "ManifestNotCleanComparisonSuitable",
                cohort.Id,
                $"Capture manifest reports that profile mode '{preset.ProfileMode}' is not clean-comparison suitable.");
        }
        if (GetBoolean(run, "ProfilePromotionEligible") !=
            _profileDefinition.PromotionEligible)
        {
            AddIssue(
                "ManifestPromotionPolicyMismatch",
                cohort.Id,
                $"Capture manifest promotion eligibility does not match profile mode '{preset.ProfileMode}'.");
        }
        if (!_profileDefinition.CommandLabelsAllowed &&
            GetBoolean(run, "VulkanCommandBufferLabelsEnabled"))
        {
            AddIssue(
                "ManifestIntrusiveCommandLabels",
                cohort.Id,
                "Capture manifest reports Vulkan command-buffer labels enabled.");
        }
        if (!_profileDefinition.P3LoggingAllowed &&
            GetBoolean(run, "P3LoggingEnabled"))
        {
            AddIssue(
                "ManifestIntrusiveP3Logging",
                cohort.Id,
                "Capture manifest reports P3 diagnostic logging enabled.");
        }
        if (_profileDefinition.CleanComparisonSuitable &&
            GetBoolean(run, "DiagnosticTraceFlagsEnabled"))
        {
            AddIssue(
                "ManifestIntrusiveDiagnosticTraceFlags",
                cohort.Id,
                $"Capture manifest reports diagnostic trace flags " +
                $"'{GetString(run, "ActiveDiagnosticTraceFlags")}' enabled.");
        }
        if (!_profileDefinition.ImGuiAllowed &&
            (!GetString(run, "EditorUiState").Equals(
                 "Disabled",
                 StringComparison.OrdinalIgnoreCase) ||
             !GetString(run, "ProfilerUiState").Equals(
                 "Disabled",
                 StringComparison.OrdinalIgnoreCase)))
        {
            AddIssue(
                "ManifestIntrusiveEditorUi",
                cohort.Id,
                "Capture manifest reports editor or profiler UI enabled.");
        }
        if (!_profileDefinition.DynamicTextAllowed &&
            (GetBoolean(run, "DynamicTextOverlayEnabled") ||
             GetBoolean(run, "DebugOverlayEnabled")))
        {
            AddIssue(
                "ManifestIntrusiveDebugOverlay",
                cohort.Id,
                "Capture manifest reports dynamic text or debug overlays enabled.");
        }
        if (GetVerbosityRank(GetString(run, "LogVerbosity")) >
            GetVerbosityRank(_profileDefinition.MaximumLogVerbosity))
        {
            AddIssue(
                "ManifestIntrusiveLogVerbosity",
                cohort.Id,
                $"Capture manifest log verbosity '{GetString(run, "LogVerbosity")}' exceeds " +
                $"profile maximum '{_profileDefinition.MaximumLogVerbosity}'.");
        }
        if (string.IsNullOrWhiteSpace(GetString(run, "LogSessionPath")))
        {
            AddIssue(
                "MissingLogSessionPath",
                cohort.Id,
                "Capture manifest does not include the exact log session path.");
        }
    }

    private static string BuildTargetExtentIdentity(JsonElement sample)
    {
        if (!sample.TryGetProperty("frame_outputs", out JsonElement frameOutputs) ||
            !frameOutputs.TryGetProperty("outputs", out JsonElement outputs) ||
            outputs.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            ";",
            outputs.EnumerateArray()
                .Where(static output => GetBoolean(output, "active"))
                .Select(static output =>
                    $"{GetString(output, "output_kind")}:" +
                    $"{GetInt32(output, "display_width")}x{GetInt32(output, "display_height")}:" +
                    $"{GetInt32(output, "internal_width")}x{GetInt32(output, "internal_height")}:" +
                    $"samples={GetInt32(output, "sample_count")}:" +
                    $"viewMask={GetInt32(output, "view_mask")}")
                .OrderBy(static value => value, StringComparer.Ordinal));
    }

    private bool ValidateIdentityCompatibility(
        string cohort,
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual,
        string comparisonName)
    {
        bool compatible = true;
        foreach (string key in expected.Keys.Union(actual.Keys).Order(
                     StringComparer.Ordinal))
        {
            expected.TryGetValue(key, out string? expectedValue);
            actual.TryGetValue(key, out string? actualValue);
            if (string.Equals(
                    expectedValue,
                    actualValue,
                    StringComparison.Ordinal))
            {
                continue;
            }

            compatible = false;
            AddIssue(
                "ManifestMismatch",
                cohort,
                $"{comparisonName} field '{key}' differs: expected '{expectedValue ?? "<missing>"}', actual '{actualValue ?? "<missing>"}'.");
        }

        return compatible;
    }

    private List<JsonDocument> LoadCaptureSamples(
        string cohort,
        string logDirectory,
        DateTimeOffset captureStart,
        DateTimeOffset captureEnd)
    {
        List<JsonDocument> samples = [];
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            AddIssue(
                "MissingLogDirectory",
                cohort,
                "Summary did not record a log directory.");
            return samples;
        }

        string path = Path.Combine(
            logDirectory,
            "profiler-render-stats.ndjson");
        if (!File.Exists(path))
        {
            AddIssue(
                "MissingFrameStream",
                cohort,
                $"Frame stream not found at '{path}'.");
            return samples;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument document = JsonDocument.Parse(line);
            DateTimeOffset timestamp = GetDateTimeOffset(
                document.RootElement,
                "ts_utc");
            if (timestamp >= captureStart && timestamp <= captureEnd)
            {
                samples.Add(document);
            }
            else
            {
                document.Dispose();
            }
        }

        return samples;
    }

    private JsonElement[] LoadSummaryRepetitions(string path)
    {
        if (!File.Exists(path))
        {
            AddIssue(
                "MissingSummary",
                string.Empty,
                $"Capture summary '{path}' does not exist.");
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path));
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(
                static item => item.Clone()).ToArray()
            : [document.RootElement.Clone()];
    }

    private static void CollectNumericMetrics(
        JsonElement sample,
        Dictionary<string, List<double>> values)
    {
        foreach (JsonProperty property in sample.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetDouble(out double value) ||
                double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                continue;
            }

            if (!values.TryGetValue(property.Name, out List<double>? metric))
            {
                metric = [];
                values.Add(property.Name, metric);
            }
            metric.Add(value);
        }
    }

    private static VulkanPerformanceMetricStatistics CreateStatistics(
        List<double> sortedValues)
        => new()
        {
            SampleCount = sortedValues.Count,
            P50 = Percentile(sortedValues, 0.50),
            P90 = Percentile(sortedValues, 0.90),
            P95 = Percentile(sortedValues, 0.95),
            P99 = Percentile(sortedValues, 0.99),
            Maximum = sortedValues[^1],
            MissedFiveMillisecondCount = sortedValues.Count(
                static value => value > 5.0),
            MissedEightPointThreeThreeMillisecondCount = sortedValues.Count(
                static value => value > 8.33),
        };

    private static string ClassifyFailure(
        bool withinBudget,
        IReadOnlyDictionary<string, VulkanPerformanceMetricStatistics> metrics,
        double budgetMilliseconds)
    {
        if (withinBudget)
            return "WithinBudget";

        double gpu = GetMetricP95(metrics, "gpu_pipeline_frame_ms");
        double waits =
            GetMetricP95(metrics, "vulkan_frame_wait_fence_ms") +
            GetMetricP95(metrics, "vulkan_frame_acquire_image_ms") +
            GetMetricP95(metrics, "vulkan_frame_wait_swapchain_image_ms") +
            GetMetricP95(metrics, "vulkan_frame_present_ms") +
            GetMetricP95(metrics, "vulkan_cpu_queue_lock_acquisition_ms");
        double cpu =
            GetMetricP95(metrics, "vulkan_cpu_primary_command_encoding_ms") +
            GetMetricP95(metrics, "vulkan_cpu_secondary_recording_ms") +
            GetMetricP95(metrics, "vulkan_cpu_frame_op_preparation_ms") +
            GetMetricP95(metrics, "vulkan_cpu_resource_planning_ms") +
            GetMetricP95(metrics, "vulkan_cpu_frame_data_refresh_ms") +
            GetMetricP95(metrics, "render_outside_vulkan_frame_ms");
        int materialContributors =
            (gpu >= budgetMilliseconds * 0.25 ? 1 : 0) +
            (waits >= budgetMilliseconds * 0.25 ? 1 : 0) +
            (cpu >= budgetMilliseconds * 0.25 ? 1 : 0);
        if (materialContributors > 1)
            return "Mixed";
        if (waits >= budgetMilliseconds * 0.25)
            return "WaitBound";
        if (gpu >= budgetMilliseconds * 0.25)
            return "GpuBound";
        return "CpuBound";
    }

    private static double GetMetricP95(
        IReadOnlyDictionary<string, VulkanPerformanceMetricStatistics> metrics,
        string name)
        => metrics.TryGetValue(
            name,
            out VulkanPerformanceMetricStatistics? statistic)
            ? statistic.P95
            : 0.0;

    public static double Percentile(
        IReadOnlyList<double> sortedValues,
        double percentile)
    {
        if (sortedValues.Count == 0)
            return double.NaN;

        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * sortedValues.Count) - 1,
            0,
            sortedValues.Count - 1);
        return sortedValues[index];
    }

    public static double CalculateRelativeRangePercent(
        IReadOnlyList<double> sortedValues,
        double median)
    {
        if (sortedValues.Count <= 1 || median <= 0.0 || double.IsNaN(median))
            return 0.0;

        return ((sortedValues[^1] - sortedValues[0]) / median) * 100.0;
    }

    private void AddIssue(string code, string cohort, string message)
    {
        string key = $"{code}\0{cohort}\0{message}";
        if (!_issueKeys.Add(key))
            return;

        _issues.Add(new VulkanPerformanceIssue
        {
            Code = code,
            Cohort = cohort,
            Message = message,
        });
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return bool.TryParse(GetString(element, name), out bool parsed) &&
            parsed;
    }

    private static bool GetBooleanFlag(
        JsonElement element,
        string booleanName,
        string legacyStringName)
    {
        if (element.TryGetProperty(booleanName, out _))
            return GetBoolean(element, booleanName);

        string value = GetString(element, legacyStringName);
        return value is "1" ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetVerbosityRank(string value)
        => value.ToLowerInvariant() switch
        {
            "none" => 0,
            "minimal" => 1,
            "normal" => 2,
            "verbose" => 3,
            _ => int.MaxValue,
        };

    private static bool IsProfileContractIssue(string code)
        => code is
            "UnknownPreset" or
            "UnknownProfileDefinition" or
            "PromotionEligibilityMismatch" or
            "ProfileModeMismatch" or
            "ProfilePromotionPolicyMismatch" or
            "IntrusiveGpuTimestamps" or
            "IntrusiveValidationOrDebugOutput" or
            "IntrusiveCommandLabels" or
            "IntrusiveP3Logging" or
            "IntrusiveDiagnosticTraceFlags" or
            "FrameProfileModeMismatch" or
            "ProfileNotCleanComparisonSuitable" or
            "FramePromotionPolicyMismatch" or
            "IntrusiveEditorUi" or
            "IntrusiveDebugOverlay" or
            "IntrusiveLogVerbosity" or
            "ColdCacheState" or
            "IntrusiveOverlayWork" or
            "ManifestProfileModeMismatch" or
            "ManifestNotCleanComparisonSuitable" or
            "ManifestPromotionPolicyMismatch" or
            "ManifestIntrusiveCommandLabels" or
            "ManifestIntrusiveP3Logging" or
            "ManifestIntrusiveDiagnosticTraceFlags" or
            "ManifestIntrusiveEditorUi" or
            "ManifestIntrusiveDebugOverlay" or
            "ManifestIntrusiveLogVerbosity" or
            "MissingLogSessionPath";

    private static int GetInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int parsed))
        {
            return parsed;
        }
        return int.TryParse(GetString(element, name), out parsed) ? parsed : 0;
    }

    private static double GetDouble(JsonElement element, string name)
        => TryGetDouble(element, name, out double value) ? value : 0.0;

    private static bool TryGetDouble(
        JsonElement element,
        string name,
        out double value)
    {
        value = 0.0;
        if (!element.TryGetProperty(name, out JsonElement property))
            return false;
        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDouble(out value);
        return double.TryParse(
            GetString(element, name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static DateTimeOffset GetDateTimeOffset(
        JsonElement element,
        string name)
    {
        string text = GetString(element, name);
        return DateTimeOffset.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}
