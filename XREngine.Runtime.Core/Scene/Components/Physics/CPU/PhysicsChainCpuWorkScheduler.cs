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
        if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
            workerCount = 0;
        _handles = new PhysicsChainArenaHandle[initialHandleCapacity];
        if (workerCount > 0)
        {
            IPhysicsChainCpuWorkerGroup group = PhysicsChainCpuWorkerGroupServices.Required.Create(workerCount, ProcessRanges)
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
            _nextIndex = 0;
            _completedRangeCount = 0;
            _failedRangeCount = 0;
            _deterministic = deterministic;

            if (deterministic || _workerGroup is null || handles.Length <= batchSize
                || OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
            {
                ProcessRanges();
            }
            else
            {
                _workerGroup.SynchronousRun();
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
            WorkerCount,
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

        _workerGroup?.Dispose();
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
