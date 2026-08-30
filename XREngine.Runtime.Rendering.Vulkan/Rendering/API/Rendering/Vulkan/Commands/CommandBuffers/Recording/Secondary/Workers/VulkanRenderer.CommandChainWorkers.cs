using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private const int MinParallelCommandChainRecordJobs = 2;
    private const int MinParallelCommandChainRecordOperations = 32;
    private const int MaxCommandChainRecordingLaneCount = 32;
    private static readonly int? s_configuredCommandChainRecordingWorkerCount =
        ResolveConfiguredCommandChainRecordingWorkerCount();

    private VulkanCommandChainRecordingBatch _commandChainRecordingBatch
    {
        get => Workers.Batch;
        set => Workers.Batch = value;
    }

    private ref int _activeCommandChainRecordingWorkerCount
        => ref Workers.ActiveWorkerCount;

    private ref int _commandChainRecordingWorkersFaulted
        => ref Workers.Faulted;

    internal static int ResolveCommandChainRecordingWorkerCount(
        int independentChainCount,
        int processorCount,
        bool singleThread,
        bool parallelDisabled)
    {
        if (singleThread || parallelDisabled || independentChainCount <= 1)
            return 1;

        int usableProcessors = Math.Max(1, processorCount - 1);
        return Math.Clamp(
            independentChainCount,
            1,
            Math.Min(usableProcessors, MaxCommandChainRecordingLaneCount));
    }

    private static int ResolveEffectiveCommandChainRecordingLaneCount(
        int independentChainCount,
        int logicalLaneCount,
        bool singleThread,
        bool parallelDisabled)
    {
        if (singleThread || parallelDisabled ||
            s_configuredCommandChainRecordingWorkerCount == 0)
        {
            return 0;
        }

        int availableLaneCount = Math.Clamp(
            logicalLaneCount,
            1,
            MaxCommandChainRecordingLaneCount);
        if (s_configuredCommandChainRecordingWorkerCount.HasValue)
        {
            // The legacy Vulkan worker override is retained only as a benchmark
            // lane cap. It never creates a second thread domain.
            return Math.Min(
                availableLaneCount,
                checked(s_configuredCommandChainRecordingWorkerCount.Value + 1));
        }

        return Math.Min(availableLaneCount, Math.Max(independentChainCount, 1));
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
            ? Math.Clamp(configuredCount, 0, MaxCommandChainRecordingLaneCount - 1)
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
        int logicalLaneCount,
        bool singleThread,
        bool parallelDisabled,
        bool workerDomainFaulted,
        bool nestedRenderLaneExecution)
    {
        if (workerDomainFaulted)
            return EVulkanCommandChainWorkerEligibility.WorkerQuarantined;

        if (nestedRenderLaneExecution ||
            singleThread ||
            parallelDisabled ||
            s_configuredCommandChainRecordingWorkerCount == 0 ||
            logicalLaneCount <= 1 ||
            independentChainCount < MinParallelCommandChainRecordJobs)
        {
            return EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork;
        }

        if (s_configuredCommandChainRecordingWorkerCount.HasValue)
            return EVulkanCommandChainWorkerEligibility.Eligible;

        if (applyGraphicsCostThreshold &&
            independentOperationCount < MinParallelCommandChainRecordOperations)
        {
            return EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork;
        }

        return EVulkanCommandChainWorkerEligibility.Eligible;
    }

    private static VulkanCommandChainWorkerEligibilityResult AssignCommandChainRecordingWorker(
        VulkanCommandChainRecordingBatch batch,
        CommandChain chain,
        int laneCount)
    {
        EVulkanCommandChainWorkerEligibility encodability =
            EvaluatePreparedCommandChainWorkerEncodability(batch, chain);
        if (encodability != EVulkanCommandChainWorkerEligibility.Eligible)
            return new VulkanCommandChainWorkerEligibilityResult(encodability);

        if (laneCount <= 0)
        {
            return new VulkanCommandChainWorkerEligibilityResult(
                EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork);
        }

        return new VulkanCommandChainWorkerEligibilityResult(
            EVulkanCommandChainWorkerEligibility.Eligible,
            ResolveCommandChainRecordingWorkerIndex(chain.Key, laneCount));
    }

    private static EVulkanCommandChainWorkerEligibility
        EvaluatePreparedCommandChainWorkerEncodability(
            VulkanCommandChainRecordingBatch batch,
            CommandChain chain)
    {
        if (chain.SourceCount < MinMeshDrawsPerRenderPacket)
            return EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork;

        int preparedStartIndex = chain.SourceStartIndex - batch.StartIndex;
        if (!batch.PreparedFrame.ContainsMeshDrawRangeForOwnerValidation(
                preparedStartIndex,
                chain.SourceCount))
        {
            return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
        }

        for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
        {
            ref readonly VkPreparedMeshDraw draw =
                ref batch.PreparedFrame.GetMeshDrawForOwnerValidation(
                    preparedStartIndex + drawIndex);
            VulkanPreparedMeshDrawState state = draw.RecordingState;
            if (state.PipelineLayout.Handle == 0 || state.PrimitiveCount <= 0)
                return EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed;
        }

        return EVulkanCommandChainWorkerEligibility.Eligible;
    }

    private EVulkanCommandChainWorkerEligibility PrepareCommandChainRecordingWorkers(
        int recordJobCount,
        int recordOperationCount,
        bool applyGraphicsCostThreshold,
        bool forceSerial,
        uint frameDataImageIndex,
        out int laneCount,
        out int frameSlot)
    {
        InitializeRenderLaneCommandAttachments();
        laneCount = 0;
        frameSlot = ResolveRenderLaneFrameSlot(frameDataImageIndex);

        bool nestedRenderLaneExecution =
            VulkanRenderLaneExecutionScope.TryGetCurrent(
                out VulkanRenderLaneFrameAttachment? currentAttachment);
        if (currentAttachment is not null)
            frameSlot = currentAttachment.FrameSlot;

        int logicalLaneCount = RenderLogicalLaneCount;
        laneCount = ResolveEffectiveCommandChainRecordingLaneCount(
            recordJobCount,
            logicalLaneCount,
            CommandChainsSingleThread,
            ParallelCommandChainRecordingDisabled);
        EVulkanCommandChainWorkerEligibility eligibility = forceSerial
            ? EVulkanCommandChainWorkerEligibility.WorkerQuarantined
            : EvaluateConfiguredParallelCommandChainRecording(
                recordJobCount,
                recordOperationCount,
                applyGraphicsCostThreshold,
                logicalLaneCount,
                CommandChainsSingleThread,
                ParallelCommandChainRecordingDisabled,
                Volatile.Read(ref _commandChainRecordingWorkersFaulted) != 0,
                nestedRenderLaneExecution);
        if (eligibility != EVulkanCommandChainWorkerEligibility.Eligible)
            laneCount = 0;

        if (CommandChainValidationEnabled &&
            recordJobCount >= MinParallelCommandChainRecordJobs &&
            eligibility != EVulkanCommandChainWorkerEligibility.Eligible)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.CommandChainWorkers.Rejected.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.CommandChainWorkers] Inline fallback reason={0} jobs={1} operations={2} logicalLanes={3} singleThread={4} disabled={5} faulted={6} nestedLane={7} frameDataImageIndex={8}.",
                eligibility,
                recordJobCount,
                recordOperationCount,
                logicalLaneCount,
                CommandChainsSingleThread,
                ParallelCommandChainRecordingDisabled,
                Volatile.Read(ref _commandChainRecordingWorkersFaulted) != 0,
                nestedRenderLaneExecution,
                frameDataImageIndex);
        }

        return eligibility;
    }

    private CommandChainWorkerTiming DispatchCommandChainRecordingWorkers(
        VulkanCommandChainRecordingBatch batch,
        int laneCount,
        int frameSlot)
    {
        if (batch.JobCount <= 0 || laneCount <= 0)
            return default;

        batch.ResetWorkerState(laneCount);
        batch.PrepareRenderWork(this, laneCount);
        int activeLaneCount = BitOperations.PopCount(batch.ActiveWorkerMask);
        if (activeLaneCount == 0)
            return default;

        batch.DispatchTimestamp = Stopwatch.GetTimestamp();
        batch.PublishQueueTelemetry(batch.JobCount);
        RenderWorkDomain domain = GetRenderWorkDomain();
        RenderWorkBatchLease lease = domain.RentBatch(activeLaneCount);
        int itemIndex = 0;
        for (int laneId = 0; laneId < laneCount; laneId++)
        {
            if ((batch.ActiveWorkerMask & (1u << laneId)) == 0)
                continue;

            batch.GetLaneWork(
                laneId,
                out int sourceStart,
                out int sourceCount,
                out int estimatedCost);
            lease.SetItem(
                itemIndex++,
                new RenderWorkItem(
                    VulkanCommandChainRecordingBatch.MeshCommandChainOperationKind,
                    sourceStart,
                    sourceCount,
                    PreferredLane: laneId,
                    EstimatedCost: estimatedCost));
        }

        long waitStarted = Stopwatch.GetTimestamp();
        RenderWorkBatchResult result;
        Volatile.Write(ref _activeCommandChainRecordingWorkerCount, activeLaneCount);
        try
        {
            using VulkanCpuStageScope workerWaitStage =
                new(FrameTelemetry, EVulkanCpuStage.WorkerWait);
            result = domain.ExecuteAndWait(
                ref lease,
                batch,
                frameSlot,
                RenderWorkDomain.FatalBatchWait);
        }
        finally
        {
            Volatile.Write(ref _activeCommandChainRecordingWorkerCount, 0);
            lease.Dispose();
        }

        TimeSpan waitTime = Stopwatch.GetElapsedTime(waitStarted);
        if (!result.Succeeded)
        {
            Interlocked.Exchange(ref _commandChainRecordingWorkersFaulted, 1);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerMetrics(
                queuedChains: batch.QueueDepth,
                workerFailures: 1,
                waitTimeouts: result.Exception is TimeoutException ? 1 : 0,
                waitForWorkersTime: waitTime);
            throw new InvalidOperationException(
                "A render-domain Vulkan command-chain batch failed; all partial artifacts were quarantined.",
                result.Exception);
        }

        long mergeStart = Stopwatch.GetTimestamp();
        batch.WorkerLocalStates.Merge(laneCount, out CommandChainWorkerTiming timing);
        batch.LocalMergeElapsedTicks = Stopwatch.GetTimestamp() - mergeStart;
        batch.LocalMergeBytes = laneCount * batch.WorkerLocalStateBlockStride;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainWorkerLayoutTelemetry(
            batch.QueueDepth,
            batch.QueueBytes,
            batch.QueueHighWaterDepth,
            batch.QueueHighWaterBytes,
            batch.LocalMergeBytes,
            batch.LocalMergeElapsedTicks,
            0,
            0);
        return timing with
        {
            QueuedChains = batch.QueueDepth,
            WaitForWorkersTime = waitTime,
        };
    }

    private void CancelCommandChainRecordingWorkers()
        => Volatile.Write(ref _commandChainRecordingBatch.CancelRequested, 1);

    internal void QuiesceCommandChainRecordingWorkersForRetirement()
    {
        CancelCommandChainRecordingWorkers();
        if (Volatile.Read(ref _activeCommandChainRecordingWorkerCount) != 0)
        {
            throw new InvalidOperationException(
                "Vulkan render-domain command recording remained active before backend retirement.");
        }
    }

    private void DestroyCommandChainRecordingWorkers()
    {
        QuiesceCommandChainRecordingWorkersForRetirement();
        DestroyRenderLaneCommandAttachments();
        _commandChainRecordingBatch.ClearReferences();
    }

    private void DestroyCommandChainRecordingWorkerPools()
    {
        // Lane/frame-slot pools are process-topology attachments rather than
        // swapchain-image arrays. Indexed cache destruction detaches the old
        // artifacts; the fixed lane arenas remain valid across WSI recreation.
    }
}
