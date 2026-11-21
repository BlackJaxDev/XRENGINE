namespace XREngine.Execution;

/// <summary>
/// Allocation-free snapshot of asynchronous render-work activity. Tick values use
/// <see cref="System.Diagnostics.Stopwatch.Frequency"/>.
/// </summary>
public readonly record struct RenderWorkAsynchronousMetrics(
    int AsynchronousRentalLimit,
    int OutstandingBatchCount,
    int PeakOutstandingBatchCount,
    long OldestUnjoinedAgeTicks,
    long RentalCount,
    long SubmitCount,
    long JoinCount,
    long WaitingJoinCount,
    long JoinWaitTicks,
    long PeakJoinWaitTicks,
    long SubmitToQuiescenceTicks,
    long DeadlineViolationCount,
    long CompletedCount,
    long CanceledCount,
    long FaultedCount,
    long RejectedCapacityDisabledCount,
    long RejectedCapacityExhaustedCount,
    long RejectedExecutorBusyCount,
    long RejectedLastFreeBatchCount,
    long RejectedDomainUnavailableCount,
    long RejectedLaneReservedCount,
    long LaneReservationCount,
    long AffineCollisionCount);
