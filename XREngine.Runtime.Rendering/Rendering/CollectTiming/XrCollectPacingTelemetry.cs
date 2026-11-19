using System.Diagnostics;

namespace XREngine.Rendering;

/// <summary>
/// Always-on cumulative counters of XR collect pacing: paced and render-paced collects, the two
/// gate waits with their time-outs and times, deactivations, and the publications with and
/// without an XR frame. Capture twice and use the differences.
/// Writers use interlocked additions on a preallocated table; only <see cref="Capture"/> allocates.
/// </summary>
public static class XrCollectPacingTelemetry
{
    private static readonly long[] s_counts = new long[(int)EXrCollectPacingCounter.Count];

    /// <summary>Adds one to <paramref name="counter"/>.</summary>
    public static void Count(EXrCollectPacingCounter counter)
        => Add(counter, 1L);

    /// <summary>Adds <paramref name="value"/> to <paramref name="counter"/>.</summary>
    public static void Add(EXrCollectPacingCounter counter, long value)
    {
        if ((uint)counter < (uint)s_counts.Length)
            Interlocked.Add(ref s_counts[(int)counter], value);
    }

    /// <summary>Adds the time from <paramref name="startTimestamp"/> to <paramref name="endTimestamp"/> (stopwatch ticks) in microseconds.</summary>
    public static void AddElapsed(EXrCollectPacingCounter counter, long startTimestamp, long endTimestamp)
        => Add(counter, Math.Max(0L, endTimestamp - startTimestamp) * 1_000_000L / Stopwatch.Frequency);

    /// <summary>Copies all counters, indexed by <see cref="EXrCollectPacingCounter"/>. Allocates; not for frame paths.</summary>
    public static long[] Capture()
    {
        long[] copy = new long[s_counts.Length];
        for (int index = 0; index < copy.Length; ++index)
            copy[index] = Interlocked.Read(ref s_counts[index]);
        return copy;
    }
}
