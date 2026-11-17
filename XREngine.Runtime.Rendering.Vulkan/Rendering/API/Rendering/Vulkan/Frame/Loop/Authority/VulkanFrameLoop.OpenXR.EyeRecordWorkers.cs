using System;
using System.Diagnostics;
using XREngine.Execution;
using XREngine.Rendering.OutputDomains;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private bool TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
    {
        using RenderOutputDomainContext.Scope outputDomainScope = RenderOutputDomainContext.Enter(ERenderOutputDomain.Xr);
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
        RenderWorkBatchLease claimedEyeLease = default;
        bool trackerOwnsSubmission = false;
        try
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.PrepareInputs"))
            {
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out preparedFirstEye))
                    throw CreateOpenXrEyePresentNowFailure(
                        firstEye.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "parallel-first-eye-preparation",
                        "OpenXREyeSubmit -> first prepared eye",
                        "Foreground first-eye preparation returned no input.");
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out preparedSecondEye))
                    throw CreateOpenXrEyePresentNowFailure(
                        secondEye.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "parallel-second-eye-preparation",
                        "OpenXREyeSubmit -> second prepared eye",
                        "Foreground second-eye preparation returned no input.");
                if (!TryCreatePairedOpenXrLogicalPlan(
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
            if (firstLaneId == secondLaneId)
                ReportSerializedParallelEyeRecording(firstLaneId);
            // Claim both eye lanes before the freeze touches their backend attachments.
            claimedEyeLease = OpenXrEyeRecordWorkerScheduler.RentAndClaimLanes(
                _commandRuntime,
                firstLaneId,
                secondLaneId);
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
                RenderWorkBatchLease lease = claimedEyeLease;
                claimedEyeLease = default;
                result = workers.Execute(
                    lease,
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
                // The worker service owns the lease once it starts; release it here only
                // when a failure happened before that hand-off.
                if (claimedEyeLease.IsValid)
                    claimedEyeLease.Dispose();
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    // Set after the first serialized-recording warning, so this frame loop warns once.
    private int _serializedParallelEyeRecordingWarned;

    /// <summary>
    /// Reports a parallel eye recording request that has only one render lane. Both eyes then
    /// map to that lane, and the render work domain records them one after the other on the
    /// render thread. The method counts every such frame in the VR stats and logs one warning
    /// for this frame loop. The frame still renders; only the recording is serial.
    /// </summary>
    private void ReportSerializedParallelEyeRecording(int laneId)
    {
        RuntimeEngine.Rendering.Stats.Vr.RecordVrOpenXrParallelEyeRecordSerialized();
        if (System.Threading.Interlocked.Exchange(ref _serializedParallelEyeRecordingWarned, 1) != 0)
            return;

        Debug.VulkanWarning(
            "[OpenXR] VR.ViewRenderMode=ParallelCommandBufferRecording records both eyes on render lane {0}, one after the other, " +
            "because the render work domain has {1} lane(s) and needs 2. Set {2} to 1 or more to record the eyes in parallel. " +
            "The stat vr.process_openxr_parallel_eye_record_serialized_frames counts the affected frames.",
            laneId,
            _commandRuntime.RenderLogicalLaneCount,
            XREngineEnvironmentVariables.RenderWorkerThreads);
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
