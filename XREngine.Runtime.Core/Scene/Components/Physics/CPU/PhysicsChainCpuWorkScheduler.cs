using System.Runtime.ExceptionServices;
using System.Threading;
using XREngine.Execution;

namespace XREngine.Components;

/// <summary>
/// Persistent coarse-range CPU scheduler. A host worker group runs the range
/// callback. Steady execution reuses a high-water handle buffer and performs
/// no managed allocations. Browser and caller-thread hosts run ranges inline.
/// </summary>
public sealed class PhysicsChainCpuWorkScheduler : IDisposable
{
    private readonly IPhysicsChainCpuWorkerGroup? _workerGroup;
    private readonly int _workerCount;
    private readonly PhysicsChainCpuWorkerCounters _workerCounters;
    private PhysicsChainArenaHandle[] _handles;
    private IPhysicsChainCpuBatchExecutor? _executor;
    private ExceptionDispatchInfo? _rangeFault;
    private int _handleCount;
    private int _batchSize;
    private int _lifecycleState;
    private bool _deterministic;
    private long _executionCount;
    private long _capacityGrowthCount;

    public PhysicsChainCpuWorkScheduler(int workerCount, int initialHandleCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialHandleCapacity, 1);
        if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
            workerCount = 0;
        // Slot zero is the shared claim. Each worker and the caller own one
        // separate completion slot after it.
        _workerCounters = new PhysicsChainCpuWorkerCounters(checked(workerCount + 2));
        _handles = new PhysicsChainArenaHandle[initialHandleCapacity];
        if (workerCount > 0)
        {
            IPhysicsChainCpuWorkerGroup group = PhysicsChainCpuWorkerGroupServices.Required.Create(workerCount, ProcessWorkerRanges)
                ?? throw new InvalidOperationException("PhysicsChainCpuWorkerGroup.InvalidGroup: the host returned no worker group.");
            try
            {
                if (group.FixedWorkerCount != workerCount)
                    throw new InvalidOperationException("PhysicsChainCpuWorkerGroup.InvalidWorkerCount: the host returned a group with a different worker count.");
            }
            catch
            {
                group.Dispose();
                throw;
            }
            _workerGroup = group;
            _workerCount = workerCount;
        }
    }

    public int WorkerCount => _workerCount;

    public bool Execute(
        IPhysicsChainCpuBatchExecutor executor,
        ReadOnlySpan<PhysicsChainArenaHandle> handles,
        int batchSize = 32,
        bool deterministic = false)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        int previousState = Interlocked.CompareExchange(ref _lifecycleState, 1, 0);
        if (previousState == 2)
            throw new ObjectDisposedException(nameof(PhysicsChainCpuWorkScheduler));
        if (previousState != 0)
            throw new InvalidOperationException("A physics-chain CPU schedule is already executing.");

        try
        {
            EnsureCapacity(handles.Length);
            handles.CopyTo(_handles);
            _executor = executor;
            _handleCount = handles.Length;
            _batchSize = batchSize;
            _workerCounters.Clear();
            _rangeFault = null;
            _deterministic = deterministic;

            if (deterministic || _workerGroup is null || handles.Length <= batchSize
                || OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
            {
                ProcessWorkerRanges(_workerCount);
            }
            else
            {
                // The group returns only after every worker signals completion, so
                // no worker uses the executor, handles, or counters after this call.
                _workerGroup.SynchronousRun();
            }

            ++_executionCount;
            CollectRangeCounts(out _, out int failedRangeCount);
            _rangeFault?.Throw();
            return failedRangeCount == 0;
        }
        finally
        {
            _executor = null;
            Volatile.Write(ref _lifecycleState, 0);
        }
    }

    public PhysicsChainCpuWorkSchedulerSnapshot GetSnapshot()
    {
        CollectRangeCounts(out int completedRangeCount, out int failedRangeCount);
        return new(
            WorkerCount,
            _handles.Length,
            _batchSize,
            _handleCount,
            completedRangeCount,
            failedRangeCount,
            _executionCount,
            _capacityGrowthCount,
            _deterministic);
    }

    private void CollectRangeCounts(out int completedRangeCount, out int failedRangeCount)
    {
        completedRangeCount = 0;
        failedRangeCount = 0;
        for (int counterIndex = 1; counterIndex < _workerCounters.SlotCount; ++counterIndex)
        {
            completedRangeCount += Volatile.Read(ref _workerCounters.CompletedRangeCount(counterIndex));
            failedRangeCount += Volatile.Read(ref _workerCounters.FailedRangeCount(counterIndex));
        }
    }

    public void Dispose()
    {
        int previousState = Interlocked.CompareExchange(ref _lifecycleState, 2, 0);
        if (previousState == 2)
            return;
        if (previousState == 1)
            throw new InvalidOperationException("The physics-chain scheduler cannot be disposed while executing.");

        _workerGroup?.Dispose();
    }

    /// <summary>
    /// Runs ranges for one worker index and records any fault. Workers use 0 to
    /// <see cref="WorkerCount"/> - 1, and the calling thread uses <see cref="WorkerCount"/>.
    /// Each index owns one counter slot.
    /// </summary>
    private void ProcessWorkerRanges(int workerIndex)
    {
        int counterSlotIndex = workerIndex + 1;
        try
        {
            ProcessRanges(counterSlotIndex);
        }
        catch (Exception ex)
        {
            RecordRangeFault(ex, counterSlotIndex);
        }
    }

    private void ProcessRanges(int counterSlotIndex)
    {
        IPhysicsChainCpuBatchExecutor executor = _executor!;
        while (true)
        {
            int start = Interlocked.Add(ref _workerCounters.CompletedRangeCount(0), _batchSize) - _batchSize;
            if (start >= _handleCount)
                return;
            int count = Math.Min(_batchSize, _handleCount - start);
            try
            {
                if (!executor.TryStepBatch(_handles.AsSpan(start, count)))
                    Interlocked.Increment(ref _workerCounters.FailedRangeCount(counterSlotIndex));
            }
            catch (Exception ex)
            {
                RecordRangeFault(ex, counterSlotIndex);
            }
            finally
            {
                Interlocked.Increment(ref _workerCounters.CompletedRangeCount(counterSlotIndex));
            }
        }
    }

    private void RecordRangeFault(Exception exception, int counterSlotIndex)
    {
        Interlocked.Increment(ref _workerCounters.FailedRangeCount(counterSlotIndex));
        Interlocked.CompareExchange(ref _rangeFault, ExceptionDispatchInfo.Capture(exception), null);
    }

    private void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= _handles.Length)
            return;

        int capacity = _handles.Length;
        while (capacity < requiredCapacity)
            capacity = checked(capacity <= int.MaxValue / 2 ? capacity * 2 : requiredCapacity);
        Array.Resize(ref _handles, capacity);
        ++_capacityGrowthCount;
    }
}
