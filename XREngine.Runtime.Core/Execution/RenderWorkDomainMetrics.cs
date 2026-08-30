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
    long UnexplainedWakeCount,
    long QueueOverflowCount,
    int QueueHighWaterMark,
    long QueueLockWaitCount,
    long QueueLockWaitTicks,
    long QueueLockWaitPeakTicks,
    long QueueLockWaitOverThresholdCount,
    long LockWaitThresholdTicks,
    long TotalWaitTicks,
    long BuildOperationCount,
    long DispatchOperationCount,
    long ExecuteOperationCount,
    long MergeOperationCount,
    long BuildAllocatedBytes,
    long DispatchAllocatedBytes,
    long ExecuteAllocatedBytes,
    long MergeAllocatedBytes,
    int MaxMigratableItemCount,
    long MigratableItemCount,
    long CapPinnedMigratableItemCount,
    long ParallelMigratableBatchCount,
    long InlineMigratableBatchCount,
    long InsufficientParallelismBatchCount,
    long UnprofitableBatchCount,
    long QueueCostSampleCount,
    long QueueCostTicks,
    long WakeCostSampleCount,
    long WakeCostTicks,
    long MergeCostSampleCount,
    long MergeCostTicks,
    long ExecutorCostTicks,
    long EstimatedTicksPerCostUnit,
    long DispatchHysteresisTicks)
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

    /// <summary>
    /// True when no render-queue lock acquisition exceeded the scheduler's
    /// 0.1 ms interference threshold between two snapshots.
    /// </summary>
    public bool HasNoOverThresholdLockWaitsSince(in RenderWorkDomainMetrics baseline)
        => QueueLockWaitOverThresholdCount ==
           baseline.QueueLockWaitOverThresholdCount;

    /// <summary>
    /// True when every worker wake since the baseline followed an explicit
    /// scheduler signal.
    /// </summary>
    public bool HasNoUnexplainedWakeupsSince(in RenderWorkDomainMetrics baseline)
        => UnexplainedWakeCount == baseline.UnexplainedWakeCount;
}
