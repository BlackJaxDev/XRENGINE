using System;
using System.Threading;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static partial class FrameLifecycle
                {
                    // Cumulative process-lifetime totals. Unlike the per-frame values that
                    // SnapshotAndReset publishes, these are never reset and ignore EnableTracking,
                    // so a measurement window can take before/after deltas with every observer
                    // disabled. Each phase has one writer thread; Interlocked keeps 64-bit reads
                    // from other threads untorn.
                    private static long _updatePhaseTicks;
                    private static long _updatePhaseCalls;
                    private static long _collectPhaseTicks;
                    private static long _collectPhaseCalls;
                    private static long _swapPhaseTicks;
                    private static long _swapPhaseCalls;
                    private static long _renderPhaseTicks;
                    private static long _renderPhaseCalls;
                    private static long _collectWaitForRenderTotalTicks;
                    private static long _collectWaitForRenderTotalCalls;
                    private static long _renderWaitForCollectTotalTicks;
                    private static long _renderWaitForCollectTotalCalls;

                    /// <summary>Records one update-thread iteration.</summary>
                    internal static void RecordUpdatePhase(long stopwatchTicks)
                        => AddPhase(ref _updatePhaseTicks, ref _updatePhaseCalls, stopwatchTicks);

                    /// <summary>Records one collect-visible dispatch.</summary>
                    internal static void RecordCollectPhase(long stopwatchTicks)
                        => AddPhase(ref _collectPhaseTicks, ref _collectPhaseCalls, stopwatchTicks);

                    /// <summary>Records one serial swap section on the collect thread.</summary>
                    internal static void RecordSwapPhase(long stopwatchTicks)
                        => AddPhase(ref _swapPhaseTicks, ref _swapPhaseCalls, stopwatchTicks);

                    /// <summary>Records one render-thread frame callback.</summary>
                    internal static void RecordRenderPhase(long stopwatchTicks)
                        => AddPhase(ref _renderPhaseTicks, ref _renderPhaseCalls, stopwatchTicks);

                    /// <summary>
                    /// Reads all cumulative phase totals. Ticks and calls are read separately, so a
                    /// concurrent phase can differ by one call; window deltas tolerate this.
                    /// </summary>
                    public static FrameLifecyclePhaseTotals CapturePhaseTotals()
                        => new(
                            ReadPhase(ref _updatePhaseTicks, ref _updatePhaseCalls),
                            ReadPhase(ref _collectPhaseTicks, ref _collectPhaseCalls),
                            ReadPhase(ref _swapPhaseTicks, ref _swapPhaseCalls),
                            ReadPhase(ref _renderPhaseTicks, ref _renderPhaseCalls),
                            ReadPhase(ref _collectWaitForRenderTotalTicks, ref _collectWaitForRenderTotalCalls),
                            ReadPhase(ref _renderWaitForCollectTotalTicks, ref _renderWaitForCollectTotalCalls));

                    private static void AddPhase(ref long ticks, ref long calls, long stopwatchTicks)
                    {
                        Interlocked.Add(ref ticks, Math.Max(0L, stopwatchTicks));
                        Interlocked.Increment(ref calls);
                    }

                    private static FrameLifecyclePhaseTotal ReadPhase(ref long ticks, ref long calls)
                        => new(Interlocked.Read(ref ticks), Interlocked.Read(ref calls));
                }
            }
        }
    }
}
