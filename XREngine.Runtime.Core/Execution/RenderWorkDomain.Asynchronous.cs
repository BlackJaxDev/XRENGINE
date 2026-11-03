using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Asynchronous batches: the lane 0 owner rents, submits, polls and joins a batch
/// that runs only on background lanes, and continues with other work between
/// submit and join. Every entry point runs on the lane 0 owner thread.
/// </summary>
public sealed partial class RenderWorkDomain
{
    private readonly object _asyncReservationSync = new();
    private readonly RenderWorkBatch?[] _laneReservationOwners =
        new RenderWorkBatch?[EngineExecutionTopology.MaximumWorkerCount + 1];
    private readonly long[] _laneReservationGenerations =
        new long[EngineExecutionTopology.MaximumWorkerCount + 1];
    private readonly int[] _laneSynchronousClaimCounts =
        new int[EngineExecutionTopology.MaximumWorkerCount + 1];
    private long _asynchronousLaneMask;
    private long _synchronousClaimLaneMask;
    private long _asyncLaneReservationCount;
    private long _asyncAffineCollisionCount;
    private int _asyncPeakOutstanding;
    private long _asyncRentalCount;
    private long _asyncSubmitCount;
    private long _asyncJoinCount;
    private long _asyncWaitingJoinCount;
    private long _asyncJoinWaitTicks;
    private long _asyncPeakJoinWaitTicks;
    private long _asyncSubmitToQuiescenceTicks;
    private long _asyncDeadlineViolationCount;
    private long _asyncCompletedCount;
    private long _asyncCanceledCount;
    private long _asyncFaultedCount;
    private long _asyncRejectedCapacityDisabled;
    private long _asyncRejectedCapacityExhausted;
    private long _asyncRejectedExecutorBusy;
    private long _asyncRejectedLastFreeBatch;
    private long _asyncRejectedDomainUnavailable;
    private long _asyncRejectedLaneReserved;

    /// <summary>
    /// Snapshot of background lanes that asynchronous batches reserve. It is for
    /// diagnostics and dispatch filtering, not a synchronization primitive.
    /// </summary>
    public ulong AsynchronousLaneMask => unchecked((ulong)Volatile.Read(ref _asynchronousLaneMask));

    /// <summary>Allocation-free snapshot of asynchronous batch activity.</summary>
    public RenderWorkAsynchronousMetrics AsynchronousMetrics
    {
        get
        {
            long oldestPublished = _batchPool.GetOldestUnjoinedAsynchronousPublishTicks();
            long oldestAge = oldestPublished == 0L ? 0L : Math.Max(0L, Stopwatch.GetTimestamp() - oldestPublished);
            return new RenderWorkAsynchronousMetrics(
                _batchPool.AsynchronousRentalLimit,
                _batchPool.AsynchronousRentalCount,
                Volatile.Read(ref _asyncPeakOutstanding),
                oldestAge,
                Interlocked.Read(ref _asyncRentalCount),
                Interlocked.Read(ref _asyncSubmitCount),
                Interlocked.Read(ref _asyncJoinCount),
                Interlocked.Read(ref _asyncWaitingJoinCount),
                Interlocked.Read(ref _asyncJoinWaitTicks),
                Interlocked.Read(ref _asyncPeakJoinWaitTicks),
                Interlocked.Read(ref _asyncSubmitToQuiescenceTicks),
                Interlocked.Read(ref _asyncDeadlineViolationCount),
                Interlocked.Read(ref _asyncCompletedCount),
                Interlocked.Read(ref _asyncCanceledCount),
                Interlocked.Read(ref _asyncFaultedCount),
                Interlocked.Read(ref _asyncRejectedCapacityDisabled),
                Interlocked.Read(ref _asyncRejectedCapacityExhausted),
                Interlocked.Read(ref _asyncRejectedExecutorBusy),
                Interlocked.Read(ref _asyncRejectedLastFreeBatch),
                Interlocked.Read(ref _asyncRejectedDomainUnavailable),
                Interlocked.Read(ref _asyncRejectedLaneReserved),
                Interlocked.Read(ref _asyncLaneReservationCount),
                Interlocked.Read(ref _asyncAffineCollisionCount));
        }
    }

    /// <summary>
    /// Rents a building lease charged to the asynchronous capacity and binds it to
    /// <paramref name="executor"/>. A rejection returns a reason and no lease.
    /// </summary>
    public RenderWorkAdmissionResult TryRentAsynchronousBatch(
        IRenderWorkExecutor executor,
        int itemCount,
        int dependencyCount,
        out RenderWorkBatchLease lease)
    {
        ArgumentNullException.ThrowIfNull(executor);
        lease = default;
        if (Volatile.Read(ref _shutdownState) != 0 || Volatile.Read(ref _disposedState) != 0 ||
            Volatile.Read(ref _poisonedState) != 0)
        {
            return RecordAdmission(new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.DomainUnavailable));
        }

        EnsureAsynchronousOwner();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        RenderWorkAdmissionResult result = _batchPool.TryRentAsynchronous(executor, itemCount, dependencyCount, out lease);
        RecordBuildAllocation(allocationBefore);
        if (result.IsAdmitted)
        {
            Interlocked.Increment(ref _asyncRentalCount);
            UpdateMaximum(ref _asyncPeakOutstanding, _batchPool.AsynchronousRentalCount);
        }

        return RecordAdmission(result);
    }

    /// <summary>
    /// Reserves every lane in <paramref name="laneMask"/> for a building asynchronous
    /// rental before backend preparation. The mask names background lanes only. A
    /// rejection leaves the rental valid and unreserved. Overlapping asynchronous
    /// reservations are rejected.
    /// </summary>
    public RenderWorkAdmissionResult TryReserveAsynchronousLanes(in RenderWorkBatchLease lease, ulong laneMask)
    {
        RenderWorkBatch batch = ValidateAsynchronousLease(lease);
        long generation = lease.Generation;
        batch.ValidateBuildingAsynchronous(generation);
        if (laneMask == 0UL || (laneMask & 1UL) != 0UL || (laneMask >> LogicalLaneCount) != 0UL)
            throw new ArgumentOutOfRangeException(nameof(laneMask), "The mask must name existing background lanes only.");

        lock (_asyncReservationSync)
        {
            if (batch.AsynchronousLaneMask != 0UL)
                throw new InvalidOperationException("The asynchronous rental already holds a lane reservation.");

            ulong reserved = AsynchronousLaneMask;
            if ((reserved & laneMask) != 0UL)
                return RecordAdmission(new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.LaneReserved));
            if ((unchecked((ulong)Volatile.Read(ref _synchronousClaimLaneMask)) & laneMask) != 0UL)
            {
                return RecordAdmission(
                    new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.LaneClaimedBySynchronousWork));
            }

            for (int lane = 1; lane < LogicalLaneCount; lane++)
            {
                if ((laneMask & (1UL << lane)) == 0UL)
                    continue;

                _laneReservationOwners[lane] = batch;
                _laneReservationGenerations[lane] = generation;
            }

            batch.SetAsynchronousLaneMask(laneMask);
            Volatile.Write(ref _asynchronousLaneMask, unchecked((long)(reserved | laneMask)));
            Interlocked.Increment(ref _asyncLaneReservationCount);
        }

        return RenderWorkAdmissionResult.Admitted;
    }

    /// <summary>
    /// Claims lanes for a building synchronous batch before its planner assigns items
    /// or touches lane-local backend storage. A claim fails when asynchronous work
    /// reserves a requested lane, and it blocks new asynchronous reservations of its
    /// lanes until the batch returns to the pool. Release or abort of the lease ends
    /// the claim. Only the claim is held during preparation and execution, not the gate.
    /// </summary>
    public RenderWorkAdmissionResult TryClaimSynchronousLanes(in RenderWorkBatchLease lease, ulong laneMask)
    {
        if (!lease.IsValid)
            throw new ArgumentException("A valid pooled batch lease is required.", nameof(lease));

        RenderWorkBatch batch = lease.Batch;
        if (!ReferenceEquals(batch.Domain, this))
            throw new ArgumentException("The batch lease belongs to a different render-work domain.", nameof(lease));
        if (_currentDomain is not null)
            throw new InvalidOperationException("Lane-plan claims cannot be taken from a scheduler lane.");

        batch.ValidateBuildingSynchronous(lease.Generation);
        if (laneMask == 0UL || (laneMask >> LogicalLaneCount) != 0UL)
            throw new ArgumentOutOfRangeException(nameof(laneMask), "The mask must name existing lanes only.");

        lock (_asyncReservationSync)
        {
            if (batch.SynchronousClaimMask != 0UL)
                throw new InvalidOperationException("The batch already holds a lane-plan claim.");
            if ((AsynchronousLaneMask & laneMask) != 0UL)
            {
                Interlocked.Increment(ref _asyncAffineCollisionCount);
                return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.LaneReserved);
            }

            ulong claimed = 0UL;
            for (int lane = 0; lane < LogicalLaneCount; lane++)
            {
                if ((laneMask & (1UL << lane)) == 0UL)
                    continue;

                _laneSynchronousClaimCounts[lane]++;
                claimed |= 1UL << lane;
            }

            batch.SetSynchronousClaimMask(claimed);
            ulong mask = unchecked((ulong)Volatile.Read(ref _synchronousClaimLaneMask)) | claimed;
            Volatile.Write(ref _synchronousClaimLaneMask, unchecked((long)mask));
        }

        return RenderWorkAdmissionResult.Admitted;
    }

    /// <summary>Ends the lane-plan claim of a batch that returns to the pool.</summary>
    internal void ReleaseSynchronousLaneClaim(RenderWorkBatch batch)
    {
        lock (_asyncReservationSync)
        {
            ulong claimed = batch.SynchronousClaimMask;
            if (claimed == 0UL)
                return;

            ulong mask = unchecked((ulong)Volatile.Read(ref _synchronousClaimLaneMask));
            for (int lane = 0; lane < LogicalLaneCount; lane++)
            {
                if ((claimed & (1UL << lane)) == 0UL)
                    continue;

                if (--_laneSynchronousClaimCounts[lane] <= 0)
                {
                    _laneSynchronousClaimCounts[lane] = 0;
                    mask &= ~(1UL << lane);
                }
            }

            Volatile.Write(ref _synchronousClaimLaneMask, unchecked((long)mask));
            batch.SetSynchronousClaimMask(0UL);
        }
    }

    /// <summary>
    /// Seals and queues an asynchronous rental, and returns without waiting. Every
    /// item must name a background lane, so lane 0 never claims this work. The
    /// absolute CPU deadline starts here and includes queue delay.
    /// </summary>
    public void SubmitAsynchronous(
        in RenderWorkBatchLease lease,
        IRenderWorkExecutor executor,
        int frameSlot)
    {
        ArgumentNullException.ThrowIfNull(executor);
        RenderWorkBatch batch = ValidateAsynchronousLease(lease);
        ThrowIfUnavailable();
        int itemCount = lease.ItemCount;
        for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
        {
            int lane = batch.GetItem(itemIndex).PreferredLane;
            if (lane < 1 || lane >= LogicalLaneCount)
            {
                throw new ArgumentException(
                    $"Asynchronous render-work item {itemIndex} must name a background lane, not {lane}.",
                    nameof(lease));
            }
            if ((batch.AsynchronousLaneMask & (1UL << lane)) == 0UL)
            {
                throw new InvalidOperationException(
                    $"Asynchronous render-work item {itemIndex} names lane {lane}, which its rental did not reserve.");
            }
        }

        long deadlineSpanTicks = Math.Max(1L, (long)(FatalBatchWait.TotalSeconds * Stopwatch.Frequency));
        long dispatchAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        batch.SubmitAsynchronous(lease.Generation, executor, frameSlot, deadlineSpanTicks);
        RecordDispatchAllocation(dispatchAllocationBefore);
        Interlocked.Increment(ref _asyncSubmitCount);
    }

    /// <summary>
    /// Reports CPU quiescence of a submitted asynchronous batch without waiting. It
    /// does not finalize fault quarantine; only the join does that.
    /// </summary>
    public bool IsAsynchronousComplete(in RenderWorkBatchLease lease)
    {
        RenderWorkBatch batch = ValidateAsynchronousLease(lease);
        batch.ValidateSubmittedAsynchronous(lease.Generation);
        if (batch.TryGetJoinedAsynchronousResult(lease.Generation, out _))
            return true;

        bool quiesced = batch.IsQuiesced;
        CheckAsynchronousDeadline(batch, lease.Generation, quiesced);
        return quiesced;
    }

    /// <summary>
    /// Waits for a submitted asynchronous batch within its remaining deadline,
    /// finalizes a fault quarantine once, and returns the stable result. A
    /// duplicate join returns the same result.
    /// </summary>
    public RenderWorkBatchResult JoinAsynchronous(in RenderWorkBatchLease lease)
    {
        RenderWorkBatch batch = ValidateAsynchronousLease(lease);
        long generation = lease.Generation;
        batch.ValidateSubmittedAsynchronous(generation);
        if (batch.TryGetJoinedAsynchronousResult(generation, out RenderWorkBatchResult joined))
            return joined;

        long mergeAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        long waitStart = Stopwatch.GetTimestamp();
        if (!batch.IsQuiesced)
        {
            batch.GetAsynchronousTimes(generation, out long deadlineTicks, out _);
            long remainingTicks = deadlineTicks - waitStart;
            bool quiesced = remainingTicks > 0L &&
                batch.WaitForQuiescence(TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
            long waited = Stopwatch.GetTimestamp() - waitStart;
            Interlocked.Increment(ref _asyncWaitingJoinCount);
            Interlocked.Add(ref _asyncJoinWaitTicks, waited);
            UpdateMaximum(ref _asyncPeakJoinWaitTicks, waited);
            if (!quiesced)
                FailAsynchronousDeadline(batch, generation, "did not quiesce before its CPU deadline");
        }

        CheckAsynchronousDeadline(batch, generation, quiesced: true);
        long mergeCostStarted = Stopwatch.GetTimestamp();
        batch.BeginAsynchronousJoin(generation);
        batch.FinalizeFaultQuarantine(generation);
        RenderWorkBatchResult result = batch.CompleteAsynchronousJoin(generation);
        ReleaseAsynchronousLanes(batch, generation);
        RecordMergeOperation(mergeAllocationBefore, mergeCostStarted);
        RecordAsynchronousJoin(batch, generation, result);
        return result;
    }

    /// <summary>
    /// Cancels asynchronous work that has not started. Running items still return,
    /// and a submitted batch still requires a join. A building rental is abandoned;
    /// dispose its lease to release the rental charge.
    /// </summary>
    public void CancelAsynchronous(in RenderWorkBatchLease lease)
    {
        RenderWorkBatch batch = ValidateAsynchronousLease(lease);
        batch.CancelAsynchronous(lease.Generation);
    }

    internal void OnAsynchronousRentalReleased(RenderWorkBatch batch)
    {
        ReleaseAsynchronousLanes(batch, batch.Generation);
        _batchPool.ReleaseAsynchronousCharge();
    }

    /// <summary>True when an asynchronous batch reserves <paramref name="laneId"/>.</summary>
    internal bool IsLaneReservedForAsynchronousWork(int laneId)
        => laneId > 0 && (AsynchronousLaneMask & (1UL << laneId)) != 0UL;

    /// <summary>
    /// Rejects a synchronous batch whose fixed affine item names a reserved lane. The
    /// item is not queued behind asynchronous work or moved to another lane.
    /// </summary>
    internal void ThrowIfSynchronousBatchCollides(RenderWorkBatch batch)
    {
        ulong reserved = AsynchronousLaneMask;
        if (reserved == 0UL)
            return;

        int itemCount = batch.ItemCount;
        for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
        {
            int lane = batch.GetItem(itemIndex).PreferredLane;
            if (lane > 0 && (reserved & (1UL << lane)) != 0UL)
            {
                Interlocked.Increment(ref _asyncAffineCollisionCount);
                throw new InvalidOperationException(
                    $"Synchronous render-work item {itemIndex} names lane {lane}, which asynchronous work reserves.");
            }
        }
    }

    private void ReleaseAsynchronousLanes(RenderWorkBatch batch, long generation)
    {
        lock (_asyncReservationSync)
        {
            ulong released = 0UL;
            for (int lane = 1; lane < LogicalLaneCount; lane++)
            {
                if (!ReferenceEquals(_laneReservationOwners[lane], batch) ||
                    _laneReservationGenerations[lane] != generation)
                {
                    continue;
                }

                _laneReservationOwners[lane] = null;
                _laneReservationGenerations[lane] = 0L;
                released |= 1UL << lane;
            }

            if (released == 0UL)
                return;

            ulong remaining = AsynchronousLaneMask & ~released;
            Volatile.Write(ref _asynchronousLaneMask, unchecked((long)remaining));
            batch.ClearAsynchronousLaneMask();
        }
    }

    private RenderWorkBatch ValidateAsynchronousLease(in RenderWorkBatchLease lease)
    {
        if (!lease.IsValid)
            throw new ArgumentException("A valid pooled batch lease is required.", nameof(lease));

        RenderWorkBatch batch = lease.Batch;
        if (!ReferenceEquals(batch.Domain, this))
            throw new ArgumentException("The batch lease belongs to a different render-work domain.", nameof(lease));
        batch.ValidateAsynchronousRental(lease.Generation);

        EnsureAsynchronousOwner();
        return batch;
    }

    /// <summary>
    /// Binds or validates the lane 0 owner. Scheduler callbacks, including those of
    /// another domain, cannot use asynchronous entry points.
    /// </summary>
    private void EnsureAsynchronousOwner()
    {
        if (_currentDomain is not null)
            throw new InvalidOperationException("Asynchronous render work cannot be used from a scheduler lane.");

        lock (_laneZeroExecutionSync)
            BindCallingThreadAsLaneZero(null, 0L);
    }

    private void CheckAsynchronousDeadline(RenderWorkBatch batch, long generation, bool quiesced)
    {
        batch.GetAsynchronousTimes(generation, out long deadlineTicks, out long quiescedTicks);
        if (quiesced)
        {
            if (quiescedTicks != 0L && quiescedTicks > deadlineTicks)
                FailAsynchronousDeadline(batch, generation, "quiesced after its CPU deadline");
            return;
        }

        if (Stopwatch.GetTimestamp() > deadlineTicks)
            FailAsynchronousDeadline(batch, generation, "did not quiesce before its CPU deadline");
    }

    private void FailAsynchronousDeadline(RenderWorkBatch batch, long generation, string detail)
    {
        Interlocked.Increment(ref _asyncDeadlineViolationCount);
        if (!batch.IsQuiesced)
            FailFastForNonQuiescentBatch(batch, generation, FatalBatchWait);

        var timeoutException = new TimeoutException(
            $"Asynchronous render-work batch {generation} {detail} " +
            $"({FatalBatchWait.TotalSeconds:F1}s bound from publication).");
        Interlocked.Exchange(ref _poisonedState, 1);
        Environment.FailFast(timeoutException.Message, timeoutException);
        throw timeoutException;
    }

    private void RecordAsynchronousJoin(RenderWorkBatch batch, long generation, in RenderWorkBatchResult result)
    {
        Interlocked.Increment(ref _asyncJoinCount);
        batch.GetAsynchronousTimes(generation, out _, out long quiescedTicks);
        long published = batch.AsynchronousPublishedTicks;
        if (published != 0L && quiescedTicks >= published)
            Interlocked.Add(ref _asyncSubmitToQuiescenceTicks, quiescedTicks - published);

        if (result.Succeeded)
            Interlocked.Increment(ref _asyncCompletedCount);
        else if (result.IsCanceled)
            Interlocked.Increment(ref _asyncCanceledCount);
        else if (result.IsFaulted)
            Interlocked.Increment(ref _asyncFaultedCount);
    }

    private RenderWorkAdmissionResult RecordAdmission(RenderWorkAdmissionResult result)
    {
        switch (result.Reason)
        {
            case ERenderWorkAdmissionReason.AsynchronousCapacityDisabled:
                Interlocked.Increment(ref _asyncRejectedCapacityDisabled);
                break;
            case ERenderWorkAdmissionReason.AsynchronousCapacityExhausted:
                Interlocked.Increment(ref _asyncRejectedCapacityExhausted);
                break;
            case ERenderWorkAdmissionReason.ExecutorBusy:
                Interlocked.Increment(ref _asyncRejectedExecutorBusy);
                break;
            case ERenderWorkAdmissionReason.LastFreeBatch:
                Interlocked.Increment(ref _asyncRejectedLastFreeBatch);
                break;
            case ERenderWorkAdmissionReason.DomainUnavailable:
                Interlocked.Increment(ref _asyncRejectedDomainUnavailable);
                break;
            case ERenderWorkAdmissionReason.LaneReserved:
                Interlocked.Increment(ref _asyncRejectedLaneReserved);
                break;
        }

        return result;
    }
}
