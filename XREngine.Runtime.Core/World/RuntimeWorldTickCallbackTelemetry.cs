using System.Diagnostics;

namespace XREngine;

/// <summary>
/// Cumulative observation of one kind of tick callback. Shared by every queue
/// entry registered with the same <see cref="RuntimeWorldTickCallbackKey"/>.
/// </summary>
internal sealed class RuntimeWorldTickCallbackTelemetry(RuntimeWorldTickCallbackKey key)
{
    private static readonly long s_quarterMillisecondTicks = Stopwatch.Frequency / 4000L;
    private static readonly long s_oneMillisecondTicks = Stopwatch.Frequency / 1000L;
    private static readonly long s_fourMillisecondTicks = Stopwatch.Frequency / 250L;
    private static readonly long s_sixteenMillisecondTicks = Stopwatch.Frequency * 16L / 1000L;

    private long _registeredInstances;
    private long _registeredInstancesHighWater;
    private long _invocations;
    private long _ticks;
    private long _maximumTicks;
    private long _maximumTimestamp;
    private long _allocatedBytes;
    private long _atLeastQuarterMillisecond;
    private long _atLeastOneMillisecond;
    private long _atLeastFourMilliseconds;
    private long _atLeastSixteenMilliseconds;
    private long _atLeastOneMillisecondWithCollection;

    public RuntimeWorldTickCallbackKey Key { get; } = key;

    public long Ticks => Interlocked.Read(ref _ticks);

    public void InstanceRegistered()
    {
        long registered = Interlocked.Increment(ref _registeredInstances);
        RaiseTo(ref _registeredInstancesHighWater, registered);
    }

    public void InstanceRemoved()
        => Interlocked.Decrement(ref _registeredInstances);

    /// <summary>Records one invocation.</summary>
    /// <param name="startedTimestamp">Stopwatch timestamp when the callback started.</param>
    /// <param name="elapsedTicks">Stopwatch ticks the callback took.</param>
    /// <param name="allocatedBytes">Managed bytes the dispatching thread allocated meanwhile.</param>
    /// <param name="collectedDuring">Whether a garbage collection completed meanwhile.</param>
    public void RecordInvocation(long startedTimestamp, long elapsedTicks, long allocatedBytes, bool collectedDuring)
    {
        Interlocked.Increment(ref _invocations);
        Interlocked.Add(ref _ticks, elapsedTicks);
        Interlocked.Add(ref _allocatedBytes, allocatedBytes);
        if (RaiseTo(ref _maximumTicks, elapsedTicks))
            Volatile.Write(ref _maximumTimestamp, startedTimestamp);

        if (elapsedTicks < s_quarterMillisecondTicks)
            return;
        Interlocked.Increment(ref _atLeastQuarterMillisecond);
        if (elapsedTicks < s_oneMillisecondTicks)
            return;
        Interlocked.Increment(ref _atLeastOneMillisecond);
        if (collectedDuring)
            Interlocked.Increment(ref _atLeastOneMillisecondWithCollection);
        if (elapsedTicks < s_fourMillisecondTicks)
            return;
        Interlocked.Increment(ref _atLeastFourMilliseconds);
        if (elapsedTicks >= s_sixteenMillisecondTicks)
            Interlocked.Increment(ref _atLeastSixteenMilliseconds);
    }

    public RuntimeWorldTickCallbackSnapshot Capture()
        => new(
            Key.Method.DeclaringType?.FullName ?? string.Empty,
            Key.Method.Name,
            Key.TargetType?.FullName ?? string.Empty,
            Key.InnerMethod?.DeclaringType?.FullName ?? string.Empty,
            Key.InnerMethod?.Name ?? string.Empty,
            Key.Group,
            Key.Order,
            Interlocked.Read(ref _registeredInstances),
            Interlocked.Read(ref _registeredInstancesHighWater),
            Interlocked.Read(ref _invocations),
            Interlocked.Read(ref _ticks),
            Interlocked.Read(ref _maximumTicks),
            Volatile.Read(ref _maximumTimestamp),
            Interlocked.Read(ref _allocatedBytes),
            Interlocked.Read(ref _atLeastQuarterMillisecond),
            Interlocked.Read(ref _atLeastOneMillisecond),
            Interlocked.Read(ref _atLeastFourMilliseconds),
            Interlocked.Read(ref _atLeastSixteenMilliseconds),
            Interlocked.Read(ref _atLeastOneMillisecondWithCollection));

    /// <summary>
    /// Raises a high-water mark without taking a lock and reports whether this
    /// call raised it.
    /// </summary>
    internal static bool RaiseTo(ref long highWater, long value)
    {
        long observed = Volatile.Read(ref highWater);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(ref highWater, value, observed);
            if (previous == observed)
                return true;
            observed = previous;
        }

        return false;
    }
}
