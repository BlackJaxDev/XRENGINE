using System.Diagnostics;

namespace XREngine.Timers;

public static class RuntimeTiming
{
    private static readonly double SecondsPerStopwatchTick = 1.0 / Stopwatch.Frequency;

    public static long SecondsToStopwatchTicks(double seconds)
        => seconds <= 0.0
            ? 0L
            : Math.Max(1L, (long)Math.Round(seconds * Stopwatch.Frequency));

    public static double TicksToSecondsDouble(long ticks)
        => ticks * SecondsPerStopwatchTick;

    public static float TicksToSeconds(long ticks)
        => (float)TicksToSecondsDouble(ticks);
}