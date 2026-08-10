using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    // One chain cannot overlap. Two independent chains are the smallest batch
    // that can prove useful concurrency; the closeout cohorts own any
    // hardware-specific threshold tuning above this correctness floor.
    private const int MinParallelCommandChainRecordJobs = 2;
    private const int CommandChainWorkerWaitTimeoutMilliseconds = 2_000;
    private object _commandChainRecordingWorkersLock => _commandRuntime.Workers.Gate;
    private ManualResetEventSlim _commandChainRecordingWorkersIdle => _commandRuntime.Workers.Idle;
    private CountdownEvent _commandChainRecordingWorkerCountdown => _commandRuntime.Workers.Countdown;
    private VulkanCommandChainRecordingBatch _commandChainRecordingBatch
    {
        get => _commandRuntime.Workers.Batch;
        set => _commandRuntime.Workers.Batch = value;
    }
    private CommandChainRecordingWorkerState[]? _commandChainRecordingWorkers
    {
        get => _commandRuntime.Workers.WorkerStates;
        set => _commandRuntime.Workers.WorkerStates = value;
    }
    private ref int _commandChainRecordingWorkerGeneration => ref _commandRuntime.Workers.Generation;
    private ref int _activeCommandChainRecordingWorkerCount => ref _commandRuntime.Workers.ActiveWorkerCount;
    private ref int _commandChainRecordingWorkersFaulted => ref _commandRuntime.Workers.Faulted;




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
        VulkanCommandChainRecordingBatch batch,
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
            VulkanCommandChainRecordingBatch batch,
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
        VulkanCommandChainRecordingBatch batch,
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
        bool completed;
        using (VulkanCpuStageScope workerWaitStage =
               new(_frameTelemetry, EVulkanCpuStage.WorkerWait))
        {
            completed = _commandChainRecordingWorkerCountdown.Wait(
                TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds));
        }
        bool timedOut = !completed;
        if (timedOut)
        {
            Volatile.Write(ref batch.CancelRequested, 1);
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            using (VulkanCpuStageScope workerWaitStage =
                   new(_frameTelemetry, EVulkanCpuStage.WorkerWait))
            {
                completed = _commandChainRecordingWorkerCountdown.Wait(
                    TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds));
            }
            if (!completed)
            {
                batch.Abandoned = true;
                _commandChainRecordingBatch = new VulkanCommandChainRecordingBatch();
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
            if (batch.Error is VulkanPlanPreconditionException planPrecondition)
                throw planPrecondition;
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

    private static TimeSpan StopwatchTicksToTimeSpan(long ticks)
        => ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);

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
                CommandChainRecordingWorkerState worker = new(_commandRuntime, i);
                worker.Start();
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
        uint graphicsFamily = _deviceContext.QueueFamilies.GraphicsFamilyIndex
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
                    MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);
            }

            worker.Arena.ClearAfterPoolRetirement();
        }
    }
}
