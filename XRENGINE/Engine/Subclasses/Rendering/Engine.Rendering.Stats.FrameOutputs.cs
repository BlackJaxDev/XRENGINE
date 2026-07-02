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
                    private static readonly double[] WholeFrameHistory = new double[FrameHistoryCapacity];

                    private static FrameOutputEntrySnapshot[] _lastOutputs = [];
                    private static FrameOutputManifestSnapshot _lastManifest = FrameOutputManifestSnapshot.Empty;
                    private static ulong _wholeFrameId;
                    private static long _wholeFrameTicks;
                    private static int _wholeFrameHistoryHead;
                    private static int _wholeFrameHistoryCount;

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

                    public static double LastWholeFrameMs => _lastManifest.WholeFrameMs;
                    public static double LastFrameBudgetMs => _lastManifest.BudgetMs;
                    public static string LastBudgetBand => _lastManifest.BudgetBand;

                    internal static void SnapshotAndReset()
                    {
                        lock (Sync)
                        {
                            FrameOutputEntrySnapshot[] outputs = new FrameOutputEntrySnapshot[CurrentOutputs.Count];
                            int index = 0;
                            foreach (OutputAccumulator output in CurrentOutputs.Values)
                                outputs[index++] = output.ToSnapshot();

                            Array.Sort(outputs, static (left, right) =>
                            {
                                int kind = left.OutputKind.CompareTo(right.OutputKind);
                                if (kind != 0)
                                    return kind;
                                int view = left.ViewKind.CompareTo(right.ViewKind);
                                return view != 0 ? view : string.CompareOrdinal(left.Name, right.Name);
                            });

                            double wholeFrameMs = StopwatchTicksToMilliseconds(Volatile.Read(ref _wholeFrameTicks));
                            FrameBudgetSnapshot budget = ResolveCurrentBudget();
                            FramePercentileSnapshot percentiles = ComputePercentilesNoLock();

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
                                outputs);

                            CurrentOutputs.Clear();
                            _wholeFrameId = 0UL;
                            _wholeFrameTicks = 0L;
                        }
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

                    public static FrameOutputPacingDecision EvaluatePacing(
                        EVrOutputViewKind viewKind,
                        EFrameOutputKind outputKind,
                        ulong frameId,
                        bool xrCritical,
                        float configuredTargetRateHz,
                        bool autoSkipWhenOverBudget)
                    {
                        float sourceRateHz = ResolveSourceRateHz();
                        if (xrCritical || configuredTargetRateHz <= 0.0f || configuredTargetRateHz >= sourceRateHz - 0.001f)
                            return RecordPacingDecision(viewKind, outputKind, frameId, true, false, false, EFrameOutputSkipReason.None, configuredTargetRateHz, sourceRateHz);

                        bool cadenceDue = IsCadenceFrameDue(frameId, configuredTargetRateHz, sourceRateHz);
                        bool autoSkip = false;
                        EFrameOutputSkipReason reason = cadenceDue ? EFrameOutputSkipReason.None : EFrameOutputSkipReason.Cadence;

                        if (cadenceDue && autoSkipWhenOverBudget && IsDesktopFacing(viewKind))
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

                        OutputKey key = new(telemetry.OutputKind, telemetry.ViewKind, telemetry.Name ?? string.Empty);
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

                            output.Apply(telemetry);
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
                        OutputKey key = new(outputKind, viewKind, string.Empty);
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
                                accumulator.SkipCount);
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

                    private readonly record struct OutputKey(EFrameOutputKind OutputKind, EVrOutputViewKind ViewKind, string Name);

                    private sealed class PacingAccumulator
                    {
                        public int RenderCount;
                        public int SkipCount;
                    }

                    private sealed class OutputAccumulator(EFrameOutputKind outputKind, EVrOutputViewKind viewKind, string name)
                    {
                        public ulong FrameId;
                        public EFrameOutputKind OutputKind { get; } = outputKind;
                        public EVrOutputViewKind ViewKind { get; } = viewKind;
                        public string Name { get; } = name;
                        public string PipelineName = string.Empty;
                        public bool Active;
                        public bool Rendered;
                        public bool SceneRendered;
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

                        public void Apply(in FrameOutputTelemetry telemetry)
                        {
                            FrameId = telemetry.Pacing.FrameId != 0UL ? telemetry.Pacing.FrameId : FrameId;
                            if (!string.IsNullOrWhiteSpace(telemetry.PipelineName))
                                PipelineName = telemetry.PipelineName!;

                            Active |= telemetry.Active;
                            Rendered |= telemetry.Rendered;
                            SceneRendered |= telemetry.SceneRendered;
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
                            CommandCount = Math.Max(CommandCount, telemetry.CommandCount);
                            DrawCalls += Math.Max(0, telemetry.DrawCalls);
                            MultiDrawCalls += Math.Max(0, telemetry.MultiDrawCalls);
                            Triangles += Math.Max(0, telemetry.Triangles);
                            GpuMs += Math.Max(0.0, telemetry.GpuMs);

                            double cpuMs = Math.Max(0.0, telemetry.CpuMs);
                            switch (telemetry.Phase)
                            {
                                case EFrameOutputPhase.Collect:
                                    CollectCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Swap:
                                    SwapCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Render:
                                    RenderCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Submit:
                                    SubmitCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Overlay:
                                    OverlayCpuMs += cpuMs;
                                    break;
                                case EFrameOutputPhase.Present:
                                    PresentCpuMs += cpuMs;
                                    break;
                            }
                        }

                        public FrameOutputEntrySnapshot ToSnapshot()
                            => new(
                                FrameId,
                                OutputKind,
                                ViewKind,
                                Name,
                                PipelineName,
                                Active,
                                Rendered,
                                SceneRendered,
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
                                AchievedRateHz,
                                TotalRenderCount,
                                TotalSkipCount,
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
                            []);
                }

                public readonly record struct FrameOutputEntrySnapshot(
                    ulong FrameId,
                    EFrameOutputKind OutputKind,
                    EVrOutputViewKind ViewKind,
                    string Name,
                    string PipelineName,
                    bool Active,
                    bool Rendered,
                    bool SceneRendered,
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
