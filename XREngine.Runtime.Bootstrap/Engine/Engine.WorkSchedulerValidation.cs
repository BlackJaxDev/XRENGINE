using XREngine.Execution;
using XREngine.Rendering;

namespace XREngine;

public static partial class Engine
{
    /// <summary>
    /// Proves the installed runtime work capability with one non-native render
    /// preparation batch and one already-completed telemetry decode.
    /// </summary>
    private static void ValidateInstalledWorkScheduler()
    {
        IRuntimeRenderWorkServices work = RuntimeRenderingHostServices.Work;
        if (!ReferenceEquals(work.GeneralJobs, Jobs))
            throw new InvalidOperationException("Engine.Jobs and RuntimeEngine.Jobs do not share the scheduler general domain.");
        if (!ReferenceEquals(RuntimeEngine.Jobs, Jobs))
            throw new InvalidOperationException("RuntimeEngine.Jobs constructed or resolved a second JobManager.");

        const int itemCount = 4;
        const int valuesPerItem = 8;
        int[] preparedValues = new int[itemCount * valuesPerItem];
        var executor = new EngineSchedulerSmokeExecutor(preparedValues);
        RenderWorkBatchLease lease = work.RenderWork.RentBatch(itemCount);
        RenderWorkBatchResult renderResult;
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

            renderResult = work.RenderWork.ExecuteAndWait(
                ref lease,
                executor,
                frameSlot: 0,
                timeout: RenderWorkDomain.FatalBatchWait);
        }
        finally
        {
            lease.Dispose();
        }

        if (!renderResult.Succeeded)
            throw new InvalidOperationException("The renderer-neutral scheduler smoke batch failed.", renderResult.Exception);

        for (int index = 0; index < preparedValues.Length; index++)
        {
            int expected = unchecked(((index + 1) * 31) ^ 0x5A5A);
            if (preparedValues[index] != expected)
            {
                throw new InvalidOperationException(
                    $"Scheduler smoke output mismatch at {index}: expected {expected}, received {preparedValues[index]}.");
            }
        }

        uint[] completedWords = [0x13579BDFu, 0x2468ACE0u, 32u, (uint)itemCount];
        CompletedDiagnosticPayload payload = CompletedDiagnosticPayload.Create(completedWords);
        CompletedDiagnosticDecodeJob diagnosticJob = work.ScheduleCompletedDiagnosticDecode(payload);
        if (!diagnosticJob.Handle.Wait(RenderWorkDomain.FatalBatchWait))
            throw new TimeoutException("The completed diagnostic decode exceeded the fatal scheduler lifecycle bound.");
        if (diagnosticJob.IsFaulted || diagnosticJob.IsCanceled || diagnosticJob.Checksum == 0)
            throw new InvalidOperationException("The completed diagnostic decode did not finish successfully.");

        EngineWorkSchedulerMetrics metrics = WorkScheduler!.Metrics;
        Debug.Out(
            $"[EngineWorkScheduler] smoke=passed sharedGeneralJobs=True " +
            $"generalWorkers={metrics.GeneralWorkerCount} renderWorkers={metrics.Render.BackgroundWorkerCount} " +
            $"renderLogicalLanes={metrics.Render.LogicalLaneCount} inlineItems={metrics.Render.InlineItemCount} " +
            $"workerItems={metrics.Render.WorkerItemCount} diagnosticChecksum=0x{diagnosticJob.Checksum:X16}");
    }
}
