using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
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
                return false;
            }

            preparedFirstEye = preparedFirstEye with { PairedLogicalPlan = pairedLogicalPlan };
            preparedSecondEye = preparedSecondEye with { PairedLogicalPlan = pairedLogicalPlan };
        }

        if (!TryFreezeOpenXrEyeRecordWorkerInput(in preparedFirstEye, out OpenXrPreparedEyeRecordWorkerInput frozenFirstEye) ||
            !TryFreezeOpenXrEyeRecordWorkerInput(in preparedSecondEye, out OpenXrPreparedEyeRecordWorkerInput frozenSecondEye))
        {
            return false;
        }

        VulkanOpenXrEyeWorkerCommandService workers = _commandRuntime.OpenXrEyeWorkers;
        OpenXrRecordedEyeCommandBuffer diagnosticFirst = CreateOpenXrWorkerDiagnosticRecord(in frozenFirstEye);
        OpenXrRecordedEyeCommandBuffer diagnosticSecond = CreateOpenXrWorkerDiagnosticRecord(in frozenSecondEye);
        VulkanSubmissionDiagnosticContext diagnosticContext =
            CreateOpenXrBatchSubmissionDiagnosticContext(
                "OpenXrEyeParallelBatchSubmit",
                "OpenXrEyeParallelBatch",
                in diagnosticFirst,
                in diagnosticSecond,
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
            return false;
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
