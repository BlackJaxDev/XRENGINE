using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class FrameOutputs
                {
                    private const int FrameHistoryCapacity = 512;
                    private const double DefaultDesktopBudgetMs = 1000.0 / 60.0;
                    private const double DefaultVrBudgetMs = 1000.0 / 90.0;

                    private static readonly object Sync = new();
                    private static readonly Dictionary<OutputKey, OutputAccumulator> CurrentOutputs = [];
                    private static readonly Dictionary<OutputKey, PacingAccumulator> Pacing = [];
                    private static readonly Dictionary<OutputKey, CompletionAccumulator> CompletionPacing = [];
                    private static readonly double[] WholeFrameHistory = new double[FrameHistoryCapacity];

                    private static FrameOutputEntrySnapshot[] _lastOutputs = [];
                    private static FrameOutputManifestSnapshot _lastManifest = FrameOutputManifestSnapshot.Empty;
                    private static ulong _wholeFrameId;
                    private static long _wholeFrameTicks;
                    private static int _wholeFrameHistoryHead;
                    private static int _wholeFrameHistoryCount;
                    private static int _sceneSnapshots;
                    private static int _visibilityBuilds;
                    private static int _compiledPlanCacheHits;
                    private static int _compiledPlanCacheMisses;
                    private static int _physicalPlanCacheHits;
                    private static int _physicalPlanCacheMisses;
                    private static int _physicalPlanGenerations;
                    private static int _physicalPlanAliasReuses;
                    private static int _plannerArenaHighWater;
                    private static long _renderGraphPlanGeneration;
                    private static int _sharedPassReuses;
                    private static int _recordedWorkItems;
                    private static int _reusedWorkItems;
                    private static int _duplicatedWorkItems;
                    private static int _cpuBudgetDeferrals;
                    private static int _gpuBudgetDeferrals;
                    private static int _staleResultReuses;
                    private static int _missedDeadlines;
                    private static int _unapprovedPolicyEvents;
                    private static int _submissionRejections;
                    private static int _plannerPrunes;
                    private static int _plannerEvictionDeferrals;
                    private static int _globalInFlightWaits;
                    private static int _forceFlushes;

                    public static FrameOutputManifestSnapshot LastManifest
                    {
                        get
                        {
                            lock (Sync)
                                return _lastManifest;
                        }
                    }

                    public static FrameOutputEntrySnapshot[] LastOutputs
                    {
                        get
                        {
                            lock (Sync)
                            {
                                FrameOutputEntrySnapshot[] copy = new FrameOutputEntrySnapshot[_lastOutputs.Length];
                                Array.Copy(_lastOutputs, copy, copy.Length);
                                return copy;
                            }
                        }
                    }

                    /// <summary>
                    /// Copies the output telemetry accumulated since the last frame snapshot without
                    /// consuming it. The return value is the total number of current outputs, which can
                    /// exceed <paramref name="destination"/> when the caller's bounded scratch buffer is
                    /// too small.
                    /// </summary>
                    public static int CopyCurrentOutputs(Span<FrameOutputEntrySnapshot> destination)
                    {
                        lock (Sync)
                        {
                            long completionTimestamp = Stopwatch.GetTimestamp();
                            double wholeFrameMs = StopwatchTicksToMilliseconds(Volatile.Read(ref _wholeFrameTicks));
                            FrameBudgetSnapshot budget = ResolveCurrentBudget();
                            int copied = 0;
                            foreach (KeyValuePair<OutputKey, OutputAccumulator> pair in CurrentOutputs)
                            {
                                if (copied >= destination.Length)
                                    break;

                                destination[copied++] = CreateObservedCompletionSnapshot(
                                    pair.Key,
                                    pair.Value,
                                    completionTimestamp,
                                    wholeFrameMs,
                                    budget);
                            }

                            return CurrentOutputs.Count;
                        }
                    }

                    public static double LastWholeFrameMs => _lastManifest.WholeFrameMs;
                    public static double LastFrameBudgetMs => _lastManifest.BudgetMs;
                    public static string LastBudgetBand => _lastManifest.BudgetBand;

                    internal static void SnapshotAndReset()
                    {
                        lock (Sync)
                        {
                            long completionTimestamp = Stopwatch.GetTimestamp();
                            double wholeFrameMs = StopwatchTicksToMilliseconds(Volatile.Read(ref _wholeFrameTicks));
                            FrameBudgetSnapshot budget = ResolveCurrentBudget();
                            FrameOutputEntrySnapshot[] outputs = new FrameOutputEntrySnapshot[CurrentOutputs.Count];
                            int index = 0;
                            foreach ((OutputKey key, OutputAccumulator output) in CurrentOutputs)
                            {
                                outputs[index++] = CreateObservedCompletionSnapshot(
                                    key,
                                    output,
                                    completionTimestamp,
                                    wholeFrameMs,
                                    budget);
                            }

                            Array.Sort(outputs, static (left, right) =>
                            {
                                int kind = left.OutputKind.CompareTo(right.OutputKind);
                                if (kind != 0)
                                    return kind;
                                int view = left.ViewKind.CompareTo(right.ViewKind);
                                if (view != 0)
                                    return view;
                                int name = string.CompareOrdinal(left.Name, right.Name);
                                return name != 0 ? name : left.Request.OutputId.CompareTo(right.Request.OutputId);
                            });

                            FramePercentileSnapshot percentiles = ComputePercentilesNoLock();
                            FrameOutputWorkSnapshot work = CaptureWorkSnapshot(outputs);
                            ulong workloadIdentityHash = ComputeWorkloadIdentityHash(outputs);

                            _lastOutputs = outputs;
                            _lastManifest = new FrameOutputManifestSnapshot(
                                _wholeFrameId,
                                Engine.VRState.IsInVR,
                                Engine.Rendering.Settings.VrMirrorMode,
                                ResolveEffectiveVrVisibilityPolicy(),
                                budget.Band,
                                budget.BudgetMs,
                                wholeFrameMs,
                                percentiles.P50,
                                percentiles.P90,
                                percentiles.P95,
                                percentiles.P99,
                                percentiles.Worst,
                                workloadIdentityHash,
                                work,
                                outputs);

                            CurrentOutputs.Clear();
                            _wholeFrameId = 0UL;
                            _wholeFrameTicks = 0L;
                        }
                    }

                    private static FrameOutputEntrySnapshot CreateObservedCompletionSnapshot(
                        in OutputKey key,
                        OutputAccumulator output,
                        long completionTimestamp,
                        double wholeFrameMs,
                        in FrameBudgetSnapshot budget)
                    {
                        if (!CompletionPacing.TryGetValue(key, out CompletionAccumulator? completion))
                        {
                            completion = new CompletionAccumulator();
                            CompletionPacing.Add(key, completion);
                        }

                        bool completed = IsOutputFamilyCompleted(output);
                        if (output.FrameId != 0UL)
                            completion.Observe(output.FrameId, completed, completionTimestamp);
                        double deadlineMs = ResolveEffectiveDeadlineMilliseconds(
                            output.Request.OutputClass,
                            output.Request.Schedule.DeadlineMs,
                            budget.BudgetMs);
                        double completionIntervalMs = completion.LastCompletionIntervalMilliseconds > 0.0
                            ? completion.LastCompletionIntervalMilliseconds
                            : wholeFrameMs;
                        bool deadlineMissed = output.DeadlineMissed || IsCompletedOutputDeadlineMissed(
                            output.Due,
                            completed,
                            completionIntervalMs,
                            deadlineMs,
                            output.Request.Schedule.HardDeadline);
                        return output.ToSnapshot(
                            completion.ObservedRateHz,
                            completion.ContentAgeFrames,
                            deadlineMissed);
                    }

                    private static bool IsOutputFamilyCompleted(OutputAccumulator output)
                    {
                        if (output.CompletedThisFrame)
                            return true;

                        if (output.Skipped || !output.Rendered ||
                            output.OutputKind is not (EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit) ||
                            output.Request.ViewFamilyId == 0UL)
                        {
                            return false;
                        }

                        foreach (OutputAccumulator candidate in CurrentOutputs.Values)
                        {
                            if (!candidate.Skipped && candidate.SubmitObserved &&
                                candidate.OutputKind == output.OutputKind &&
                                candidate.Request.ViewFamilyId == output.Request.ViewFamilyId)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    public static void RecordWholeFrameRenderThread(ulong frameId, long stopwatchTicks)
                    {
                        if (!EnableTracking || stopwatchTicks <= 0L)
                            return;

                        double milliseconds = StopwatchTicksToMilliseconds(stopwatchTicks);
                        lock (Sync)
                        {
                            _wholeFrameId = frameId;
                            _wholeFrameTicks = stopwatchTicks;
                            WholeFrameHistory[_wholeFrameHistoryHead] = milliseconds;
                            _wholeFrameHistoryHead = (_wholeFrameHistoryHead + 1) % FrameHistoryCapacity;
                            if (_wholeFrameHistoryCount < FrameHistoryCapacity)
                                _wholeFrameHistoryCount++;
                        }
                    }

                    public static void RecordWork(in FrameOutputWorkTelemetry telemetry)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _sceneSnapshots, telemetry.SceneSnapshots);
                        AddNonNegative(ref _visibilityBuilds, telemetry.VisibilityBuilds);
                        AddNonNegative(ref _compiledPlanCacheHits, telemetry.CompiledPlanCacheHits);
                        AddNonNegative(ref _compiledPlanCacheMisses, telemetry.CompiledPlanCacheMisses);
                        AddNonNegative(ref _physicalPlanCacheHits, telemetry.PhysicalPlanCacheHits);
                        AddNonNegative(ref _physicalPlanCacheMisses, telemetry.PhysicalPlanCacheMisses);
                        AddNonNegative(ref _physicalPlanGenerations, telemetry.PhysicalPlanGenerations);
                        AddNonNegative(ref _physicalPlanAliasReuses, telemetry.PhysicalPlanAliasReuses);
                        UpdateMaximum(ref _plannerArenaHighWater, telemetry.PlannerArenaHighWater);
                        UpdateMaximum(ref _renderGraphPlanGeneration, telemetry.RenderGraphPlanGeneration);
                        AddNonNegative(ref _sharedPassReuses, telemetry.SharedPassReuses);
                        AddNonNegative(ref _recordedWorkItems, telemetry.RecordedWorkItems);
                        AddNonNegative(ref _reusedWorkItems, telemetry.ReusedWorkItems);
                        AddNonNegative(ref _duplicatedWorkItems, telemetry.DuplicatedWorkItems);
                        AddNonNegative(ref _cpuBudgetDeferrals, telemetry.CpuBudgetDeferrals);
                        AddNonNegative(ref _gpuBudgetDeferrals, telemetry.GpuBudgetDeferrals);
                        AddNonNegative(ref _staleResultReuses, telemetry.StaleResultReuses);
                        AddNonNegative(ref _missedDeadlines, telemetry.MissedDeadlines);
                        AddNonNegative(ref _unapprovedPolicyEvents, telemetry.UnapprovedPolicyEvents);
                        AddNonNegative(ref _submissionRejections, telemetry.SubmissionRejections);
                        AddNonNegative(ref _plannerPrunes, telemetry.PlannerPrunes);
                        AddNonNegative(ref _plannerEvictionDeferrals, telemetry.PlannerEvictionDeferrals);
                        AddNonNegative(ref _globalInFlightWaits, telemetry.GlobalInFlightWaits);
                        AddNonNegative(ref _forceFlushes, telemetry.ForceFlushes);
                    }

                    public static void RecordSceneSnapshot()
                        => RecordWork(new FrameOutputWorkTelemetry(SceneSnapshots: 1));

                    public static void RecordVisibilityBuild()
                        => RecordWork(new FrameOutputWorkTelemetry(VisibilityBuilds: 1));

                    public static FrameOutputPacingDecision EvaluatePacing(
                        EVrOutputViewKind viewKind,
                        EFrameOutputKind outputKind,
                        ulong frameId,
                        bool xrCritical,
                        float configuredTargetRateHz,
                        bool autoSkipWhenOverBudget)
                    {
                        float sourceRateHz = ResolveSourceRateHz();
                        bool desktopFacing = IsDesktopFacing(viewKind);
                        if (!xrCritical &&
                            autoSkipWhenOverBudget &&
                            desktopFacing &&
                            Engine.VRState.IsInVR)
                        {
                            FrameBudgetSnapshot budget = ResolveCurrentBudget();
                            double lastWholeFrameMs = LastWholeFrameMs;
                            if (budget.BudgetMs > 0.0 && lastWholeFrameMs > budget.BudgetMs)
                            {
                                return RecordPacingDecision(
                                    viewKind,
                                    outputKind,
                                    frameId,
                                    isDue: false,
                                    cadenceSkipped: false,
                                    autoSkipped: true,
                                    EFrameOutputSkipReason.Budget,
                                    configuredTargetRateHz,
                                    sourceRateHz);
                            }
                        }

                        if (xrCritical || configuredTargetRateHz <= 0.0f || configuredTargetRateHz >= sourceRateHz - 0.001f)
                            return RecordPacingDecision(viewKind, outputKind, frameId, true, false, false, EFrameOutputSkipReason.None, configuredTargetRateHz, sourceRateHz);

                        bool cadenceDue = IsCadenceFrameDue(frameId, configuredTargetRateHz, sourceRateHz);
                        bool autoSkip = false;
                        EFrameOutputSkipReason reason = cadenceDue ? EFrameOutputSkipReason.None : EFrameOutputSkipReason.Cadence;

                        if (cadenceDue && autoSkipWhenOverBudget && desktopFacing)
                        {
                            FrameBudgetSnapshot budget = ResolveCurrentBudget();
                            double lastWholeFrameMs = LastWholeFrameMs;
                            if (budget.BudgetMs > 0.0 && lastWholeFrameMs > budget.BudgetMs)
                            {
                                autoSkip = true;
                                cadenceDue = false;
                                reason = EFrameOutputSkipReason.Budget;
                            }
                        }

                        return RecordPacingDecision(viewKind, outputKind, frameId, cadenceDue, !cadenceDue && !autoSkip, autoSkip, reason, configuredTargetRateHz, sourceRateHz);
                    }

                    public static FrameOutputPacingDecision RecordForcedSkip(
                        EVrOutputViewKind viewKind,
                        EFrameOutputKind outputKind,
                        ulong frameId,
                        EFrameOutputSkipReason reason,
                        float configuredTargetRateHz = 0.0f)
                    {
                        float sourceRateHz = ResolveSourceRateHz();
                        return RecordPacingDecision(
                            viewKind,
                            outputKind,
                            frameId,
                            isDue: false,
                            cadenceSkipped: reason == EFrameOutputSkipReason.Cadence,
                            autoSkipped: reason == EFrameOutputSkipReason.Budget,
                            reason,
                            configuredTargetRateHz,
                            sourceRateHz);
                    }

                    public static void RecordOutput(in FrameOutputTelemetry telemetry)
                    {
                        if (!EnableTracking)
                            return;

                        RenderOutputRequest request = ResolveRequest(telemetry);
                        string identityName = request.OutputId == 0UL ? telemetry.Name ?? string.Empty : string.Empty;
                        OutputKey key = new(request.OutputId, telemetry.OutputKind, telemetry.ViewKind, identityName);
                        lock (Sync)
                        {
                            if (!CurrentOutputs.TryGetValue(key, out OutputAccumulator? output))
                            {
                                output = new OutputAccumulator(
                                    telemetry.OutputKind,
                                    telemetry.ViewKind,
                                    telemetry.Name ?? string.Empty);
                                CurrentOutputs.Add(key, output);
                            }

                            output.Apply(telemetry, request);
                        }
                    }

                    private static FrameOutputPacingDecision RecordPacingDecision(
                        EVrOutputViewKind viewKind,
                        EFrameOutputKind outputKind,
                        ulong frameId,
                        bool isDue,
                        bool cadenceSkipped,
                        bool autoSkipped,
                        EFrameOutputSkipReason reason,
                        float configuredTargetRateHz,
                        float sourceRateHz)
                    {
                        OutputKey key = new(0UL, outputKind, viewKind, string.Empty);
                        lock (Sync)
                        {
                            if (!Pacing.TryGetValue(key, out PacingAccumulator? accumulator))
                            {
                                accumulator = new PacingAccumulator();
                                Pacing.Add(key, accumulator);
                            }

                            if (isDue)
                                accumulator.RenderCount++;
                            else
                                accumulator.SkipCount++;

                            double achievedRateHz = sourceRateHz;
                            int total = accumulator.RenderCount + accumulator.SkipCount;
                            if (total > 0 && sourceRateHz > 0.0f)
                                achievedRateHz = sourceRateHz * (accumulator.RenderCount / (double)total);

                            RenderOutputRequest request = RenderOutputRequest.CreateDefault(
                                viewKind,
                                outputKind,
                                frameId,
                                configuredTargetRateHz,
                                sourceRateHz);
                            return new FrameOutputPacingDecision(
                                viewKind,
                                outputKind,
                                frameId,
                                isDue,
                                cadenceSkipped,
                                autoSkipped,
                                reason,
                                configuredTargetRateHz,
                                sourceRateHz,
                                achievedRateHz,
                                accumulator.RenderCount,
                                accumulator.SkipCount,
                                request);
                        }
                    }

                    private static bool IsCadenceFrameDue(ulong frameId, float targetRateHz, float sourceRateHz)
                    {
                        if (frameId == 0UL)
                            return true;

                        double current = Math.Floor(frameId * targetRateHz / sourceRateHz);
                        double previous = Math.Floor((frameId - 1UL) * targetRateHz / sourceRateHz);
                        return current > previous;
                    }

                    private static float ResolveSourceRateHz()
                    {
                        double vrHz = Engine.Rendering.Stats.Vr.VrRenderFrameRateHz;
                        if (Engine.VRState.IsInVR && vrHz > 1.0 && double.IsFinite(vrHz))
                            return (float)Math.Clamp(vrHz, 1.0, 1000.0);

                        float target = Engine.Time.Timer.TargetRenderFrequency;
                        return target > 1.0f ? target : 90.0f;
                    }

                    private static FrameBudgetSnapshot ResolveCurrentBudget()
                    {
                        if (!Engine.VRState.IsInVR && !RuntimeEngine.Rendering.State.IsStereoPass)
                            return new("Desktop60", DefaultDesktopBudgetMs);

                        double hz = Engine.Rendering.Stats.Vr.VrRenderFrameRateHz;
                        if (!double.IsFinite(hz) || hz <= 1.0)
                            hz = 90.0;

                        if (hz >= 110.0)
                            return new("VR120", 1000.0 / 120.0);
                        if (hz >= 81.0)
                            return new("VR90", 1000.0 / 90.0);
                        if (hz >= 65.0)
                            return new("VR72", 1000.0 / 72.0);

                        return new("VR90", DefaultVrBudgetMs);
                    }

                    private static EVrVisibilityPolicy ResolveEffectiveVrVisibilityPolicy()
                    {
                        EVrMirrorMode mirrorMode = Engine.Rendering.Settings.VrMirrorMode;
                        return mirrorMode == EVrMirrorMode.FullIndependentRender
                            ? EVrVisibilityPolicy.IndependentDesktopAndVrEyes
                            : EVrVisibilityPolicy.CombinedRuntimeLeftRightCyclopean;
                    }

                    private static bool IsDesktopFacing(EVrOutputViewKind viewKind)
                        => viewKind is EVrOutputViewKind.DesktopEditor or EVrOutputViewKind.CyclopeanDesktop;

                    private static FramePercentileSnapshot ComputePercentilesNoLock()
                    {
                        if (_wholeFrameHistoryCount == 0)
                            return default;

                        double[] values = new double[_wholeFrameHistoryCount];
                        for (int i = 0; i < _wholeFrameHistoryCount; i++)
                            values[i] = WholeFrameHistory[i];

                        Array.Sort(values);
                        return new(
                            Percentile(values, 0.50),
                            Percentile(values, 0.90),
                            Percentile(values, 0.95),
                            Percentile(values, 0.99),
                            values[^1]);
                    }

                    private static double Percentile(double[] sortedValues, double percentile)
                    {
                        if (sortedValues.Length == 0)
                            return 0.0;
                        if (sortedValues.Length == 1)
                            return sortedValues[0];

                        double position = (sortedValues.Length - 1) * Math.Clamp(percentile, 0.0, 1.0);
                        int lower = (int)Math.Floor(position);
                        int upper = (int)Math.Ceiling(position);
                        if (lower == upper)
                            return sortedValues[lower];

                        double fraction = position - lower;
                        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
                    }

                    private static double StopwatchTicksToMilliseconds(long ticks)
                        => ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;

                    internal static double UpdateObservedCompletionRateHz(
                        double previousRateHz,
                        long previousCompletionTimestamp,
                        long completionTimestamp)
                    {
                        double completionIntervalMs = CalculateCompletionIntervalMilliseconds(
                            previousCompletionTimestamp,
                            completionTimestamp);
                        if (completionIntervalMs <= 0.0)
                            return Math.Max(0.0, previousRateHz);

                        double instantaneousRateHz = Math.Clamp(1000.0 / completionIntervalMs, 0.0, 1000.0);
                        if (previousRateHz <= 0.0 || !double.IsFinite(previousRateHz))
                            return instantaneousRateHz;

                        const double smoothing = 0.25;
                        return previousRateHz + ((instantaneousRateHz - previousRateHz) * smoothing);
                    }

                    internal static double CalculateCompletionIntervalMilliseconds(
                        long previousCompletionTimestamp,
                        long completionTimestamp)
                    {
                        if (previousCompletionTimestamp <= 0L || completionTimestamp <= previousCompletionTimestamp)
                            return 0.0;

                        double milliseconds =
                            (completionTimestamp - previousCompletionTimestamp) * 1000.0 / Stopwatch.Frequency;
                        return double.IsFinite(milliseconds) && milliseconds > 0.0 ? milliseconds : 0.0;
                    }

                    internal static bool IsCompletedOutputDeadlineMissed(
                        bool isDue,
                        bool completed,
                        double completedFrameMs,
                        double deadlineMs,
                        bool hardDeadline)
                    {
                        if (!isDue || deadlineMs <= 0.0 || !double.IsFinite(deadlineMs))
                            return false;
                        if (!completed)
                            return hardDeadline;
                        return double.IsFinite(completedFrameMs) && completedFrameMs > deadlineMs;
                    }

                    internal static double ResolveEffectiveDeadlineMilliseconds(
                        ERenderOutputClass outputClass,
                        double requestedDeadlineMs,
                        double activeBudgetMs)
                    {
                        if (outputClass != ERenderOutputClass.XrCritical ||
                            activeBudgetMs <= 0.0 ||
                            !double.IsFinite(activeBudgetMs))
                        {
                            return requestedDeadlineMs;
                        }

                        if (requestedDeadlineMs <= 0.0 || !double.IsFinite(requestedDeadlineMs))
                            return activeBudgetMs;
                        return Math.Min(requestedDeadlineMs, activeBudgetMs);
                    }

                    private static RenderOutputRequest ResolveRequest(in FrameOutputTelemetry telemetry)
                    {
                        if (telemetry.Request.IsDefined)
                            return telemetry.Request;
                        if (telemetry.Pacing.Request.IsDefined)
                            return telemetry.Pacing.Request;
                        return RenderOutputRequest.CreateDefault(
                            telemetry.ViewKind,
                            telemetry.OutputKind,
                            telemetry.Pacing.FrameId,
                            telemetry.Pacing.ConfiguredTargetRateHz,
                            telemetry.Pacing.SourceRateHz);
                    }

                    private static void AddNonNegative(ref int field, int value)
                    {
                        if (value > 0)
                            Interlocked.Add(ref field, value);
                    }

                    private static FrameOutputWorkSnapshot CaptureWorkSnapshot(FrameOutputEntrySnapshot[] outputs)
                    {
                        int logicalOutputRequests = 0;
                        int uniqueViewFamilies = 0;
                        int targetVariants = 0;
                        int outputEvents = 0;
                        int collectEvents = 0;
                        int swapEvents = 0;
                        int renderEvents = 0;
                        int submitEvents = 0;
                        int overlayEvents = 0;
                        int presentEvents = 0;
                        int cpuBudgetDeferrals = Interlocked.Exchange(ref _cpuBudgetDeferrals, 0);
                        int gpuBudgetDeferrals = Interlocked.Exchange(ref _gpuBudgetDeferrals, 0);
                        int staleResultReuses = Interlocked.Exchange(ref _staleResultReuses, 0);
                        int missedDeadlines = Interlocked.Exchange(ref _missedDeadlines, 0);
                        int unapprovedPolicyEvents = Interlocked.Exchange(ref _unapprovedPolicyEvents, 0);
                        for (int i = 0; i < outputs.Length; i++)
                        {
                            FrameOutputEntrySnapshot output = outputs[i];
                            if (IsLogicalRenderRequest(outputs, i))
                                logicalOutputRequests++;
                            if (output.Request.ViewFamilyId != 0UL && IsFirstViewFamily(outputs, i))
                                uniqueViewFamilies++;
                            if (output.Request.Target.IsSpecified && IsFirstTargetVariant(outputs, i))
                                targetVariants++;
                            if (output.WorkDisposition == ERenderOutputWorkDisposition.Deferred)
                            {
                                if (output.PolicyReason == ERenderOutputPolicyReason.GpuBudget)
                                    gpuBudgetDeferrals++;
                                else
                                    cpuBudgetDeferrals++;
                            }
                            if (output.WorkDisposition == ERenderOutputWorkDisposition.ReusedStale)
                                staleResultReuses++;
                            if (output.DeadlineMissed)
                                missedDeadlines++;
                            if (!output.PolicyAuthorized)
                                unapprovedPolicyEvents++;
                            outputEvents += output.OutputEventCount;
                            collectEvents += output.CollectEventCount;
                            swapEvents += output.SwapEventCount;
                            renderEvents += output.RenderEventCount;
                            submitEvents += output.SubmitEventCount;
                            overlayEvents += output.OverlayEventCount;
                            presentEvents += output.PresentEventCount;
                        }

                        return new(
                            OutputRequestCount: logicalOutputRequests,
                            OutputEventCount: outputEvents,
                            CollectEventCount: collectEvents,
                            SwapEventCount: swapEvents,
                            RenderEventCount: renderEvents,
                            SubmitEventCount: submitEvents,
                            OverlayEventCount: overlayEvents,
                            PresentEventCount: presentEvents,
                            UniqueViewFamilyCount: uniqueViewFamilies,
                            TargetVariantCount: targetVariants,
                            SceneSnapshotCount: Interlocked.Exchange(ref _sceneSnapshots, 0),
                            VisibilityBuildCount: Interlocked.Exchange(ref _visibilityBuilds, 0),
                            CompiledPlanCacheHits: Interlocked.Exchange(ref _compiledPlanCacheHits, 0),
                            CompiledPlanCacheMisses: Interlocked.Exchange(ref _compiledPlanCacheMisses, 0),
                            PhysicalPlanCacheHits: Interlocked.Exchange(ref _physicalPlanCacheHits, 0),
                            PhysicalPlanCacheMisses: Interlocked.Exchange(ref _physicalPlanCacheMisses, 0),
                            PhysicalPlanGenerations: Interlocked.Exchange(ref _physicalPlanGenerations, 0),
                            PhysicalPlanAliasReuses: Interlocked.Exchange(ref _physicalPlanAliasReuses, 0),
                            PlannerArenaHighWater: Interlocked.Exchange(ref _plannerArenaHighWater, 0),
                            RenderGraphPlanGeneration: Interlocked.Exchange(ref _renderGraphPlanGeneration, 0L),
                            SharedPassReuseCount: Interlocked.Exchange(ref _sharedPassReuses, 0),
                            RecordedWorkItemCount: Interlocked.Exchange(ref _recordedWorkItems, 0),
                            ReusedWorkItemCount: Interlocked.Exchange(ref _reusedWorkItems, 0),
                            DuplicatedWorkItemCount: Interlocked.Exchange(ref _duplicatedWorkItems, 0),
                            CpuBudgetDeferralCount: cpuBudgetDeferrals,
                            GpuBudgetDeferralCount: gpuBudgetDeferrals,
                            StaleResultReuseCount: staleResultReuses,
                            MissedDeadlineCount: missedDeadlines,
                            UnapprovedPolicyEventCount: unapprovedPolicyEvents,
                            SubmissionRejectionCount: Interlocked.Exchange(ref _submissionRejections, 0),
                            PlannerPruneCount: Interlocked.Exchange(ref _plannerPrunes, 0),
                            PlannerEvictionDeferralCount: Interlocked.Exchange(ref _plannerEvictionDeferrals, 0),
                            GlobalInFlightWaitCount: Interlocked.Exchange(ref _globalInFlightWaits, 0),
                            ForceFlushCount: Interlocked.Exchange(ref _forceFlushes, 0));
                    }

                    private static bool IsLogicalRenderRequest(FrameOutputEntrySnapshot[] outputs, int index)
                    {
                        FrameOutputEntrySnapshot candidate = outputs[index];
                        if (!candidate.RenderPhaseSceneRendered && !candidate.SceneRendered && !candidate.Skipped)
                            return false;

                        ulong familyId = candidate.Request.ViewFamilyId;
                        ulong outputId = candidate.Request.OutputId;
                        for (int i = 0; i < index; i++)
                        {
                            FrameOutputEntrySnapshot previous = outputs[i];
                            if (!previous.RenderPhaseSceneRendered && !previous.SceneRendered && !previous.Skipped)
                                continue;
                            if (familyId != 0UL
                                ? previous.Request.ViewFamilyId == familyId
                                : previous.Request.OutputId == outputId)
                            {
                                return false;
                            }
                        }
                        return true;
                    }

                    private static void UpdateMaximum(ref int target, int candidate)
                    {
                        if (candidate <= 0)
                            return;

                        int current = Volatile.Read(ref target);
                        while (candidate > current)
                        {
                            int observed = Interlocked.CompareExchange(ref target, candidate, current);
                            if (observed == current)
                                return;
                            current = observed;
                        }
                    }

                    private static void UpdateMaximum(ref long target, long candidate)
                    {
                        if (candidate <= 0L)
                            return;

                        long current = Volatile.Read(ref target);
                        while (candidate > current)
                        {
                            long observed = Interlocked.CompareExchange(ref target, candidate, current);
                            if (observed == current)
                                return;
                            current = observed;
                        }
                    }

                    private static bool IsFirstViewFamily(FrameOutputEntrySnapshot[] outputs, int index)
                    {
                        ulong familyId = outputs[index].Request.ViewFamilyId;
                        for (int i = 0; i < index; i++)
                        {
                            if (outputs[i].Request.ViewFamilyId == familyId)
                                return false;
                        }
                        return true;
                    }

                    private static bool IsFirstTargetVariant(FrameOutputEntrySnapshot[] outputs, int index)
                    {
                        ulong compatibilityKey = outputs[index].Request.Target.CompatibilityKey;
                        int externalSlot = outputs[index].Request.Target.ExternalImageSlot;
                        for (int i = 0; i < index; i++)
                        {
                            RenderOutputTargetDescriptor target = outputs[i].Request.Target;
                            if (target.IsSpecified &&
                                target.CompatibilityKey == compatibilityKey &&
                                target.ExternalImageSlot == externalSlot)
                            {
                                return false;
                            }
                        }
                        return true;
                    }

                    private static ulong ComputeWorkloadIdentityHash(FrameOutputEntrySnapshot[] outputs)
                    {
                        ulong hash = 1469598103934665603UL;
                        AddHash(ref hash, (ulong)outputs.Length);
                        for (int i = 0; i < outputs.Length; i++)
                        {
                            FrameOutputEntrySnapshot output = outputs[i];
                            AddHash(ref hash, output.Request.OutputId);
                            AddHash(ref hash, output.Request.ViewFamilyId);
                            AddHash(ref hash, (ulong)output.Request.OutputKind);
                            AddHash(ref hash, (ulong)output.Request.ViewKind);
                            AddHash(ref hash, (ulong)output.Request.OutputClass);
                            RenderOutputTargetDescriptor target = output.Request.Target;
                            AddHash(ref hash, (ulong)target.TargetClass);
                            AddHash(ref hash, target.StableTargetId);
                            AddHash(ref hash, target.TargetGeneration);
                            AddHash(ref hash, target.DisplayWidth);
                            AddHash(ref hash, target.DisplayHeight);
                            AddHash(ref hash, target.InternalWidth);
                            AddHash(ref hash, target.InternalHeight);
                            AddHash(ref hash, target.FormatCompatibilityKey);
                            AddHash(ref hash, target.SampleCount);
                            AddHash(ref hash, target.ViewMask);
                            AddHash(ref hash, (ulong)output.Request.QualityRequirements);
                            AddHash(ref hash, (ulong)output.Request.FallbackPolicy);
                        }
                        return hash;
                    }

                    private static void AddHash(ref ulong hash, ulong value)
                    {
                        hash ^= value;
                        hash *= 1099511628211UL;
                    }

                    private readonly record struct OutputKey(ulong OutputId, EFrameOutputKind OutputKind, EVrOutputViewKind ViewKind, string Name);

                    private sealed class PacingAccumulator
                    {
                        public int RenderCount;
                        public int SkipCount;
                    }

                    private sealed class CompletionAccumulator
                    {
                        public ulong LastObservedFrameId;
                        public long LastCompletionTimestamp;
                        public double ObservedRateHz;
                        public double LastCompletionIntervalMilliseconds;
                        public uint ContentAgeFrames;

                        public void Observe(ulong frameId, bool completed, long completionTimestamp)
                        {
                            if (frameId != 0UL && frameId == LastObservedFrameId)
                                return;

                            if (frameId != 0UL)
                                LastObservedFrameId = frameId;

                            if (!completed)
                            {
                                if (ContentAgeFrames < uint.MaxValue)
                                    ContentAgeFrames++;
                                return;
                            }

                            LastCompletionIntervalMilliseconds = CalculateCompletionIntervalMilliseconds(
                                LastCompletionTimestamp,
                                completionTimestamp);
                            ObservedRateHz = UpdateObservedCompletionRateHz(
                                ObservedRateHz,
                                LastCompletionTimestamp,
                                completionTimestamp);
                            LastCompletionTimestamp = completionTimestamp;
                            ContentAgeFrames = 0u;
                        }
                    }

                    private sealed class OutputAccumulator(EFrameOutputKind outputKind, EVrOutputViewKind viewKind, string name)
                    {
                        public ulong FrameId;
                        public EFrameOutputKind OutputKind { get; } = outputKind;
                        public EVrOutputViewKind ViewKind { get; } = viewKind;
                        public string Name { get; } = name;
                        public string PipelineName = string.Empty;
                        public int PipelineInstanceId;
                        public int ResourcePlanGeneration;
                        public ulong CommandGeneration;
                        public string AntiAliasingMode = string.Empty;
                        public bool Active;
                        public bool Rendered;
                        public bool SceneRendered;
                        public bool RenderPhaseSceneRendered;
                        public bool Mirror;
                        public bool SeparateSceneRender;
                        public bool SharedVisibility;
                        public bool Due = true;
                        public bool Skipped;
                        public bool CadenceSkipped;
                        public bool AutoSkipped;
                        public EFrameOutputSkipReason SkipReason;
                        public float ConfiguredTargetRateHz;
                        public float SourceRateHz;
                        public double AchievedRateHz;
                        public int TotalRenderCount;
                        public int TotalSkipCount;
                        public int CommandCount;
                        public int DrawCalls;
                        public int MultiDrawCalls;
                        public int Triangles;
                        public double CollectCpuMs;
                        public double SwapCpuMs;
                        public double RenderCpuMs;
                        public double SubmitCpuMs;
                        public double OverlayCpuMs;
                        public double PresentCpuMs;
                        public double GpuMs;
                        public RenderOutputRequest Request;
                        public ERenderOutputWorkDisposition WorkDisposition;
                        public uint ContentAgeFrames;
                        public bool DeadlineMissed;
                        public bool PolicyAuthorized = true;
                        public ERenderOutputPolicyReason PolicyReason;
                        public bool SubmitObserved;
                        public bool PresentObserved;
                        public int OutputEventCount;
                        public int CollectEventCount;
                        public int SwapEventCount;
                        public int RenderEventCount;
                        public int SubmitEventCount;
                        public int OverlayEventCount;
                        public int PresentEventCount;

                        public bool CompletedThisFrame
                            => !Skipped && Rendered && OutputKind switch
                            {
                                EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit => SubmitObserved,
                                EFrameOutputKind.Present => PresentObserved,
                                EFrameOutputKind.ImGuiOverlay or EFrameOutputKind.DynamicTextOverlay => OverlayEventCount > 0,
                                _ => RenderPhaseSceneRendered || SceneRendered,
                            };

                        public void Apply(in FrameOutputTelemetry telemetry, in RenderOutputRequest request)
                        {
                            FrameId = telemetry.Pacing.FrameId != 0UL ? telemetry.Pacing.FrameId : FrameId;
                            if (!string.IsNullOrWhiteSpace(telemetry.PipelineName))
                                PipelineName = telemetry.PipelineName!;
                            if (telemetry.PipelineInstanceId > 0)
                                PipelineInstanceId = telemetry.PipelineInstanceId;
                            if (telemetry.ResourcePlanGeneration >= 0)
                                ResourcePlanGeneration = telemetry.ResourcePlanGeneration;
                            if (telemetry.CommandGeneration > 0UL)
                                CommandGeneration = telemetry.CommandGeneration;
                            if (!string.IsNullOrWhiteSpace(telemetry.AntiAliasingMode))
                                AntiAliasingMode = telemetry.AntiAliasingMode!;

                            Active |= telemetry.Active;
                            Rendered |= telemetry.Rendered;
                            SceneRendered |= telemetry.SceneRendered;
                            RenderPhaseSceneRendered |= telemetry.Phase == EFrameOutputPhase.Render && telemetry.SceneRendered;
                            SubmitObserved |= telemetry.Phase == EFrameOutputPhase.Submit && telemetry.Rendered;
                            PresentObserved |= telemetry.Phase == EFrameOutputPhase.Present && telemetry.Rendered;
                            Mirror |= telemetry.Mirror;
                            SeparateSceneRender |= telemetry.SeparateSceneRender;
                            SharedVisibility |= telemetry.SharedVisibility;
                            Due &= telemetry.Pacing.IsDue;
                            Skipped |= telemetry.Pacing.Skipped;
                            CadenceSkipped |= telemetry.Pacing.CadenceSkipped;
                            AutoSkipped |= telemetry.Pacing.AutoSkipped;
                            if (telemetry.Pacing.SkipReason != EFrameOutputSkipReason.None)
                                SkipReason = telemetry.Pacing.SkipReason;
                            ConfiguredTargetRateHz = Math.Max(ConfiguredTargetRateHz, telemetry.Pacing.ConfiguredTargetRateHz);
                            SourceRateHz = Math.Max(SourceRateHz, telemetry.Pacing.SourceRateHz);
                            AchievedRateHz = telemetry.Pacing.AchievedRateHz > 0.0 ? telemetry.Pacing.AchievedRateHz : AchievedRateHz;
                            TotalRenderCount = Math.Max(TotalRenderCount, telemetry.Pacing.TotalRenderCount);
                            TotalSkipCount = Math.Max(TotalSkipCount, telemetry.Pacing.TotalSkipCount);
                            Request = request;
                            ERenderOutputWorkDisposition disposition = ResolveDisposition(telemetry);
                            WorkDisposition = MergeDisposition(WorkDisposition, disposition);
                            ContentAgeFrames = Math.Max(ContentAgeFrames, telemetry.ContentAgeFrames);
                            DeadlineMissed |= telemetry.DeadlineMissed;
                            ERenderOutputPolicyReason policyReason = telemetry.PolicyReason != ERenderOutputPolicyReason.None
                                ? telemetry.PolicyReason
                                : ResolvePolicyReason(telemetry.Pacing.SkipReason);
                            if (policyReason != ERenderOutputPolicyReason.None)
                                PolicyReason = policyReason;
                            bool availabilitySkip = telemetry.Pacing.SkipReason is
                                EFrameOutputSkipReason.MirrorOff or
                                EFrameOutputSkipReason.SurfaceUnavailable or
                                EFrameOutputSkipReason.VrGated or
                                EFrameOutputSkipReason.Disabled;
                            PolicyAuthorized &= telemetry.PolicyAuthorized &&
                                (availabilitySkip || request.Allows(disposition));
                            CommandCount = Math.Max(CommandCount, telemetry.CommandCount);
                            DrawCalls += Math.Max(0, telemetry.DrawCalls);
                            MultiDrawCalls += Math.Max(0, telemetry.MultiDrawCalls);
                            Triangles += Math.Max(0, telemetry.Triangles);
                            GpuMs += Math.Max(0.0, telemetry.GpuMs);
                            OutputEventCount++;

                            double cpuMs = Math.Max(0.0, telemetry.CpuMs);
                            switch (telemetry.Phase)
                            {
                                case EFrameOutputPhase.Collect:
                                    CollectEventCount++;
                                    CollectCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Swap:
                                    SwapEventCount++;
                                    SwapCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Render:
                                    RenderEventCount++;
                                    RenderCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Submit:
                                    SubmitEventCount++;
                                    SubmitCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Overlay:
                                    OverlayEventCount++;
                                    OverlayCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Present:
                                    PresentEventCount++;
                                    PresentCpuMs += cpuMs;
                                    break;
                            }
                        }

                        public FrameOutputEntrySnapshot ToSnapshot(
                            double observedRateHz,
                            uint observedContentAgeFrames,
                            bool observedDeadlineMissed)
                            => new(
                                FrameId,
                                OutputKind,
                                ViewKind,
                                Name,
                                PipelineName,
                                PipelineInstanceId,
                                ResourcePlanGeneration,
                                CommandGeneration,
                                AntiAliasingMode,
                                Active,
                                Rendered,
                                SceneRendered,
                                RenderPhaseSceneRendered,
                                Mirror,
                                SeparateSceneRender,
                                SharedVisibility,
                                Due,
                                Skipped,
                                CadenceSkipped,
                                AutoSkipped,
                                SkipReason,
                                ConfiguredTargetRateHz,
                                SourceRateHz,
                                observedRateHz,
                                TotalRenderCount,
                                TotalSkipCount,
                                Request,
                                WorkDisposition,
                                Math.Max(ContentAgeFrames, observedContentAgeFrames),
                                observedDeadlineMissed,
                                PolicyAuthorized,
                                PolicyReason,
                                SubmitObserved,
                                PresentObserved,
                                OutputEventCount,
                                CollectEventCount,
                                SwapEventCount,
                                RenderEventCount,
                                SubmitEventCount,
                                OverlayEventCount,
                                PresentEventCount,
                                CommandCount,
                                DrawCalls,
                                MultiDrawCalls,
                                Triangles,
                                CollectCpuMs,
                                SwapCpuMs,
                                RenderCpuMs,
                                SubmitCpuMs,
                                OverlayCpuMs,
                                PresentCpuMs,
                                GpuMs);

                        public FrameOutputEntrySnapshot ToSnapshot()
                            => ToSnapshot(AchievedRateHz, ContentAgeFrames, DeadlineMissed);

                        private static ERenderOutputWorkDisposition ResolveDisposition(in FrameOutputTelemetry telemetry)
                        {
                            if (!telemetry.Pacing.Skipped)
                                return telemetry.WorkDisposition;
                            return telemetry.Pacing.SkipReason switch
                            {
                                EFrameOutputSkipReason.HeldLastImage => ERenderOutputWorkDisposition.ReusedStale,
                                EFrameOutputSkipReason.Cadence or EFrameOutputSkipReason.Budget => ERenderOutputWorkDisposition.Deferred,
                                _ => ERenderOutputWorkDisposition.Skipped,
                            };
                        }

                        private static ERenderOutputWorkDisposition MergeDisposition(
                            ERenderOutputWorkDisposition current,
                            ERenderOutputWorkDisposition next)
                            => (ERenderOutputWorkDisposition)Math.Max((int)current, (int)next);

                        private static ERenderOutputPolicyReason ResolvePolicyReason(EFrameOutputSkipReason reason)
                            => reason switch
                            {
                                EFrameOutputSkipReason.Cadence => ERenderOutputPolicyReason.Cadence,
                                EFrameOutputSkipReason.Budget => ERenderOutputPolicyReason.CpuBudget,
                                EFrameOutputSkipReason.MirrorOff => ERenderOutputPolicyReason.MirrorDisabled,
                                EFrameOutputSkipReason.SurfaceUnavailable => ERenderOutputPolicyReason.SurfaceUnavailable,
                                EFrameOutputSkipReason.VrGated => ERenderOutputPolicyReason.VrGated,
                                EFrameOutputSkipReason.Disabled => ERenderOutputPolicyReason.OutputDisabled,
                                EFrameOutputSkipReason.HeldLastImage => ERenderOutputPolicyReason.HeldLastImage,
                                _ => ERenderOutputPolicyReason.None,
                            };
                    }

                    private readonly record struct FrameBudgetSnapshot(string Band, double BudgetMs);
                    private readonly record struct FramePercentileSnapshot(double P50, double P90, double P95, double P99, double Worst);
                }

                public readonly record struct FrameOutputManifestSnapshot(
                    ulong FrameId,
                    bool VrActive,
                    EVrMirrorMode MirrorMode,
                    EVrVisibilityPolicy VisibilityPolicy,
                    string BudgetBand,
                    double BudgetMs,
                    double WholeFrameMs,
                    double WholeFrameP50Ms,
                    double WholeFrameP90Ms,
                    double WholeFrameP95Ms,
                    double WholeFrameP99Ms,
                    double WholeFrameWorstMs,
                    ulong WorkloadIdentityHash,
                    FrameOutputWorkSnapshot Work,
                    FrameOutputEntrySnapshot[] Outputs)
                {
                    public static FrameOutputManifestSnapshot Empty
                        => new(
                            0UL,
                            false,
                            EVrMirrorMode.Off,
                            EVrVisibilityPolicy.CombinedRuntimeLeftRightCyclopean,
                            string.Empty,
                            0.0,
                            0.0,
                            0.0,
                            0.0,
                            0.0,
                            0.0,
                            0.0,
                            0UL,
                            default,
                            []);
                }

                public readonly record struct FrameOutputWorkSnapshot(
                    int OutputRequestCount,
                    int OutputEventCount,
                    int CollectEventCount,
                    int SwapEventCount,
                    int RenderEventCount,
                    int SubmitEventCount,
                    int OverlayEventCount,
                    int PresentEventCount,
                    int UniqueViewFamilyCount,
                    int TargetVariantCount,
                    int SceneSnapshotCount,
                    int VisibilityBuildCount,
                    int CompiledPlanCacheHits,
                    int CompiledPlanCacheMisses,
                    int PhysicalPlanCacheHits,
                    int PhysicalPlanCacheMisses,
                    int PhysicalPlanGenerations,
                    int PhysicalPlanAliasReuses,
                    int PlannerArenaHighWater,
                    long RenderGraphPlanGeneration,
                    int SharedPassReuseCount,
                    int RecordedWorkItemCount,
                    int ReusedWorkItemCount,
                    int DuplicatedWorkItemCount,
                    int CpuBudgetDeferralCount,
                    int GpuBudgetDeferralCount,
                    int StaleResultReuseCount,
                    int MissedDeadlineCount,
                    int UnapprovedPolicyEventCount,
                    int SubmissionRejectionCount,
                    int PlannerPruneCount,
                    int PlannerEvictionDeferralCount,
                    int GlobalInFlightWaitCount,
                    int ForceFlushCount);

                public readonly record struct FrameOutputEntrySnapshot(
                    ulong FrameId,
                    EFrameOutputKind OutputKind,
                    EVrOutputViewKind ViewKind,
                    string Name,
                    string PipelineName,
                    int PipelineInstanceId,
                    int ResourcePlanGeneration,
                    ulong CommandGeneration,
                    string AntiAliasingMode,
                    bool Active,
                    bool Rendered,
                    bool SceneRendered,
                    bool RenderPhaseSceneRendered,
                    bool Mirror,
                    bool SeparateSceneRender,
                    bool SharedVisibility,
                    bool Due,
                    bool Skipped,
                    bool CadenceSkipped,
                    bool AutoSkipped,
                    EFrameOutputSkipReason SkipReason,
                    float ConfiguredTargetRateHz,
                    float SourceRateHz,
                    double AchievedRateHz,
                    int TotalRenderCount,
                    int TotalSkipCount,
                    RenderOutputRequest Request,
                    ERenderOutputWorkDisposition WorkDisposition,
                    uint ContentAgeFrames,
                    bool DeadlineMissed,
                    bool PolicyAuthorized,
                    ERenderOutputPolicyReason PolicyReason,
                    bool SubmitObserved,
                    bool PresentObserved,
                    int OutputEventCount,
                    int CollectEventCount,
                    int SwapEventCount,
                    int RenderEventCount,
                    int SubmitEventCount,
                    int OverlayEventCount,
                    int PresentEventCount,
                    int CommandCount,
                    int DrawCalls,
                    int MultiDrawCalls,
                    int Triangles,
                    double CollectCpuMs,
                    double SwapCpuMs,
                    double RenderCpuMs,
                    double SubmitCpuMs,
                    double OverlayCpuMs,
                    double PresentCpuMs,
                    double GpuMs)
                {
                    public string OutputKindName => OutputKind.ToString();
                    public string ViewKindName => ViewKind.ToString();
                    public string SkipReasonName => SkipReason.ToString();
                    public string Summary
                        => string.Create(
                            CultureInfo.InvariantCulture,
                            $"{OutputKind}/{ViewKind} due={Due} skipped={Skipped} cpu={CollectCpuMs + SwapCpuMs + RenderCpuMs + SubmitCpuMs + OverlayCpuMs + PresentCpuMs:0.###}ms gpu={GpuMs:0.###}ms cmds={CommandCount}");
                }
            }
        }
    }
}
