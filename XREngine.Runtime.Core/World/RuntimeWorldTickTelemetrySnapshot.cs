namespace XREngine;

/// <summary>
/// Cumulative world tick counters captured with independent atomic reads.
/// Compare two snapshots to observe a window; the fields do not describe one
/// atomic update.
/// </summary>
/// <param name="Enabled">Whether observation was enabled at launch.</param>
/// <param name="StopwatchFrequency">Ticks per second for every tick field.</param>
/// <param name="CapturedTimestamp">Stopwatch timestamp of this capture.</param>
/// <param name="UpdateCalls">World update calls that dispatched the ordinary and late groups.</param>
/// <param name="UpdateSkippedNotPlaying">World update calls that returned because the world was not playing.</param>
/// <param name="FixedUpdateCalls">World fixed-update calls that dispatched the physics groups.</param>
/// <param name="FixedUpdateSkippedNotPlaying">World fixed-update calls that returned because the world was not playing.</param>
/// <param name="RegisterRequests">Tick registrations requested from any thread.</param>
/// <param name="UnregisterRequests">Tick removals requested from any thread.</param>
/// <param name="CallbackKinds">Distinct kinds of callback seen since launch.</param>
/// <param name="Groups">One entry per tick group.</param>
/// <param name="Callbacks">The costliest kinds of callback, largest total time first.</param>
public readonly record struct RuntimeWorldTickTelemetrySnapshot(
    bool Enabled,
    long StopwatchFrequency,
    long CapturedTimestamp,
    long UpdateCalls,
    long UpdateSkippedNotPlaying,
    long FixedUpdateCalls,
    long FixedUpdateSkippedNotPlaying,
    long RegisterRequests,
    long UnregisterRequests,
    int CallbackKinds,
    RuntimeWorldTickGroupSnapshot[] Groups,
    RuntimeWorldTickCallbackSnapshot[] Callbacks);
