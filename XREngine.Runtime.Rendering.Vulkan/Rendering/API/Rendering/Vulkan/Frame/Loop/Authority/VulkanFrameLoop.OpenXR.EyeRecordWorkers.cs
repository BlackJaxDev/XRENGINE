using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private bool TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
    {
        OpenXrPreparedEyeCommandBufferInput preparedFirstEye;
        OpenXrPreparedEyeCommandBufferInput preparedSecondEye;
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.PrepareInputs"))
        {
            if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out preparedFirstEye) ||
                !TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out preparedSecondEye) ||
                !TryCreatePairedOpenXrLogicalPlan(
                    in preparedFirstEye,
                    in preparedSecondEye,
                    out FramePlan pairedLogicalPlan))
            {
                throw CreateOpenXrEyePresentNowFailure(
                    firstEye.OpenXrViewIndex,
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    "parallel-paired-plan",
                    "OpenXREyeSubmit -> parallel paired logical plan",
                    "Foreground parallel-eye preparation returned no sealed paired plan.");
            }

            preparedFirstEye = BindOpenXrEyeOutputContract(
                in preparedFirstEye,
                pairedLogicalPlan);
            preparedSecondEye = BindOpenXrEyeOutputContract(
                in preparedSecondEye,
                pairedLogicalPlan);
        }

        if (!TryFreezeOpenXrEyeRecordWorkerInput(in preparedFirstEye, out OpenXrPreparedEyeRecordWorkerInput frozenFirstEye) ||
            !TryFreezeOpenXrEyeRecordWorkerInput(in preparedSecondEye, out OpenXrPreparedEyeRecordWorkerInput frozenSecondEye))
        {
            throw CreateOpenXrEyePresentNowFailure(
                firstEye.OpenXrViewIndex,
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "parallel-freeze",
                "OpenXREyeSubmit -> parallel immutable worker inputs",
                "Foreground parallel-eye freeze returned no exact worker input.");
        }

        VulkanOpenXrEyeWorkerCommandService workers = _commandRuntime.OpenXrEyeWorkers;
        OpenXrRecordedEyeCommandBuffer diagnosticFirst = CreateOpenXrWorkerDiagnosticRecord(in frozenFirstEye);
        OpenXrRecordedEyeCommandBuffer diagnosticSecond = CreateOpenXrWorkerDiagnosticRecord(in frozenSecondEye);
        VulkanSubmissionDiagnosticContext diagnosticContext =
            _commandRuntime.CreateOpenXrBatchSubmissionDiagnosticContext(
                AcceptedAttemptCount,
                "OpenXrEyeParallelBatchSubmit",
                "OpenXrEyeParallelBatch",
                in diagnosticFirst,
                firstEye.Extent);
        VulkanOpenXrEyeWorkerCommandResult result;
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.WorkerRecordAndSubmit"))
        {
            result = workers.Execute(
                in frozenFirstEye,
                in frozenSecondEye,
                in diagnosticContext);
        }

        OpenXrEyeRecordWorkerBatchResult batch = result.Batch;
        if (!batch.Left.Success || !batch.Right.Success)
        {
            LogOpenXrEyeRecordWorkerFailure(in batch);
            throw CreateOpenXrEyePresentNowFailure(
                firstEye.OpenXrViewIndex,
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "parallel-primary",
                "OpenXREyeSubmit -> parallel exact primary recording",
                "Foreground parallel eye recording returned an unsuccessful batch without a propagated exception.");
        }

        if (result.Submitted)
        {
            OpenXrRecordedEyeCommandBuffer leftRecorded = batch.Left.Recorded;
            OpenXrRecordedEyeCommandBuffer rightRecorded = batch.Right.Recorded;
            CompleteOpenXrGpuProfilerSubmission(in leftRecorded);
            CompleteOpenXrGpuProfilerSubmission(in rightRecorded);
            ForceFlushCompletedNonImageRetiredResources();
        }

        return result.Submitted;
    }

    private void DestroyOpenXrEyeRecordWorkers()
        => _commandRuntime.OpenXrEyeWorkers.Dispose();

    private static OpenXrRecordedEyeCommandBuffer CreateOpenXrWorkerDiagnosticRecord(
        in OpenXrPreparedEyeRecordWorkerInput prepared)
        => new(
            default,
            prepared.FrameContext,
            prepared.OpenXrViewIndex,
            prepared.OpenXrImageIndex,
            prepared.FrameDataSlotIndex,
            prepared.LogicalViewId,
            prepared.RequiredOutputIndex,
            prepared.OutputContract,
            prepared.FrameOpsSignature,
            prepared.PlannerRevision,
            prepared.FrameOpContextId,
            prepared.ResourceGeneration,
            prepared.DescriptorGeneration,
            OwnedByOpenXrPrimaryCache: true);

    private static void LogOpenXrEyeRecordWorkerFailure(in OpenXrEyeRecordWorkerBatchResult batch)
    {
        Debug.VulkanWarningEvery(
            "OpenXR.Vulkan.ParallelCommandBufferRecording.RecordFailure",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Parallel eye primary recording failed. leftSuccess={0} rightSuccess={1} leftThread={2} rightThread={3} leftError={4} rightError={5}",
            batch.Left.Success,
            batch.Right.Success,
            batch.Left.ThreadId,
            batch.Right.ThreadId,
            batch.Left.ErrorMessage ?? "<none>",
            batch.Right.ErrorMessage ?? "<none>");
    }
}
