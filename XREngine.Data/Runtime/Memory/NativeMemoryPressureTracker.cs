namespace XREngine.Data.Runtime.Memory;

public readonly record struct NativeMemoryPressureSnapshot(
    long ActiveBytes,
    long PeakBytes,
    long AddCount,
    long RemoveCount,
    long ActiveLeaseCount);

public static class NativeMemoryPressureTracker
{
    private static long _activeBytes;
    private static long _peakBytes;
    private static long _addCount;
    private static long _removeCount;
    private static long _activeLeaseCount;

    public static NativeMemoryPressureSnapshot Snapshot => new(
        Interlocked.Read(ref _activeBytes),
        Interlocked.Read(ref _peakBytes),
        Interlocked.Read(ref _addCount),
        Interlocked.Read(ref _removeCount),
        Interlocked.Read(ref _activeLeaseCount));

    public static NativeMemoryPressureLease Add(long bytes)
    {
        if (bytes <= 0L)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        GC.AddMemoryPressure(bytes);
        long active = Interlocked.Add(ref _activeBytes, bytes);
        Interlocked.Increment(ref _addCount);
        Interlocked.Increment(ref _activeLeaseCount);
        UpdatePeak(active);
        return new NativeMemoryPressureLease(bytes);
    }

    internal static void Remove(long bytes)
    {
        GC.RemoveMemoryPressure(bytes);
        Interlocked.Add(ref _activeBytes, -bytes);
        Interlocked.Increment(ref _removeCount);
        Interlocked.Decrement(ref _activeLeaseCount);
    }

    private static void UpdatePeak(long active)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _peakBytes);
            if (active <= current)
                return;

            if (Interlocked.CompareExchange(ref _peakBytes, active, current) == current)
                return;
        }
    }
}

public struct NativeMemoryPressureLease : IDisposable
{
    private long _bytes;

    internal NativeMemoryPressureLease(long bytes)
        => _bytes = bytes;

    public long Bytes => _bytes;

    public void Dispose()
    {
        long bytes = Interlocked.Exchange(ref _bytes, 0L);
        if (bytes > 0L)
            NativeMemoryPressureTracker.Remove(bytes);
    }
}
