using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace XREngine.Rendering;

/// <summary>
/// Records frame pacing points (collect, render wait, and OpenXR frame calls)
/// with their timestamp and thread into a fixed ring. It is off unless
/// <c>XRE_FRAME_PACING_TIMELINE=1</c>. A mark allocates nothing; <see cref="Dump"/>
/// writes the ring as CSV for offline analysis.
/// </summary>
public static class FramePacingTimeline
{
    private const int Capacity = 1 << 16;

    /// <summary>Gets whether the timeline records marks.</summary>
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.FramePacingTimeline) == "1";

    private static readonly long[] s_timestamps = Enabled ? new long[Capacity] : [];
    private static readonly int[] s_threads = Enabled ? new int[Capacity] : [];
    private static readonly EFramePacingMark[] s_marks = Enabled ? new EFramePacingMark[Capacity] : [];
    private static long s_next = -1;

    /// <summary>Records <paramref name="mark"/> at the current time on the current thread.</summary>
    public static void Mark(EFramePacingMark mark)
    {
        if (!Enabled)
            return;

        int slot = (int)(Interlocked.Increment(ref s_next) & (Capacity - 1));
        s_timestamps[slot] = Stopwatch.GetTimestamp();
        s_threads[slot] = Environment.CurrentManagedThreadId;
        s_marks[slot] = mark;
    }

    /// <summary>
    /// Writes the recorded marks, oldest first, to <paramref name="path"/> as
    /// CSV (milliseconds since the first mark, thread, mark). Returns the row count.
    /// </summary>
    public static int Dump(string path)
    {
        if (!Enabled)
            return 0;

        long last = Interlocked.Read(ref s_next);
        if (last < 0)
            return 0;

        long first = Math.Max(0, last - Capacity + 1);
        long origin = s_timestamps[(int)(first & (Capacity - 1))];
        StringBuilder builder = new("ms,thread,mark\n");
        for (long index = first; index <= last; ++index)
        {
            int slot = (int)(index & (Capacity - 1));
            double ms = (s_timestamps[slot] - origin) * 1000.0 / Stopwatch.Frequency;
            builder.Append(ms.ToString("F4", CultureInfo.InvariantCulture))
                .Append(',').Append(s_threads[slot])
                .Append(',').Append(s_marks[slot]).Append('\n');
        }
        File.WriteAllText(path, builder.ToString());
        return checked((int)(last - first + 1));
    }
}
