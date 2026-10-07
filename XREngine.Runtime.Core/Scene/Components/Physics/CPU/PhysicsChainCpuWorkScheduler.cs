using System.Threading;
using System.Runtime.ExceptionServices;

namespace XREngine.Components;

/// <summary>
/// Persistent coarse-range CPU scheduler. Worker threads and synchronization
/// primitives are created once; steady execution reuses a high-water handle
/// buffer and performs no managed allocations.
/// </summary>
public sealed class PhysicsChainCpuWorkScheduler : IDisposable
{
    private readonly Thread[] _threads;
    private readonly AutoResetEvent[] _workSignals;
    private readonly CountdownEvent _completion;
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
        _threads = new Thread[workerCount];
        _workSignals = new AutoResetEvent[workerCount];
        _completion = new CountdownEvent(workerCount);
        // Slot zero is the shared claim. Each worker and the caller own one
        // separate completion slot after it.
        _workerCounters = new PhysicsChainCpuWorkerCounters(checked(workerCount + 2));
        _handles = new PhysicsChainArenaHandle[initialHandleCapacity];

        for (int workerIndex = 0; workerIndex < workerCount; ++workerIndex)
        {
            var signal = new AutoResetEvent(false);
            _workSignals[workerIndex] = signal;
            var thread = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = $"PhysicsChainCpu-{workerIndex}",
            };
            _threads[workerIndex] = thread;
            thread.Start(workerIndex);
        }
    }

    public int WorkerCount => _threads.Length;

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

        bool workersDispatched = false;
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

            if (deterministic || _threads.Length == 0 || handles.Length <= batchSize)
            {
                ProcessRanges(_workerCounters.SlotCount - 1);
            }
            else
            {
                _completion.Reset(_threads.Length);
                workersDispatched = true;
                for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
                    _workSignals[workerIndex].Set();
                ProcessRanges(_workerCounters.SlotCount - 1);
                _completion.Wait();
                workersDispatched = false;
            }

            ++_executionCount;
            CollectRangeCounts(out _, out int failedRangeCount);
            _rangeFault?.Throw();
            return failedRangeCount == 0;
        }
        finally
        {
            // Dispatched workers must stop using the executor, handles, and
            // counter bank before another schedule can reuse them.
            if (workersDispatched)
                _completion.Wait();
            _executor = null;
            Volatile.Write(ref _lifecycleState, 0);
        }
    }

    public PhysicsChainCpuWorkSchedulerSnapshot GetSnapshot()
    {
        CollectRangeCounts(out int completedRangeCount, out int failedRangeCount);
        return new(
            _threads.Length,
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

        for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
            _workSignals[workerIndex].Set();
        for (int workerIndex = 0; workerIndex < _threads.Length; ++workerIndex)
            _threads[workerIndex].Join();
        for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
            _workSignals[workerIndex].Dispose();
        _completion.Dispose();
    }

    private void WorkerMain(object? state)
    {
        int workerIndex = (int)state!;
        AutoResetEvent signal = _workSignals[workerIndex];
        while (true)
        {
            signal.WaitOne();
            if (Volatile.Read(ref _lifecycleState) == 2)
                return;
            try
            {
                ProcessRanges(workerIndex + 1);
            }
            catch (Exception ex)
            {
                RecordRangeFault(ex, workerIndex + 1);
            }
            finally
            {
                _completion.Signal();
            }
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
