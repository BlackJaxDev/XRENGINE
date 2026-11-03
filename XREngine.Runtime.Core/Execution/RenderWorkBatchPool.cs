using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Fixed control-block pool. Each control block retains ArrayPool-backed storage
/// across generations, so warm submissions allocate no managed memory.
/// </summary>
internal sealed class RenderWorkBatchPool
{
    private readonly RenderWorkBatch[] _batches;
    private readonly object _rentalSync = new();
    private int _activeRentOperations;
    private int _asynchronousRentalCount;
    private int _shutdownState;

    internal RenderWorkBatchPool(
        RenderWorkDomain domain,
        int batchCapacity,
        int initialItemCapacity,
        int initialDependentCapacity)
    {
        if (batchCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchCapacity));

        _batches = new RenderWorkBatch[batchCapacity];
        for (int index = 0; index < batchCapacity; index++)
        {
            _batches[index] = new RenderWorkBatch(
                this,
                domain,
                initialItemCapacity,
                initialDependentCapacity);
        }
    }

    internal RenderWorkBatchLease Rent(int itemCount, int dependentCount)
    {
        Interlocked.Increment(ref _activeRentOperations);
        try
        {
            if (Volatile.Read(ref _shutdownState) != 0)
                throw new ObjectDisposedException(nameof(RenderWorkBatchPool));

            // Every rental takes the rental gate, so the asynchronous capacity rules
            // below see a consistent count of free blocks.
            lock (_rentalSync)
            {
                foreach (RenderWorkBatch batch in _batches)
                {
                    if (!batch.TryRent(itemCount, dependentCount, out long generation))
                        continue;

                    var lease = new RenderWorkBatchLease(batch, generation);
                    if (Volatile.Read(ref _shutdownState) == 0)
                        return lease;

                    lease.Dispose();
                    throw new ObjectDisposedException(nameof(RenderWorkBatchPool));
                }
            }

            throw new InvalidOperationException(
                $"The bounded render-work batch pool ({_batches.Length} leases) is exhausted. " +
                "A frame slot still owns an earlier generation or the configured pool is too small.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeRentOperations);
        }
    }

    /// <summary>
    /// Maximum concurrent asynchronous rentals: <c>min(2, max(0, capacity - 1))</c>.
    /// A one-block pool admits none, so synchronous work always keeps a block.
    /// </summary>
    internal int AsynchronousRentalLimit => Math.Min(2, Math.Max(0, _batches.Length - 1));

    internal int AsynchronousRentalCount => Volatile.Read(ref _asynchronousRentalCount);

    /// <summary>
    /// Rents a building lease charged to the asynchronous capacity. The rental fails
    /// with a reason when the limit is reached, when the executor already holds a
    /// rental, or when it would take the last free block.
    /// </summary>
    internal RenderWorkAdmissionResult TryRentAsynchronous(
        IRenderWorkExecutor executor,
        int itemCount,
        int dependentCount,
        out RenderWorkBatchLease lease)
    {
        lease = default;
        Interlocked.Increment(ref _activeRentOperations);
        try
        {
            if (Volatile.Read(ref _shutdownState) != 0)
                return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.DomainUnavailable);

            int limit = AsynchronousRentalLimit;
            if (limit == 0)
                return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.AsynchronousCapacityDisabled);

            lock (_rentalSync)
            {
                if (Volatile.Read(ref _asynchronousRentalCount) >= limit)
                    return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.AsynchronousCapacityExhausted);

                int freeCount = 0;
                foreach (RenderWorkBatch batch in _batches)
                {
                    if (ReferenceEquals(batch.AsynchronousExecutor, executor))
                        return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.ExecutorBusy);
                    if (batch.IsIdle)
                        freeCount++;
                }

                if (freeCount < 2)
                    return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.LastFreeBatch);

                foreach (RenderWorkBatch batch in _batches)
                {
                    if (!batch.TryRent(itemCount, dependentCount, out long generation))
                        continue;

                    batch.BeginAsynchronousRental(generation, executor);
                    Interlocked.Increment(ref _asynchronousRentalCount);
                    lease = new RenderWorkBatchLease(batch, generation);
                    return RenderWorkAdmissionResult.Admitted;
                }

                return new RenderWorkAdmissionResult(ERenderWorkAdmissionReason.LastFreeBatch);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeRentOperations);
        }
    }

    internal void ReleaseAsynchronousCharge()
        => Interlocked.Decrement(ref _asynchronousRentalCount);

    internal long GetOldestUnjoinedAsynchronousPublishTicks()
    {
        long oldest = 0L;
        foreach (RenderWorkBatch batch in _batches)
        {
            if (!batch.IsAsynchronousSubmitted)
                continue;

            long published = batch.AsynchronousPublishedTicks;
            if (published != 0L && (oldest == 0L || published < oldest))
                oldest = published;
        }

        return oldest;
    }

    internal void Return(RenderWorkBatch batch)
    {
        // Availability is represented by the batch's atomic Idle state. The
        // fixed pool requires no stack node or per-return allocation.
    }

    internal void BeginShutdown()
    {
        Interlocked.Exchange(ref _shutdownState, 1);

        CancelAllBatches();
    }

    internal void CancelAllBatches()
    {
        foreach (RenderWorkBatch batch in _batches)
            batch.CancelForShutdown();
    }

    internal bool WaitForRentOperations(TimeSpan timeout)
    {
        long deadline = CreateDeadline(timeout);
        var spinner = new SpinWait();
        while (Volatile.Read(ref _activeRentOperations) != 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                return false;

            spinner.SpinOnce();
        }

        return true;
    }

    internal bool WaitForQuiescence(TimeSpan timeout)
    {
        long deadline = CreateDeadline(timeout);
        foreach (RenderWorkBatch batch in _batches)
        {
            TimeSpan remaining = GetRemaining(deadline);
            if (remaining <= TimeSpan.Zero || !batch.WaitForQuiescence(remaining))
                return false;
        }

        return true;
    }

    internal void FinalizeFaultQuarantinesOnOwnerThread()
    {
        foreach (RenderWorkBatch batch in _batches)
            batch.FinalizeFaultQuarantine(batch.Generation);
    }

    internal bool HasUnquiescedBatchesExcept(RenderWorkBatch? ignoredBatch, long ignoredGeneration)
    {
        foreach (RenderWorkBatch batch in _batches)
        {
            if (ReferenceEquals(batch, ignoredBatch) && batch.Generation == ignoredGeneration)
                continue;
            if (batch.HasUnquiescedGeneration)
                return true;
        }

        return false;
    }

    internal bool WaitForLeaseReturns(TimeSpan timeout)
    {
        long deadline = CreateDeadline(timeout);
        foreach (RenderWorkBatch batch in _batches)
        {
            TimeSpan remaining = GetRemaining(deadline);
            if (remaining <= TimeSpan.Zero || !batch.WaitForLeaseReturn(remaining))
                return false;
        }

        return true;
    }

    internal void DisposeStorage()
    {
        foreach (RenderWorkBatch batch in _batches)
            batch.DisposeStorage();
    }

    private static long CreateDeadline(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return Stopwatch.GetTimestamp();

        double timeoutTicks = timeout.TotalSeconds * Stopwatch.Frequency;
        long boundedTicks = timeoutTicks >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1L, (long)timeoutTicks);
        long now = Stopwatch.GetTimestamp();
        return boundedTicks >= long.MaxValue - now ? long.MaxValue : now + boundedTicks;
    }

    private static TimeSpan GetRemaining(long deadline)
    {
        long ticks = deadline - Stopwatch.GetTimestamp();
        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }
}
