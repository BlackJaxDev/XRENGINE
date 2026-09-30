using System.Threading;

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
    private PhysicsChainArenaHandle[] _handles;
    private IPhysicsChainCpuBatchExecutor? _executor;
    private int _handleCount;
    private int _batchSize;
    private int _nextIndex;
    private int _completedRangeCount;
    private int _failedRangeCount;
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

        try
        {
            EnsureCapacity(handles.Length);
            handles.CopyTo(_handles);
            _executor = executor;
            _handleCount = handles.Length;
            _batchSize = batchSize;
            _nextIndex = 0;
            _completedRangeCount = 0;
            _failedRangeCount = 0;
            _deterministic = deterministic;

            if (deterministic || _threads.Length == 0 || handles.Length <= batchSize)
            {
                ProcessRanges();
            }
            else
            {
                _completion.Reset(_threads.Length);
                for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
                    _workSignals[workerIndex].Set();
                ProcessRanges();
                _completion.Wait();
            }

            ++_executionCount;
            return _failedRangeCount == 0;
        }
        finally
        {
            _executor = null;
            Volatile.Write(ref _lifecycleState, 0);
        }
    }

    public PhysicsChainCpuWorkSchedulerSnapshot GetSnapshot()
        => new(
            _threads.Length,
            _handles.Length,
            _batchSize,
            _handleCount,
            _completedRangeCount,
            _failedRangeCount,
            _executionCount,
            _capacityGrowthCount,
            _deterministic);

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
            ProcessRanges();
            _completion.Signal();
        }
    }

    private void ProcessRanges()
    {
        IPhysicsChainCpuBatchExecutor executor = _executor!;
        while (true)
        {
            int start = Interlocked.Add(ref _nextIndex, _batchSize) - _batchSize;
            if (start >= _handleCount)
                return;
            int count = Math.Min(_batchSize, _handleCount - start);
            if (!executor.TryStepBatch(_handles.AsSpan(start, count)))
                Interlocked.Increment(ref _failedRangeCount);
            Interlocked.Increment(ref _completedRangeCount);
        }
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
