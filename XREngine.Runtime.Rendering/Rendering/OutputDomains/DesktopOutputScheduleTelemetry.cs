using System.Threading;

namespace XREngine.Rendering.OutputDomains;

/// <summary>
/// Always-on cumulative counters of the desktop shadow source in a VR session with an independent
/// desktop window: the collects that render or retain the source, the directional cascade
/// requests and refresh grants of each source, and the mismatches between the collect decision
/// and the desktop render. Capture twice and use the differences.
/// Writers use interlocked additions on a preallocated table; only <see cref="Capture"/> allocates.
/// </summary>
public static class DesktopOutputScheduleTelemetry
{
    private static readonly long[] s_counts = new long[(int)EDesktopOutputScheduleCounter.Count];

    /// <summary>Adds one to <paramref name="counter"/>.</summary>
    public static void Count(EDesktopOutputScheduleCounter counter)
    {
        if ((uint)counter < (uint)s_counts.Length)
            Interlocked.Increment(ref s_counts[(int)counter]);
    }

    /// <summary>Copies all counters, indexed by <see cref="EDesktopOutputScheduleCounter"/>. Allocates; not for frame paths.</summary>
    public static long[] Capture()
    {
        long[] copy = new long[s_counts.Length];
        for (int index = 0; index < copy.Length; ++index)
            copy[index] = Interlocked.Read(ref s_counts[index]);
        return copy;
    }
}
