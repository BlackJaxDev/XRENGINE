using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private bool TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
    {
        if (!_commandRuntime.OpenXrSubmissionTracker.TryReserveSubmission(
                out OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? admissionTicket))
        {
            throw CreateOpenXrEyePresentNowFailure(
                firstEye.OpenXrViewIndex,
                EVulkanPresentNowReadinessStage.QueueSubmission,
                "parallel-eye-admission",
                "OpenXREyeSubmit -> bounded parallel submission admission",
                "OpenXR parallel submission capacity remained full after the bounded recovery wait.");
        }

        OpenXrPreparedEyeCommandBufferInput preparedFirstEye = default;
        OpenXrPreparedEyeCommandBufferInput preparedSecondEye = default;
        bool trackerOwnsSubmission = false;
        try
        {
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

            int firstLaneId = _commandRuntime.ResolveOpenXrEyeRenderLaneId(0);
            int secondLaneId = _commandRuntime.ResolveOpenXrEyeRenderLaneId(1);
            if (!TryFreezeOpenXrEyeRecordWorkerInput(
                    in preparedFirstEye,
                    firstLaneId,
                    out OpenXrPreparedEyeRecordWorkerInput frozenFirstEye) ||
                !TryFreezeOpenXrEyeRecordWorkerInput(
                    in preparedSecondEye,
                    secondLaneId,
                    out OpenXrPreparedEyeRecordWorkerInput frozenSecondEye))
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
                    in preparedFirstEye,
                    in preparedSecondEye,
                    admissionTicket!.Value,
                    ref trackerOwnsSubmission,
                    firstEye.SubmissionMetadata,
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

            if (!result.Submitted)
            {
                OpenXrRecordedEyeCommandBuffer leftRecorded = batch.Left.Recorded;
                ThrowOpenXrRecordedPresentNowSubmissionFailure(
                    in leftRecorded,
                    result.CommandBuffersCompleted,
                    "parallel-eye-submit");
            }

            return result.Submitted;
        }
        finally
        {
            try
            {
                if (!trackerOwnsSubmission)
                {
                    ReleasePreparedOpenXrEyeInput(in preparedSecondEye);
                    ReleasePreparedOpenXrEyeInput(in preparedFirstEye);
                }
            }
            finally
            {
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
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
            "[OpenXR] Parallel eye primary recording failed. leftSuccess={0} rightSuccess={1} leftManagedThread={2} rightManagedThread={3} leftError={4} rightError={5}",
            batch.Left.Success,
            batch.Right.Success,
            batch.Left.ThreadId,
            batch.Right.ThreadId,
            batch.Left.ErrorMessage ?? "<none>",
            batch.Right.ErrorMessage ?? "<none>");
    }
}
