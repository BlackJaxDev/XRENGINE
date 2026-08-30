using XREngine.Execution;
using XREngine.Rendering;

namespace XREngine;

public static partial class Engine
{
    /// <summary>
    /// Proves the installed runtime work capability, including post-warmup
    /// allocation closure, and decodes one already-completed telemetry payload.
    /// </summary>
    private static void ValidateInstalledWorkScheduler()
    {
        IRuntimeRenderWorkServices work = RuntimeRenderingHostServices.Work;
        if (!ReferenceEquals(work.GeneralJobs, Jobs))
            throw new InvalidOperationException(
                "The runtime rendering host and Engine.Jobs do not share the scheduler general domain.");

        const int itemCount = 4;
        const int valuesPerItem = 8;
        const int allocationProofBatchCount = 32;
        int[] preparedValues = new int[itemCount * valuesPerItem];
        var executor = new EngineSchedulerSmokeExecutor(preparedValues);
        RenderWorkBatchResult warmupResult = ExecuteSchedulerSmokeBatch(
            work.RenderWork,
            executor,
            itemCount,
            valuesPerItem);
        ValidateSchedulerSmokeResult(warmupResult, preparedValues);

        RenderWorkDomainMetrics allocationBaseline = work.RenderWork.Metrics;
        for (int iteration = 0; iteration < allocationProofBatchCount; iteration++)
        {
            RenderWorkBatchResult proofResult = ExecuteSchedulerSmokeBatch(
                work.RenderWork,
                executor,
                itemCount,
                valuesPerItem);
            if (!proofResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "A post-warmup renderer-neutral allocation proof batch failed.",
                    proofResult.Exception);
            }
        }

        RenderWorkDomainMetrics allocationResult = work.RenderWork.Metrics;
        ValidateSchedulerAllocationProof(allocationBaseline, allocationResult);
        ValidateSchedulerSmokeOutput(preparedValues);

        int capProbeItemCount = checked(work.RenderWork.MaxMigratableItemCount + 1);
        int[] capProbeValues = new int[capProbeItemCount];
        var capProbeExecutor = new EngineSchedulerSmokeExecutor(capProbeValues);
        RenderWorkDomainMetrics capBaseline = work.RenderWork.Metrics;
        RenderWorkBatchResult capProbeResult = ExecuteSchedulerSmokeBatch(
            work.RenderWork,
            capProbeExecutor,
            capProbeItemCount,
            valuesPerItem: 1);
        ValidateSchedulerSmokeResult(capProbeResult, capProbeValues);
        RenderWorkDomainMetrics capResult = work.RenderWork.Metrics;
        if (capResult.CapPinnedMigratableItemCount - capBaseline.CapPinnedMigratableItemCount != 1)
        {
            throw new InvalidOperationException(
                "The renderer-neutral migration-cap proof did not pin exactly one over-budget item.");
        }

        uint[] completedWords = [0x13579BDFu, 0x2468ACE0u, 32u, (uint)itemCount];
        CompletedDiagnosticPayload payload = CompletedDiagnosticPayload.Create(completedWords);
        CompletedDiagnosticDecodeJob diagnosticJob = work.ScheduleCompletedDiagnosticDecode(payload);
        if (!diagnosticJob.Handle.Wait(RenderWorkDomain.FatalBatchWait))
            throw new TimeoutException("The completed diagnostic decode exceeded the fatal scheduler lifecycle bound.");
        if (diagnosticJob.IsFaulted || diagnosticJob.IsCanceled || diagnosticJob.Checksum == 0)
            throw new InvalidOperationException("The completed diagnostic decode did not finish successfully.");

        EngineWorkSchedulerMetrics metrics = WorkScheduler!.Metrics;
        if (metrics.JobAuxiliary is not { WorkerCount: 2, RunningWorkerCount: 2 })
        {
            throw new InvalidOperationException(
                "The scheduler-owned deferred-admission and remote-dispatch lanes are not both running.");
        }

        string schedulerSummary =
            $"[EngineWorkScheduler] smoke=passed sharedGeneralJobs=True " +
            $"generalWorkers={metrics.GeneralWorkerCount} " +
            $"jobAuxiliaryWorkers={metrics.JobAuxiliary.RunningWorkerCount}/{metrics.JobAuxiliary.WorkerCount} " +
            $"renderWorkers={metrics.Render.BackgroundWorkerCount} " +
            $"renderLogicalLanes={metrics.Render.LogicalLaneCount} inlineItems={metrics.Render.InlineItemCount} " +
            $"workerItems={metrics.Render.WorkerItemCount} " +
            $"maxMigratableItems={metrics.Render.MaxMigratableItemCount} " +
            $"parallelBatches={metrics.Render.ParallelMigratableBatchCount} " +
            $"unprofitableBatches={metrics.Render.UnprofitableBatchCount} " +
            $"capPinnedItems={metrics.Render.CapPinnedMigratableItemCount} " +
            $"allocationProof=passed allocationProofBatches={allocationProofBatchCount} " +
            $"proofBuildBytes=0 proofDispatchBytes=0 proofExecuteBytes=0 proofMergeBytes=0 " +
            $"diagnosticChecksum=0x{diagnosticJob.Checksum:X16}";
        Debug.Out(EOutputVerbosity.Normal, debugOnly: false, schedulerSummary);
        Debug.WriteAuxiliaryLog("work-scheduler.log", schedulerSummary);
    }

    private static RenderWorkBatchResult ExecuteSchedulerSmokeBatch(
        RenderWorkDomain domain,
        EngineSchedulerSmokeExecutor executor,
        int itemCount,
        int valuesPerItem)
    {
        RenderWorkBatchLease lease = domain.RentBatch(itemCount);
        try
        {
            for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
            {
                lease.SetItem(
                    itemIndex,
                    new RenderWorkItem(
                        OperationKind: 1,
                        SourceStart: itemIndex * valuesPerItem,
                        SourceCount: valuesPerItem));
            }

            return domain.ExecuteAndWait(
                ref lease,
                executor,
                frameSlot: 0,
                timeout: RenderWorkDomain.FatalBatchWait);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static void ValidateSchedulerSmokeResult(
        in RenderWorkBatchResult result,
        int[] preparedValues)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("The renderer-neutral scheduler smoke batch failed.", result.Exception);

        ValidateSchedulerSmokeOutput(preparedValues);
    }

    private static void ValidateSchedulerSmokeOutput(int[] preparedValues)
    {
        for (int index = 0; index < preparedValues.Length; index++)
        {
            int expected = unchecked(((index + 1) * 31) ^ 0x5A5A);
            if (preparedValues[index] != expected)
            {
                throw new InvalidOperationException(
                    $"Scheduler smoke output mismatch at {index}: expected {expected}, received {preparedValues[index]}.");
            }
        }
    }

    private static void ValidateSchedulerAllocationProof(
        in RenderWorkDomainMetrics baseline,
        in RenderWorkDomainMetrics result)
    {
        bool observedEveryStage =
            result.BuildOperationCount > baseline.BuildOperationCount &&
            result.DispatchOperationCount > baseline.DispatchOperationCount &&
            result.ExecuteOperationCount > baseline.ExecuteOperationCount &&
            result.MergeOperationCount > baseline.MergeOperationCount;
        if (!observedEveryStage)
        {
            throw new InvalidOperationException(
                "The post-warmup scheduler allocation proof did not observe every measured lifecycle stage.");
        }

        if (result.HasNoManagedAllocationsSince(baseline))
            return;

        throw new InvalidOperationException(
            "Post-warmup renderer-neutral work allocated managed memory: " +
            $"build={result.BuildAllocatedBytes - baseline.BuildAllocatedBytes}, " +
            $"dispatch={result.DispatchAllocatedBytes - baseline.DispatchAllocatedBytes}, " +
            $"execute={result.ExecuteAllocatedBytes - baseline.ExecuteAllocatedBytes}, " +
            $"merge={result.MergeAllocatedBytes - baseline.MergeAllocatedBytes} bytes.");
    }
}
