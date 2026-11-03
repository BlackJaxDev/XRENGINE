using XREngine.Execution;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The split desktop frame of <see cref="EVrDesktopFrameExecutionMode.WorkerRecord"/>.
/// The first half prepares the frame slot, builds the plan and hands the recording
/// to the worker lane, then ends without an acquire and without advancing the frame
/// slot. The next desktop opportunity runs the second half on the same frame slot:
/// it acquires, joins the worker, copies, submits and presents. The worker records
/// while the render thread runs the next eye frame.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    private readonly VulkanDesktopWorkerCarry _desktopWorkerCarry = new();
    // True while the first half of a split worker frame prepares its recording.
    private bool _desktopWorkerFirstHalf;
    // True while a frame that will run as a first half prepares its frame slot.
    private bool _desktopWorkerPreparingFirstHalf;
    // True after a second half submitted its scene in this desktop callback. The
    // callback then starts the next first half at once, with this callback's operations.
    private bool _desktopWorkerSecondHalfPresented;
    // True when this desktop callback ran a second half. A second half consumes no
    // desktop operations, so the callback's operations go to the next first half.
    private bool _desktopWorkerSecondHalfRan;
    private long _lastSplitOutcomeReportTicks;
    private int _reportedSecondHalfFailureCount;
    private int _reportedSplitExceptionCount;
    private long _lastSplitRecoveryRequestTicks;
    // The cost of the last follow-on first halves, in milliseconds. It decays slowly,
    // so one fast first half cannot admit a slow one into a short XR slack window.
    private double _desktopWorkerFirstHalfCostMs = DesktopWorkerInitialFirstHalfCostMs;
    private const double DesktopWorkerInitialFirstHalfCostMs = 1.5;
    private const double DesktopWorkerFirstHalfSlackMarginMs = 0.5;

    /// <summary>
    /// True when the follow-on first half can finish before the next XR frame needs the
    /// render thread. A first half that runs past the XR wake delays the eye batch, and
    /// the eye frame then misses its deadline. Without an XR frame estimate, or with the
    /// slack gate user setting off, the first half always runs.
    /// </summary>
    private bool CanRunFollowOnDesktopFirstHalf()
    {
        if (!RuntimeRenderingHostServices.Presentation.VrDesktopSplitSlackGate)
            return true;
        double slackMs = RuntimeEngine.VRState.OpenXRApi?.NextFrameSlackMs ?? -1.0;
        return slackMs < 0.0 || slackMs >= _desktopWorkerFirstHalfCostMs + DesktopWorkerFirstHalfSlackMarginMs;
    }

    /// <summary>Records the measured cost of a follow-on first half.</summary>
    private void RecordFollowOnDesktopFirstHalfCost(long startTicks)
    {
        double costMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _desktopWorkerFirstHalfCostMs = costMs >= _desktopWorkerFirstHalfCostMs
            ? costMs
            : _desktopWorkerFirstHalfCostMs * 0.95 + costMs * 0.05;
    }

    /// <summary>
    /// True when this desktop attempt splits across two desktop opportunities.
    /// An interactive resize keeps the single-transaction path.
    /// </summary>
    private bool ShouldSplitDesktopWorkerFrame(in VulkanFrameAttempt attempt)
    {
        IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
        return presentation.IsInVR &&
            presentation.VrDesktopFrameExecutionMode == EVrDesktopFrameExecutionMode.WorkerRecord &&
            !attempt.InteractiveResize &&
            _commandRuntime.RenderDomain.LogicalLaneCount > DesktopRecordLaneId;
    }

    /// <summary>
    /// True when the pending recording still targets the live offscreen image of its
    /// frame slot. A swapchain recreation retires the offscreen images, and a scene
    /// recorded at the old extent must not be copied into the new swapchain.
    /// </summary>
    private bool IsDesktopWorkerCarryTargetCurrent()
    {
        VulkanDesktopOffscreenColorResources? offscreen = OutputRuntime.Desktop.OffscreenColorResources;
        int slot = _desktopWorkerCarry.FrameSlot;
        return offscreen is not null &&
            (uint)slot < (uint)offscreen.Images.Length &&
            offscreen.Images[slot].Handle == _desktopWorkerCarry.DesktopOffscreenImage.Handle &&
            offscreen.Extent.Width == OutputRuntime.Desktop.Extent.Width &&
            offscreen.Extent.Height == OutputRuntime.Desktop.Extent.Height;
    }

    /// <summary>Prepares the frame slot; a first half does not wait for the swapchain images.</summary>
    private EDesktopFrameFlow PrepareDesktopFrameSlotForSplit(ref VulkanFrameAttempt attempt, bool firstHalf)
    {
        _desktopWorkerPreparingFirstHalf = firstHalf;
        try
        {
            return PrepareDesktopFrameSlot(ref attempt);
        }
        finally
        {
            _desktopWorkerPreparingFirstHalf = false;
        }
    }

    /// <summary>
    /// Runs the first half after readiness: builds the plan, starts the worker
    /// recording and ends without an acquire.
    /// </summary>
    private VulkanDesktopFramePhaseResult RunDesktopWorkerFirstHalf(ref VulkanFrameAttempt attempt)
    {
        VulkanPrimaryCommandRecordingResult result;
        _desktopWorkerFirstHalf = true;
        try
        {
            result = RecordPreparedDesktopPrimary(
                ref attempt,
                imageIndex: 0,
                preserveSwapchainForImGuiOverlay: false);
        }
        finally
        {
            _desktopWorkerFirstHalf = false;
        }

        if (_desktopWorkerCarry.IsActive)
        {
            _desktopWorkerCarry.CaptureReadiness(ref attempt);
            attempt.Reason = EDesktopFrameReason.RecordingDeferred;
            return attempt.CompletePhase(EVulkanFrameStage.CommandRecord, EDesktopFrameFlow.Completed);
        }

        ReportDesktopSplitOutcome(
            "first half did not start the worker",
            in attempt,
            $"disposition={result.Disposition} failure={result.FailureKind} reason={result.Reason ?? "<none>"} " +
            $"acceptedPlan={(attempt.PresentNowReadinessCompleted && attempt.AcceptedFramePlan is not null)}");
        attempt.Stop(EDesktopFrameReason.RecordingDeferred);
        return attempt.CompletePhase(EVulkanFrameStage.CommandRecord, EDesktopFrameFlow.Stop);
    }

    /// <summary>
    /// Hands the frozen input to the worker lane. The plan, the operation leases
    /// and the lane arena guard stay in the carry until the second half.
    /// </summary>
    private void StartDesktopWorkerRecording(
        ref VulkanFrameAttempt attempt,
        in VulkanPreparedPrimaryCommandInput input,
        FramePlan framePlan,
        FrameOp[] staticOperations,
        int staticOperationCount,
        FrameOp[] dynamicUiOperations,
        int dynamicUiOperationCount,
        FrameOp[] textureUploadOperations,
        int textureUploadOperationCount)
    {
        RenderWorkDomain domain = _commandRuntime.RenderDomain;
        RenderWorkAdmissionResult admission = domain.TryRentAsynchronousBatch(
            _desktopRecordExecutor,
            itemCount: 1,
            dependencyCount: 0,
            out RenderWorkBatchLease lease);
        if (!admission.IsAdmitted)
            throw new InvalidOperationException($"Desktop worker recording was not admitted: {admission.Reason}.");

        VulkanPendingDesktopRecord record = _desktopWorkerCarry.Record;
        bool submitted = false;
        try
        {
            admission = domain.TryReserveAsynchronousLanes(lease, 1UL << DesktopRecordLaneId);
            if (!admission.IsAdmitted)
            {
                throw new InvalidOperationException(
                    $"Desktop worker recording could not reserve lane {DesktopRecordLaneId}: {admission.Reason}.");
            }

            int laneFrameSlot = _commandRuntime.ResolveRenderLaneFrameSlot(input.FrameDataSlotIndex);
            record.BeginPreparation(
                _commandRuntime.GetRenderLaneAttachment(DesktopRecordLaneId, laneFrameSlot).Graphics,
                DesktopRecordLaneId,
                laneFrameSlot,
                input.FrameDataSlotIndex);
            _desktopRecordExecutor.Prepare(
                _commandRuntime,
                CaptureDesktopRecordingPlannerGeneration(),
                in input,
                DesktopRecordLaneId);
            lease.SetItem(
                0,
                new RenderWorkItem(
                    VulkanDesktopRecordWorkExecutor.DesktopPrimaryOperationKind,
                    SourceStart: 0,
                    SourceCount: 1,
                    PreferredLane: DesktopRecordLaneId,
                    EstimatedCost: 64));
            long submitTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            FramePacingTimeline.Mark(EFramePacingMark.DesktopWorkerSubmit);
            domain.SubmitAsynchronous(lease, _desktopRecordExecutor, laneFrameSlot);
            submitted = true;
            record.MarkSubmitted(in lease);
            _desktopWorkerCarry.BeginFirstHalf(
                attempt.FrameSlot,
                attempt.FrameNumber,
                submitTicks,
                attempt.DesktopOffscreenImage,
                framePlan,
                input.PrimaryCommandBuffer,
                staticOperations,
                staticOperationCount,
                dynamicUiOperations,
                dynamicUiOperationCount,
                textureUploadOperations,
                textureUploadOperationCount);
        }
        catch
        {
            if (submitted)
                _ = domain.JoinAsynchronous(lease);
            _desktopRecordExecutor.Clear();
            if (record.Phase == EVulkanPendingDesktopRecordPhase.Submitted)
                record.MarkJoined(default);
            record.Discard();
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Joins the worker for the second half and returns its result under the
    /// present-now contract of the first half's plan. Releases the operation leases,
    /// and settles the submission markers when nothing will be submitted.
    /// </summary>
    private VulkanPrimaryCommandRecordingResult FinishDesktopWorkerRecording(ref VulkanFrameAttempt attempt)
    {
        VulkanDesktopWorkerCarry carry = _desktopWorkerCarry;
        VulkanPrimaryCommandRecordingResult result = JoinDesktopWorkerRecording(carry, out Exception? workerFailure);
        try
        {
            if (workerFailure is not null)
            {
                _ = ReportWorkerFailure(workerFailure);
                result = VulkanPrimaryCommandRecordingResult.Failed(
                    $"Desktop worker recording failed: {workerFailure.Message}",
                    attempt.ReadinessPolicy,
                    attempt.WorkClass,
                    attempt.FrameNumber,
                    EVulkanCommandRecordingFailureKind.RetryFrame);
            }
            else
            {
                result = ApplyDesktopPresentNowResultContract(ref attempt, result, carry.FramePlan!);
                if (result.RequiresReplan)
                {
                    // The first half sealed the plan, so it cannot be rebound now.
                    result = result with
                    {
                        Disposition = EVulkanPrimaryCommandRecordingDisposition.Failed,
                        FailureKind = EVulkanCommandRecordingFailureKind.RetryFrame,
                    };
                }
                if (!result.Succeeded)
                {
                    ReportDesktopSplitOutcome(
                        "second half got an unsuccessful recording",
                        in attempt,
                        $"disposition={result.Disposition} failure={result.FailureKind} reason={result.Reason ?? "<none>"}");
                }
            }

            if (!result.Succeeded || result.CommandBuffer.Handle == 0)
            {
                _commandRuntime.DiscardSubmissionMarkersForCommandBuffer(carry.PrimaryCommandBuffer);
                carry.AcceptedPlan?.SettleUnsubmittedSubmissionMarkers();
            }
            return result;
        }
        finally
        {
            ReleaseDesktopWorkerOperations(carry);
        }
    }

    /// <summary>Joins the pending worker batch and takes its result or its failure.</summary>
    private VulkanPrimaryCommandRecordingResult JoinDesktopWorkerRecording(
        VulkanDesktopWorkerCarry carry,
        out Exception? failure)
    {
        failure = null;
        VulkanPendingDesktopRecord record = carry.Record;
        RenderWorkBatchResult batchResult = _commandRuntime.RenderDomain.JoinAsynchronous(record.Lease);
        FramePacingTimeline.Mark(EFramePacingMark.DesktopWorkerJoined);
        RecordWorkerTiming(carry.SubmitTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        record.MarkJoined(in batchResult);
        record.Lease.Dispose();
        if (!batchResult.Succeeded)
        {
            _desktopRecordExecutor.Clear();
            failure = new InvalidOperationException(
                $"Desktop worker recording batch ended as {batchResult.Status}.",
                batchResult.Exception);
            return default;
        }

        try
        {
            return _desktopRecordExecutor.TakeResult();
        }
        catch (Exception ex)
        {
            failure = ex;
            return default;
        }
    }

    /// <summary>
    /// Ends the carry after the second half. The lane arena guard ends as accepted
    /// when the queue accepted the scene, otherwise as discarded.
    /// </summary>
    private void CompleteDesktopWorkerCarry(bool submissionAccepted)
    {
        VulkanPendingDesktopRecord record = _desktopWorkerCarry.Record;
        if (submissionAccepted &&
            record.Phase == EVulkanPendingDesktopRecordPhase.Recorded &&
            record.JoinResult.Succeeded)
        {
            record.CompleteAfterAcceptedSubmission();
        }
        else
        {
            record.Discard();
        }
        // The carry released the operations' leases; the plan must not release
        // them again once the workspace reuses them.
        _desktopWorkerCarry.AcceptedPlan?.DetachReleasedOperations();
        _desktopWorkerCarry.Clear();
    }

    /// <summary>
    /// Drops a pending worker recording without presenting it, for example when the
    /// mode, the VR state or the frame slot changed. Joins the worker first, because
    /// it can still use the lane arena.
    /// </summary>
    private void DiscardDesktopWorkerCarry(string reason)
    {
        VulkanDesktopWorkerCarry carry = _desktopWorkerCarry;
        if (!carry.IsActive)
            return;

        try
        {
            if (carry.Record.Phase == EVulkanPendingDesktopRecordPhase.Submitted)
            {
                VulkanPrimaryCommandRecordingResult result = JoinDesktopWorkerRecording(carry, out _);
                if (result.TextureUploadCommandBuffer.Handle != 0)
                {
                    Debug.WriteAuxiliaryLog(
                        "desktop-execution-mode.log",
                        "A discarded desktop worker recording held an unsubmitted texture-upload command buffer.");
                }
            }
            _commandRuntime.DiscardSubmissionMarkersForCommandBuffer(carry.PrimaryCommandBuffer);
            carry.AcceptedPlan?.SettleUnsubmittedSubmissionMarkers();
            ReleaseDesktopWorkerOperations(carry);
            Debug.WriteAuxiliaryLog("desktop-execution-mode.log", $"Discarded a pending desktop worker recording: {reason}");
        }
        finally
        {
            CompleteDesktopWorkerCarry(submissionAccepted: false);
        }
    }

    /// <summary>
    /// Writes a split-frame outcome to the auxiliary log at most once per second,
    /// because a Release build compiles out the general log.
    /// </summary>
    private void ReportDesktopSplitOutcome(string stage, in VulkanFrameAttempt attempt, string detail)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastSplitOutcomeReportTicks < System.Diagnostics.Stopwatch.Frequency)
            return;

        _lastSplitOutcomeReportTicks = now;
        Debug.WriteAuxiliaryLog(
            "desktop-execution-mode.log",
            $"Split desktop frame: {stage}; frame={attempt.FrameNumber} slot={attempt.FrameSlot} " +
            $"reason={attempt.Reason} flow={attempt.Flow} carry={_desktopWorkerCarry.IsActive} {detail} " +
            $"rejected={attempt.RejectedFailure?.Message ?? "<none>"} deferred={attempt.DeferredFailure?.Message ?? "<none>"}");
    }

    /// <summary>
    /// A recoverable present-now failure waits for a renderer state change before it
    /// probes again. The split frame produces no such change by itself, so it asks
    /// for a probe at most twice per second; a terminal failure ignores the request.
    /// </summary>
    private void RequestDesktopSplitReadinessRecovery(in VulkanFrameAttempt attempt)
    {
        if (attempt.Reason != EDesktopFrameReason.PresentNowReadinessFailed)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastSplitRecoveryRequestTicks < System.Diagnostics.Stopwatch.Frequency / 2)
            return;

        _lastSplitRecoveryRequestTicks = now;
        RequestPresentNowRecovery("split desktop worker frame retry");
    }

    /// <summary>
    /// Writes the first desktop frame exceptions in WorkerRecord mode to the
    /// auxiliary log. The window backs off its render loop after an exception, so an
    /// unreported exception shows only as a missing eye frame.
    /// </summary>
    private void ReportDesktopSplitException(Exception failure, in VulkanFrameAttempt attempt)
    {
        if (_reportedSplitExceptionCount >= 20 ||
            RuntimeRenderingHostServices.Presentation.VrDesktopFrameExecutionMode != EVrDesktopFrameExecutionMode.WorkerRecord)
        {
            return;
        }

        _reportedSplitExceptionCount++;
        Debug.WriteAuxiliaryLog(
            "desktop-execution-mode.log",
            $"Desktop frame exception: frame={attempt.FrameNumber} slot={attempt.FrameSlot} carry={_desktopWorkerCarry.IsActive} " +
            $"phase={attempt.Phase} {failure}");
    }

    private static void ReleaseDesktopWorkerOperations(VulkanDesktopWorkerCarry carry)
    {
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            carry.StaticOperations.AsSpan(0, carry.StaticOperationCount));
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            carry.DynamicUiOperations.AsSpan(0, carry.DynamicUiOperationCount));
        VulkanAdvancedVisibilityInputLease.ReleaseOperations(
            carry.TextureUploadOperations.AsSpan(0, carry.TextureUploadOperationCount));
    }
}
