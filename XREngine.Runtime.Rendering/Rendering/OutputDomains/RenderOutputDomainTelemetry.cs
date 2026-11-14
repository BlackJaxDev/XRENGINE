using System.Threading;

namespace XREngine.Rendering.OutputDomains;

/// <summary>
/// Always-on cumulative counters of the work that each output domain does to rebuild state:
/// plan builds and their sort cost, mesh cohort reuse, binding artifact replacement and
/// descriptor owner misses. The domain of a count is <see cref="RenderOutputDomainContext.Current"/>
/// on the counting thread. Capture twice around a timed window and use the differences.
/// </summary>
/// <remarks>
/// The writers use interlocked additions on a preallocated table, so any thread can count and a
/// count allocates nothing. Only <see cref="Capture"/> allocates.
/// </remarks>
public static class RenderOutputDomainTelemetry
{
    private const int DomainCount = (int)ERenderOutputDomain.Count;
    private const int CounterCount = (int)ERenderOutputDomainCounter.Count;

    private static readonly long[] s_counts = new long[DomainCount * CounterCount];

    /// <summary>Adds one to <paramref name="counter"/> of the current thread's domain.</summary>
    public static void Count(ERenderOutputDomainCounter counter)
        => Interlocked.Increment(ref s_counts[ToIndex(RenderOutputDomainContext.Current, counter)]);

    /// <summary>Adds <paramref name="amount"/> to <paramref name="counter"/> of the current thread's domain.</summary>
    public static void Add(ERenderOutputDomainCounter counter, long amount)
    {
        if (amount != 0L)
            Interlocked.Add(ref s_counts[ToIndex(RenderOutputDomainContext.Current, counter)], amount);
    }

    /// <summary>
    /// Copies all counters. The result holds <see cref="ERenderOutputDomain.Count"/> rows of
    /// <see cref="ERenderOutputDomainCounter.Count"/> values; read it with <see cref="Read"/>.
    /// This method allocates the copy. Do not call it on a frame path.
    /// </summary>
    public static long[] Capture()
    {
        long[] copy = new long[s_counts.Length];
        for (int index = 0; index < copy.Length; ++index)
            copy[index] = Interlocked.Read(ref s_counts[index]);
        return copy;
    }

    /// <summary>Reads one value of a table from <see cref="Capture"/>.</summary>
    public static long Read(long[] captured, ERenderOutputDomain domain, ERenderOutputDomainCounter counter)
        => captured[ToIndex(domain, counter)];

    // An out-of-range domain or counter counts as None or Frames, so a count never throws.
    private static int ToIndex(ERenderOutputDomain domain, ERenderOutputDomainCounter counter)
    {
        int d = (uint)domain < DomainCount ? (int)domain : 0;
        int c = (uint)counter < CounterCount ? (int)counter : 0;
        return d * CounterCount + c;
    }
}
