using System;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class SkinnedBounds
                {
                    // Skinned-bounds refresh stats use the same swap-cycle model as render-matrix stats.
                    private static int _skinnedBoundsDeferredScheduledCurrent;
                    private static int _skinnedBoundsDeferredCompletedCurrent;
                    private static int _skinnedBoundsDeferredFailedCurrent;
                    private static int _skinnedBoundsDeferredInFlightLive;
                    private static int _skinnedBoundsDeferredMaxInFlightCurrent;
                    private static long _skinnedBoundsDeferredQueueWaitTicksCurrent;
                    private static long _skinnedBoundsDeferredCpuJobTicksCurrent;
                    private static long _skinnedBoundsDeferredApplyTicksCurrent;
                    private static long _skinnedBoundsDeferredMaxQueueWaitTicksCurrent;
                    private static long _skinnedBoundsDeferredMaxCpuJobTicksCurrent;
                    private static long _skinnedBoundsDeferredMaxApplyTicksCurrent;
                    private static int _skinnedBoundsGpuCompletedCurrent;
                    private static long _skinnedBoundsGpuComputeTicksCurrent;
                    private static long _skinnedBoundsGpuApplyTicksCurrent;
                    private static long _skinnedBoundsGpuMaxComputeTicksCurrent;
                    private static long _skinnedBoundsGpuMaxApplyTicksCurrent;
                    private static int _skinnedBoundsDeferredScheduledDisplay;
                    private static int _skinnedBoundsDeferredCompletedDisplay;
                    private static int _skinnedBoundsDeferredFailedDisplay;
                    private static int _skinnedBoundsDeferredInFlightDisplay;
                    private static int _skinnedBoundsDeferredMaxInFlightDisplay;
                    private static long _skinnedBoundsDeferredQueueWaitTicksDisplay;
                    private static long _skinnedBoundsDeferredCpuJobTicksDisplay;
                    private static long _skinnedBoundsDeferredApplyTicksDisplay;
                    private static long _skinnedBoundsDeferredMaxQueueWaitTicksDisplay;
                    private static long _skinnedBoundsDeferredMaxCpuJobTicksDisplay;
                    private static long _skinnedBoundsDeferredMaxApplyTicksDisplay;
                    private static int _skinnedBoundsGpuCompletedDisplay;
                    private static long _skinnedBoundsGpuComputeTicksDisplay;
                    private static long _skinnedBoundsGpuApplyTicksDisplay;
                    private static long _skinnedBoundsGpuMaxComputeTicksDisplay;
                    private static long _skinnedBoundsGpuMaxApplyTicksDisplay;
                    private static bool _skinnedBoundsStatsReady;
                    private static int _skinnedBoundsStatsDirty;


                    /// <summary>
                    /// Enables collection of deferred skinned-bounds refresh statistics.
                    /// </summary>
                    public static bool EnableSkinnedBoundsStats { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                        true;
#endif

                    /// <summary>
                    /// Whether skinned-bounds refresh stats have been populated at least once.
                    /// </summary>
                    public static bool SkinnedBoundsStatsReady => _skinnedBoundsStatsReady;

                    public static int SkinnedBoundsDeferredScheduledCount => _skinnedBoundsDeferredScheduledDisplay;
                    public static int SkinnedBoundsDeferredCompletedCount => _skinnedBoundsDeferredCompletedDisplay;
                    public static int SkinnedBoundsDeferredFailedCount => _skinnedBoundsDeferredFailedDisplay;
                    public static int SkinnedBoundsDeferredInFlightCount => _skinnedBoundsDeferredInFlightDisplay;
                    public static int SkinnedBoundsDeferredMaxInFlightCount => _skinnedBoundsDeferredMaxInFlightDisplay;
                    public static double SkinnedBoundsDeferredQueueWaitMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredQueueWaitTicksDisplay);
                    public static double SkinnedBoundsDeferredCpuJobMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredCpuJobTicksDisplay);
                    public static double SkinnedBoundsDeferredApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredApplyTicksDisplay);
                    public static double SkinnedBoundsDeferredMaxQueueWaitMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredMaxQueueWaitTicksDisplay);
                    public static double SkinnedBoundsDeferredMaxCpuJobMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredMaxCpuJobTicksDisplay);
                    public static double SkinnedBoundsDeferredMaxApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredMaxApplyTicksDisplay);
                    public static int SkinnedBoundsGpuCompletedCount => _skinnedBoundsGpuCompletedDisplay;
                    public static double SkinnedBoundsGpuComputeMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuComputeTicksDisplay);
                    public static double SkinnedBoundsGpuApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuApplyTicksDisplay);
                    public static double SkinnedBoundsGpuMaxComputeMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuMaxComputeTicksDisplay);
                    public static double SkinnedBoundsGpuMaxApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuMaxApplyTicksDisplay);

                    /// <summary>
                    /// Swaps skinned-bounds refresh stats from current to display buffer. Call from SwapBuffers phase.
                    /// </summary>
                    public static void SwapSkinnedBoundsStats()
                    {
                        if (!EnableSkinnedBoundsStats)
                            return;

                        if (Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 0) == 0)
                            return;

                        _skinnedBoundsDeferredScheduledDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredScheduledCurrent, 0);
                        _skinnedBoundsDeferredCompletedDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredCompletedCurrent, 0);
                        _skinnedBoundsDeferredFailedDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredFailedCurrent, 0);
                        _skinnedBoundsDeferredInFlightDisplay = Math.Max(0, Volatile.Read(ref _skinnedBoundsDeferredInFlightLive));
                        _skinnedBoundsDeferredMaxInFlightDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxInFlightCurrent, 0);
                        _skinnedBoundsDeferredQueueWaitTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredQueueWaitTicksCurrent, 0);
                        _skinnedBoundsDeferredCpuJobTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredCpuJobTicksCurrent, 0);
                        _skinnedBoundsDeferredApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredApplyTicksCurrent, 0);
                        _skinnedBoundsDeferredMaxQueueWaitTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxQueueWaitTicksCurrent, 0);
                        _skinnedBoundsDeferredMaxCpuJobTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxCpuJobTicksCurrent, 0);
                        _skinnedBoundsDeferredMaxApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxApplyTicksCurrent, 0);
                        _skinnedBoundsGpuCompletedDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuCompletedCurrent, 0);
                        _skinnedBoundsGpuComputeTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuComputeTicksCurrent, 0);
                        _skinnedBoundsGpuApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuApplyTicksCurrent, 0);
                        _skinnedBoundsGpuMaxComputeTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuMaxComputeTicksCurrent, 0);
                        _skinnedBoundsGpuMaxApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuMaxApplyTicksCurrent, 0);
                        _skinnedBoundsStatsReady = true;
                    }

                    public static void RecordSkinnedBoundsRefreshDeferredScheduled()
                    {
                        if (!EnableSkinnedBoundsStats)
                            return;

                        int inFlight = Interlocked.Increment(ref _skinnedBoundsDeferredInFlightLive);
                        Interlocked.Increment(ref _skinnedBoundsDeferredScheduledCurrent);
                        UpdateMaxCounter(ref _skinnedBoundsDeferredMaxInFlightCurrent, inFlight);
                        Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 1);
                    }

                    public static void RecordSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
                    {
                        if (!EnableSkinnedBoundsStats)
                            return;

                        if (succeeded)
                            Interlocked.Increment(ref _skinnedBoundsDeferredCompletedCurrent);
                        else
                            Interlocked.Increment(ref _skinnedBoundsDeferredFailedCurrent);

                        queueWaitTicks = Math.Max(0L, queueWaitTicks);
                        cpuJobTicks = Math.Max(0L, cpuJobTicks);
                        applyTicks = Math.Max(0L, applyTicks);

                        Interlocked.Add(ref _skinnedBoundsDeferredQueueWaitTicksCurrent, queueWaitTicks);
                        Interlocked.Add(ref _skinnedBoundsDeferredCpuJobTicksCurrent, cpuJobTicks);
                        Interlocked.Add(ref _skinnedBoundsDeferredApplyTicksCurrent, applyTicks);
                        UpdateMaxCounter(ref _skinnedBoundsDeferredMaxQueueWaitTicksCurrent, queueWaitTicks);
                        UpdateMaxCounter(ref _skinnedBoundsDeferredMaxCpuJobTicksCurrent, cpuJobTicks);
                        UpdateMaxCounter(ref _skinnedBoundsDeferredMaxApplyTicksCurrent, applyTicks);

                        int inFlight = Interlocked.Decrement(ref _skinnedBoundsDeferredInFlightLive);
                        if (inFlight < 0)
                        {
                            Interlocked.Exchange(ref _skinnedBoundsDeferredInFlightLive, 0);
                        }

                        Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 1);
                    }

                    public static void RecordSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks)
                    {
                        if (!EnableSkinnedBoundsStats)
                            return;

                        computeTicks = Math.Max(0L, computeTicks);
                        applyTicks = Math.Max(0L, applyTicks);

                        Interlocked.Increment(ref _skinnedBoundsGpuCompletedCurrent);
                        Interlocked.Add(ref _skinnedBoundsGpuComputeTicksCurrent, computeTicks);
                        Interlocked.Add(ref _skinnedBoundsGpuApplyTicksCurrent, applyTicks);
                        UpdateMaxCounter(ref _skinnedBoundsGpuMaxComputeTicksCurrent, computeTicks);
                        UpdateMaxCounter(ref _skinnedBoundsGpuMaxApplyTicksCurrent, applyTicks);
                        Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 1);
                    }
                }
            }
        }
    }
}
