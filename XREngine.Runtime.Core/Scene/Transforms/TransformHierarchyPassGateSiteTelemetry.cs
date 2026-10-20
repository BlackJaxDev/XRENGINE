namespace XREngine.Scene.Transforms;

/// <summary>
/// Contains the cumulative pass-gate counters of one entry site, summed over all transform hierarchy stores.
/// Tick values use <see cref="System.Diagnostics.Stopwatch"/> ticks.
/// </summary>
/// <param name="Entries">Number of gate entries, which includes reentrant entries.</param>
/// <param name="ContendedEntries">Number of entries that did not get the gate on the first try and had to wait.</param>
/// <param name="WaitTicks">Total ticks that contended entries waited for the gate.</param>
/// <param name="MaxWaitTicks">Longest single wait for the process lifetime.</param>
/// <param name="MaxWaitTimestamp">Timestamp at the end of the longest wait. Compare it with a snapshot capture timestamp to find a maximum inside a window.</param>
/// <param name="HoldTicks">Total ticks from the start of the work to its end while the gate is held. Only the propagation pass and the publications record this value.</param>
/// <param name="HoldNotifyTicks">Part of <paramref name="HoldTicks"/> that the matrix notification callbacks use while the gate is held.</param>
/// <param name="PublishedRows">Total render rows that the publications copied. Other sites report 0.</param>
/// <param name="NotifiedOwners">Total transforms that got matrix notifications while the gate is held.</param>
/// <param name="ReentrantSkips">Number of entries that returned with no work because a pass or a publication was active on the same thread.</param>
/// <param name="WaitsUnder50Us">Number of waits shorter than 50 microseconds.</param>
/// <param name="Waits50To250Us">Number of waits from 50 microseconds to less than 250 microseconds.</param>
/// <param name="Waits250UsTo1Ms">Number of waits from 250 microseconds to less than 1 millisecond.</param>
/// <param name="Waits1To4Ms">Number of waits from 1 millisecond to less than 4 milliseconds.</param>
/// <param name="WaitsAtLeast4Ms">Number of waits of 4 milliseconds or more.</param>
/// <param name="BlockedByProcess">Number of waits that started while the propagation pass held the gate.</param>
/// <param name="BlockedByPublication">Number of waits that started while a publication held the gate.</param>
/// <param name="BlockedByOther">Number of waits that started while a different measured site held the gate.</param>
/// <param name="BlockedByUnknown">Number of waits with no known holder. An unmeasured site held the gate, or the holder released it before the read.</param>
public readonly record struct TransformHierarchyPassGateSiteTelemetry(
    long Entries,
    long ContendedEntries,
    long WaitTicks,
    long MaxWaitTicks,
    long MaxWaitTimestamp,
    long HoldTicks,
    long HoldNotifyTicks,
    long PublishedRows,
    long NotifiedOwners,
    long ReentrantSkips,
    long WaitsUnder50Us,
    long Waits50To250Us,
    long Waits250UsTo1Ms,
    long Waits1To4Ms,
    long WaitsAtLeast4Ms,
    long BlockedByProcess,
    long BlockedByPublication,
    long BlockedByOther,
    long BlockedByUnknown);
