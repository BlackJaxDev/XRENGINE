using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Fixed control-block pool. Each control block retains ArrayPool-backed storage
/// across generations, so warm submissions allocate no managed memory.
/// </summary>
internal sealed class RenderWorkBatchPool
{
    private readonly RenderWorkBatch[] _batches;
    private int _activeRentOperations;
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

            throw new InvalidOperationException(
                $"The bounded render-work batch pool ({_batches.Length} leases) is exhausted. " +
                "A frame slot still owns an earlier generation or the configured pool is too small.");
        }
        finally
        {
            Interlocked.Decrement(ref _activeRentOperations);
        }
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

    internal bool HasUnquiescedBatchesExcept(RenderWorkBatch ignoredBatch, long ignoredGeneration)
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
