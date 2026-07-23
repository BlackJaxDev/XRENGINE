using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuWorkSchedulerTests
{
    [Test]
    public void PersistentWorkersConsumeEveryHandleExactlyOnce()
    {
        using var scheduler = new PhysicsChainCpuWorkScheduler(3, 128);
        var executor = new CountingExecutor(128);
        PhysicsChainArenaHandle[] handles = CreateHandles(128);

        scheduler.Execute(executor, handles, batchSize: 7).ShouldBeTrue();

        executor.Total.ShouldBe(handles.Length);
        executor.Duplicates.ShouldBe(0);
        scheduler.GetSnapshot().CompletedRangeCount.ShouldBe((handles.Length + 6) / 7);
    }

    [Test]
    public void WarmSteadyExecutionAllocatesNoManagedMemoryOnCallingThread()
    {
        using var scheduler = new PhysicsChainCpuWorkScheduler(2, 64);
        var executor = new CountingExecutor(64);
        PhysicsChainArenaHandle[] handles = CreateHandles(64);
        scheduler.Execute(executor, handles, batchSize: 8).ShouldBeTrue();
        for (int warmup = 0; warmup < 10; ++warmup)
            scheduler.Execute(executor, handles, batchSize: 8);

        executor.Reset();
        bool succeeded = true;
        long minimumAllocated = long.MaxValue;
        for (int sample = 0; sample < 3; ++sample)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 100; ++iteration)
                succeeded &= scheduler.Execute(executor, handles, batchSize: 8);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }

        succeeded.ShouldBeTrue();
        minimumAllocated.ShouldBe(0L);
    }

    [Test]
    public void DeterministicModeUsesStableCoarseRanges()
    {
        using var scheduler = new PhysicsChainCpuWorkScheduler(2, 16);
        var executor = new OrderingExecutor();
        PhysicsChainArenaHandle[] handles = CreateHandles(10);

        scheduler.Execute(executor, handles, batchSize: 3, deterministic: true).ShouldBeTrue();

        executor.Observed.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        scheduler.GetSnapshot().Deterministic.ShouldBeTrue();
    }

    private static PhysicsChainArenaHandle[] CreateHandles(int count)
    {
        var handles = new PhysicsChainArenaHandle[count];
        for (int index = 0; index < count; ++index)
            handles[index] = new PhysicsChainArenaHandle(index, 1u);
        return handles;
    }

    private sealed class CountingExecutor(int capacity) : IPhysicsChainCpuBatchExecutor
    {
        private readonly int[] _seen = new int[capacity];
        private int _total;
        private int _duplicates;

        public int Total => _total;
        public int Duplicates => _duplicates;

        public bool TryStepBatch(ReadOnlySpan<PhysicsChainArenaHandle> handles)
        {
            for (int index = 0; index < handles.Length; ++index)
            {
                int slot = handles[index].Slot;
                if (Interlocked.Increment(ref _seen[slot]) != 1)
                    Interlocked.Increment(ref _duplicates);
                Interlocked.Increment(ref _total);
            }
            return true;
        }

        public void Reset()
        {
            Array.Clear(_seen);
            _total = 0;
            _duplicates = 0;
        }
    }

    private sealed class OrderingExecutor : IPhysicsChainCpuBatchExecutor
    {
        public List<int> Observed { get; } = [];

        public bool TryStepBatch(ReadOnlySpan<PhysicsChainArenaHandle> handles)
        {
            for (int index = 0; index < handles.Length; ++index)
                Observed.Add(handles[index].Slot + 1);
            return true;
        }
    }
}
