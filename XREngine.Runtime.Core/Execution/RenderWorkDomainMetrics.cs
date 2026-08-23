namespace XREngine.Execution;

/// <summary>
/// Allocation-free snapshot of render-domain scheduling activity.
/// </summary>
public readonly record struct RenderWorkDomainMetrics(
    int BackgroundWorkerCount,
    int LogicalLaneCount,
    int ActiveBatchCount,
    int ActiveLaneCount,
    int PeakConcurrency,
    long SubmittedBatchCount,
    long BuiltItemCount,
    long QueuedItemCount,
    long CompletedBatchCount,
    long CanceledBatchCount,
    long CanceledItemCount,
    long FaultedBatchCount,
    long TimeoutCount,
    long QuarantineCount,
    long InlineItemCount,
    long WorkerItemCount,
    long StolenItemCount,
    long WakeCount,
    long EmptyWakeCount,
    long QueueOverflowCount,
    int QueueHighWaterMark,
    long TotalWaitTicks);
