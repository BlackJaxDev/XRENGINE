namespace XREngine.Scene.Transforms;

/// <summary>
/// Contains the cumulative pass-gate counters of each measured entry site, summed over all transform hierarchy stores.
/// The counters are always active. Use the difference between two snapshots to get the values for a window.
/// </summary>
/// <param name="StopwatchFrequency">Number of <see cref="System.Diagnostics.Stopwatch"/> ticks in one second.</param>
/// <param name="CapturedTimestamp">Timestamp at which this snapshot started.</param>
/// <param name="PublishPreCollect">Counters for the publication that runs before visibility collection.</param>
/// <param name="PublishSwap">Counters for the publication that runs in the serial buffer swap.</param>
/// <param name="PublishOther">Counters for publications from the public parameterless entry.</param>
/// <param name="ProcessPass">Counters for the update-thread propagation pass.</param>
/// <param name="FlushHierarchy">Counters for the subtree-range rebuild after hierarchy edits.</param>
public readonly record struct TransformHierarchyPassGateTelemetrySnapshot(
    long StopwatchFrequency,
    long CapturedTimestamp,
    TransformHierarchyPassGateSiteTelemetry PublishPreCollect,
    TransformHierarchyPassGateSiteTelemetry PublishSwap,
    TransformHierarchyPassGateSiteTelemetry PublishOther,
    TransformHierarchyPassGateSiteTelemetry ProcessPass,
    TransformHierarchyPassGateSiteTelemetry FlushHierarchy);
