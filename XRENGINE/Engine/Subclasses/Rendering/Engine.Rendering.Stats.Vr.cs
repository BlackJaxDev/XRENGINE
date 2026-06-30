using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class Vr
                {
                    private static int _vrLeftEyeDraws;
                    private static int _vrRightEyeDraws;
                    private static int _lastFrameVrLeftEyeDraws;
                    private static int _lastFrameVrRightEyeDraws;
                    private static int _vrLeftEyeVisible;
                    private static int _vrRightEyeVisible;
                    private static int _lastFrameVrLeftEyeVisible;
                    private static int _lastFrameVrRightEyeVisible;
                    private static long _vrLeftWorkerBuildTimeTicks;
                    private static long _vrRightWorkerBuildTimeTicks;
                    private static long _lastFrameVrLeftWorkerBuildTimeTicks;
                    private static long _lastFrameVrRightWorkerBuildTimeTicks;
                    private static long _vrRenderSubmitTimeTicks;
                    private static long _lastFrameVrRenderSubmitTimeTicks;
                    private static long _vrXrWaitFrameBlockTimeTicks;
                    private static long _lastFrameVrXrWaitFrameBlockTimeTicks;
                    private static long _vrXrEndFrameSubmitTimeTicks;
                    private static long _lastFrameVrXrEndFrameSubmitTimeTicks;
                    private static long _vrXrPredictedToLatePoseDeltaMillimetersBits;
                    private static long _lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits;
                    private static long _vrXrPredictedToLatePoseDeltaDegreesBits;
                    private static long _lastFrameVrXrPredictedToLatePoseDeltaDegreesBits;
                    private static long _vrXrPredictedDisplayLeadTimeMsBits = BitConverter.DoubleToInt64Bits(double.NaN);
                    private static long _lastFrameVrXrPredictedDisplayLeadTimeMsBits = BitConverter.DoubleToInt64Bits(double.NaN);
                    private static int _vrXrMissedDeadlineFrames;
                    private static int _lastFrameVrXrMissedDeadlineFrames;
                    private static int _vrXrTrackingLossFrames;
                    private static int _lastFrameVrXrTrackingLossFrames;
                    private static long _vrXrRelocatePredictedTimeTicks;
                    private static long _lastFrameVrXrRelocatePredictedTimeTicks;
                    private static long _vrXrCollectFrustumExpansionDegreesBits;
                    private static long _lastFrameVrXrCollectFrustumExpansionDegreesBits;
                    private static long _vrXrPacingThreadIdleTimeTicks;
                    private static long _lastFrameVrXrPacingThreadIdleTimeTicks;
                    private static int _vrXrPacingHandoffStalls;
                    private static int _lastFrameVrXrPacingHandoffStalls;
                    private static int _vrRenderPassDrawCalls;
                    private static int _vrRenderPassMultiDrawCalls;
                    private static int _vrRenderPassTrianglesRendered;
                    private static int _lastFrameVrRenderPassDrawCalls;
                    private static int _lastFrameVrRenderPassMultiDrawCalls;
                    private static int _lastFrameVrRenderPassTrianglesRendered;
                    private static long _vrRenderPassTimeTicks;
                    private static long _lastFrameVrRenderPassTimeTicks;
                    private static long _vrLastPresentedTimestampTicks;
                    private static long _vrRenderFrameIntervalTicks;
                    private static long _lastFrameVrRenderFrameIntervalTicks;
                    private static int _vrFrameStatsActivity;

                    // Render-matrix stats use a separate swap cycle aligned with SwapBuffers phase.

                    public static int VrLeftEyeDraws => _lastFrameVrLeftEyeDraws;
                    public static int VrRightEyeDraws => _lastFrameVrRightEyeDraws;
                    public static int VrLeftEyeVisible => _lastFrameVrLeftEyeVisible;
                    public static int VrRightEyeVisible => _lastFrameVrRightEyeVisible;
                    public static double VrLeftWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrLeftWorkerBuildTimeTicks).TotalMilliseconds;
                    public static double VrRightWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrRightWorkerBuildTimeTicks).TotalMilliseconds;
                    public static double VrRenderSubmitTimeMs => TimeSpan.FromTicks(_lastFrameVrRenderSubmitTimeTicks).TotalMilliseconds;
                    public static double VrXrWaitFrameBlockTimeMs => TimeSpan.FromTicks(_lastFrameVrXrWaitFrameBlockTimeTicks).TotalMilliseconds;
                    public static double VrXrEndFrameSubmitTimeMs => TimeSpan.FromTicks(_lastFrameVrXrEndFrameSubmitTimeTicks).TotalMilliseconds;
                    public static double VrXrPredictedToLatePoseDeltaMillimeters => BitConverter.Int64BitsToDouble(_lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits);
                    public static double VrXrPredictedToLatePoseDeltaDegrees => BitConverter.Int64BitsToDouble(_lastFrameVrXrPredictedToLatePoseDeltaDegreesBits);
                    public static double VrXrPredictedDisplayLeadTimeMs => BitConverter.Int64BitsToDouble(_lastFrameVrXrPredictedDisplayLeadTimeMsBits);
                    public static int VrXrMissedDeadlineFrames => _lastFrameVrXrMissedDeadlineFrames;
                    public static int VrXrTrackingLossFrames => _lastFrameVrXrTrackingLossFrames;
                    public static double VrXrRelocatePredictedTimeMs => TimeSpan.FromTicks(_lastFrameVrXrRelocatePredictedTimeTicks).TotalMilliseconds;
                    public static double VrXrCollectFrustumExpansionDegrees => BitConverter.Int64BitsToDouble(_lastFrameVrXrCollectFrustumExpansionDegreesBits);
                    public static double VrXrPacingThreadIdleTimeMs => TimeSpan.FromTicks(_lastFrameVrXrPacingThreadIdleTimeTicks).TotalMilliseconds;
                    public static int VrXrPacingHandoffStalls => _lastFrameVrXrPacingHandoffStalls;
                    public static int VrRenderPassDrawCalls => _lastFrameVrRenderPassDrawCalls;
                    public static int VrRenderPassMultiDrawCalls => _lastFrameVrRenderPassMultiDrawCalls;
                    public static int VrRenderPassTrianglesRendered => _lastFrameVrRenderPassTrianglesRendered;
                    public static RenderPassCounters VrRenderPassCounters
                        => new(
                            _lastFrameVrRenderPassDrawCalls,
                            _lastFrameVrRenderPassMultiDrawCalls,
                            _lastFrameVrRenderPassTrianglesRendered);
                    public static double VrRenderPassTimeMs => TimeSpan.FromTicks(_lastFrameVrRenderPassTimeTicks).TotalMilliseconds;
                    public static double VrRenderFrameIntervalMs
                    {
                        get
                        {
                            long intervalTicks = Volatile.Read(ref _lastFrameVrRenderFrameIntervalTicks);
                            return intervalTicks > 0
                                ? intervalTicks * 1000.0 / Stopwatch.Frequency
                                : 0.0;
                        }
                    }
                    public static double VrRenderFrameRateHz
                    {
                        get
                        {
                            long intervalTicks = Volatile.Read(ref _lastFrameVrRenderFrameIntervalTicks);
                            return intervalTicks > 0
                                ? Stopwatch.Frequency / (double)intervalTicks
                                : 0.0;
                        }
                    }

                    internal static void SnapshotAndReset()
                    {
                        bool hasVrFrameStats = Interlocked.Exchange(ref _vrFrameStatsActivity, 0) != 0;
                        if (hasVrFrameStats)
                            PublishFrameScopedStats();
                        else if (!Engine.VRState.IsInVR && !RuntimeEngine.Rendering.State.IsStereoPass)
                        {
                            DiscardPendingFrameScopedStats();
                            ClearPublishedFrameScopedStats();
                        }

                        _lastFrameVrXrMissedDeadlineFrames = Interlocked.Exchange(ref _vrXrMissedDeadlineFrames, 0);
                        _lastFrameVrXrTrackingLossFrames = Interlocked.Exchange(ref _vrXrTrackingLossFrames, 0);
                        _lastFrameVrXrPacingHandoffStalls = Interlocked.Exchange(ref _vrXrPacingHandoffStalls, 0);
                        _lastFrameVrRenderFrameIntervalTicks = Volatile.Read(ref _vrRenderFrameIntervalTicks);
                    }

                    private static void PublishFrameScopedStats()
                    {
                        _lastFrameVrLeftEyeDraws = Interlocked.Exchange(ref _vrLeftEyeDraws, 0);
                        _lastFrameVrRightEyeDraws = Interlocked.Exchange(ref _vrRightEyeDraws, 0);
                        _lastFrameVrLeftEyeVisible = Interlocked.Exchange(ref _vrLeftEyeVisible, 0);
                        _lastFrameVrRightEyeVisible = Interlocked.Exchange(ref _vrRightEyeVisible, 0);
                        _lastFrameVrLeftWorkerBuildTimeTicks = Interlocked.Exchange(ref _vrLeftWorkerBuildTimeTicks, 0);
                        _lastFrameVrRightWorkerBuildTimeTicks = Interlocked.Exchange(ref _vrRightWorkerBuildTimeTicks, 0);
                        _lastFrameVrRenderSubmitTimeTicks = Interlocked.Exchange(ref _vrRenderSubmitTimeTicks, 0);
                        _lastFrameVrXrWaitFrameBlockTimeTicks = Interlocked.Exchange(ref _vrXrWaitFrameBlockTimeTicks, 0);
                        _lastFrameVrXrEndFrameSubmitTimeTicks = Interlocked.Exchange(ref _vrXrEndFrameSubmitTimeTicks, 0);
                        _lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits = Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaMillimetersBits, 0);
                        _lastFrameVrXrPredictedToLatePoseDeltaDegreesBits = Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaDegreesBits, 0);
                        _lastFrameVrXrPredictedDisplayLeadTimeMsBits = Interlocked.Exchange(
                            ref _vrXrPredictedDisplayLeadTimeMsBits,
                            BitConverter.DoubleToInt64Bits(double.NaN));
                        _lastFrameVrXrRelocatePredictedTimeTicks = Interlocked.Exchange(ref _vrXrRelocatePredictedTimeTicks, 0);
                        _lastFrameVrXrCollectFrustumExpansionDegreesBits = Interlocked.Exchange(ref _vrXrCollectFrustumExpansionDegreesBits, 0);
                        _lastFrameVrXrPacingThreadIdleTimeTicks = Interlocked.Exchange(ref _vrXrPacingThreadIdleTimeTicks, 0);
                        _lastFrameVrRenderPassDrawCalls = Interlocked.Exchange(ref _vrRenderPassDrawCalls, 0);
                        _lastFrameVrRenderPassMultiDrawCalls = Interlocked.Exchange(ref _vrRenderPassMultiDrawCalls, 0);
                        _lastFrameVrRenderPassTrianglesRendered = Interlocked.Exchange(ref _vrRenderPassTrianglesRendered, 0);
                        _lastFrameVrRenderPassTimeTicks = Interlocked.Exchange(ref _vrRenderPassTimeTicks, 0);
                    }

                    private static void DiscardPendingFrameScopedStats()
                    {
                        Interlocked.Exchange(ref _vrLeftEyeDraws, 0);
                        Interlocked.Exchange(ref _vrRightEyeDraws, 0);
                        Interlocked.Exchange(ref _vrLeftEyeVisible, 0);
                        Interlocked.Exchange(ref _vrRightEyeVisible, 0);
                        Interlocked.Exchange(ref _vrLeftWorkerBuildTimeTicks, 0);
                        Interlocked.Exchange(ref _vrRightWorkerBuildTimeTicks, 0);
                        Interlocked.Exchange(ref _vrRenderSubmitTimeTicks, 0);
                        Interlocked.Exchange(ref _vrXrWaitFrameBlockTimeTicks, 0);
                        Interlocked.Exchange(ref _vrXrEndFrameSubmitTimeTicks, 0);
                        Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaMillimetersBits, 0);
                        Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaDegreesBits, 0);
                        Interlocked.Exchange(
                            ref _vrXrPredictedDisplayLeadTimeMsBits,
                            BitConverter.DoubleToInt64Bits(double.NaN));
                        Interlocked.Exchange(ref _vrXrRelocatePredictedTimeTicks, 0);
                        Interlocked.Exchange(ref _vrXrCollectFrustumExpansionDegreesBits, 0);
                        Interlocked.Exchange(ref _vrXrPacingThreadIdleTimeTicks, 0);
                        Interlocked.Exchange(ref _vrRenderPassDrawCalls, 0);
                        Interlocked.Exchange(ref _vrRenderPassMultiDrawCalls, 0);
                        Interlocked.Exchange(ref _vrRenderPassTrianglesRendered, 0);
                        Interlocked.Exchange(ref _vrRenderPassTimeTicks, 0);
                    }

                    private static void ClearPublishedFrameScopedStats()
                    {
                        _lastFrameVrLeftEyeDraws = 0;
                        _lastFrameVrRightEyeDraws = 0;
                        _lastFrameVrLeftEyeVisible = 0;
                        _lastFrameVrRightEyeVisible = 0;
                        _lastFrameVrLeftWorkerBuildTimeTicks = 0;
                        _lastFrameVrRightWorkerBuildTimeTicks = 0;
                        _lastFrameVrRenderSubmitTimeTicks = 0;
                        _lastFrameVrXrWaitFrameBlockTimeTicks = 0;
                        _lastFrameVrXrEndFrameSubmitTimeTicks = 0;
                        _lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits = 0;
                        _lastFrameVrXrPredictedToLatePoseDeltaDegreesBits = 0;
                        _lastFrameVrXrPredictedDisplayLeadTimeMsBits = BitConverter.DoubleToInt64Bits(double.NaN);
                        _lastFrameVrXrRelocatePredictedTimeTicks = 0;
                        _lastFrameVrXrCollectFrustumExpansionDegreesBits = 0;
                        _lastFrameVrXrPacingThreadIdleTimeTicks = 0;
                        _lastFrameVrRenderPassDrawCalls = 0;
                        _lastFrameVrRenderPassMultiDrawCalls = 0;
                        _lastFrameVrRenderPassTrianglesRendered = 0;
                        _lastFrameVrRenderPassTimeTicks = 0;
                    }

                    private static void MarkFrameScopedStatsActive()
                    {
                        Interlocked.Exchange(ref _vrFrameStatsActivity, 1);
                    }

                    public static void RecordVrRenderPass(RenderPassCounters before, RenderPassCounters after, TimeSpan elapsed)
                    {
                        if (!EnableTracking)
                            return;

                        RecordVrRenderPass(RenderPassCounters.Delta(before, after), elapsed);
                    }

                    public static void RecordVrRenderPass(RenderPassCounters counters, TimeSpan elapsed)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        if (counters.DrawCalls > 0)
                            Interlocked.Add(ref _vrRenderPassDrawCalls, counters.DrawCalls);
                        if (counters.MultiDrawCalls > 0)
                            Interlocked.Add(ref _vrRenderPassMultiDrawCalls, counters.MultiDrawCalls);
                        if (counters.TrianglesRendered > 0)
                            Interlocked.Add(ref _vrRenderPassTrianglesRendered, counters.TrianglesRendered);
                        if (elapsed.Ticks > 0)
                            Interlocked.Add(ref _vrRenderPassTimeTicks, elapsed.Ticks);
                    }

                    public static void RecordVrRenderFramePresented()
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        long now = Stopwatch.GetTimestamp();
                        long previous = Interlocked.Exchange(ref _vrLastPresentedTimestampTicks, now);
                        if (previous > 0 && now > previous)
                            Volatile.Write(ref _vrRenderFrameIntervalTicks, now - previous);
                    }

                    public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrLeftEyeDraws, (int)Math.Min(leftDraws, int.MaxValue));
                        Interlocked.Exchange(ref _vrRightEyeDraws, (int)Math.Min(rightDraws, int.MaxValue));
                    }

                    public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrLeftEyeVisible, (int)Math.Min(leftVisible, int.MaxValue));
                        Interlocked.Exchange(ref _vrRightEyeVisible, (int)Math.Min(rightVisible, int.MaxValue));
                    }

                    public static void RecordVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrLeftWorkerBuildTimeTicks, leftBuildTime.Ticks);
                        Interlocked.Exchange(ref _vrRightWorkerBuildTimeTicks, rightBuildTime.Ticks);
                    }

                    public static void RecordVrRenderSubmitTime(TimeSpan submitTime)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrRenderSubmitTimeTicks, submitTime.Ticks);
                        RecordVrRenderFramePresented();
                    }

                    public static void RecordVrXrWaitFrameBlockTime(TimeSpan waitTime)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrXrWaitFrameBlockTimeTicks, waitTime.Ticks);
                    }

                    public static void RecordVrXrEndFrameSubmitTime(TimeSpan submitTime)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrXrEndFrameSubmitTimeTicks, submitTime.Ticks);
                    }

                    public static void RecordVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaMillimetersBits, BitConverter.DoubleToInt64Bits(millimeters));
                        Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaDegreesBits, BitConverter.DoubleToInt64Bits(degrees));
                    }

                    public static void RecordVrXrPredictedDisplayLeadTime(double leadTimeMs)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrXrPredictedDisplayLeadTimeMsBits, BitConverter.DoubleToInt64Bits(leadTimeMs));
                    }

                    public static void RecordVrXrMissedDeadlineFrame()
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _vrXrMissedDeadlineFrames);
                    }

                    public static void RecordVrXrTrackingLossFrame()
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _vrXrTrackingLossFrames);
                    }

                    public static void RecordVrXrRelocatePredictedTime(TimeSpan elapsed)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrXrRelocatePredictedTimeTicks, elapsed.Ticks);
                    }

                    public static void RecordVrXrCollectFrustumExpansionDegrees(double degrees)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Exchange(ref _vrXrCollectFrustumExpansionDegreesBits, BitConverter.DoubleToInt64Bits(degrees));
                    }

                    /// <summary>
                    /// Records the time the OpenXR pacing thread spent idle waiting for the render thread's frame-submit signal.
                    /// Accumulated across pacing-thread wakes in a single frame.
                    /// </summary>
                    public static void RecordVrXrPacingThreadIdleTime(TimeSpan elapsed)
                    {
                        if (!EnableTracking)
                            return;

                        MarkFrameScopedStatsActive();
                        Interlocked.Add(ref _vrXrPacingThreadIdleTimeTicks, elapsed.Ticks);
                    }

                    /// <summary>
                    /// Records that the render thread had to stall because the pacing thread had not yet published the
                    /// next OpenXR frame (predicted views) before the engine wanted to render.
                    /// </summary>
                    public static void RecordVrXrPacingHandoffStall()
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _vrXrPacingHandoffStalls);
                    }
                }
            }
        }
    }
}
