using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    // Worker recording remains quarantined after renderer-family affinity and
    // planner-state serialization still reproduced validation-clean submit-time
    // device loss. Command chains continue through the serial secondary recorder
    // until workers consume immutable recording snapshots only.
    private const bool ParallelCommandChainWorkerRecordingSafe = false;
    private readonly object _commandChainRecordingWorkersLock = new();
    private readonly ManualResetEventSlim _commandChainRecordingWorkersIdle = new(initialState: true);
    private readonly CountdownEvent _commandChainRecordingWorkerCountdown = new(initialCount: 1);
    private readonly CommandChainRecordingBatch _commandChainRecordingBatch = new();
    private CommandChainRecordingWorkerState[]? _commandChainRecordingWorkers;
    private int _commandChainRecordingWorkerGeneration;
    private int _activeCommandChainRecordingWorkerCount;

    private readonly record struct CommandChainWorkerTiming(TimeSpan WorkerRecordTime, TimeSpan WaitForWorkersTime);

    private sealed class CommandChainRecordingBatch
    {
        public FrameOp[] Ops = [];
        public CommandChain[] Chains = [];
        public CommandBuffer[] SecondaryBuffers = [];
        public int[] RecordJobChainIndices = [];
        public int[] RecordJobWorkerIndices = [];
        public int[] UniformSlots = [];
        public int StartIndex;
        public int JobCount;
        public int PassIndex;
        public int FrameSlot;
        public uint ActiveWorkerMask;
        public bool DynamicRendering;
        public RenderPass RenderPass;
        public Framebuffer Framebuffer;
        public DynamicRenderingFormatSignature DynamicRenderingFormats;
        public bool DepthStencilReadOnly;
        public SampleCountFlags Samples;
        public string TargetName = "<swapchain>";
        public Exception? Error;

        public void ClearReferences()
        {
            Ops = [];
            Chains = [];
            SecondaryBuffers = [];
            RecordJobChainIndices = [];
            RecordJobWorkerIndices = [];
            UniformSlots = [];
            JobCount = 0;
            ActiveWorkerMask = 0;
            TargetName = "<swapchain>";
            Error = null;
        }
    }

    private sealed class CommandChainRecordingWorkerState(int workerIndex)
    {
        public int WorkerIndex { get; } = workerIndex;
        public readonly AutoResetEvent WorkAvailable = new(initialState: false);
        public CommandPool[] GraphicsCommandPoolsByFrameSlot = [];
        public Thread? Thread;
        public VulkanRenderer? Owner;
        public volatile bool StopRequested;
        public ulong LastFrameId;

        public void Start(VulkanRenderer owner)
        {
            Owner = owner;
            Thread = new Thread(static state => ((CommandChainRecordingWorkerState)state!).Run())
            {
                IsBackground = true,
                Name = $"Vulkan Command Chain {WorkerIndex}",
            };
            Thread.Start(this);
        }

        private void Run()
        {
            while (true)
            {
                WorkAvailable.WaitOne();
                if (StopRequested)
                    return;

                Owner!.RunCommandChainRecordingWorker(this);
            }
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

    private bool TryPrepareCommandChainRecordingWorkers(
        int recordJobCount,
        uint frameDataImageIndex,
        out CommandChainRecordingWorkerState[] workers,
        out int workerCount,
        out int frameSlot)
    {
        workers = [];
        workerCount = 0;
        frameSlot = -1;
        int requestedWorkerCount = ResolveCommandChainRecordingWorkerCount(
            recordJobCount,
            Environment.ProcessorCount,
            CommandChainsSingleThread,
            ParallelCommandChainRecordingDisabled);
        bool workerRecordingAvailable =
            ParallelCommandChainWorkerRecordingSafe &&
            !CommandChainsSingleThread &&
            !ParallelCommandChainRecordingDisabled &&
            Math.Max(Environment.ProcessorCount - 1, 1) > 1;
        if (!workerRecordingAvailable ||
            recordJobCount <= 0 ||
            !TryGetIndexedCommandChainCacheSlot(frameDataImageIndex, out frameSlot))
        {
            return false;
        }

        workers = EnsureCommandChainRecordingWorkers(Math.Max(requestedWorkerCount, 2));
        // EnsureCommandChainRecordingWorkers creates the fixed bounded worker
        // capacity on first use. Hash against that capacity for the lifetime of
        // the pools so a changing dirty subset cannot migrate a chain.
        workerCount = workers.Length;
        int frameSlotCount = ResolveIndexedCommandChainCacheCount();
        EnsureCommandChainWorkerFrameSlotPools(workers, frameSlotCount);
        return frameSlot < frameSlotCount;
    }

    private CommandChainWorkerTiming DispatchCommandChainRecordingWorkers(
        CommandChainRecordingBatch batch,
        CommandChainRecordingWorkerState[] workers,
        int workerCount)
    {
        if (batch.JobCount <= 0 || workerCount <= 1 || batch.ActiveWorkerMask == 0)
            return default;

        long dispatchStart = Stopwatch.GetTimestamp();
        int activeWorkerCount = 0;
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            if ((batch.ActiveWorkerMask & (1u << workerIndex)) != 0)
                activeWorkerCount++;
        }

        if (activeWorkerCount == 0)
            return default;

        _commandChainRecordingWorkersIdle.Reset();
        _commandChainRecordingWorkerCountdown.Reset(activeWorkerCount);
        Volatile.Write(ref _activeCommandChainRecordingWorkerCount, activeWorkerCount);
        unchecked
        {
            _commandChainRecordingWorkerGeneration++;
        }

        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            if ((batch.ActiveWorkerMask & (1u << workerIndex)) != 0)
                workers[workerIndex].WorkAvailable.Set();
        }

        long waitStart = Stopwatch.GetTimestamp();
        _commandChainRecordingWorkerCountdown.Wait();
        _commandChainRecordingWorkersIdle.Set();
        Volatile.Write(ref _activeCommandChainRecordingWorkerCount, 0);

        if (batch.Error is not null)
            throw new InvalidOperationException("A Vulkan command-chain worker failed to record a secondary command buffer.", batch.Error);

        return new CommandChainWorkerTiming(
            Stopwatch.GetElapsedTime(dispatchStart),
            Stopwatch.GetElapsedTime(waitStart));
    }

    private void RunCommandChainRecordingWorker(CommandChainRecordingWorkerState worker)
    {
        using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording);
        try
        {
            CommandChainRecordingBatch batch = _commandChainRecordingBatch;
            worker.LastFrameId = VulkanFrameCounter;
            for (int jobIndex = 0; jobIndex < batch.JobCount; jobIndex++)
            {
                if (Volatile.Read(ref batch.Error) is not null)
                    break;

                if (batch.RecordJobWorkerIndices[jobIndex] != worker.WorkerIndex)
                    continue;

                try
                {
                    int chainIndex = batch.RecordJobChainIndices[jobIndex];
                    RecordScheduledMeshCommandChainWorker(batch, chainIndex);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref batch.Error, ex, null);
                    break;
                }
            }
        }
        finally
        {
            _commandChainRecordingWorkerCountdown.Signal();
        }
    }

    private CommandChainRecordingWorkerState[] EnsureCommandChainRecordingWorkers(int workerCount)
    {
        lock (_commandChainRecordingWorkersLock)
        {
            if (_commandChainRecordingWorkers is { Length: var existingCount } && existingCount >= workerCount)
                return _commandChainRecordingWorkers;

            if (_commandChainRecordingWorkers is not null)
                throw new InvalidOperationException("Vulkan command-chain worker capacity cannot grow while worker-owned command pools are live.");

            int capacity = Math.Clamp(Math.Max(Environment.ProcessorCount - 1, 1), 1, 8);
            CommandChainRecordingWorkerState[] workers = new CommandChainRecordingWorkerState[capacity];
            for (int i = 0; i < workers.Length; i++)
            {
                CommandChainRecordingWorkerState worker = new(i);
                worker.Start(this);
                workers[i] = worker;
            }

            _commandChainRecordingWorkers = workers;
            return workers;
        }
    }

    private void EnsureCommandChainWorkerFrameSlotPools(
        CommandChainRecordingWorkerState[] workers,
        int frameSlotCount)
    {
        uint graphicsFamily = FamilyQueueIndices.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("Graphics queue family is not available.");
        for (int workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            CommandChainRecordingWorkerState worker = workers[workerIndex];
            if (worker.GraphicsCommandPoolsByFrameSlot.Length == frameSlotCount)
                continue;

            if (worker.GraphicsCommandPoolsByFrameSlot.Length != 0)
                throw new InvalidOperationException("Vulkan command-chain frame-slot pool count changed while cached secondaries are live.");

            worker.GraphicsCommandPoolsByFrameSlot = new CommandPool[frameSlotCount];
            for (int frameSlot = 0; frameSlot < frameSlotCount; frameSlot++)
                worker.GraphicsCommandPoolsByFrameSlot[frameSlot] = CreateCommandPoolForFamily(graphicsFamily);
        }
    }

    private void CancelCommandChainRecordingWorkers()
    {
        _commandChainRecordingWorkersIdle.Wait();
        unchecked
        {
            _commandChainRecordingWorkerGeneration++;
        }
    }

    private void DestroyCommandChainRecordingWorkers()
    {
        lock (_commandChainRecordingWorkersLock)
            DestroyCommandChainRecordingWorkersLocked();
    }

    private void DestroyCommandChainRecordingWorkersLocked()
    {
        _commandChainRecordingWorkersIdle.Wait();
        if (_commandChainRecordingWorkers is null)
            return;

        for (int i = 0; i < _commandChainRecordingWorkers.Length; i++)
        {
            CommandChainRecordingWorkerState worker = _commandChainRecordingWorkers[i];
            worker.StopRequested = true;
            worker.WorkAvailable.Set();
        }

        for (int i = 0; i < _commandChainRecordingWorkers.Length; i++)
        {
            CommandChainRecordingWorkerState worker = _commandChainRecordingWorkers[i];
            worker.Thread?.Join();
            worker.WorkAvailable.Dispose();
            worker.Thread = null;
            worker.Owner = null;
        }

        DestroyCommandChainRecordingWorkerPoolsLocked();
        _commandChainRecordingWorkers = null;
        _commandChainRecordingBatch.ClearReferences();
    }

    private void DestroyCommandChainRecordingWorkerPools()
    {
        lock (_commandChainRecordingWorkersLock)
        {
            _commandChainRecordingWorkersIdle.Wait();
            DestroyCommandChainRecordingWorkerPoolsLocked();
        }
    }

    private void DestroyCommandChainRecordingWorkerPoolsLocked()
    {
        if (_commandChainRecordingWorkers is null)
            return;

        for (int workerIndex = 0; workerIndex < _commandChainRecordingWorkers.Length; workerIndex++)
        {
            CommandChainRecordingWorkerState worker = _commandChainRecordingWorkers[workerIndex];
            for (int frameSlot = 0; frameSlot < worker.GraphicsCommandPoolsByFrameSlot.Length; frameSlot++)
            {
                CommandPool pool = worker.GraphicsCommandPoolsByFrameSlot[frameSlot];
                if (pool.Handle != 0)
                    Api!.DestroyCommandPool(device, pool, null);
            }

            worker.GraphicsCommandPoolsByFrameSlot = [];
        }
    }
}
