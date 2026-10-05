using System.Collections.Concurrent;
using System.Diagnostics;
using XREngine.Components;

namespace XREngine;

/// <summary>
/// Default-off observation of the world tick path: how often each tick group
/// is dispatched, what applying pending registrations costs, and which kinds
/// of callback own the time. Enable before launch by setting
/// <see cref="XREngineEnvironmentVariables.WorldTickTelemetry"/> to 1.
/// Counters are cumulative and process-wide; a reader compares two snapshots.
/// While disabled, every entry point returns before touching a counter.
/// </summary>
public static class RuntimeWorldTickTelemetry
{
    /// <summary>Most callback kinds returned by one snapshot.</summary>
    public const int MaximumCallbackKindsPerSnapshot = 64;

    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.WorldTickTelemetry) == "1";

    private static readonly int s_groupCount = Enabled ? Enum.GetValues<ETickGroup>().Length : 0;

    private static readonly ConcurrentDictionary<RuntimeWorldTickCallbackKey, RuntimeWorldTickCallbackTelemetry> s_callbacks = new();

    private static readonly long[] s_dispatches = new long[s_groupCount];
    private static readonly long[] s_queueVisits = new long[s_groupCount];
    private static readonly long[] s_callbackInvocations = new long[s_groupCount];
    private static readonly long[] s_dispatchTicks = new long[s_groupCount];
    private static readonly long[] s_snapshotTicks = new long[s_groupCount];
    private static readonly long[] s_pendingTicks = new long[s_groupCount];
    private static readonly long[] s_callbackTicks = new long[s_groupCount];
    private static readonly long[] s_observerTicks = new long[s_groupCount];
    private static readonly long[] s_allocatedBytes = new long[s_groupCount];
    private static readonly long[] s_pendingAdds = new long[s_groupCount];
    private static readonly long[] s_pendingDuplicateAdds = new long[s_groupCount];
    private static readonly long[] s_pendingRemoves = new long[s_groupCount];
    private static readonly long[] s_pendingMissingRemoves = new long[s_groupCount];
    private static readonly long[] s_membershipComparisons = new long[s_groupCount];
    private static readonly long[] s_registeredCallbacksHighWater = new long[s_groupCount];
    private static readonly long[] s_pendingChangesHighWater = new long[s_groupCount];
    private static readonly long[] s_pendingTicksHighWater = new long[s_groupCount];
    private static readonly long[] s_pendingTicksHighWaterTimestamp = new long[s_groupCount];
    private static readonly long[] s_dispatchTicksHighWater = new long[s_groupCount];
    private static readonly long[] s_dispatchTicksHighWaterTimestamp = new long[s_groupCount];

    private static long s_updateCalls;
    private static long s_updateSkippedNotPlaying;
    private static long s_fixedUpdateCalls;
    private static long s_fixedUpdateSkippedNotPlaying;
    private static long s_registerRequests;
    private static long s_unregisterRequests;

    /// <summary>Counts one world update call and whether the play state let it run.</summary>
    internal static void UpdateCalled(bool dispatched)
    {
        if (!Enabled)
            return;
        if (dispatched)
            Interlocked.Increment(ref s_updateCalls);
        else
            Interlocked.Increment(ref s_updateSkippedNotPlaying);
    }

    /// <summary>Counts one world fixed-update call and whether the play state let it run.</summary>
    internal static void FixedUpdateCalled(bool dispatched)
    {
        if (!Enabled)
            return;
        if (dispatched)
            Interlocked.Increment(ref s_fixedUpdateCalls);
        else
            Interlocked.Increment(ref s_fixedUpdateSkippedNotPlaying);
    }

    /// <summary>Counts a registration or removal request on the requesting thread.</summary>
    internal static void RegistrationRequested(bool add)
    {
        if (!Enabled)
            return;
        if (add)
            Interlocked.Increment(ref s_registerRequests);
        else
            Interlocked.Increment(ref s_unregisterRequests);
    }

    /// <summary>
    /// Returns the shared observation of the callback's kind, creating it on
    /// first sight. Reflects over closures; call it only while applying a
    /// registration.
    /// </summary>
    internal static RuntimeWorldTickCallbackTelemetry ResolveCallback(ETickGroup group, int order, WorldTick callback)
        => s_callbacks.GetOrAdd(
            RuntimeWorldTickCallbackKey.From(group, order, callback),
            static created => new RuntimeWorldTickCallbackTelemetry(created));

    /// <summary>Records what one ordered queue did during one dispatch.</summary>
    internal static void QueueDispatched(
        ETickGroup group,
        long pendingStartedTimestamp,
        long pendingTicks,
        long observerTicks,
        long callbackTicks,
        int callbacksInvoked,
        int adds,
        int duplicateAdds,
        int removes,
        int missingRemoves,
        long membershipComparisons,
        int registeredCallbacks)
    {
        int index = (int)group;
        Interlocked.Increment(ref s_queueVisits[index]);
        Interlocked.Add(ref s_callbackInvocations[index], callbacksInvoked);
        Interlocked.Add(ref s_pendingTicks[index], pendingTicks);
        Interlocked.Add(ref s_observerTicks[index], observerTicks);
        Interlocked.Add(ref s_callbackTicks[index], callbackTicks);
        Interlocked.Add(ref s_pendingAdds[index], adds);
        Interlocked.Add(ref s_pendingDuplicateAdds[index], duplicateAdds);
        Interlocked.Add(ref s_pendingRemoves[index], removes);
        Interlocked.Add(ref s_pendingMissingRemoves[index], missingRemoves);
        Interlocked.Add(ref s_membershipComparisons[index], membershipComparisons);
        RuntimeWorldTickCallbackTelemetry.RaiseTo(ref s_registeredCallbacksHighWater[index], registeredCallbacks);
        int changes = adds + duplicateAdds + removes + missingRemoves;
        if (changes == 0)
            return;
        RuntimeWorldTickCallbackTelemetry.RaiseTo(ref s_pendingChangesHighWater[index], changes);
        if (RuntimeWorldTickCallbackTelemetry.RaiseTo(ref s_pendingTicksHighWater[index], pendingTicks))
            Volatile.Write(ref s_pendingTicksHighWaterTimestamp[index], pendingStartedTimestamp);
    }

    /// <summary>Records one whole dispatch of a tick group.</summary>
    internal static void GroupDispatched(ETickGroup group, long startedTimestamp, long snapshotTicks, long dispatchTicks, long allocatedBytes)
    {
        int index = (int)group;
        Interlocked.Increment(ref s_dispatches[index]);
        Interlocked.Add(ref s_snapshotTicks[index], snapshotTicks);
        Interlocked.Add(ref s_dispatchTicks[index], dispatchTicks);
        Interlocked.Add(ref s_allocatedBytes[index], allocatedBytes);
        if (RuntimeWorldTickCallbackTelemetry.RaiseTo(ref s_dispatchTicksHighWater[index], dispatchTicks))
            Volatile.Write(ref s_dispatchTicksHighWaterTimestamp[index], startedTimestamp);
    }

    /// <summary>
    /// Copies the counters. Allocates; call it from a diagnostic reader, never
    /// from the tick path.
    /// </summary>
    public static RuntimeWorldTickTelemetrySnapshot CaptureSnapshot()
    {
        RuntimeWorldTickGroupSnapshot[] groups = new RuntimeWorldTickGroupSnapshot[s_groupCount];
        for (int index = 0; index < s_groupCount; ++index)
        {
            groups[index] = new RuntimeWorldTickGroupSnapshot(
                (ETickGroup)index,
                Interlocked.Read(ref s_dispatches[index]),
                Interlocked.Read(ref s_queueVisits[index]),
                Interlocked.Read(ref s_callbackInvocations[index]),
                Interlocked.Read(ref s_dispatchTicks[index]),
                Interlocked.Read(ref s_snapshotTicks[index]),
                Interlocked.Read(ref s_pendingTicks[index]),
                Interlocked.Read(ref s_callbackTicks[index]),
                Interlocked.Read(ref s_observerTicks[index]),
                Interlocked.Read(ref s_allocatedBytes[index]),
                Interlocked.Read(ref s_pendingAdds[index]),
                Interlocked.Read(ref s_pendingDuplicateAdds[index]),
                Interlocked.Read(ref s_pendingRemoves[index]),
                Interlocked.Read(ref s_pendingMissingRemoves[index]),
                Interlocked.Read(ref s_membershipComparisons[index]),
                Interlocked.Read(ref s_registeredCallbacksHighWater[index]),
                Interlocked.Read(ref s_pendingChangesHighWater[index]),
                Interlocked.Read(ref s_pendingTicksHighWater[index]),
                Volatile.Read(ref s_pendingTicksHighWaterTimestamp[index]),
                Interlocked.Read(ref s_dispatchTicksHighWater[index]),
                Volatile.Read(ref s_dispatchTicksHighWaterTimestamp[index]));
        }

        List<RuntimeWorldTickCallbackTelemetry> kinds = [.. s_callbacks.Values];
        kinds.Sort(static (left, right) => right.Ticks.CompareTo(left.Ticks));
        int returned = Math.Min(kinds.Count, MaximumCallbackKindsPerSnapshot);
        RuntimeWorldTickCallbackSnapshot[] callbacks = new RuntimeWorldTickCallbackSnapshot[returned];
        for (int position = 0; position < returned; ++position)
            callbacks[position] = kinds[position].Capture();

        return new RuntimeWorldTickTelemetrySnapshot(
            Enabled,
            Stopwatch.Frequency,
            Stopwatch.GetTimestamp(),
            Interlocked.Read(ref s_updateCalls),
            Interlocked.Read(ref s_updateSkippedNotPlaying),
            Interlocked.Read(ref s_fixedUpdateCalls),
            Interlocked.Read(ref s_fixedUpdateSkippedNotPlaying),
            Interlocked.Read(ref s_registerRequests),
            Interlocked.Read(ref s_unregisterRequests),
            kinds.Count,
            groups,
            callbacks);
    }
}
