using System.Buffers;
using System.Runtime.CompilerServices;

namespace XREngine.Execution;

/// <summary>
/// Reusable control block and pooled storage for one render-work generation.
/// </summary>
internal sealed class RenderWorkBatch
{
    private const int StateReturning = -3;
    private const int StateRenting = -2;
    private const int StateIdle = -1;
    private const int StateBuilding = 0;
    private const int StateRunning = 1;
    private const int StateCompleted = 2;
    private const int StateCanceled = 3;
    private const int StateFaulted = 4;
    private const int StateFaulting = 5;

    private readonly RenderWorkBatchPool _pool;
    private readonly RenderWorkDomain _domain;
    private readonly object _leaseSync = new();
    private readonly ManualResetEventSlim _terminalEvent = new(initialState: true);
    private readonly ManualResetEventSlim _leaseReturnedEvent = new(initialState: true);
    private RenderWorkItem[] _items;
    private int[] _itemStates;
    private int[] _remainingPrerequisites;
    private int[] _validationPrerequisites;
    private int[] _validationQueue;
    private int[] _dependents;
    private int _state = StateIdle;
    private int _itemCount;
    private int _dependentCount;
    private int _configuredItemCount;
    private int _configuredDependentCount;
    private int _remainingItems;
    private int _canceledItemCountSnapshot;
    private int _activeClaims;
    private int _queuedReferences;
    private int _leaseReleased;
    private int _quarantineFinalized;
    private int _faultingItemIndex;
    private int _faultingLaneId;
    private int _frameSlot;
    private bool _inlineOnly;
    private long _generation;
    private Exception? _fault;
    private IRenderWorkExecutor? _executor;

    internal RenderWorkBatch(
        RenderWorkBatchPool pool,
        RenderWorkDomain domain,
        int initialItemCapacity,
        int initialDependentCapacity)
    {
        _pool = pool;
        _domain = domain;
        _items = ArrayPool<RenderWorkItem>.Shared.Rent(Math.Max(1, initialItemCapacity));
        _itemStates = ArrayPool<int>.Shared.Rent(Math.Max(1, initialItemCapacity));
        _remainingPrerequisites = ArrayPool<int>.Shared.Rent(Math.Max(1, initialItemCapacity));
        _validationPrerequisites = ArrayPool<int>.Shared.Rent(Math.Max(1, initialItemCapacity));
        _validationQueue = ArrayPool<int>.Shared.Rent(Math.Max(1, initialItemCapacity));
        _dependents = ArrayPool<int>.Shared.Rent(Math.Max(1, initialDependentCapacity));
    }

    internal long Generation => Volatile.Read(ref _generation);
    internal int ItemCount => _itemCount;
    internal int FrameSlot => _frameSlot;
    internal RenderWorkDomain Domain => _domain;
    internal IRenderWorkExecutor Executor
        => _executor ?? throw new InvalidOperationException("The render-work batch has no sealed executor.");
    internal bool InlineOnly => _inlineOnly;
    internal bool IsQuiesced => _terminalEvent.IsSet;
    internal bool HasUnquiescedGeneration
    {
        get
        {
            int state = Volatile.Read(ref _state);
            return state is StateRenting or StateBuilding or StateRunning || !_terminalEvent.IsSet;
        }
    }

    internal bool TryRent(int itemCount, int dependentCount, out long generation)
    {
        lock (_leaseSync)
        {
            if (Interlocked.CompareExchange(ref _state, StateRenting, StateIdle) != StateIdle)
            {
                generation = 0;
                return false;
            }

            generation = Interlocked.Increment(ref _generation);

            try
            {
                EnsureItemCapacity(itemCount);
                EnsureDependentCapacity(dependentCount);

                _itemCount = itemCount;
                _dependentCount = dependentCount;
                _configuredItemCount = 0;
                _configuredDependentCount = 0;
                _remainingItems = 0;
                _canceledItemCountSnapshot = 0;
                _activeClaims = 0;
                _queuedReferences = 0;
                _frameSlot = 0;
                _inlineOnly = false;
                _fault = null;
                _executor = null;
                Volatile.Write(ref _leaseReleased, 0);
                Volatile.Write(ref _quarantineFinalized, 0);
                _faultingItemIndex = -1;
                _faultingLaneId = -1;
                _terminalEvent.Reset();
                _leaseReturnedEvent.Reset();

                if (itemCount > 0)
                {
                    Array.Fill(_itemStates, -1, 0, itemCount);
                    Array.Clear(_remainingPrerequisites, 0, itemCount);
                    Array.Clear(_validationPrerequisites, 0, itemCount);
                }

                if (dependentCount > 0)
                    Array.Fill(_dependents, -1, 0, dependentCount);

                Volatile.Write(ref _state, StateBuilding);
                return true;
            }
            catch
            {
                _terminalEvent.Set();
                _leaseReturnedEvent.Set();
                Volatile.Write(ref _state, StateIdle);
                throw;
            }
        }
    }

    internal int GetItemCount(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            return _itemCount;
        }
    }

    internal void SetItem(long generation, int itemIndex, in RenderWorkItem item)
    {
        lock (_leaseSync)
        {
            ValidateBuildingLease(generation);
            if ((uint)itemIndex >= (uint)_itemCount)
                throw new ArgumentOutOfRangeException(nameof(itemIndex));
            if (item.SourceStart < 0)
                throw new ArgumentOutOfRangeException(nameof(item), "SourceStart cannot be negative.");
            if (item.SourceCount < 0)
                throw new ArgumentOutOfRangeException(nameof(item), "SourceCount cannot be negative.");
            if (item.PrerequisiteCount < 0 || item.DependentStart < 0 || item.DependentCount < 0)
                throw new ArgumentOutOfRangeException(nameof(item), "Dependency fields cannot be negative.");
            if (item.EstimatedCost <= 0)
                throw new ArgumentOutOfRangeException(nameof(item), "EstimatedCost must be positive.");

            if (Interlocked.CompareExchange(ref _itemStates[itemIndex], 0, -1) != -1)
                throw new InvalidOperationException($"Render-work item {itemIndex} was already configured.");

            _items[itemIndex] = item;
            _remainingPrerequisites[itemIndex] = item.PrerequisiteCount;
            Interlocked.Increment(ref _configuredItemCount);
        }
    }

    internal void SetDependent(long generation, int dependentSlot, int dependentItemIndex)
    {
        lock (_leaseSync)
        {
            ValidateBuildingLease(generation);
            if ((uint)dependentSlot >= (uint)_dependentCount)
                throw new ArgumentOutOfRangeException(nameof(dependentSlot));
            if ((uint)dependentItemIndex >= (uint)_itemCount)
                throw new ArgumentOutOfRangeException(nameof(dependentItemIndex));
            if (Interlocked.CompareExchange(ref _dependents[dependentSlot], dependentItemIndex, -1) != -1)
                throw new InvalidOperationException($"Dependent slot {dependentSlot} was already configured.");

            Interlocked.Increment(ref _configuredDependentCount);
        }
    }

    internal void SealAndQueue(long generation, IRenderWorkExecutor executor, int frameSlot, bool requestInline)
    {
        lock (_leaseSync)
        {
            ArgumentNullException.ThrowIfNull(executor);
            ValidateBuildingLease(generation);
            if ((uint)frameSlot >= (uint)_domain.BackendAttachments.FrameSlotCount)
                throw new ArgumentOutOfRangeException(nameof(frameSlot));
            if (_configuredItemCount != _itemCount)
                throw new InvalidOperationException(
                    $"Only {_configuredItemCount} of {_itemCount} render-work items were configured.");
            if (_configuredDependentCount != _dependentCount)
                throw new InvalidOperationException(
                    $"Only {_configuredDependentCount} of {_dependentCount} dependency slots were configured.");

            ValidateGraph();
            RenderWorkDispatchProfile dispatchProfile = BuildDispatchProfile();
            bool migrateWork = _domain.ShouldMigrateWork(dispatchProfile, requestInline);
            if (!migrateWork)
                PinMigratableItemsToLaneZero();

            _executor = executor;
            _frameSlot = frameSlot;
            _inlineOnly = !dispatchProfile.RequiresBackgroundLane && !migrateWork;
            Volatile.Write(ref _remainingItems, _itemCount);

            if (Interlocked.CompareExchange(ref _state, StateRunning, StateBuilding) != StateBuilding)
                throw new InvalidOperationException("The render-work batch changed state while it was being sealed.");

            _domain.OnBatchSubmitted(_itemCount);
            if (_itemCount == 0)
            {
                Volatile.Write(ref _state, StateCompleted);
                _domain.OnBatchCompleted();
                _terminalEvent.Set();
                TryReturnReleasedLeaseUnderLock();
                return;
            }

            for (int itemIndex = 0; itemIndex < _itemCount; itemIndex++)
            {
                if (_remainingPrerequisites[itemIndex] == 0)
                    _domain.QueueReadyItem(this, generation, itemIndex);
            }

            _domain.WakeForSubmittedBatch(_inlineOnly);
        }
    }

    internal bool TryBeginClaim(long generation, int itemIndex)
    {
        if (generation != Generation || Volatile.Read(ref _state) != StateRunning)
            return false;
        if ((uint)itemIndex >= (uint)_itemCount)
            return false;
        if (Interlocked.CompareExchange(ref _itemStates[itemIndex], 1, 0) != 0)
            return false;

        Interlocked.Increment(ref _activeClaims);
        if (generation == Generation && Volatile.Read(ref _state) == StateRunning)
            return true;

        EndClaim();
        return false;
    }

    internal bool TryAddQueuedReference(long generation)
    {
        if (generation != Generation || Volatile.Read(ref _state) != StateRunning)
            return false;

        Interlocked.Increment(ref _queuedReferences);
        if (generation == Generation && Volatile.Read(ref _state) == StateRunning)
            return true;

        ReleaseQueuedReference(generation);
        return false;
    }

    internal void ReleaseQueuedReference(long generation)
    {
        if (generation != Generation)
            return;

        Interlocked.Decrement(ref _queuedReferences);
        TrySignalTerminal();
    }

    internal RenderWorkItem GetItem(int itemIndex)
        => _items[itemIndex];

    internal void CompleteClaim(long generation, int itemIndex)
    {
        if (generation == Generation && Volatile.Read(ref _state) == StateRunning)
        {
            Volatile.Write(ref _itemStates[itemIndex], 2);
            RenderWorkItem item = _items[itemIndex];
            int end = checked(item.DependentStart + item.DependentCount);
            for (int dependentSlot = item.DependentStart; dependentSlot < end; dependentSlot++)
            {
                int dependentItemIndex = _dependents[dependentSlot];
                if (Interlocked.Decrement(ref _remainingPrerequisites[dependentItemIndex]) == 0)
                    _domain.QueueReadyItem(this, generation, dependentItemIndex);
            }

            if (Interlocked.Decrement(ref _remainingItems) == 0 &&
                Interlocked.CompareExchange(ref _state, StateCompleted, StateRunning) == StateRunning)
            {
                _domain.OnBatchCompleted();
            }
        }

        EndClaim();
    }

    internal void FaultClaim(long generation, int itemIndex, int laneId, Exception exception)
    {
        if (generation == Generation)
            FaultCore(itemIndex, laneId, exception);

        EndClaim();
    }

    internal void FaultScheduling(long generation, int itemIndex, Exception exception)
    {
        lock (_leaseSync)
        {
            if (generation == Generation)
                FaultCore(itemIndex, laneId: 0, exception);
        }
    }

    internal void FinalizeFaultQuarantine(long generation)
    {
        IRenderWorkExecutor? executor;
        RenderWorkBatchFaultContext context;
        lock (_leaseSync)
        {
            if (generation != Generation || Volatile.Read(ref _state) != StateFaulted || !_terminalEvent.IsSet)
                return;
            if (!_domain.IsLaneZeroOwnerThread)
                return;
            if (Volatile.Read(ref _quarantineFinalized) == 2)
            {
                TryReturnReleasedLeaseUnderLock();
                return;
            }
            if (Interlocked.CompareExchange(ref _quarantineFinalized, 1, 0) != 0)
                return;

            Exception exception = _fault ?? new InvalidOperationException("Render-work batch faulted without an exception.");
            executor = _executor;
            context = new RenderWorkBatchFaultContext(
                generation,
                _frameSlot,
                _faultingItemIndex,
                _faultingLaneId,
                exception);
        }

        try
        {
            executor?.QuarantineFaultedBatch(context);
        }
        catch (Exception quarantineException)
        {
            _domain.OnBatchQuarantineFailed();
            lock (_leaseSync)
            {
                _fault = new AggregateException(
                    "Render-work fault quarantine failed; partial output ownership remains retained.",
                    context.Exception,
                    quarantineException);
            }

            throw new InvalidOperationException(
                "Render-work executor failed to quarantine partial output. " +
                "The domain is poisoned and the batch will not return to the pool.",
                quarantineException);
        }

        lock (_leaseSync)
        {
            if (generation != Generation || Volatile.Read(ref _state) != StateFaulted ||
                Volatile.Read(ref _quarantineFinalized) != 1)
                return;

            Volatile.Write(ref _quarantineFinalized, 2);
            _domain.OnBatchQuarantined();
            TryReturnReleasedLeaseUnderLock();
        }
    }

    internal RenderWorkBatchResult GetResult(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            int state = Volatile.Read(ref _state);
            return new RenderWorkBatchResult(generation, MapStatus(state), _itemCount, _fault);
        }
    }

    internal void Cancel(long generation)
    {
        lock (_leaseSync)
        {
            ValidateLease(generation);
            CancelUnderLock();
        }
    }

    internal void ReleaseLease(long generation)
    {
        lock (_leaseSync)
        {
            if (generation != Generation || Volatile.Read(ref _leaseReleased) != 0)
                return;

            int state = Volatile.Read(ref _state);
            if (state is StateBuilding or StateRunning)
                CancelUnderLock();

            Volatile.Write(ref _leaseReleased, 1);
            TryReturnReleasedLeaseUnderLock();
        }
    }

    internal void CancelForShutdown()
    {
        lock (_leaseSync)
        {
            int state = Volatile.Read(ref _state);
            if (state is StateBuilding or StateRunning)
                CancelUnderLock();
        }
    }

    internal bool WaitForQuiescence(TimeSpan timeout)
        => _terminalEvent.Wait(timeout);

    internal bool WaitForLeaseReturn(TimeSpan timeout)
        => _leaseReturnedEvent.Wait(timeout);

    internal void DisposeStorage()
    {
        _terminalEvent.Dispose();
        _leaseReturnedEvent.Dispose();
        ArrayPool<RenderWorkItem>.Shared.Return(_items);
        ArrayPool<int>.Shared.Return(_itemStates);
        ArrayPool<int>.Shared.Return(_remainingPrerequisites);
        ArrayPool<int>.Shared.Return(_validationPrerequisites);
        ArrayPool<int>.Shared.Return(_validationQueue);
        ArrayPool<int>.Shared.Return(_dependents);
        _items = [];
        _itemStates = [];
        _remainingPrerequisites = [];
        _validationPrerequisites = [];
        _validationQueue = [];
        _dependents = [];
    }

    private void ValidateGraph()
    {
        if (_itemCount == 0)
        {
            if (_dependentCount != 0)
                throw new InvalidOperationException("An empty render-work batch cannot contain dependency edges.");
            return;
        }

        Array.Clear(_validationPrerequisites, 0, _itemCount);
        for (int itemIndex = 0; itemIndex < _itemCount; itemIndex++)
        {
            RenderWorkItem item = _items[itemIndex];
            if (item.PreferredLane < RenderWorkItem.AnyLane || item.PreferredLane >= _domain.LogicalLaneCount)
            {
                throw new InvalidOperationException(
                    $"Render-work item {itemIndex} requested invalid lane {item.PreferredLane}; " +
                    $"valid lanes are 0..{_domain.LogicalLaneCount - 1} or {RenderWorkItem.AnyLane} for migratable work.");
            }

            int dependentEnd = checked(item.DependentStart + item.DependentCount);
            if (dependentEnd > _dependentCount)
                throw new InvalidOperationException($"Render-work item {itemIndex} has an out-of-range dependent span.");

            for (int dependentSlot = item.DependentStart; dependentSlot < dependentEnd; dependentSlot++)
            {
                int dependentItemIndex = _dependents[dependentSlot];
                if ((uint)dependentItemIndex >= (uint)_itemCount)
                    throw new InvalidOperationException($"Dependency slot {dependentSlot} is not configured.");
                _validationPrerequisites[dependentItemIndex]++;
            }
        }

        int queueHead = 0;
        int queueTail = 0;
        for (int itemIndex = 0; itemIndex < _itemCount; itemIndex++)
        {
            int declared = _items[itemIndex].PrerequisiteCount;
            if (_validationPrerequisites[itemIndex] != declared)
            {
                throw new InvalidOperationException(
                    $"Render-work item {itemIndex} declares {declared} prerequisites but " +
                    $"the dependency table contains {_validationPrerequisites[itemIndex]}.");
            }

            _remainingPrerequisites[itemIndex] = declared;
            if (declared == 0)
                _validationQueue[queueTail++] = itemIndex;
        }

        int visited = 0;
        while (queueHead < queueTail)
        {
            int itemIndex = _validationQueue[queueHead++];
            visited++;
            RenderWorkItem item = _items[itemIndex];
            int dependentEnd = item.DependentStart + item.DependentCount;
            for (int dependentSlot = item.DependentStart; dependentSlot < dependentEnd; dependentSlot++)
            {
                int dependentItemIndex = _dependents[dependentSlot];
                int remaining = --_validationPrerequisites[dependentItemIndex];
                if (remaining == 0)
                    _validationQueue[queueTail++] = dependentItemIndex;
            }
        }

        if (visited != _itemCount)
            throw new InvalidOperationException("The render-work dependency graph contains a cycle.");
    }

    private RenderWorkDispatchProfile BuildDispatchProfile()
    {
        int migratableItemCount = 0;
        int independentMigratableItemCount = 0;
        long independentEstimatedCost = 0;
        int maximumIndependentEstimatedCost = 0;
        int capPinnedItemCount = 0;
        bool requiresBackgroundLane = false;
        int migratableLimit = _domain.MaxMigratableItemCount;

        for (int itemIndex = 0; itemIndex < _itemCount; itemIndex++)
        {
            RenderWorkItem item = _items[itemIndex];
            if (item.PreferredLane > 0)
            {
                requiresBackgroundLane = true;
                continue;
            }
            if (item.PreferredLane != RenderWorkItem.AnyLane)
                continue;

            if (migratableItemCount >= migratableLimit)
            {
                _items[itemIndex] = item with { PreferredLane = 0 };
                capPinnedItemCount++;
                continue;
            }

            migratableItemCount++;
            if (item.PrerequisiteCount != 0)
                continue;

            independentMigratableItemCount++;
            independentEstimatedCost = AddSaturating(independentEstimatedCost, item.EstimatedCost);
            maximumIndependentEstimatedCost = Math.Max(maximumIndependentEstimatedCost, item.EstimatedCost);
        }

        return new RenderWorkDispatchProfile(
            migratableItemCount,
            independentMigratableItemCount,
            independentEstimatedCost,
            maximumIndependentEstimatedCost,
            capPinnedItemCount,
            requiresBackgroundLane);
    }

    private void PinMigratableItemsToLaneZero()
    {
        for (int itemIndex = 0; itemIndex < _itemCount; itemIndex++)
        {
            RenderWorkItem item = _items[itemIndex];
            if (item.PreferredLane == RenderWorkItem.AnyLane)
                _items[itemIndex] = item with { PreferredLane = 0 };
        }
    }

    private static long AddSaturating(long left, int right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private void FaultCore(int faultingItemIndex, int laneId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        bool upgradedCancellation = false;
        int previouslyCanceledItemCount = 0;
        lock (_leaseSync)
        {
            int previousState = Interlocked.CompareExchange(ref _state, StateFaulting, StateRunning);
            if (previousState != StateRunning)
            {
                if (previousState != StateCanceled ||
                    Interlocked.CompareExchange(ref _state, StateFaulting, StateCanceled) != StateCanceled)
                    return;

                upgradedCancellation = true;
                previouslyCanceledItemCount = Volatile.Read(ref _canceledItemCountSnapshot);
            }

            Interlocked.CompareExchange(ref _fault, exception, null);
            _faultingItemIndex = faultingItemIndex;
            _faultingLaneId = laneId;
            Volatile.Write(ref _state, StateFaulted);
        }

        if (upgradedCancellation)
        {
            _domain.OnCanceledBatchFaulted(
                exception is TimeoutException,
                previouslyCanceledItemCount);
        }
        else
            _domain.OnBatchFaulted(exception is TimeoutException);

        _domain.WakeForTerminalDrain();
        TrySignalTerminal();
    }

    private void EndClaim()
    {
        Interlocked.Decrement(ref _activeClaims);
        TrySignalTerminal();
    }

    private void TrySignalTerminal()
    {
        int state = Volatile.Read(ref _state);
        if (state is not StateCompleted and not StateCanceled and not StateFaulted)
            return;
        if (Volatile.Read(ref _activeClaims) != 0)
            return;
        if (Volatile.Read(ref _queuedReferences) != 0)
            return;

        _terminalEvent.Set();
        TryReturnReleasedLease();
    }

    private void CancelUnderLock()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state is StateCompleted or StateCanceled or StateFaulted)
                return;
            if (state is not StateBuilding and not StateRunning)
                return;
            if (Interlocked.CompareExchange(ref _state, StateCanceled, state) != state)
                continue;

            int canceledItemCount = state == StateRunning
                ? Math.Max(0, Volatile.Read(ref _remainingItems))
                : 0;
            Volatile.Write(ref _canceledItemCountSnapshot, canceledItemCount);
            _domain.OnBatchCanceled(
                state == StateRunning,
                canceledItemCount);
            _domain.WakeForTerminalDrain();
            TrySignalTerminal();
            return;
        }
    }

    private void TryReturnReleasedLease()
    {
        lock (_leaseSync)
            TryReturnReleasedLeaseUnderLock();
    }

    private void TryReturnReleasedLeaseUnderLock()
    {
        if (Volatile.Read(ref _leaseReleased) == 0 || !_terminalEvent.IsSet)
            return;

        int state = Volatile.Read(ref _state);
        if (state == StateFaulted && Volatile.Read(ref _quarantineFinalized) != 2)
            return;
        if (state is not StateCompleted and not StateCanceled and not StateFaulted)
            return;
        if (Interlocked.CompareExchange(ref _state, StateReturning, state) != state)
            return;

        _executor = null;
        _fault = null;
        _pool.Return(this);
        _leaseReturnedEvent.Set();
        Volatile.Write(ref _state, StateIdle);
    }

    private void ValidateBuildingLease(long generation)
    {
        ValidateLease(generation);
        if (Volatile.Read(ref _state) != StateBuilding)
            throw new InvalidOperationException("The render-work batch is already sealed or terminal.");
    }

    private void ValidateLease(long generation)
    {
        int state = Volatile.Read(ref _state);
        if (generation != Generation || Volatile.Read(ref _leaseReleased) != 0 ||
            state is StateIdle or StateRenting or StateReturning)
            throw new ObjectDisposedException(nameof(RenderWorkBatchLease), "The pooled batch lease is stale.");
    }

    private static RenderWorkBatchStatus MapStatus(int state)
        => state switch
        {
            StateRunning => RenderWorkBatchStatus.Running,
            StateCompleted => RenderWorkBatchStatus.Completed,
            StateCanceled => RenderWorkBatchStatus.Canceled,
            StateFaulted => RenderWorkBatchStatus.Faulted,
            _ => RenderWorkBatchStatus.Building,
        };

    private void EnsureItemCapacity(int required)
    {
        if (required < 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (_items.Length >= Math.Max(1, required))
            return;

        int capacity = Math.Max(1, required);
        ReplacePooledArray(ref _items, capacity);
        ReplacePooledArray(ref _itemStates, capacity);
        ReplacePooledArray(ref _remainingPrerequisites, capacity);
        ReplacePooledArray(ref _validationPrerequisites, capacity);
        ReplacePooledArray(ref _validationQueue, capacity);
    }

    private void EnsureDependentCapacity(int required)
    {
        if (required < 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (_dependents.Length >= Math.Max(1, required))
            return;

        ReplacePooledArray(ref _dependents, Math.Max(1, required));
    }

    private static void ReplacePooledArray<T>(ref T[] array, int capacity)
    {
        T[] replacement = ArrayPool<T>.Shared.Rent(capacity);
        ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        array = replacement;
    }
}
