using System.Threading;

namespace XREngine.Rendering.UI.State;

/// <summary>
/// Collects optional process-wide UI work counts without per-frame storage allocation.
/// Enable before measurement, then subtract two samples at controlled frame boundaries.
/// </summary>
public static class UIRuntimeCounters
{
    private static readonly long[] Counts = new long[(int)UIWorkCounter.Count];
    private static bool _enabled;

    /// <summary>Enables recording. Existing counts are retained when recording is disabled.</summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set => Volatile.Write(ref _enabled, value);
    }

    /// <summary>Adds work performed by update, collection, or render owners.</summary>
    public static void Add(UIWorkCounter counter, long amount = 1)
    {
        if ((uint)counter >= (uint)UIWorkCounter.Count)
            throw new ArgumentOutOfRangeException(nameof(counter));
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (Enabled)
            Interlocked.Add(ref Counts[(int)counter], amount);
    }

    /// <summary>Reads a cumulative counter.</summary>
    public static long Read(UIWorkCounter counter)
    {
        if ((uint)counter >= (uint)UIWorkCounter.Count)
            throw new ArgumentOutOfRangeException(nameof(counter));
        return Interlocked.Read(ref Counts[(int)counter]);
    }

    /// <summary>Copies counters to caller-owned storage. Concurrent writers can advance between fields.</summary>
    public static void CopyTo(Span<long> destination)
    {
        if (destination.Length < Counts.Length)
            throw new ArgumentException("The counter destination is too short.", nameof(destination));
        for (int i = 0; i < Counts.Length; i++)
            destination[i] = Interlocked.Read(ref Counts[i]);
    }
}
