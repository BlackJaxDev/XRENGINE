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
    long TotalWaitTicks,
    long BuildOperationCount,
    long DispatchOperationCount,
    long ExecuteOperationCount,
    long MergeOperationCount,
    long BuildAllocatedBytes,
    long DispatchAllocatedBytes,
    long ExecuteAllocatedBytes,
    long MergeAllocatedBytes)
{
    /// <summary>
    /// True when the observed scheduler-owned stages added no managed bytes
    /// between two snapshots. Executor-owned allocation is included in execute;
    /// merge covers the complete join/wait interval and pooled lease return.
    /// </summary>
    public bool HasNoManagedAllocationsSince(in RenderWorkDomainMetrics baseline)
        => BuildAllocatedBytes == baseline.BuildAllocatedBytes &&
           DispatchAllocatedBytes == baseline.DispatchAllocatedBytes &&
           ExecuteAllocatedBytes == baseline.ExecuteAllocatedBytes &&
           MergeAllocatedBytes == baseline.MergeAllocatedBytes;
}
