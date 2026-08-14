using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    // One chain cannot overlap. Two independent chains are the smallest batch
    // that can prove useful concurrency; the closeout cohorts own any
    // hardware-specific threshold tuning above this correctness floor.
    private const int MinParallelCommandChainRecordJobs = 2;
    // A bounded Sponza replacement batch with 16 prepared mesh operations was
    // consistently faster inline than through the worker handoff. Dispatch only
    // when the immutable graphics packet contains enough encoding work to repay
    // queue, wake, merge, and render-thread wait overhead. Explicit worker-count
    // experiments intentionally bypass this production heuristic.
    private const int MinParallelCommandChainRecordOperations = 32;
    private const int DefaultMaxCommandChainRecordingWorkerCount = 4;
    private const int MaxCommandChainRecordingWorkerCount = 8;
    // Non-graphics packets are normally one dispatch, barrier, transfer, or
    // query operation. Keep tiny cohorts on the render thread because waking
    // persistent workers costs more than encoding two or three such packets.
    private const int MinParallelNonGraphicsRecordJobs = 4;
    private const int CommandChainWorkerWaitTimeoutMilliseconds = 2_000;
    // This is intentionally a launch-only setting. Worker-owned command pools and
    // cached secondary buffers cannot safely migrate between capacities at runtime.
    private static readonly int? s_configuredCommandChainRecordingWorkerCount =
        ResolveConfiguredCommandChainRecordingWorkerCount();
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
    private VulkanNonGraphicsRecordingBatch _nonGraphicsRecordingBatch = new();




    internal static int ResolveCommandChainRecordingWorkerCount(
        int independentChainCount,
        int processorCount,
        bool singleThread,
        bool parallelDisabled)
    {
        if (singleThread || parallelDisabled || independentChainCount <= 1)
            return 1;

        int usableProcessors = Math.Max(1, processorCount - 1);
        return Math.Clamp(independentChainCount, 1, Math.Min(usableProcessors, MaxCommandChainRecordingWorkerCount));
    }

    private static int ResolveEffectiveCommandChainRecordingWorkerCount(
        int independentChainCount,
        int processorCount,
        bool singleThread,
        bool parallelDisabled)
    {
        if (singleThread || parallelDisabled ||
            s_configuredCommandChainRecordingWorkerCount == 0)
        {
            return 0;
        }

        if (s_configuredCommandChainRecordingWorkerCount.HasValue)
            return s_configuredCommandChainRecordingWorkerCount.Value;

        // Preserve one stable default ownership domain for the process lifetime.
        // The dirty subset changes from frame to frame; sizing the domain from
        // independentChainCount would migrate a chain between worker-owned pools
        // or make the second eligible batch fail after the first capacity was
        // instantiated. ActiveWorkerMask still wakes only workers that own work.
        return Math.Clamp(
            Math.Max(processorCount - 1, 1),
            1,
            DefaultMaxCommandChainRecordingWorkerCount);
    }

    private static int? ResolveConfiguredCommandChainRecordingWorkerCount()
    {
        string? rawValue = XREnvironment.GetLaunchValue(CommandChainWorkerCountEnvVar);
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        return int.TryParse(
                rawValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int configuredCount)
            ? Math.Clamp(configuredCount, 0, MaxCommandChainRecordingWorkerCount)
            : null;
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

    private static EVulkanCommandChainWorkerEligibility EvaluateConfiguredParallelCommandChainRecording(
        int independentChainCount,
        int independentOperationCount,
        bool applyGraphicsCostThreshold,
        int processorCount,
        bool singleThread,
        bool parallelDisabled,
        bool workerDomainFaulted)
    {
        if (workerDomainFaulted)
            return EVulkanCommandChainWorkerEligibility.WorkerQuarantined;

        if (singleThread ||
            parallelDisabled ||
            s_configuredCommandChainRecordingWorkerCount == 0 ||
            independentChainCount < MinParallelCommandChainRecordJobs)
        {
            return EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork;
        }

        // An explicit capacity is a controlled diagnostic experiment. In
        // particular, one worker verifies worker-owned state and wait behavior
        // without pretending it provides CPU parallelism.
        if (s_configuredCommandChainRecordingWorkerCount.HasValue)
            return EVulkanCommandChainWorkerEligibility.Eligible;

        if (applyGraphicsCostThreshold &&
            independentOperationCount < MinParallelCommandChainRecordOperations)
        {
            return EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork;
        }

        return EvaluateParallelCommandChainRecording(
            independentChainCount,
            processorCount,
            singleThread,
            parallelDisabled,
            workerDomainFaulted);
    }

    private static EVulkanCommandChainWorkerEligibility EvaluateCommandChainWorkerEncodability(
        FrameOperationSequence ops,
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
            int operationIndex = chain.SourceStartIndex + drawIndex;
            EVulkanPrimaryPlanNodeKind kind = ops.GetHeader(operationIndex).OpCode;
            if (kind is EVulkanPrimaryPlanNodeKind.IndirectDraw or EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
                return EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream;

            if (kind != EVulkanPrimaryPlanNodeKind.MeshDraw)
                return EVulkanCommandChainWorkerEligibility.UnsupportedOperation;

            if (ops.GetMeshDraw(operationIndex).Draw.Renderer is null)
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

        if (workerCount <= 0)
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
            if (batch.PreparedFrame.GetMeshDrawColdData(draw.RecordingState.ColdDataIndex).Owner is null)
                return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
        }

        return EVulkanCommandChainWorkerEligibility.Eligible;
    }

    private EVulkanCommandChainWorkerEligibility PrepareCommandChainRecordingWorkers(
        int recordJobCount,
        int recordOperationCount,
        bool applyGraphicsCostThreshold,
        uint frameDataImageIndex,
        out CommandChainRecordingWorkerState[] workers,
        out int workerCount,
        out int frameSlot)
    {
        workers = [];
        workerCount = 0;
        frameSlot = -1;
        if (Volatile.Read(ref _commandChainRecordingWorkersFaulted) != 0 &&
            Volatile.Read(ref _activeCommandChainRecordingWorkerCount) != 0)
        {
            // A timed-out worker may still own a chain artifact and its arena.
            // Serial fallback must not migrate or rerecord that artifact until
            // the final abandoned worker has released its recording lease.
            throw new InvalidOperationException(
                "Vulkan command recording is quarantined until the abandoned persistent workers exit.");
        }

        int requestedWorkerCount = ResolveEffectiveCommandChainRecordingWorkerCount(
            recordJobCount,
            Environment.ProcessorCount,
            CommandChainsSingleThread,
            ParallelCommandChainRecordingDisabled);
        EVulkanCommandChainWorkerEligibility eligibility =
            EvaluateConfiguredParallelCommandChainRecording(
            recordJobCount,
            recordOperationCount,
            applyGraphicsCostThreshold,
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
                    "[Vulkan.CommandChainWorkers] Serial fallback reason={0} jobs={1} operations={2} processors={3} singleThread={4} disabled={5} faulted={6} indexedFrameSlot={7} frameDataImageIndex={8}.",
                    eligibility,
                    recordJobCount,
                    recordOperationCount,
                    Environment.ProcessorCount,
                    CommandChainsSingleThread,
                    ParallelCommandChainRecordingDisabled,
                    Volatile.Read(ref _commandChainRecordingWorkersFaulted) != 0,
                    hasIndexedFrameSlot,
                    frameDataImageIndex);
            }

            return eligibility;
        }

        workers = EnsureCommandChainRecordingWorkers(requestedWorkerCount);
        // Hash against the fixed instantiated capacity for the lifetime of the
        // pools so a changing dirty subset cannot migrate a chain.
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
        if (batch.JobCount <= 0 || workerCount <= 0 || batch.ActiveWorkerMask == 0)
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

        batch.ResetWorkerState(workerCount);
        batch.DispatchTimestamp = dispatchStart;
        int queuedEntryCount = 0;
        for (int entryIndex = 0; entryIndex < batch.EntryCount; entryIndex++)
        {
            ref VulkanCommandChainRecordingEntry entry = ref batch.Entries[entryIndex];
            if (entry.NeedsRecording && entry.WorkerIndex >= 0)
                queuedEntryCount++;
        }
        batch.PublishQueueTelemetry(queuedEntryCount);

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
                queuedChains: batch.QueueDepth,
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

        long mergeStart = Stopwatch.GetTimestamp();
        batch.WorkerLocalStates.Merge(workerCount, out CommandChainWorkerTiming timing);
        batch.LocalMergeElapsedTicks = Stopwatch.GetTimestamp() - mergeStart;
        batch.LocalMergeBytes = workerCount * batch.WorkerLocalStateBlockStride;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerLayoutTelemetry(
            batch.QueueDepth,
            batch.QueueBytes,
            batch.QueueHighWaterDepth,
            batch.QueueHighWaterBytes,
            batch.LocalMergeBytes,
            batch.LocalMergeElapsedTicks,
            0,
            0);
        return timing with { QueuedChains = batch.QueueDepth, WaitForWorkersTime = Stopwatch.GetElapsedTime(waitStart) };
    }

    private bool TryPrepareNonGraphicsRecordingWorkers(
        int entryCount,
        uint imageIndex,
        out CommandChainRecordingWorkerState[] workers,
        out int workerCount)
    {
        ThrowIfAbandonedRecordingWorkersRemainActive();
        if (entryCount < MinParallelNonGraphicsRecordJobs)
        {
            workers = [];
            workerCount = 0;
            return false;
        }

        EVulkanCommandChainWorkerEligibility eligibility =
            PrepareCommandChainRecordingWorkers(
                entryCount,
                entryCount,
                false,
                imageIndex,
                out workers,
                out workerCount,
                out _);
        return eligibility == EVulkanCommandChainWorkerEligibility.Eligible &&
               workerCount > 0;
    }

    private void ThrowIfAbandonedRecordingWorkersRemainActive()
    {
        if (Volatile.Read(ref _commandChainRecordingWorkersFaulted) == 0 ||
            Volatile.Read(ref _activeCommandChainRecordingWorkerCount) == 0)
            return;

        throw new InvalidOperationException(
            "Vulkan command recording is quarantined until the abandoned persistent workers exit.");
    }

    private void DispatchNonGraphicsRecordingWorkers(
        CommandChainRecordingWorkerState[] workers,
        int workerCount,
        FrameOperationSequence operations,
        uint imageIndex,
        CommandChain[] chains,
        CommandBuffer[] secondaryBuffers,
        int count,
        VulkanQuerySecondaryInheritanceContract queryInheritance)
    {
        VulkanNonGraphicsRecordingBatch batch = _nonGraphicsRecordingBatch;
        batch.Reset(operations, imageIndex, queryInheritance, count);
        for (int index = 0; index < count; index++)
        {
            CommandChain chain = chains[index];
            int workerIndex = ResolveCommandChainRecordingWorkerIndex(
                chain.Key,
                workerCount);
            batch.Entries[index] = new VulkanNonGraphicsRecordingEntry
            {
                Chain = chain,
                SecondaryBuffer = secondaryBuffers[index],
                WorkerIndex = workerIndex,
            };
            batch.ActiveWorkerMask |= 1u << workerIndex;
        }

        int activeWorkerCount = BitOperations.PopCount(batch.ActiveWorkerMask);
        if (activeWorkerCount <= 0)
            throw new InvalidOperationException(
                "Persistent non-graphics recording produced no active workers.");

        _commandChainRecordingWorkersIdle.Reset();
        _commandChainRecordingWorkerCountdown.Reset(activeWorkerCount);
        Volatile.Write(ref _activeCommandChainRecordingWorkerCount, activeWorkerCount);
        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            if ((batch.ActiveWorkerMask & (1u << workerIndex)) == 0)
                continue;
            workers[workerIndex].NonGraphicsBatch = batch;
            workers[workerIndex].WorkAvailable.Set();
        }

        long waitStarted = Stopwatch.GetTimestamp();
        bool completed;
        using (VulkanCpuStageScope workerWaitStage =
               new(_frameTelemetry, EVulkanCpuStage.WorkerWait))
        {
            completed = _commandChainRecordingWorkerCountdown.Wait(
                TimeSpan.FromMilliseconds(
                    CommandChainWorkerWaitTimeoutMilliseconds));
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
                    TimeSpan.FromMilliseconds(
                        CommandChainWorkerWaitTimeoutMilliseconds));
            }
            if (!completed)
            {
                batch.Abandoned = true;
                _nonGraphicsRecordingBatch = new VulkanNonGraphicsRecordingBatch();
                throw new TimeoutException(
                    $"Persistent Vulkan non-graphics workers did not stop within {CommandChainWorkerWaitTimeoutMilliseconds * 2} ms; " +
                    "the worker domain is quarantined and the frame will not be submitted.");
            }
        }

        _commandChainRecordingWorkersIdle.Set();
        Volatile.Write(ref _activeCommandChainRecordingWorkerCount, 0);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(
            queuedChains: count,
            waitForWorkersTime: Stopwatch.GetElapsedTime(waitStarted));
        Exception? workerError = batch.Error;
        batch.ClearReferences();
        if (timedOut)
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            throw new TimeoutException(
                $"Persistent Vulkan non-graphics recording exceeded the {CommandChainWorkerWaitTimeoutMilliseconds} ms deadline; " +
                "cancellation completed during the grace interval, but the partial secondary batch is rejected and will not execute.");
        }
        if (workerError is not null)
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            throw new InvalidOperationException(
                "A persistent Vulkan non-graphics recording worker failed.",
                workerError);
        }
    }

    private CommandChainRecordingWorkerState[] EnsureCommandChainRecordingWorkers(int workerCount)
    {
        lock (_commandChainRecordingWorkersLock)
        {
            if (_commandChainRecordingWorkers is { Length: var existingCount } && existingCount == workerCount)
                return _commandChainRecordingWorkers;

            if (_commandChainRecordingWorkers is not null)
                throw new InvalidOperationException("Vulkan command-chain worker capacity cannot grow while worker-owned command pools are live.");

            if (workerCount is <= 0 or > MaxCommandChainRecordingWorkerCount)
                throw new ArgumentOutOfRangeException(nameof(workerCount));

            CommandChainRecordingWorkerState[] workers = new CommandChainRecordingWorkerState[workerCount];
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

    /// <summary>
    /// Establishes a strict CPU-side command-recording boundary before the
    /// renderer waits for device idle. Unlike ordinary recovery cancellation,
    /// retirement cannot continue with a quarantined worker still accessing
    /// native command pools.
    /// </summary>
    internal void QuiesceCommandChainRecordingWorkersForRetirement()
    {
        RequestCommandChainRecordingWorkerCancellation();
        if (!_commandChainRecordingWorkersIdle.Wait(
                TimeSpan.FromMilliseconds(CommandChainWorkerWaitTimeoutMilliseconds)))
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            throw new InvalidOperationException(
                "Vulkan command-chain workers did not become idle before backend retirement.");
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
        Volatile.Write(ref _nonGraphicsRecordingBatch.CancelRequested, 1);
        if (_commandChainRecordingWorkers is null)
            return;

        for (int i = 0; i < _commandChainRecordingWorkers.Length; i++)
        {
            if (_commandChainRecordingWorkers[i].Batch is { } batch)
                Volatile.Write(ref batch.CancelRequested, 1);
            if (_commandChainRecordingWorkers[i].NonGraphicsBatch is { } nonGraphicsBatch)
                Volatile.Write(ref nonGraphicsBatch.CancelRequested, 1);
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
