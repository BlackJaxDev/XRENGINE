using System.Diagnostics;

namespace XREngine.Scene.Transforms;

/// <summary>
/// Keeps process-wide pass-gate counters for each <see cref="ETransformHierarchyPassGateSite"/>.
/// The counters are the sum over all transform hierarchy stores. Writers use interlocked operations,
/// because two worlds can enter the same site on different threads at the same time.
/// The methods do not allocate.
/// </summary>
internal static class TransformHierarchyPassGateTelemetry
{
    // Each site block is 256 bytes. The used counters fill the first 152 bytes, so the counters of
    // two sites are always more than one cache line apart, also when the array is not cache-line aligned.
    private const int Stride = 32;

    private const int Entries = 0;
    private const int ContendedEntries = 1;
    private const int WaitTicks = 2;
    private const int MaxWaitTicks = 3;
    private const int MaxWaitTimestamp = 4;
    private const int HoldTicks = 5;
    private const int HoldNotifyTicks = 6;
    private const int PublishedRows = 7;
    private const int NotifiedOwners = 8;
    private const int ReentrantSkips = 9;
    private const int WaitsUnder50Us = 10;
    private const int Waits50To250Us = 11;
    private const int Waits250UsTo1Ms = 12;
    private const int Waits1To4Ms = 13;
    private const int WaitsAtLeast4Ms = 14;
    private const int BlockedByProcess = 15;
    private const int BlockedByPublication = 16;
    private const int BlockedByOther = 17;
    private const int BlockedByUnknown = 18;

    private static readonly long[] s_counters = new long[(int)ETransformHierarchyPassGateSite.Count * Stride];
    private static readonly long s_fiftyMicrosecondTicks = Stopwatch.Frequency / 20_000L;
    private static readonly long s_quarterMillisecondTicks = Stopwatch.Frequency / 4_000L;
    private static readonly long s_oneMillisecondTicks = Stopwatch.Frequency / 1_000L;
    private static readonly long s_fourMillisecondTicks = Stopwatch.Frequency / 250L;

    /// <summary>Returns true when the site is one of the render-matrix publication sites.</summary>
    internal static bool IsPublicationSite(ETransformHierarchyPassGateSite site)
        => site is ETransformHierarchyPassGateSite.PublishPreCollect
            or ETransformHierarchyPassGateSite.PublishSwap
            or ETransformHierarchyPassGateSite.PublishOther;

    /// <summary>Counts one gate entry, which includes a reentrant entry.</summary>
    internal static void CountEntry(ETransformHierarchyPassGateSite site)
        => Interlocked.Increment(ref s_counters[(int)site * Stride + Entries]);

    /// <summary>Counts one entry that returned with no work because a pass or a publication was active on the same thread.</summary>
    internal static void CountReentrantSkip(ETransformHierarchyPassGateSite site)
        => Interlocked.Increment(ref s_counters[(int)site * Stride + ReentrantSkips]);

    /// <summary>
    /// Records one wait for the gate.
    /// </summary>
    /// <param name="site">The site that waited.</param>
    /// <param name="holder">The holder value read before the wait: the holder site plus 1, or 0 when the holder is not known.</param>
    /// <param name="waitEnded">The timestamp at which the wait ended.</param>
    /// <param name="waitTicks">The duration of the wait.</param>
    internal static void RecordWait(ETransformHierarchyPassGateSite site, int holder, long waitEnded, long waitTicks)
    {
        int block = (int)site * Stride;
        Interlocked.Increment(ref s_counters[block + ContendedEntries]);
        Interlocked.Add(ref s_counters[block + WaitTicks], waitTicks);
        if (RuntimeWorldTickCallbackTelemetry.RaiseTo(ref s_counters[block + MaxWaitTicks], waitTicks))
            Volatile.Write(ref s_counters[block + MaxWaitTimestamp], waitEnded);

        Interlocked.Increment(ref s_counters[block + GetWaitBucket(waitTicks)]);
        Interlocked.Increment(ref s_counters[block + GetHolderClass(holder)]);
    }

    /// <summary>
    /// Records the gate occupancy of one completed propagation pass or publication.
    /// </summary>
    /// <param name="site">The site that held the gate.</param>
    /// <param name="holdTicks">Ticks from the start of the work to its end.</param>
    /// <param name="notifyTicks">Part of <paramref name="holdTicks"/> that the matrix notification callbacks used.</param>
    /// <param name="publishedRows">Number of render rows that the publication copied, or 0.</param>
    /// <param name="notifiedOwners">Number of transforms that got matrix notifications.</param>
    internal static void RecordHold(
        ETransformHierarchyPassGateSite site, long holdTicks, long notifyTicks, int publishedRows, int notifiedOwners)
    {
        int block = (int)site * Stride;
        Interlocked.Add(ref s_counters[block + HoldTicks], holdTicks);
        if (notifyTicks != 0)
            Interlocked.Add(ref s_counters[block + HoldNotifyTicks], notifyTicks);
        if (publishedRows != 0)
            Interlocked.Add(ref s_counters[block + PublishedRows], publishedRows);
        if (notifiedOwners != 0)
            Interlocked.Add(ref s_counters[block + NotifiedOwners], notifiedOwners);
    }

    /// <summary>Reads the cumulative counters of all measured sites.</summary>
    public static TransformHierarchyPassGateTelemetrySnapshot Capture()
    {
        long captured = Stopwatch.GetTimestamp();
        return new(
            Stopwatch.Frequency,
            captured,
            CaptureSite(ETransformHierarchyPassGateSite.PublishPreCollect),
            CaptureSite(ETransformHierarchyPassGateSite.PublishSwap),
            CaptureSite(ETransformHierarchyPassGateSite.PublishOther),
            CaptureSite(ETransformHierarchyPassGateSite.ProcessPass),
            CaptureSite(ETransformHierarchyPassGateSite.FlushHierarchy));
    }

    private static TransformHierarchyPassGateSiteTelemetry CaptureSite(ETransformHierarchyPassGateSite site)
    {
        int block = (int)site * Stride;
        return new(
            Read(block + Entries),
            Read(block + ContendedEntries),
            Read(block + WaitTicks),
            Read(block + MaxWaitTicks),
            Read(block + MaxWaitTimestamp),
            Read(block + HoldTicks),
            Read(block + HoldNotifyTicks),
            Read(block + PublishedRows),
            Read(block + NotifiedOwners),
            Read(block + ReentrantSkips),
            Read(block + WaitsUnder50Us),
            Read(block + Waits50To250Us),
            Read(block + Waits250UsTo1Ms),
            Read(block + Waits1To4Ms),
            Read(block + WaitsAtLeast4Ms),
            Read(block + BlockedByProcess),
            Read(block + BlockedByPublication),
            Read(block + BlockedByOther),
            Read(block + BlockedByUnknown));
    }

    private static long Read(int index) => Interlocked.Read(ref s_counters[index]);

    private static int GetWaitBucket(long waitTicks)
    {
        if (waitTicks < s_fiftyMicrosecondTicks)
            return WaitsUnder50Us;
        if (waitTicks < s_quarterMillisecondTicks)
            return Waits50To250Us;
        if (waitTicks < s_oneMillisecondTicks)
            return Waits250UsTo1Ms;
        return waitTicks < s_fourMillisecondTicks ? Waits1To4Ms : WaitsAtLeast4Ms;
    }

    private static int GetHolderClass(int holder)
    {
        // The holder value is the holder site plus 1. The value 0 means that no measured site held the gate.
        if (holder == 0)
            return BlockedByUnknown;

        var holderSite = (ETransformHierarchyPassGateSite)(holder - 1);
        if (holderSite == ETransformHierarchyPassGateSite.ProcessPass)
            return BlockedByProcess;
        return IsPublicationSite(holderSite) ? BlockedByPublication : BlockedByOther;
    }
}
