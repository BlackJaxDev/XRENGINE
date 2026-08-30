using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>
/// Persistent renderer-neutral render-critical domain. Logical lane 0 is the
/// participating render thread; lanes 1..R are background OS threads.
/// </summary>
public sealed partial class RenderWorkDomain : IDisposable
{
    public const int DefaultFrameSlotCount = 3;
    public const int DefaultQueueCapacityPerLane = 4096;
    public const int DefaultBatchPoolCapacity = 8;
    public const int DefaultInitialItemCapacity = 256;
    public const int DefaultInitialDependencyCapacity = 512;
    public const int DefaultInlineItemThreshold = 4;
    public const int MaximumMigratableItemsPerLogicalLane = 4;
    public static readonly long LockWaitThresholdTicks =
        Math.Max(1L, Stopwatch.Frequency / 10_000L);
    public static readonly TimeSpan FatalBatchWait = TimeSpan.FromSeconds(2);

    [ThreadStatic]
    private static RenderWorkDomain? _currentDomain;

    [ThreadStatic]
    private static int _currentLaneId;

    private readonly Thread?[] _workers;
    private readonly AutoResetEvent[] _laneSignals;
    private readonly BoundedRenderWorkQueue[] _migratableQueues;
    private readonly BoundedRenderWorkQueue[] _affineQueues;
    private readonly Exception?[] _workerStartupFaults;
    private readonly int[] _managedThreadIds;
    private readonly long[] _laneExecutedItemCounts;
    private readonly long[] _laneExecuteAllocatedBytes;
    private readonly long[] _laneWakeCounts;
    private readonly long[] _laneEmptyWakeCounts;
    private readonly long[] _laneUnexplainedWakeCounts;
    private readonly long[] _laneSignalSequences;
    private readonly long[] _laneObservedSignalSequences;
    private readonly ManualResetEventSlim _workerStartupSignal;
    private readonly RenderWorkBatchPool _batchPool;
    private readonly ERenderWorkerQos _qos;
    private readonly int _inlineItemThreshold;
    private readonly object _laneZeroExecutionSync = new();
    private readonly object _shutdownSync = new();
    private int _workerStartupRemaining;
    private int _roundRobinLane;
    private int _laneZeroThreadId;
    private int _shutdownState;
    private int _disposedState;
    private int _poisonedState;
    private int _activeBatchCount;
    private long _activeLaneMask;
    private int _activeExecutionCount;
    private int _peakConcurrency;
    private int _queueHighWaterMark;
    private long _submittedBatchCount;
    private long _builtItemCount;
    private long _queuedItemCount;
    private long _completedBatchCount;
    private long _canceledBatchCount;
    private long _canceledItemCount;
    private long _faultedBatchCount;
    private long _timeoutCount;
    private long _quarantineCount;
    private long _inlineItemCount;
    private long _workerItemCount;
    private long _stolenItemCount;
    private long _wakeCount;
    private long _emptyWakeCount;
    private long _unexplainedWakeCount;
    private long _queueOverflowCount;
    private long _queueLockWaitCount;
    private long _queueLockWaitTicks;
    private long _queueLockWaitPeakTicks;
    private long _queueLockWaitOverThresholdCount;
    private long _totalWaitTicks;
    private long _buildOperationCount;
    private long _dispatchOperationCount;
    private long _executeOperationCount;
    private long _mergeOperationCount;
    private long _buildAllocatedBytes;
    private long _dispatchAllocatedBytes;
    private long _executeAllocatedBytes;
    private long _mergeAllocatedBytes;

    public RenderWorkDomain(
        int backgroundWorkerCount,
        ERenderWorkerQos qos,
        int frameSlotCount = DefaultFrameSlotCount,
        int queueCapacityPerLane = DefaultQueueCapacityPerLane,
        int batchPoolCapacity = DefaultBatchPoolCapacity,
        int initialItemCapacity = DefaultInitialItemCapacity,
        int initialDependencyCapacity = DefaultInitialDependencyCapacity,
        int inlineItemThreshold = DefaultInlineItemThreshold)
    {
        if (backgroundWorkerCount is < 0 or > EngineExecutionTopology.MaximumWorkerCount)
            throw new ArgumentOutOfRangeException(nameof(backgroundWorkerCount));
        if (!Enum.IsDefined(qos))
            throw new ArgumentOutOfRangeException(nameof(qos));
        if (inlineItemThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(inlineItemThreshold));

        _qos = qos;
        _inlineItemThreshold = inlineItemThreshold;
        LogicalLaneCount = checked(backgroundWorkerCount + 1);
        BackendAttachments = new RenderLaneBackendAttachments(LogicalLaneCount, frameSlotCount);
        _workers = new Thread?[backgroundWorkerCount];
        _workerStartupFaults = new Exception?[backgroundWorkerCount];
        _managedThreadIds = new int[LogicalLaneCount];
        _laneExecutedItemCounts = new long[LogicalLaneCount];
        _laneExecuteAllocatedBytes = new long[LogicalLaneCount];
        _laneWakeCounts = new long[LogicalLaneCount];
        _laneEmptyWakeCounts = new long[LogicalLaneCount];
        _laneUnexplainedWakeCounts = new long[LogicalLaneCount];
        _laneSignalSequences = new long[LogicalLaneCount];
        _laneObservedSignalSequences = new long[LogicalLaneCount];
        _laneSignalTimestamps = new long[LogicalLaneCount];
        _workerStartupRemaining = backgroundWorkerCount;
        _workerStartupSignal = new ManualResetEventSlim(backgroundWorkerCount == 0);
        _workerWakeCalibrationSignal = new ManualResetEventSlim(backgroundWorkerCount == 0);
        _laneSignals = new AutoResetEvent[LogicalLaneCount];
        _migratableQueues = new BoundedRenderWorkQueue[LogicalLaneCount];
        _affineQueues = new BoundedRenderWorkQueue[LogicalLaneCount];

        for (int laneId = 0; laneId < LogicalLaneCount; laneId++)
        {
            _laneSignals[laneId] = new AutoResetEvent(false);
            _migratableQueues[laneId] = new BoundedRenderWorkQueue(queueCapacityPerLane);
            _affineQueues[laneId] = new BoundedRenderWorkQueue(queueCapacityPerLane);
        }

        _batchPool = new RenderWorkBatchPool(
            this,
            batchPoolCapacity,
            initialItemCapacity,
            initialDependencyCapacity);
        MeasureQueueAndMergeBaselines();

        try
        {
            for (int workerIndex = 0; workerIndex < backgroundWorkerCount; workerIndex++)
            {
                int laneId = workerIndex + 1;
                var worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"XRE-Render-{laneId}",
                };
                _workers[workerIndex] = worker;
                worker.Start(laneId);
            }
        }
        catch (Exception exception)
        {
            if (!Shutdown(waitForWorkers: true))
            {
                Environment.FailFast(
                    "A render scheduler thread failed to start and the partially started domain did not quiesce.",
                    exception);
            }

            throw;
        }

        if (!_workerStartupSignal.Wait(FatalBatchWait))
        {
            var exception = new TimeoutException(
                "Persistent render workers did not start within the fatal lifecycle bound.");
            if (!Shutdown(waitForWorkers: true))
                Environment.FailFast(exception.Message, exception);
            throw exception;
        }

        for (int index = 0; index < _workerStartupFaults.Length; index++)
        {
            if (_workerStartupFaults[index] is Exception exception)
            {
                var startupException = new InvalidOperationException(
                    $"Render worker lane {index + 1} failed to apply requested QoS {qos}.",
                    exception);
                if (!Shutdown(waitForWorkers: true))
                    Environment.FailFast(startupException.Message, startupException);
                throw startupException;
            }
        }

        CalibrateWorkerWakeCosts();
    }

    public int BackgroundWorkerCount => _workers.Length;
    public int LogicalLaneCount { get; }
    public int InlineItemThreshold => _inlineItemThreshold;
    public int MaxMigratableItemCount
        => checked(MaximumMigratableItemsPerLogicalLane * LogicalLaneCount);
    public ERenderWorkerQos Qos => _qos;
    public RenderLaneBackendAttachments BackendAttachments { get; }

    public RenderWorkDomainMetrics Metrics => new(
        BackgroundWorkerCount,
        LogicalLaneCount,
        Volatile.Read(ref _activeBatchCount),
        BitOperations.PopCount((ulong)Volatile.Read(ref _activeLaneMask)),
        Volatile.Read(ref _peakConcurrency),
        Interlocked.Read(ref _submittedBatchCount),
        Interlocked.Read(ref _builtItemCount),
        Interlocked.Read(ref _queuedItemCount),
        Interlocked.Read(ref _completedBatchCount),
        Interlocked.Read(ref _canceledBatchCount),
        Interlocked.Read(ref _canceledItemCount),
        Interlocked.Read(ref _faultedBatchCount),
        Interlocked.Read(ref _timeoutCount),
        Interlocked.Read(ref _quarantineCount),
        Interlocked.Read(ref _inlineItemCount),
        Interlocked.Read(ref _workerItemCount),
        Interlocked.Read(ref _stolenItemCount),
        Interlocked.Read(ref _wakeCount),
        Interlocked.Read(ref _emptyWakeCount),
        Interlocked.Read(ref _unexplainedWakeCount),
        Interlocked.Read(ref _queueOverflowCount),
        Volatile.Read(ref _queueHighWaterMark),
        Interlocked.Read(ref _queueLockWaitCount),
        Interlocked.Read(ref _queueLockWaitTicks),
        Interlocked.Read(ref _queueLockWaitPeakTicks),
        Interlocked.Read(ref _queueLockWaitOverThresholdCount),
        LockWaitThresholdTicks,
        Interlocked.Read(ref _totalWaitTicks),
        Interlocked.Read(ref _buildOperationCount),
        Interlocked.Read(ref _dispatchOperationCount),
        Interlocked.Read(ref _executeOperationCount),
        Interlocked.Read(ref _mergeOperationCount),
        Interlocked.Read(ref _buildAllocatedBytes),
        Interlocked.Read(ref _dispatchAllocatedBytes),
        Interlocked.Read(ref _executeAllocatedBytes),
        Interlocked.Read(ref _mergeAllocatedBytes),
        MaxMigratableItemCount,
        Interlocked.Read(ref _migratableItemCount),
        Interlocked.Read(ref _capPinnedMigratableItemCount),
        Interlocked.Read(ref _parallelMigratableBatchCount),
        Interlocked.Read(ref _inlineMigratableBatchCount),
        Interlocked.Read(ref _insufficientParallelismBatchCount),
        Interlocked.Read(ref _unprofitableBatchCount),
        Interlocked.Read(ref _queueCostSampleCount),
        Interlocked.Read(ref _queueCostTicks),
        Interlocked.Read(ref _wakeCostSampleCount),
        Interlocked.Read(ref _wakeCostTicks),
        Interlocked.Read(ref _mergeCostSampleCount),
        Interlocked.Read(ref _mergeCostTicks),
        Interlocked.Read(ref _executorCostTicks),
        Volatile.Read(ref _estimatedTicksPerCostUnit),
        MinimumDispatchHysteresisTicks);

    public RenderWorkLaneSnapshot GetLaneSnapshot(int laneId)
    {
        if ((uint)laneId >= (uint)LogicalLaneCount)
            throw new ArgumentOutOfRangeException(nameof(laneId));

        return new RenderWorkLaneSnapshot(
            laneId,
            Volatile.Read(ref _managedThreadIds[laneId]),
            laneId == 0 ? ERenderWorkerQos.OsDefault : _qos,
            _migratableQueues[laneId].Count,
            _affineQueues[laneId].Count,
            _migratableQueues[laneId].Capacity,
            Interlocked.Read(ref _laneExecutedItemCounts[laneId]),
            Interlocked.Read(ref _laneWakeCounts[laneId]),
            Interlocked.Read(ref _laneEmptyWakeCounts[laneId]),
            Interlocked.Read(ref _laneUnexplainedWakeCounts[laneId]),
            Interlocked.Read(ref _laneExecuteAllocatedBytes[laneId]));
    }

    public RenderWorkBatchLease RentBatch(int itemCount, int dependencyCount = 0)
    {
        ThrowIfUnavailable();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        RenderWorkBatchLease lease = _batchPool.Rent(itemCount, dependencyCount);
        RecordBuildAllocation(allocationBefore);
        return lease;
    }

    /// <summary>
    /// Seals, executes, and joins a batch while the calling render thread
    /// participates as lane 0. Partial output is invalid for any non-success result.
    /// </summary>
    public RenderWorkBatchResult ExecuteAndWait(
        ref RenderWorkBatchLease lease,
        IRenderWorkExecutor executor,
        int frameSlot = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!lease.IsValid)
            throw new ArgumentException("A valid pooled batch lease is required.", nameof(lease));
        ArgumentNullException.ThrowIfNull(executor);
        if (!ReferenceEquals(lease.Batch.Domain, this))
            throw new ArgumentException("The batch lease belongs to a different render-work domain.", nameof(lease));
        if (_currentDomain is not null)
            throw new InvalidOperationException("Nested render-work execution on a scheduler lane is forbidden.");

        lock (_laneZeroExecutionSync)
        {
            ThrowIfUnavailable();
            BindCallingThreadAsLaneZero(lease.Batch, lease.Generation);
            _batchPool.FinalizeFaultQuarantinesOnOwnerThread();
            return ExecuteAndWaitCore(ref lease, executor, frameSlot, timeout, cancellationToken);
        }
    }

    private RenderWorkBatchResult ExecuteAndWaitCore(
        ref RenderWorkBatchLease lease,
        IRenderWorkExecutor executor,
        int frameSlot,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        TimeSpan lifecycleBound = timeout ?? FatalBatchWait;
        if (lifecycleBound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        RenderWorkBatch batch = lease.Batch;
        bool requestInline = lease.ItemCount <= _inlineItemThreshold;
        long dispatchAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        batch.SealAndQueue(lease.Generation, executor, frameSlot, requestInline);
        RecordDispatchAllocation(dispatchAllocationBefore);

        RenderWorkDomain? previousDomain = _currentDomain;
        int previousLaneId = _currentLaneId;
        _currentDomain = this;
        _currentLaneId = 0;
        long start = Stopwatch.GetTimestamp();
        long mergeAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        long timeoutTicks = Math.Max(1L, (long)(lifecycleBound.TotalSeconds * Stopwatch.Frequency));
        try
        {
            while (!batch.IsQuiesced)
            {
                if (cancellationToken.IsCancellationRequested)
                    batch.Cancel(lease.Generation);

                if (TryExecuteOne(0))
                    continue;

                long elapsed = Stopwatch.GetTimestamp() - start;
                if (elapsed >= timeoutTicks)
                {
                    FailFastForNonQuiescentBatch(batch, lease.Generation, lifecycleBound);
                }

                _laneSignals[0].WaitOne(1);
            }

            long mergeCostStarted = Stopwatch.GetTimestamp();
            batch.FinalizeFaultQuarantine(lease.Generation);
            RenderWorkBatchResult result = batch.GetResult(lease.Generation);
            RecordMergeOperation(mergeAllocationBefore, mergeCostStarted);
            return result;
        }
        finally
        {
            _currentDomain = previousDomain;
            _currentLaneId = previousLaneId;
        }
    }

    public bool Shutdown(bool waitForWorkers = true)
        => Shutdown(waitForWorkers, FatalBatchWait);

    internal bool Shutdown(bool waitForWorkers, TimeSpan timeout)
    {
        long deadline = CreateDeadline(timeout);
        if (!Monitor.TryEnter(_shutdownSync, waitForWorkers ? GetRemaining(deadline) : TimeSpan.Zero))
        {
            Interlocked.Exchange(ref _shutdownState, 1);
            return false;
        }

        try
        {
            if (Volatile.Read(ref _disposedState) != 0)
                return true;

            Interlocked.Exchange(ref _shutdownState, 1);
            _batchPool.BeginShutdown();
            WakeForTerminalDrain();
            DrainCanceledQueueReferences();

            if (!waitForWorkers)
                return false;

            if (!_batchPool.WaitForRentOperations(GetRemaining(deadline)))
                return false;
            _batchPool.CancelAllBatches();

            if (!Monitor.TryEnter(_laneZeroExecutionSync, GetRemaining(deadline)))
                return false;

            bool workersStopped = true;
            try
            {
                foreach (Thread? worker in _workers)
                {
                    if (worker is null)
                        continue;
                    if (ReferenceEquals(worker, Thread.CurrentThread))
                    {
                        workersStopped = false;
                        continue;
                    }

                    TimeSpan remaining = GetRemaining(deadline);
                    if (worker.IsAlive && (remaining <= TimeSpan.Zero || !worker.Join(remaining)))
                        workersStopped = false;
                }

                DrainCanceledQueueReferences();
                if (!workersStopped || !_batchPool.WaitForQuiescence(GetRemaining(deadline)))
                    return false;

                if (IsLaneZeroOwnerThread)
                    _batchPool.FinalizeFaultQuarantinesOnOwnerThread();

                if (!_batchPool.WaitForLeaseReturns(GetRemaining(deadline)))
                    return false;

                DisposeResources();
                return true;
            }
            finally
            {
                Monitor.Exit(_laneZeroExecutionSync);
            }
        }
        finally
        {
            Monitor.Exit(_shutdownSync);
        }
    }

    /// <summary>
    /// Performs a bounded clean shutdown. A timeout is surfaced instead of
    /// silently releasing ownership while a lane can still reference caller
    /// supplied executors or backend attachments.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The domain did not quiesce within the fatal lifecycle bound. Callers
    /// must retain dependent executor/backend state and retry or abandon the
    /// process; it is unsafe to continue ordinary teardown.
    /// </exception>
    public void Dispose()
    {
        if (!Shutdown(waitForWorkers: true))
        {
            throw new TimeoutException(
                "Render-work disposal timed out with live workers, batches, or leases. " +
                "Dependent executor/backend state must remain alive until a later clean shutdown.");
        }
    }

    internal void QueueReadyItem(RenderWorkBatch batch, long generation, int itemIndex)
    {
        long queueCostStarted = Stopwatch.GetTimestamp();
        if (!batch.TryAddQueuedReference(generation))
        {
            RecordQueueCost(queueCostStarted);
            return;
        }

        RenderWorkItem item = batch.GetItem(itemIndex);
        bool affine = batch.InlineOnly || item.PreferredLane >= 0;
        int targetLane = batch.InlineOnly
            ? 0
            : affine
                ? item.PreferredLane
                : (int)((uint)Interlocked.Increment(ref _roundRobinLane) % (uint)LogicalLaneCount);
        BoundedRenderWorkQueue queue = affine
            ? _affineQueues[targetLane]
            : _migratableQueues[targetLane];
        var claim = new RenderWorkClaim(
            batch,
            generation,
            itemIndex,
            affine,
            Stopwatch.GetTimestamp());

        if (!queue.TryEnqueue(
                claim,
                out bool transitionedFromEmpty,
                out int queueDepth,
                out long lockWaitTicks))
        {
            RecordQueueLockWait(lockWaitTicks);
            batch.ReleaseQueuedReference(generation);
            Interlocked.Increment(ref _queueOverflowCount);
            RecordQueueCost(queueCostStarted);
            batch.FaultScheduling(
                generation,
                itemIndex,
                new InvalidOperationException(
                    $"Bounded render queue for lane {targetLane} is full (capacity={queue.Capacity})."));
            return;
        }

        RecordQueueLockWait(lockWaitTicks);
        Interlocked.Increment(ref _queuedItemCount);
        UpdateMaximum(ref _queueHighWaterMark, queueDepth);
        RecordQueueCost(queueCostStarted);

        if (transitionedFromEmpty)
            SignalLane(targetLane, allowSteal: !affine);
    }

    internal void WakeForSubmittedBatch(bool inlineOnly)
    {
        _laneSignals[0].Set();
        _ = inlineOnly;
    }

    internal void WakeForTerminalDrain()
    {
        if (Volatile.Read(ref _disposedState) != 0)
            return;

        _laneSignals[0].Set();
        for (int laneId = 1; laneId < LogicalLaneCount; laneId++)
            SignalBackgroundLane(laneId);
    }

    internal void OnBatchSubmitted(int itemCount)
    {
        Interlocked.Increment(ref _submittedBatchCount);
        Interlocked.Add(ref _builtItemCount, itemCount);
        Interlocked.Increment(ref _activeBatchCount);
    }

    internal void OnBatchCompleted()
    {
        Interlocked.Increment(ref _completedBatchCount);
        Interlocked.Decrement(ref _activeBatchCount);
    }

    internal void OnBatchCanceled(bool wasRunning, int canceledItemCount)
    {
        Interlocked.Increment(ref _canceledBatchCount);
        Interlocked.Add(ref _canceledItemCount, canceledItemCount);
        if (wasRunning)
            Interlocked.Decrement(ref _activeBatchCount);
    }

    internal void OnBatchFaulted(bool timedOut)
    {
        Interlocked.Increment(ref _faultedBatchCount);
        if (timedOut)
            Interlocked.Increment(ref _timeoutCount);
        Interlocked.Decrement(ref _activeBatchCount);
    }

    internal void OnCanceledBatchFaulted(bool timedOut, int previouslyCanceledItemCount)
    {
        Interlocked.Decrement(ref _canceledBatchCount);
        Interlocked.Add(ref _canceledItemCount, -previouslyCanceledItemCount);
        Interlocked.Increment(ref _faultedBatchCount);
        if (timedOut)
            Interlocked.Increment(ref _timeoutCount);
    }

    internal void OnBatchQuarantined()
        => Interlocked.Increment(ref _quarantineCount);

    internal void OnBatchQuarantineFailed()
        => Interlocked.Exchange(ref _poisonedState, 1);

    internal void RecordBuildAllocation(long allocationBefore)
    {
        Interlocked.Increment(ref _buildOperationCount);
        Interlocked.Add(ref _buildAllocatedBytes, GetAllocatedByteDelta(allocationBefore));
    }

    private void RecordDispatchAllocation(long allocationBefore)
    {
        Interlocked.Increment(ref _dispatchOperationCount);
        Interlocked.Add(ref _dispatchAllocatedBytes, GetAllocatedByteDelta(allocationBefore));
    }

    internal void RecordMergeOperation(long allocationBefore, long costStarted)
    {
        Interlocked.Increment(ref _mergeOperationCount);
        Interlocked.Add(ref _mergeAllocatedBytes, GetAllocatedByteDelta(allocationBefore));
        RecordCostSample(
            ref _mergeCostSampleCount,
            ref _mergeCostTicks,
            Math.Max(1L, Stopwatch.GetTimestamp() - costStarted));
    }

    private static long GetAllocatedByteDelta(long allocationBefore)
        => Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - allocationBefore);

    private void WorkerLoop(object? state)
    {
        int laneId = (int)state!;
        try
        {
            if (_qos == ERenderWorkerQos.High)
                WindowsThreadQos.ApplyHighRenderPriority();
        }
        catch (Exception exception)
        {
            _workerStartupFaults[laneId - 1] = exception;
            SignalWorkerStarted();
            return;
        }

        SignalWorkerStarted();
        _currentDomain = this;
        _currentLaneId = laneId;
        Volatile.Write(ref _managedThreadIds[laneId], Environment.CurrentManagedThreadId);
        try
        {
            while (true)
            {
                if (TryExecuteOne(laneId))
                    continue;
                if (Volatile.Read(ref _shutdownState) != 0)
                    return;

                _laneSignals[laneId].WaitOne();

                bool explicitlySignaled = ObserveWorkerSignal(laneId);
                RecordWorkerWakeCost(laneId);
                if (Volatile.Read(ref _workerWakeCalibrationState) != 0)
                {
                    if (Interlocked.Decrement(ref _workerWakeCalibrationRemaining) == 0)
                        _workerWakeCalibrationSignal.Set();
                    continue;
                }

                if (Volatile.Read(ref _shutdownState) != 0)
                    return;

                Interlocked.Increment(ref _wakeCount);
                Interlocked.Increment(ref _laneWakeCounts[laneId]);
                if (!explicitlySignaled)
                {
                    Interlocked.Increment(ref _unexplainedWakeCount);
                    Interlocked.Increment(
                        ref _laneUnexplainedWakeCounts[laneId]);
                }
                if (!TryExecuteOne(laneId))
                {
                    Interlocked.Increment(ref _emptyWakeCount);
                    Interlocked.Increment(ref _laneEmptyWakeCounts[laneId]);
                }
            }
        }
        finally
        {
            _currentDomain = null;
            _currentLaneId = -1;
        }
    }

    private bool TryExecuteOne(int laneId)
    {
        long executeAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        while (TryTakeClaim(laneId, out RenderWorkClaim claim, out bool stolen))
        {
            bool claimStarted = claim.Batch.TryBeginClaim(claim.Generation, claim.ItemIndex);
            claim.Batch.ReleaseQueuedReference(claim.Generation);
            if (!claimStarted)
                continue;

            if (stolen)
                Interlocked.Increment(ref _stolenItemCount);
            Interlocked.Add(
                ref _totalWaitTicks,
                Math.Max(0L, Stopwatch.GetTimestamp() - claim.EnqueuedTimestamp));

            RenderWorkItem item = claim.Batch.GetItem(claim.ItemIndex);
            bool inlineOnly = claim.Batch.InlineOnly;
            var context = new RenderWorkerContext(
                laneId,
                Environment.CurrentManagedThreadId,
                claim.Batch.FrameSlot,
                claim.Generation,
                claim.ItemIndex,
                BackendAttachments);
            int active = Interlocked.Increment(ref _activeExecutionCount);
            UpdateMaximum(ref _peakConcurrency, active);
            Interlocked.Or(ref _activeLaneMask, 1L << laneId);
            try
            {
                long executorCostStarted = Stopwatch.GetTimestamp();
                try
                {
                    claim.Batch.Executor.Execute(item, ref context);
                }
                finally
                {
                    RecordExecutorCost(item.EstimatedCost, executorCostStarted);
                }

                claim.Batch.CompleteClaim(claim.Generation, claim.ItemIndex);
            }
            catch (Exception exception)
            {
                claim.Batch.FaultClaim(claim.Generation, claim.ItemIndex, laneId, exception);
            }
            finally
            {
                long allocatedBytes = GetAllocatedByteDelta(executeAllocationBefore);
                Interlocked.Increment(ref _executeOperationCount);
                Interlocked.Add(ref _executeAllocatedBytes, allocatedBytes);
                Interlocked.Add(ref _laneExecuteAllocatedBytes[laneId], allocatedBytes);
                Interlocked.And(ref _activeLaneMask, ~(1L << laneId));
                Interlocked.Decrement(ref _activeExecutionCount);
            }

            Interlocked.Increment(ref _laneExecutedItemCounts[laneId]);
            if (inlineOnly)
                Interlocked.Increment(ref _inlineItemCount);
            if (laneId != 0)
                Interlocked.Increment(ref _workerItemCount);
            return true;
        }

        return false;
    }

    private bool TryTakeClaim(int laneId, out RenderWorkClaim claim, out bool stolen)
    {
        long queueCostStarted = Stopwatch.GetTimestamp();
        bool claimed = TryTakeClaimCore(laneId, out claim, out stolen);
        RecordQueueCost(queueCostStarted);
        return claimed;
    }

    private bool TryTakeClaimCore(int laneId, out RenderWorkClaim claim, out bool stolen)
    {
        if (TryDequeueAndRecord(_affineQueues[laneId], out claim) ||
            TryDequeueAndRecord(_migratableQueues[laneId], out claim))
        {
            stolen = false;
            return true;
        }

        for (int offset = 1; offset < LogicalLaneCount; offset++)
        {
            int sourceLane = (laneId + offset) % LogicalLaneCount;
            if (TryDequeueAndRecord(
                    _migratableQueues[sourceLane],
                    out claim))
            {
                stolen = true;
                return true;
            }
        }

        claim = default;
        stolen = false;
        return false;
    }

    private bool TryDequeueAndRecord(
        BoundedRenderWorkQueue queue,
        out RenderWorkClaim claim)
    {
        bool dequeued = queue.TryDequeue(
            out claim,
            out long lockWaitTicks);
        RecordQueueLockWait(lockWaitTicks);
        return dequeued;
    }

    private void RecordQueueLockWait(long waitTicks)
    {
        if (waitTicks <= 0L)
            return;

        Interlocked.Increment(ref _queueLockWaitCount);
        Interlocked.Add(ref _queueLockWaitTicks, waitTicks);
        UpdateMaximum(ref _queueLockWaitPeakTicks, waitTicks);
        if (waitTicks > LockWaitThresholdTicks)
            Interlocked.Increment(ref _queueLockWaitOverThresholdCount);
    }

    private void BindCallingThreadAsLaneZero(RenderWorkBatch candidateBatch, long candidateGeneration)
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int boundThreadId = Volatile.Read(ref _laneZeroThreadId);
        if (boundThreadId == currentThreadId)
        {
            Volatile.Write(ref _managedThreadIds[0], currentThreadId);
            return;
        }
        if (boundThreadId == 0 && Interlocked.CompareExchange(ref _laneZeroThreadId, currentThreadId, 0) == 0)
        {
            Volatile.Write(ref _managedThreadIds[0], currentThreadId);
            return;
        }
        if (_batchPool.HasUnquiescedBatchesExcept(candidateBatch, candidateGeneration) ||
            BackendAttachments.HasAnyForLane(0))
        {
            throw new InvalidOperationException(
                $"Render lane 0 is bound to managed thread {boundThreadId} and cannot move to {currentThreadId} " +
                "while a batch is unquiesced or a backend attachment is active.");
        }

        Interlocked.Exchange(ref _laneZeroThreadId, currentThreadId);
        Volatile.Write(ref _managedThreadIds[0], currentThreadId);
    }

    private void SignalLane(int targetLane, bool allowSteal)
    {
        if (targetLane == 0)
            _laneSignals[targetLane].Set();
        else
            SignalBackgroundLane(targetLane);

        if (!allowSteal || BackgroundWorkerCount == 0 || targetLane != 0)
            return;

        int backgroundLane = 1 + (int)((uint)Interlocked.Increment(ref _roundRobinLane) % (uint)BackgroundWorkerCount);
        SignalBackgroundLane(backgroundLane);
    }

    private void SignalBackgroundLane(int laneId)
    {
        long timestamp = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _laneSignalSequences[laneId]);
        Interlocked.CompareExchange(ref _laneSignalTimestamps[laneId], timestamp, 0L);
        _laneSignals[laneId].Set();
    }

    private bool ObserveWorkerSignal(int laneId)
    {
        long sequence = Volatile.Read(ref _laneSignalSequences[laneId]);
        long previous = Interlocked.Exchange(
            ref _laneObservedSignalSequences[laneId],
            sequence);
        return sequence != previous;
    }

    private void SignalWorkerStarted()
    {
        if (Interlocked.Decrement(ref _workerStartupRemaining) == 0)
            _workerStartupSignal.Set();
    }

    private void DrainCanceledQueueReferences()
    {
        for (int laneId = 0; laneId < LogicalLaneCount; laneId++)
        {
            while (TryDequeueAndRecord(
                       _affineQueues[laneId],
                       out RenderWorkClaim affine))
                affine.Batch.ReleaseQueuedReference(affine.Generation);
            while (TryDequeueAndRecord(
                       _migratableQueues[laneId],
                       out RenderWorkClaim migratable))
                migratable.Batch.ReleaseQueuedReference(migratable.Generation);
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _disposedState, 1) != 0)
            return;

        _batchPool.DisposeStorage();
        BackendAttachments.Clear();
        foreach (AutoResetEvent signal in _laneSignals)
            signal.Dispose();
        _workerStartupSignal.Dispose();
        _workerWakeCalibrationSignal.Dispose();
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _shutdownState) != 0 || Volatile.Read(ref _disposedState) != 0 ||
            Volatile.Read(ref _poisonedState) != 0)
            throw new ObjectDisposedException(nameof(RenderWorkDomain));
    }

    internal bool IsLaneZeroOwnerThread
        => Volatile.Read(ref _laneZeroThreadId) == Environment.CurrentManagedThreadId;

    private void FailFastForNonQuiescentBatch(
        RenderWorkBatch batch,
        long generation,
        TimeSpan lifecycleBound)
    {
        var timeoutException = new TimeoutException(
            $"Render-work batch {generation} remained non-quiescent after the fatal " +
            $"{lifecycleBound.TotalSeconds:F1}s lifecycle bound.");
        Interlocked.Exchange(ref _poisonedState, 1);
        batch.FaultScheduling(generation, itemIndex: -1, timeoutException);
        WakeForTerminalDrain();

        string message =
            $"{timeoutException.Message} itemCount={batch.ItemCount}, " +
            $"activeBatches={Volatile.Read(ref _activeBatchCount)}, " +
            $"activeLanes=0x{Volatile.Read(ref _activeLaneMask):X}, " +
            $"lane0Thread={Volatile.Read(ref _laneZeroThreadId)}. " +
            "Returning would release caller-owned executor/backend state while a worker may still use it.";
        Environment.FailFast(message, timeoutException);
        throw timeoutException;
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

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
                return;
        }
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        while (true)
        {
            long current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) ==
                current)
            {
                return;
            }
        }
    }
}
