using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    // One chain cannot overlap. Two independent chains are the smallest batch
    // that can prove useful concurrency; the closeout cohorts own any
    // hardware-specific threshold tuning above this correctness floor.
    private const int MinParallelCommandChainRecordJobs = 2;
    private const int CommandChainWorkerWaitTimeoutMilliseconds = 2_000;
    private readonly object _commandChainRecordingWorkersLock = new();
    private readonly ManualResetEventSlim _commandChainRecordingWorkersIdle = new(initialState: true);
    private readonly CountdownEvent _commandChainRecordingWorkerCountdown = new(initialCount: 1);
    private CommandChainRecordingBatch _commandChainRecordingBatch = new();
    private CommandChainRecordingWorkerState[]? _commandChainRecordingWorkers;
    private int _commandChainRecordingWorkerGeneration;
    private int _activeCommandChainRecordingWorkerCount;
    private int _commandChainRecordingWorkersFaulted;

    private readonly record struct CommandChainWorkerTiming(
        int QueuedChains,
        int WorkersStarted,
        int WorkersCompleted,
        int PeakConcurrentWorkers,
        TimeSpan QueueDelay,
        TimeSpan WorkerRecordTime,
        TimeSpan WorkerActiveSpan,
        TimeSpan WorkerOverlapTime,
        TimeSpan WaitForWorkersTime);

    private sealed class CommandChainRecordingBatch
    {
        public readonly VulkanPreparedFrameRecording PreparedFrame = new();
        public CommandChain[] Chains = new CommandChain[16];
        public CommandBuffer[] SecondaryBuffers = new CommandBuffer[16];
        public int[] RecordJobChainIndices = new int[16];
        public int[] RecordJobWorkerIndices = new int[16];
        public int[] UniformSlots = new int[16];
        public int StartIndex;
        public int ChainCount;
        public int JobCount;
        public uint ActiveWorkerMask;
        public Exception? Error;
        public long DispatchTimestamp;
        public long FirstWorkerStartTimestamp;
        public long LastWorkerCompletionTimestamp;
        public long WorkerRecordTimestampTotal;
        public long MaximumQueueDelayTimestamp;
        public int WorkersStarted;
        public int WorkersCompleted;
        public int ConcurrentWorkers;
        public int PeakConcurrentWorkers;
        public int QueuedChains;
        public int CancelRequested;
        public bool Abandoned;

        public void EnsureCapacity(int count)
        {
            if (Chains.Length >= count)
                return;

            int capacity = Math.Max(count, Chains.Length * 2);
            Array.Resize(ref Chains, capacity);
            Array.Resize(ref SecondaryBuffers, capacity);
            Array.Resize(ref RecordJobChainIndices, capacity);
            Array.Resize(ref RecordJobWorkerIndices, capacity);
            Array.Resize(ref UniformSlots, capacity);
        }

        public void ResetTiming()
        {
            DispatchTimestamp = 0;
            FirstWorkerStartTimestamp = long.MaxValue;
            LastWorkerCompletionTimestamp = 0;
            WorkerRecordTimestampTotal = 0;
            MaximumQueueDelayTimestamp = 0;
            WorkersStarted = 0;
            WorkersCompleted = 0;
            ConcurrentWorkers = 0;
            PeakConcurrentWorkers = 0;
            QueuedChains = 0;
            CancelRequested = 0;
            Abandoned = false;
        }

        public void ClearReferences()
        {
            Array.Clear(Chains, 0, ChainCount);
            PreparedFrame.Reset();
            ChainCount = 0;
            JobCount = 0;
            ActiveWorkerMask = 0;
            Error = null;
        }
    }

    private sealed class CommandChainRecordingWorkerState(int workerIndex)
    {
        public int WorkerIndex { get; } = workerIndex;
        public readonly AutoResetEvent WorkAvailable = new(initialState: false);
        public readonly VulkanWorkerSecondaryCommandArena Arena =
            new(workerIndex);
        public Thread? Thread;
        public VulkanRenderer? Owner;
        public CommandChainRecordingBatch? Batch;
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

    internal static EVulkanCommandChainWorkerEligibility EvaluateParallelCommandChainRecording(
        int independentChainCount,
        int processorCount,
        bool singleThread,
        bool parallelDisabled,
        bool workerDomainFaulted)
    {
        if (workerDomainFaulted)
            return EVulkanCommandChainWorkerEligibility.WorkerQuarantined;

        if (singleThread ||
            parallelDisabled ||
            independentChainCount < MinParallelCommandChainRecordJobs ||
            Math.Max(processorCount - 1, 1) <= 1)
        {
            return EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork;
        }

        return EVulkanCommandChainWorkerEligibility.Eligible;
    }

    private static EVulkanCommandChainWorkerEligibility EvaluateCommandChainWorkerEncodability(
        FrameOp[] ops,
        CommandChain chain)
    {
        if (chain.SourceStartIndex < 0 ||
            chain.SourceCount <= 0 ||
            chain.SourceStartIndex > ops.Length - chain.SourceCount)
        {
            return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
        }

        for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
        {
            FrameOp op = ops[chain.SourceStartIndex + drawIndex];
            if (op is IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                return EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream;

            if (op is not MeshDrawOp meshDraw)
                return EVulkanCommandChainWorkerEligibility.UnsupportedOperation;

            if (meshDraw.Draw.Renderer is null)
                return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
        }

        return EVulkanCommandChainWorkerEligibility.Eligible;
    }

    private static VulkanCommandChainWorkerEligibilityResult AssignCommandChainRecordingWorker(
        CommandChainRecordingBatch batch,
        CommandChain chain,
        int workerCount)
    {
        EVulkanCommandChainWorkerEligibility encodability =
            EvaluatePreparedCommandChainWorkerEncodability(batch, chain);
        if (encodability != EVulkanCommandChainWorkerEligibility.Eligible)
            return new VulkanCommandChainWorkerEligibilityResult(encodability);

        if (workerCount <= 1)
        {
            return new VulkanCommandChainWorkerEligibilityResult(
                EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork);
        }

        return new VulkanCommandChainWorkerEligibilityResult(
            EVulkanCommandChainWorkerEligibility.Eligible,
            ResolveCommandChainRecordingWorkerIndex(chain.Key, workerCount));
    }

    private static EVulkanCommandChainWorkerEligibility
        EvaluatePreparedCommandChainWorkerEncodability(
            CommandChainRecordingBatch batch,
            CommandChain chain)
    {
        int preparedStartIndex = chain.SourceStartIndex - batch.StartIndex;
        // Eligibility is assigned while the render thread is still appending
        // prepared command-chain records. The worker batch is frozen before it is
        // dispatched, so validate the already-published draw range here without
        // requiring the not-yet-possible frozen state.
        if (!batch.PreparedFrame.ContainsMeshDrawRangeForOwnerValidation(
                preparedStartIndex,
                chain.SourceCount))
            return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;

        for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
        {
            ref readonly VkPreparedMeshDraw draw =
                ref batch.PreparedFrame.GetMeshDrawForOwnerValidation(
                    preparedStartIndex + drawIndex);
            if (draw.OwnerIdentity is null)
                return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
        }

        return EVulkanCommandChainWorkerEligibility.Eligible;
    }

    private static ref readonly VkPreparedMeshDraw GetPreparedCommandChainDraw(
        CommandChainRecordingBatch batch,
        int chainIndex,
        int drawIndex)
    {
        ref readonly VulkanPreparedCommandChain preparedChain =
            ref batch.PreparedFrame.GetCommandChain(chainIndex);
        if ((uint)drawIndex >= (uint)preparedChain.SourceCount)
            throw new ArgumentOutOfRangeException(nameof(drawIndex));

        int preparedIndex = preparedChain.PreparedDrawStartIndex + drawIndex;
        return ref batch.PreparedFrame.GetMeshDraw(preparedIndex);
    }

    private EVulkanCommandChainWorkerEligibility PrepareCommandChainRecordingWorkers(
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
        EVulkanCommandChainWorkerEligibility eligibility =
            EvaluateParallelCommandChainRecording(
            recordJobCount,
            Environment.ProcessorCount,
            CommandChainsSingleThread,
            ParallelCommandChainRecordingDisabled,
            Volatile.Read(ref _commandChainRecordingWorkersFaulted) != 0);
        bool hasIndexedFrameSlot = TryGetIndexedCommandChainCacheSlot(frameDataImageIndex, out frameSlot);
        if (eligibility != EVulkanCommandChainWorkerEligibility.Eligible ||
            recordJobCount <= 0 ||
            !hasIndexedFrameSlot)
        {
            if (eligibility == EVulkanCommandChainWorkerEligibility.Eligible)
            {
                eligibility =
                    EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
            }

            if (CommandChainValidationEnabled && recordJobCount >= MinParallelCommandChainRecordJobs)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.CommandChainWorkers.Rejected.{GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan.CommandChainWorkers] Serial fallback reason={0} jobs={1} processors={2} singleThread={3} disabled={4} faulted={5} indexedFrameSlot={6} frameDataImageIndex={7}.",
                    eligibility,
                    recordJobCount,
                    Environment.ProcessorCount,
                    CommandChainsSingleThread,
                    ParallelCommandChainRecordingDisabled,
                    Volatile.Read(ref _commandChainRecordingWorkersFaulted) != 0,
                    hasIndexedFrameSlot,
                    frameDataImageIndex);
            }

            return eligibility;
        }

        workers = EnsureCommandChainRecordingWorkers(Math.Max(requestedWorkerCount, 2));
        // EnsureCommandChainRecordingWorkers creates the fixed bounded worker
        // capacity on first use. Hash against that capacity for the lifetime of
        // the pools so a changing dirty subset cannot migrate a chain.
        workerCount = workers.Length;
        int frameSlotCount = ResolveIndexedCommandChainCacheCount();
        EnsureCommandChainWorkerFrameSlotPools(workers, frameSlotCount);
        return frameSlot < frameSlotCount
            ? EVulkanCommandChainWorkerEligibility.Eligible
            : EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
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

        batch.ResetTiming();
        batch.DispatchTimestamp = dispatchStart;
        for (int jobIndex = 0; jobIndex < batch.JobCount; jobIndex++)
        {
            if (batch.RecordJobWorkerIndices[jobIndex] >= 0)
                batch.QueuedChains++;
        }

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
            {
                workers[workerIndex].Batch = batch;
                workers[workerIndex].WorkAvailable.Set();
            }
        }

        long waitStart = Stopwatch.GetTimestamp();
        bool completed = _commandChainRecordingWorkerCountdown.Wait(
            TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds));
        bool timedOut = !completed;
        if (timedOut)
        {
            Volatile.Write(ref batch.CancelRequested, 1);
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            completed = _commandChainRecordingWorkerCountdown.Wait(
                TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds));
            if (!completed)
            {
                batch.Abandoned = true;
                _commandChainRecordingBatch = new CommandChainRecordingBatch();
            }
        }

        if (completed)
        {
            _commandChainRecordingWorkersIdle.Set();
            Volatile.Write(ref _activeCommandChainRecordingWorkerCount, 0);
        }

        if (timedOut)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(
                queuedChains: batch.QueuedChains,
                workersStarted: Volatile.Read(ref batch.WorkersStarted),
                workersCompleted: Volatile.Read(ref batch.WorkersCompleted),
                peakConcurrentWorkers: Volatile.Read(ref batch.PeakConcurrentWorkers),
                waitTimeouts: 1,
                workerFailures: 1,
                waitForWorkersTime: Stopwatch.GetElapsedTime(waitStart));
            throw new TimeoutException(
                completed
                    ? $"Vulkan command-chain workers exceeded the {CommandChainWorkerWaitTimeoutMilliseconds} ms recording deadline; " +
                      "the cancelled worker domain is quarantined and the frame will not be submitted."
                    : $"Vulkan command-chain workers did not stop within {CommandChainWorkerWaitTimeoutMilliseconds * 2} ms; " +
                "the worker domain is quarantined and the frame will not be submitted.");
        }

        if (batch.Error is not null)
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(workerFailures: 1);
            throw new InvalidOperationException("A Vulkan command-chain worker failed to record a secondary command buffer.", batch.Error);
        }

        long firstStart = Volatile.Read(ref batch.FirstWorkerStartTimestamp);
        long lastCompletion = Volatile.Read(ref batch.LastWorkerCompletionTimestamp);
        long workerRecordTicks = Volatile.Read(ref batch.WorkerRecordTimestampTotal);
        TimeSpan activeSpan = firstStart == long.MaxValue || lastCompletion <= firstStart
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(firstStart, lastCompletion);
        TimeSpan workerRecordTime = StopwatchTicksToTimeSpan(workerRecordTicks);
        TimeSpan overlap = workerRecordTime > activeSpan
            ? workerRecordTime - activeSpan
            : TimeSpan.Zero;

        return new CommandChainWorkerTiming(
            batch.QueuedChains,
            Volatile.Read(ref batch.WorkersStarted),
            Volatile.Read(ref batch.WorkersCompleted),
            Volatile.Read(ref batch.PeakConcurrentWorkers),
            StopwatchTicksToTimeSpan(Volatile.Read(ref batch.MaximumQueueDelayTimestamp)),
            workerRecordTime,
            activeSpan,
            overlap,
            Stopwatch.GetElapsedTime(waitStart));
    }

    private void RunCommandChainRecordingWorker(CommandChainRecordingWorkerState worker)
    {
        using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording);
        CommandChainRecordingBatch? batch = worker.Batch;
        if (batch is null)
            return;

        long workerStart = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref batch.WorkersStarted);
        UpdateMinimum(ref batch.FirstWorkerStartTimestamp, workerStart);
        UpdateMaximum(ref batch.MaximumQueueDelayTimestamp, workerStart - batch.DispatchTimestamp);
        int concurrentWorkers = Interlocked.Increment(ref batch.ConcurrentWorkers);
        UpdateMaximum(ref batch.PeakConcurrentWorkers, concurrentWorkers);
        try
        {
            worker.LastFrameId = VulkanFrameCounter;
            for (int jobIndex = 0; jobIndex < batch.JobCount; jobIndex++)
            {
                if (Volatile.Read(ref batch.Error) is not null ||
                    Volatile.Read(ref batch.CancelRequested) != 0)
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
            long workerCompletion = Stopwatch.GetTimestamp();
            Interlocked.Add(ref batch.WorkerRecordTimestampTotal, workerCompletion - workerStart);
            UpdateMaximum(ref batch.LastWorkerCompletionTimestamp, workerCompletion);
            Interlocked.Decrement(ref batch.ConcurrentWorkers);
            Interlocked.Increment(ref batch.WorkersCompleted);
            worker.Batch = null;
            bool lastWorker = _commandChainRecordingWorkerCountdown.Signal();
            if (lastWorker)
            {
                Volatile.Write(ref _activeCommandChainRecordingWorkerCount, 0);
                _commandChainRecordingWorkersIdle.Set();
                if (batch.Abandoned)
                    batch.ClearReferences();
            }
        }
    }

    private static TimeSpan StopwatchTicksToTimeSpan(long ticks)
        => ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current = Volatile.Read(ref target);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void UpdateMinimum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate < current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
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
            if (worker.Arena.FrameSlotCount == frameSlotCount)
                continue;

            if (worker.Arena.FrameSlotCount != 0)
                throw new InvalidOperationException("Vulkan command-chain frame-slot pool count changed while cached secondaries are live.");

            CommandPool[] poolsByFrameSlot = new CommandPool[frameSlotCount];
            for (int frameSlot = 0; frameSlot < frameSlotCount; frameSlot++)
                poolsByFrameSlot[frameSlot] =
                    CreateCommandPoolForFamily(graphicsFamily);
            worker.Arena.Initialize(poolsByFrameSlot);
        }
    }

    private void CancelCommandChainRecordingWorkers()
    {
        RequestCommandChainRecordingWorkerCancellation();
        if (!_commandChainRecordingWorkersIdle.Wait(
                TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds)))
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            Debug.VulkanWarning(
                "[Vulkan] Command-chain workers did not become idle during cancellation; " +
                "the worker domain remains quarantined.");
            return;
        }

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
        RequestCommandChainRecordingWorkerCancellation();
        if (!_commandChainRecordingWorkersIdle.Wait(
                TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds)))
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            Debug.VulkanWarning(
                "[Vulkan] Command-chain worker shutdown timed out; worker-owned pools were retained.");
            return;
        }

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
            if (worker.Thread is not null &&
                !worker.Thread.Join(TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds)))
            {
                Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
                Debug.VulkanWarning(
                    $"[Vulkan] Command-chain worker {worker.WorkerIndex} did not stop; worker-owned pools were retained.");
                return;
            }
        }

        for (int i = 0; i < _commandChainRecordingWorkers.Length; i++)
        {
            CommandChainRecordingWorkerState worker = _commandChainRecordingWorkers[i];
            worker.WorkAvailable.Dispose();
            worker.Thread = null;
            worker.Owner = null;
        }

        DestroyCommandChainRecordingWorkerPoolsLocked();
        _commandChainRecordingWorkers = null;
        _commandChainRecordingBatch.ClearReferences();
    }

    private void RequestCommandChainRecordingWorkerCancellation()
    {
        Volatile.Write(ref _commandChainRecordingBatch.CancelRequested, 1);
        if (_commandChainRecordingWorkers is null)
            return;

        for (int i = 0; i < _commandChainRecordingWorkers.Length; i++)
        {
            if (_commandChainRecordingWorkers[i].Batch is { } batch)
                Volatile.Write(ref batch.CancelRequested, 1);
        }
    }

    private void DestroyCommandChainRecordingWorkerPools()
    {
        lock (_commandChainRecordingWorkersLock)
        {
            if (!_commandChainRecordingWorkersIdle.Wait(
                    TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds)))
            {
                Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
                Debug.VulkanWarning(
                    "[Vulkan] Command-chain worker pool destruction timed out; pools were retained.");
                return;
            }

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
            for (int frameSlot = 0; frameSlot < worker.Arena.FrameSlotCount; frameSlot++)
            {
                CommandPool pool = worker.Arena.GetPool(frameSlot);
                if (pool.Handle != 0)
                    Api!.DestroyCommandPool(device, pool, null);
            }

            worker.Arena.ClearAfterPoolDestruction();
        }
    }
}
