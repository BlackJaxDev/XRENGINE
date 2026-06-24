using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _commandChainRecordingWorkersLock = new();
    private CommandChainRecordingWorkerState[]? _commandChainRecordingWorkers;
    private CancellationTokenSource? _commandChainRecordingWorkerCancellation;
    private int _commandChainRecordingWorkerGeneration;

    private readonly record struct CommandChainWorkerTiming(TimeSpan WorkerRecordTime, TimeSpan WaitForWorkersTime);

    private sealed class CommandChainRecordingWorkerState(int workerIndex)
    {
        public int WorkerIndex { get; } = workerIndex;
        public CommandPool GraphicsCommandPool;
        public CommandPool ComputeCommandPool;
        public readonly List<CommandChainKey> ChainScratch = new(128);
        public CommandBufferBindState BindState;
        public ulong LastFrameId;

        public void Reset(ulong frameId)
        {
            LastFrameId = frameId;
            ChainScratch.Clear();
            BindState = default;
        }
    }

    internal static int ResolveCommandChainRecordingWorkerCount(
        int independentChainCount,
        int processorCount,
        bool singleThread,
        bool parallelDisabled)
    {
        if (singleThread || parallelDisabled || independentChainCount <= 1)
            return 1;

        int usableProcessors = Math.Max(1, processorCount - 1);
        return Math.Clamp(independentChainCount, 1, Math.Min(usableProcessors, 8));
    }

    private CommandChainWorkerTiming DispatchCommandChainRecordingWorkers(CommandChainSchedule? schedule)
    {
        if (schedule is null || schedule.Groups.Length == 0)
            return default;

        int independentChainCount = CountCommandChainWorkerJobs(schedule);
        int workerCount = ResolveCommandChainRecordingWorkerCount(
            independentChainCount,
            Environment.ProcessorCount,
            CommandChainsSingleThread,
            ParallelCommandChainRecordingDisabled);
        if (workerCount <= 1)
            return default;

        CommandChainRecordingWorkerState[] workers = EnsureCommandChainRecordingWorkers(workerCount);
        CancellationToken token = ResetCommandChainRecordingWorkerCancellation();
        long dispatchStart = Stopwatch.GetTimestamp();
        Task[] tasks = new Task[workerCount];
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            int capturedWorkerIndex = workerIndex;
            tasks[workerIndex] = Task.Run(
                () => RunCommandChainRecordingWorker(schedule, workers[capturedWorkerIndex], workerCount, token),
                token);
        }

        long waitStart = Stopwatch.GetTimestamp();
        Task.WaitAll(tasks);
        return new CommandChainWorkerTiming(
            Stopwatch.GetElapsedTime(dispatchStart),
            Stopwatch.GetElapsedTime(waitStart));
    }

    private static int CountCommandChainWorkerJobs(CommandChainSchedule schedule)
    {
        int count = 0;
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int i = 0; i < groups.Length; i++)
            count += groups[i].ChainKeys.Length;
        return count;
    }

    private void RunCommandChainRecordingWorker(
        CommandChainSchedule schedule,
        CommandChainRecordingWorkerState worker,
        int workerCount,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        worker.Reset(VulkanFrameCounter);

        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = worker.WorkerIndex; groupIndex < groups.Length; groupIndex += workerCount)
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<CommandChainKey> keys = groups[groupIndex].ChainKeys.Span;
            worker.ChainScratch.EnsureCapacity(worker.ChainScratch.Count + keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
                worker.ChainScratch.Add(keys[keyIndex]);
        }
    }

    private CommandChainRecordingWorkerState[] EnsureCommandChainRecordingWorkers(int workerCount)
    {
        lock (_commandChainRecordingWorkersLock)
        {
            if (_commandChainRecordingWorkers is { Length: var existingCount } && existingCount >= workerCount)
                return _commandChainRecordingWorkers;

            DestroyCommandChainRecordingWorkersLocked();

            CommandChainRecordingWorkerState[] workers = new CommandChainRecordingWorkerState[workerCount];
            var queueFamilyIndices = FamilyQueueIndices;
            uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            uint computeFamily = queueFamilyIndices.ComputeFamilyIndex ?? graphicsFamily;
            for (int i = 0; i < workers.Length; i++)
            {
                CommandChainRecordingWorkerState worker = new(i)
                {
                    GraphicsCommandPool = CreateCommandPoolForFamily(graphicsFamily),
                };
                worker.ComputeCommandPool = computeFamily == graphicsFamily
                    ? worker.GraphicsCommandPool
                    : CreateCommandPoolForFamily(computeFamily);
                workers[i] = worker;
            }

            _commandChainRecordingWorkers = workers;
            return workers;
        }
    }

    private CancellationToken ResetCommandChainRecordingWorkerCancellation()
    {
        lock (_commandChainRecordingWorkersLock)
        {
            _commandChainRecordingWorkerCancellation?.Cancel();
            _commandChainRecordingWorkerCancellation?.Dispose();
            _commandChainRecordingWorkerCancellation = new CancellationTokenSource();
            unchecked
            {
                _commandChainRecordingWorkerGeneration++;
            }
            return _commandChainRecordingWorkerCancellation.Token;
        }
    }

    private void CancelCommandChainRecordingWorkers()
    {
        lock (_commandChainRecordingWorkersLock)
        {
            _commandChainRecordingWorkerCancellation?.Cancel();
            unchecked
            {
                _commandChainRecordingWorkerGeneration++;
            }
        }
    }

    private void DestroyCommandChainRecordingWorkers()
    {
        lock (_commandChainRecordingWorkersLock)
            DestroyCommandChainRecordingWorkersLocked();
    }

    private void DestroyCommandChainRecordingWorkersLocked()
    {
        _commandChainRecordingWorkerCancellation?.Cancel();
        _commandChainRecordingWorkerCancellation?.Dispose();
        _commandChainRecordingWorkerCancellation = null;

        if (_commandChainRecordingWorkers is null)
            return;

        HashSet<ulong> destroyed = [];
        for (int i = 0; i < _commandChainRecordingWorkers.Length; i++)
        {
            CommandChainRecordingWorkerState worker = _commandChainRecordingWorkers[i];
            DestroyWorkerCommandPool(worker.GraphicsCommandPool, destroyed);
            DestroyWorkerCommandPool(worker.ComputeCommandPool, destroyed);
            worker.GraphicsCommandPool = default;
            worker.ComputeCommandPool = default;
            worker.ChainScratch.Clear();
            worker.BindState = default;
        }

        _commandChainRecordingWorkers = null;
    }

    private void DestroyWorkerCommandPool(CommandPool pool, HashSet<ulong> destroyed)
    {
        if (pool.Handle == 0 || !destroyed.Add(pool.Handle))
            return;

        if (!_deviceLost)
            Api!.DestroyCommandPool(device, pool, null);
    }
}
