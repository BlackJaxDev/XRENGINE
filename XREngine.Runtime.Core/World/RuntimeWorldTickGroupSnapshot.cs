using XREngine.Components;

namespace XREngine;

/// <summary>
/// Cumulative dispatch and pending-registration counters of one tick group.
/// Compare two snapshots to observe a window. High-water fields and their
/// timestamps are maxima since launch, not window values.
/// </summary>
/// <param name="Group">The tick group these counters describe.</param>
/// <param name="Dispatches">Times the group was dispatched.</param>
/// <param name="QueueVisits">Ordered queues visited across all dispatches.</param>
/// <param name="CallbackInvocations">Callbacks invoked.</param>
/// <param name="DispatchTicks">Whole group dispatch, including the parts below.</param>
/// <param name="SnapshotTicks">Copying the ordered queues under the group lock.</param>
/// <param name="PendingTicks">Applying pending registrations and removals, excluding observer work.</param>
/// <param name="CallbackTicks">Running the callbacks.</param>
/// <param name="ObserverTicks">Identifying newly registered callbacks for observation; exists only while observing.</param>
/// <param name="AllocatedBytes">Managed bytes allocated on the dispatching thread during dispatch, including observer work.</param>
/// <param name="PendingAdds">Registrations applied.</param>
/// <param name="PendingDuplicateAdds">Registrations ignored because the callback was already present.</param>
/// <param name="PendingRemoves">Removals applied.</param>
/// <param name="PendingMissingRemoves">Removals that found no matching callback.</param>
/// <param name="MembershipComparisons">Callback comparisons made while looking for duplicates and removal targets.</param>
/// <param name="RegisteredCallbacksHighWater">Largest callback count of any single queue in the group.</param>
/// <param name="PendingChangesInOneDispatchHighWater">Most pending changes applied by one queue in one dispatch.</param>
/// <param name="PendingTicksInOneDispatchHighWater">Longest pending application by one queue in one dispatch.</param>
/// <param name="PendingTicksHighWaterTimestamp">Stopwatch timestamp of that longest pending application.</param>
/// <param name="DispatchTicksHighWater">Longest single dispatch of the group.</param>
/// <param name="DispatchTicksHighWaterTimestamp">Stopwatch timestamp at which that dispatch started.</param>
public readonly record struct RuntimeWorldTickGroupSnapshot(
    ETickGroup Group,
    long Dispatches,
    long QueueVisits,
    long CallbackInvocations,
    long DispatchTicks,
    long SnapshotTicks,
    long PendingTicks,
    long CallbackTicks,
    long ObserverTicks,
    long AllocatedBytes,
    long PendingAdds,
    long PendingDuplicateAdds,
    long PendingRemoves,
    long PendingMissingRemoves,
    long MembershipComparisons,
    long RegisteredCallbacksHighWater,
    long PendingChangesInOneDispatchHighWater,
    long PendingTicksInOneDispatchHighWater,
    long PendingTicksHighWaterTimestamp,
    long DispatchTicksHighWater,
    long DispatchTicksHighWaterTimestamp);
