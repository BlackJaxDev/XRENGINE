using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns cache-line-isolated mutable worker state for one dispatched batch.</summary>
internal sealed unsafe class VulkanCommandChainWorkerLocalStateBlocks : IDisposable
{
    private const int CacheLineBytes = 64;
    private byte* _base;
    private int _capacity;

    public int BaseAlignmentRemainder => _base is null ? 0 : (int)((nuint)_base % CacheLineBytes);
    public int Stride => CacheLineBytes;

    public void Reset(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        EnsureCapacity(workerCount);
        new Span<byte>(_base, checked(workerCount * CacheLineBytes)).Clear();
    }

    public void Begin(int workerIndex, long startTimestamp, long dispatchTimestamp)
    {
        ref VulkanCommandChainWorkerLocalState state = ref Get(workerIndex);
        state.StartTimestamp = startTimestamp;
        state.QueueDelayTimestamp = startTimestamp - dispatchTimestamp;
        state.Started = 1;
    }

    public void Complete(int workerIndex, long completionTimestamp)
    {
        ref VulkanCommandChainWorkerLocalState state = ref Get(workerIndex);
        state.CompletionTimestamp = completionTimestamp;
        state.RecordElapsedTicks = completionTimestamp - state.StartTimestamp;
        state.Completed = 1;
    }

    public void Merge(int workerCount, out CommandChainWorkerTiming timing)
    {
        int started = 0;
        int completed = 0;
        long firstStart = long.MaxValue;
        long lastCompletion = 0;
        long recordTicks = 0;
        long maxQueueDelay = 0;
        int peakConcurrentWorkers = 0;
        for (int index = 0; index < workerCount; index++)
        {
            ref VulkanCommandChainWorkerLocalState state = ref Get(index);
            started += state.Started;
            completed += state.Completed;
            if (state.Started != 0)
            {
                firstStart = Math.Min(firstStart, state.StartTimestamp);
                maxQueueDelay = Math.Max(maxQueueDelay, state.QueueDelayTimestamp);
            }
            if (state.Completed != 0)
                lastCompletion = Math.Max(lastCompletion, state.CompletionTimestamp);
            recordTicks += state.RecordElapsedTicks;
            if (state.Started == 0)
                continue;

            int concurrentWorkers = 0;
            for (int candidateIndex = 0; candidateIndex < workerCount; candidateIndex++)
            {
                ref VulkanCommandChainWorkerLocalState candidate = ref Get(candidateIndex);
                if (candidate.Started != 0 &&
                    candidate.StartTimestamp <= state.StartTimestamp &&
                    candidate.CompletionTimestamp >= state.StartTimestamp)
                {
                    concurrentWorkers++;
                }
            }
            peakConcurrentWorkers = Math.Max(peakConcurrentWorkers, concurrentWorkers);
        }

        TimeSpan activeSpan = firstStart == long.MaxValue || lastCompletion <= firstStart
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(firstStart, lastCompletion);
        TimeSpan recordTime = StopwatchTicksToTimeSpan(recordTicks);
        timing = new CommandChainWorkerTiming(
            0, started, completed, peakConcurrentWorkers, StopwatchTicksToTimeSpan(maxQueueDelay),
            recordTime, activeSpan, recordTime > activeSpan ? recordTime - activeSpan : TimeSpan.Zero, TimeSpan.Zero);
    }

    private void EnsureCapacity(int workerCount)
    {
        if (_capacity >= workerCount)
            return;

        DisposeAllocation();
        int capacity = Math.Max(workerCount, 1);
        nuint bytes = checked((nuint)capacity * CacheLineBytes);
        byte* allocation = (byte*)NativeMemory.AlignedAlloc(bytes, CacheLineBytes);
        if (allocation is null)
            throw new OutOfMemoryException("Unable to allocate Vulkan command-chain worker-local state blocks.");
        _base = allocation;
        _capacity = capacity;
    }

    private ref VulkanCommandChainWorkerLocalState Get(int workerIndex)
    {
        if ((uint)workerIndex >= (uint)_capacity || _base is null)
            throw new ArgumentOutOfRangeException(nameof(workerIndex));
        return ref Unsafe.AsRef<VulkanCommandChainWorkerLocalState>(_base + (workerIndex * CacheLineBytes));
    }

    public void Dispose()
    {
        DisposeAllocation();
        GC.SuppressFinalize(this);
    }

    ~VulkanCommandChainWorkerLocalStateBlocks() => DisposeAllocation();

    private void DisposeAllocation()
    {
        if (_base is null)
            return;
        NativeMemory.AlignedFree(_base);
        _base = null;
        _capacity = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanCommandChainWorkerLocalState
    {
        public long StartTimestamp;
        public long CompletionTimestamp;
        public long RecordElapsedTicks;
        public long QueueDelayTimestamp;
        public int Started;
        public int Completed;
    }

    private static TimeSpan StopwatchTicksToTimeSpan(long ticks)
        => ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
}
