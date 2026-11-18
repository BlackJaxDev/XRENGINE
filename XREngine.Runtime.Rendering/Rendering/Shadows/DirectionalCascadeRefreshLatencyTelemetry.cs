using System.Diagnostics;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Measures how long a directional shadow cascade stays stale after its content changed (a sun
/// move or a camera move changes the cascade fit) until the render thread records its refresh.
/// The collect thread marks a cascade stale when shadow planning finds that its depth content
/// changed but the cascade waits for a refresh slot (<c>MaxDirectionalCascadeRefreshesPerFrame</c>).
/// The render thread clears the mark when it records the cascade draw, and adds the time since
/// the first stale mark as one sample. The values are cumulative per source (desktop, HMD);
/// capture twice and use the differences. The time does not include GPU execution or display.
/// </summary>
public static class DirectionalCascadeRefreshLatencyTelemetry
{
    private const int MaxCascades = 8;
    private const int SourceCount = 2;

    /// <summary>First stale timestamp per source and cascade, or 0 while the cascade is current.</summary>
    private static readonly long[] s_staleSince = new long[SourceCount * MaxCascades];
    private static readonly long[] s_samples = new long[SourceCount];
    private static readonly long[] s_totalMicroseconds = new long[SourceCount];
    private static readonly long[] s_maxMicroseconds = new long[SourceCount];

    /// <summary>Marks a cascade as stale, unless it is already stale. Called by shadow planning.</summary>
    public static void NoteStale(ShadowRequestSource source, int cascadeIndex)
    {
        if ((uint)cascadeIndex >= MaxCascades)
            return;

        Interlocked.CompareExchange(ref s_staleSince[Slot(source, cascadeIndex)], Stopwatch.GetTimestamp(), 0L);
    }

    /// <summary>Clears the stale mark of a cascade whose draw was recorded, and adds the stale time as a sample.</summary>
    public static void NoteRefreshed(ShadowRequestSource source, int cascadeIndex)
    {
        if ((uint)cascadeIndex >= MaxCascades)
            return;

        long staleSince = Interlocked.Exchange(ref s_staleSince[Slot(source, cascadeIndex)], 0L);
        if (staleSince == 0L)
            return;

        int sourceSlot = SourceSlot(source);
        long microseconds = Math.Max(0L, Stopwatch.GetTimestamp() - staleSince) * 1_000_000L / Stopwatch.Frequency;
        Interlocked.Increment(ref s_samples[sourceSlot]);
        Interlocked.Add(ref s_totalMicroseconds[sourceSlot], microseconds);
        long max = Interlocked.Read(ref s_maxMicroseconds[sourceSlot]);
        while (microseconds > max)
        {
            long seen = Interlocked.CompareExchange(ref s_maxMicroseconds[sourceSlot], microseconds, max);
            if (seen == max)
                break;
            max = seen;
        }
    }

    /// <summary>Number of refreshes of stale cascades of <paramref name="source"/>.</summary>
    public static long GetSamples(ShadowRequestSource source)
        => Interlocked.Read(ref s_samples[SourceSlot(source)]);

    /// <summary>Total stale time of those refreshes, in microseconds.</summary>
    public static long GetTotalMicroseconds(ShadowRequestSource source)
        => Interlocked.Read(ref s_totalMicroseconds[SourceSlot(source)]);

    /// <summary>Longest stale time of one refresh since start, in microseconds.</summary>
    public static long GetMaxMicroseconds(ShadowRequestSource source)
        => Interlocked.Read(ref s_maxMicroseconds[SourceSlot(source)]);

    private static int SourceSlot(ShadowRequestSource source)
        => source == ShadowRequestSource.Hmd ? 1 : 0;

    private static int Slot(ShadowRequestSource source, int cascadeIndex)
        => SourceSlot(source) * MaxCascades + cascadeIndex;
}
