using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Asynchronous ownership state of a pooled batch. The phase is separate from the
/// execution state: <c>Building → Submitted → Joined</c>, and release returns the
/// block to the pool. CPU quiescence is observed through the terminal event.
/// </summary>
internal sealed partial class RenderWorkBatch
{
    private const int AsyncNone = 0;
    private const int AsyncBuilding = 1;
    private const int AsyncSubmitted = 2;
    private const int AsyncJoined = 3;

    private int _asyncPhase;
    private bool _asyncJoining;
    private IRenderWorkExecutor? _asyncExecutor;
    private long _asyncPublishedTicks;
    private long _asyncDeadlineTicks;
    private long _asyncQuiescedTicks;
    private RenderWorkBatchResult _asyncJoinResult;
    private long _asyncLaneMask;

    internal bool IsAsynchronous => Volatile.Read(ref _asyncPhase) != AsyncNone;
    internal bool IsAsynchronousSubmitted => Volatile.Read(ref _asyncPhase) == AsyncSubmitted;
    internal IRenderWorkExecutor? AsynchronousExecutor => Volatile.Read(ref _asyncExecutor);
    internal long AsynchronousPublishedTicks => Volatile.Read(ref _asyncPublishedTicks);
    internal ulong AsynchronousLaneMask => unchecked((ulong)Volatile.Read(ref _asyncLaneMask));

    internal void ValidateBuildingAsynchronous(long generation)
    {
        lock (_leaseSync)
        {
            ValidateBuildingLease(generation);
            if (Volatile.Read(ref _asyncPhase) != AsyncBuilding)
                throw new InvalidOperationException("Only a building asynchronous rental can reserve lanes.");
        }
    }

    /// <summary>
    /// Records the reserved lanes. The caller holds the domain reservation gate and
    /// has validated the building generation; this does not take the lease lock, so
    /// the gate is never held while waiting for a batch lock.
    /// </summary>
    internal void SetAsynchronousLaneMask(ulong laneMask)
        => Volatile.Write(ref _asyncLaneMask, unchecked((long)laneMask));

    internal void ClearAsynchronousLaneMask()
        => Volatile.Write(ref _asyncLaneMask, 0L);

    /// <summary>Marks a freshly rented building generation as an asynchronous rental.</summary>
    internal void BeginAsynchronousRental(long generation, IRenderWorkExecutor executor)
    {
        lock (_leaseSync)
        {
            ValidateBuildingLease(generation);
            if (Volatile.Read(ref _asyncPhase) != AsyncNone)
                throw new InvalidOperationException("The render-work batch already has an asynchronous rental.");

            _asyncExecutor = executor;
            Volatile.Write(ref _asyncPhase, AsyncBuilding);
        }
    }

    /// <summary>
    /// Publishes a building asynchronous generation. A validation failure before
    /// publication keeps the lease in the building phase.
    /// </summary>
    internal void SubmitAsynchronous(long generation, IRenderWorkExecutor executor, int frameSlot, long deadlineSpanTicks)
    {
        lock (_leaseSync)
        {
            ValidateBuildingLease(generation);
            if (Volatile.Read(ref _asyncPhase) != AsyncBuilding)
                throw new InvalidOperationException("Only a building asynchronous rental can be submitted.");
            if (!ReferenceEquals(_asyncExecutor, executor))
                throw new ArgumentException("The executor does not match the asynchronous rental.", nameof(executor));

            long published = Stopwatch.GetTimestamp();
            _asyncPublishedTicks = published;
            _asyncDeadlineTicks = deadlineSpanTicks >= long.MaxValue - published
                ? long.MaxValue
                : published + deadlineSpanTicks;
            Volatile.Write(ref _asyncQuiescedTicks, 0L);
            Volatile.Write(ref _asyncPhase, AsyncSubmitted);
            try
            {
                SealAndQueue(generation, executor, frameSlot, requestInline: false);
            }
            catch
            {
                // Validation runs before the running state is published. Nothing was
                // queued, so the rental returns to the building phase.
                if (Volatile.Read(ref _state) == StateBuilding)
                {
                    Volatile.Write(ref _asyncPhase, AsyncBuilding);
                    _asyncPublishedTicks = 0L;
                    _asyncDeadlineTicks = 0L;
                }

                throw;
            }
        }
    }

    /// <summary>Validates the generation first, then that it is an asynchronous rental.</summary>
    internal void ValidateAsynchronousRental(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            if (Volatile.Read(ref _asyncPhase) == AsyncNone)
                throw new InvalidOperationException("The batch lease is not an asynchronous rental.");
        }
    }

    /// <summary>Validates an owner request that requires a published asynchronous generation.</summary>
    internal void ValidateSubmittedAsynchronous(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            int phase = Volatile.Read(ref _asyncPhase);
            if (phase is not AsyncSubmitted and not AsyncJoined)
                throw new InvalidOperationException("The asynchronous render-work batch has not been submitted.");
        }
    }

    /// <summary>Returns the deadline and quiescence timestamps of a submitted generation.</summary>
    internal void GetAsynchronousTimes(long generation, out long deadlineTicks, out long quiescedTicks)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            deadlineTicks = _asyncDeadlineTicks;
            quiescedTicks = Volatile.Read(ref _asyncQuiescedTicks);
        }
    }

    internal bool TryGetJoinedAsynchronousResult(long generation, out RenderWorkBatchResult result)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            if (Volatile.Read(ref _asyncPhase) == AsyncJoined)
            {
                result = _asyncJoinResult;
                return true;
            }

            result = default;
            return false;
        }
    }

    /// <summary>Allows the designated join to finalize this generation's fault quarantine.</summary>
    internal void BeginAsynchronousJoin(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            if (Volatile.Read(ref _asyncPhase) != AsyncSubmitted)
                throw new InvalidOperationException("Only a submitted asynchronous batch can be joined.");
            if (!_terminalEvent.IsSet)
                throw new InvalidOperationException("An asynchronous batch must quiesce before its join completes.");

            _asyncJoining = true;
        }
    }

    internal RenderWorkBatchResult CompleteAsynchronousJoin(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            if (Volatile.Read(ref _asyncPhase) != AsyncSubmitted || !_asyncJoining)
                throw new InvalidOperationException("The asynchronous join was not started.");

            int state = Volatile.Read(ref _state);
            if (state == StateFaulted && Volatile.Read(ref _quarantineFinalized) != 2)
                throw new InvalidOperationException("A faulted asynchronous batch was joined before its quarantine finalized.");

            _asyncJoinResult = new RenderWorkBatchResult(generation, MapStatus(state), _itemCount, _fault);
            _asyncJoining = false;
            Volatile.Write(ref _asyncPhase, AsyncJoined);
            return _asyncJoinResult;
        }
    }

    /// <summary>
    /// Cancels asynchronous work that has not started. A building rental is abandoned;
    /// a quiescent or joined generation is not changed.
    /// </summary>
    internal void CancelAsynchronous(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            int phase = Volatile.Read(ref _asyncPhase);
            if (phase == AsyncNone)
                throw new InvalidOperationException("The render-work batch is not an asynchronous rental.");
            if (phase == AsyncJoined || (phase == AsyncSubmitted && _terminalEvent.IsSet))
                return;

            CancelUnderLock();
        }
    }

    private void ResetAsynchronousState()
    {
        Volatile.Write(ref _asyncPhase, AsyncNone);
        _asyncJoining = false;
        Volatile.Write(ref _asyncExecutor, null);
        _asyncPublishedTicks = 0L;
        _asyncDeadlineTicks = 0L;
        Volatile.Write(ref _asyncQuiescedTicks, 0L);
        _asyncJoinResult = default;
    }

    private void RecordAsynchronousQuiescence()
    {
        if (Volatile.Read(ref _asyncPhase) == AsyncSubmitted)
            Interlocked.CompareExchange(ref _asyncQuiescedTicks, Stopwatch.GetTimestamp(), 0L);
    }

    private bool IsAsynchronousQuarantineDeferredUnderLock()
        => Volatile.Read(ref _asyncPhase) == AsyncSubmitted && !_asyncJoining;

    private void ThrowIfAsynchronousReleaseRejectedUnderLock()
    {
        if (Volatile.Read(ref _asyncPhase) == AsyncSubmitted)
        {
            throw new InvalidOperationException(
                "A submitted asynchronous render-work batch must be joined before its lease is released.");
        }
    }

    private long _synchronousClaimMask;

    internal ulong SynchronousClaimMask => unchecked((ulong)Volatile.Read(ref _synchronousClaimMask));

    internal void ValidateBuildingSynchronous(long generation)
    {
        lock (_leaseSync)
        {
            ValidateBuildingLease(generation);
            if (Volatile.Read(ref _asyncPhase) != AsyncNone)
                throw new InvalidOperationException("An asynchronous rental cannot take a synchronous lane-plan claim.");
        }
    }

    /// <summary>Records the claimed lanes. The caller holds the domain reservation gate.</summary>
    internal void SetSynchronousClaimMask(ulong laneMask)
        => Volatile.Write(ref _synchronousClaimMask, unchecked((long)laneMask));

    private void ReleaseSynchronousClaimUnderLock()
    {
        if (Volatile.Read(ref _synchronousClaimMask) == 0L)
            return;

        _domain.ReleaseSynchronousLaneClaim(this);
    }

    private void ReleaseAsynchronousChargeUnderLock()
    {
        if (Volatile.Read(ref _asyncPhase) == AsyncNone)
            return;

        _domain.OnAsynchronousRentalReleased(this);
        ResetAsynchronousState();
    }
}
