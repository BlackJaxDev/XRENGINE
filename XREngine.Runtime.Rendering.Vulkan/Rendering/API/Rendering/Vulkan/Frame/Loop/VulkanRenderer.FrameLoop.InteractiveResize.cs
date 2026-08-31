using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Runs a bounded presentation transaction for a modal Windows resize callback.
    /// Scene readiness, scene recording, uploads, and collect-visible publication stay
    /// owned by the next ordinary frame.
    /// </summary>
    private void RunInteractiveResizeDesktopFramePhases(
        ref VulkanFrameAttempt attempt)
    {
        long phaseStarted = BeginDesktopFramePhase(
            EVulkanFrameStage.ResourcePrepare);
        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.ResourcePrepare,
                PrepareInteractiveResizeOverlay(ref attempt),
                phaseStarted).ShouldContinue)
        {
            return;
        }

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.OutputAcquire);
        EDesktopFrameFlow acquireFlow = AcquireDesktopSwapchainImageCore(
            ref attempt);
        if (acquireFlow != EDesktopFrameFlow.Continue)
        {
            _ = CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.OutputAcquire,
                acquireFlow,
                phaseStarted);
            return;
        }

        PrepareAcquiredDesktopImage(ref attempt);
        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.OutputAcquire,
                acquireFlow,
                phaseStarted).ShouldContinue)
        {
            return;
        }

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.CommandRecord);
        VulkanDesktopFramePhaseResult recordResult =
            RecordInteractiveResizeOverlay(ref attempt);
        CompleteDesktopFramePhaseTiming(
            ref attempt,
            EVulkanFrameStage.CommandRecord,
            phaseStarted);
        if (!recordResult.ShouldContinue)
            return;

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.QueueSubmit);
        VulkanDesktopFramePhaseResult submitResult =
            SubmitInteractiveResizeOverlay(ref attempt);
        CompleteDesktopFramePhaseTiming(
            ref attempt,
            EVulkanFrameStage.QueueSubmit,
            phaseStarted);
        if (!submitResult.ShouldContinue)
            return;

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.OutputComplete);
        _ = CompleteDesktopFramePhase(
            ref attempt,
            EVulkanFrameStage.OutputComplete,
            PresentSubmittedDesktopFrame(ref attempt),
            phaseStarted);
    }

    private EDesktopFrameFlow PrepareInteractiveResizeOverlay(
        ref VulkanFrameAttempt attempt)
    {
        long stageStartTimestamp = Stopwatch.GetTimestamp();
        DrainSkippedResizeFrameOps(
            "interactive resize overlay-only frame",
            preserveTextureUploads: true);

        attempt.ReadinessPolicy = ERenderOutputReadinessPolicy.AllowDeferral;
        attempt.WorkClass = ERenderOutputWorkClass.Background;
        if (!ImGuiOverlayAdmission.TryConsumeRenderableSnapshot(
                interactiveResizeInProgress: true,
                out VulkanImGuiFrameSnapshot? snapshot) ||
            snapshot is null)
        {
            attempt.Timing.SnapshotImGuiOverlay +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            MarkSkippedResizeFrameObserved(attempt.StartTimestamp);
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(CpuBudgetDeferrals: 1));
            attempt.Stop(EDesktopFrameReason.RecordingDeferred);
            return EDesktopFrameFlow.Stop;
        }

        attempt.InteractiveResizeOverlayOnly = true;
        attempt.InteractiveResizeImGuiSnapshot = snapshot;
        attempt.Timing.SnapshotImGuiOverlay +=
            Stopwatch.GetElapsedTime(stageStartTimestamp);
        return EDesktopFrameFlow.Continue;
    }

    private VulkanDesktopFramePhaseResult RecordInteractiveResizeOverlay(
        ref VulkanFrameAttempt attempt)
        => attempt.CompletePhase(
            EVulkanFrameStage.CommandRecord,
            RecordInteractiveResizeOverlayCore(ref attempt));

    private EDesktopFrameFlow RecordInteractiveResizeOverlayCore(
        ref VulkanFrameAttempt attempt)
    {
        VulkanImGuiFrameSnapshot snapshot =
            attempt.InteractiveResizeImGuiSnapshot ??
            throw new InvalidOperationException(
                "Interactive resize overlay recording requires an admitted ImGui snapshot.");

        bool hasValidPriorContent =
            OutputRuntime.Desktop.ImageHasValidPresentedContent is not null &&
            attempt.ImageIndex <
                OutputRuntime.Desktop.ImageHasValidPresentedContent.Length &&
            OutputRuntime.Desktop.ImageHasValidPresentedContent[attempt.ImageIndex];
        ImageLayout initialLayout = hasValidPriorContent
            ? ImageLayout.PresentSrcKhr
            : ImageLayout.Undefined;

        long stageStartTimestamp = Stopwatch.GetTimestamp();
        attempt.RecordStartedTimestamp = stageStartTimestamp;
        bool recorded;
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                   "Vulkan.FrameLifecycle.RecordInteractiveResizeOverlay"))
        {
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.OverlayRecording);
            recorded = TryRecordImGuiOverlay(
                attempt.ImageIndex,
                snapshot,
                initialLayout,
                predecessorCommandBuffer: default,
                // Every swapchain image keeps its last completed scene package.
                // Clear only a never-published image; clearing a valid image here
                // destroys the frozen scene that modal resize is meant to retain.
                clearSwapchain: !hasValidPriorContent,
                out attempt.ImGuiOverlayCommandBuffer);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(stageStartTimestamp);
        attempt.Timing.RecordImGuiOverlay += elapsed;
        attempt.Timing.RecordCommandBuffer += elapsed;
        attempt.RecordCompletedTimestamp = Stopwatch.GetTimestamp();
        if (!recorded)
        {
            return RecoverUnavailableInteractiveResizeOverlay(
                ref attempt,
                snapshot);
        }

        attempt.HasImGuiOverlayCommandBuffer = true;
        attempt.ScenePrimaryRecordedThisFrame = false;
        attempt.SceneSwapchainWriteCount = 0;
        attempt.SwapchainLayoutAfterScene = ImageLayout.PresentSrcKhr;
        attempt.AdvanceTo(EDesktopFramePhase.Recorded);
        attempt.AdvanceTo(EDesktopFramePhase.Validated);

        RecordOverlayFrameOutput(
            EFrameOutputKind.ImGuiOverlay,
            "Vulkan interactive-resize ImGui overlay command buffer",
            rendered: true,
            commandCount: 1,
            Stopwatch.GetTimestamp() - stageStartTimestamp);
        return EDesktopFrameFlow.Continue;
    }

    private EDesktopFrameFlow RecoverUnavailableInteractiveResizeOverlay(
        ref VulkanFrameAttempt attempt,
        VulkanImGuiFrameSnapshot snapshot)
    {
        if (TryRecoverRejectedDesktopImage(
                ref attempt,
                commandBufferDirtyFlagSet: false,
                commandBuffersDirtiedAfterSceneRecord: false,
                recordedSwapchainWriteCount: 0,
                rejectionStage: "InteractiveResizeOverlayUnavailable",
                rejectedSubmitResult: null,
                recoveryOverlaySnapshot: snapshot))
        {
            attempt.Reason = EDesktopFrameReason.RecordingDeferred;
            attempt.Flow = EDesktopFrameFlow.Completed;
            return EDesktopFrameFlow.Completed;
        }

        _ = ConsumeDesktopAcquireForRecovery(
            ref attempt,
            "InteractiveResizeOverlayUnavailable");
        ResolveDesktopAcquireBySwapchainRecreation(
            ref attempt,
            "Interactive resize overlay could not settle the acquired image");
        CompleteDesktopFrameSlot(ref attempt);
        attempt.Stop(
            EDesktopFrameReason.OverlayRecordingFailed,
            EDesktopFrameRecoveryAction.RecreateSwapchain);
        return EDesktopFrameFlow.Stop;
    }

    private VulkanDesktopFramePhaseResult SubmitInteractiveResizeOverlay(
        ref VulkanFrameAttempt attempt)
        => attempt.CompletePhase(
            EVulkanFrameStage.QueueSubmit,
            SubmitInteractiveResizeOverlayCore(ref attempt));

    private unsafe EDesktopFrameFlow SubmitInteractiveResizeOverlayCore(
        ref VulkanFrameAttempt attempt)
    {
        _ = attempt.CompletePhase(
            EVulkanFrameStage.SubmitPrepare,
            EDesktopFrameFlow.Continue);
        if (!attempt.HasImGuiOverlayCommandBuffer ||
            attempt.ImGuiOverlayCommandBuffer.Handle == 0)
        {
            return RecoverUnavailableInteractiveResizeOverlay(
                ref attempt,
                attempt.InteractiveResizeImGuiSnapshot ??
                throw new InvalidOperationException(
                    "Interactive resize submission lost its ImGui snapshot."));
        }

        ThrowIfDesktopFrameFaultInjected(
            EVulkanDesktopFrameFaultPoint.Submission);
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[1];
        commandBuffers[0] = attempt.ImGuiOverlayCommandBuffer;
        VulkanSubmissionDiagnosticContext diagnosticContext =
            CreateDesktopSubmissionDiagnosticContext(
                "InteractiveResizeOverlay",
                attempt.ImageIndex,
                attempt.FrameNumber,
                attempt.FrameSlot,
                attempt.AcquireTimelineValue,
                0UL,
                0L,
                0UL,
                ResourcePlannerRevision,
                0UL,
                0UL,
                _resourceRuntime.DescriptorTableGeneration);

        long stageStartTimestamp = Stopwatch.GetTimestamp();
        attempt.SubmitStartedTimestamp = stageStartTimestamp;
        VulkanSubmissionReceipt receipt;
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                   "Vulkan.FrameLifecycle.SubmitInteractiveResizeOverlay"))
        using (VulkanCpuStageScope cpuStage =
               new(_frameTelemetry, EVulkanCpuStage.Submission))
        {
            receipt = SubmitFrameTargetLease(
                in attempt.FrameTargetLease,
                commandBuffers,
                commandBufferCount: 1,
                signalGraphicsTimeline: true,
                minimumGraphicsTimelineSignalValue:
                    attempt.AcquireTimelineValue + 1UL,
                out attempt.GraphicsSignalValue,
                in diagnosticContext,
                caller: "InteractiveResizeCallback");
            attempt.SubmitResult = receipt.Result;
            if (receipt.SubmissionAccepted)
            {
                attempt.Submitted = true;
                attempt.CommandArtifactsSettled = true;
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .ConsumedBySubmissionImagePendingPresent);
                attempt.AdvanceTo(EDesktopFramePhase.Submitted);
                PublishAcceptedDesktopSubmissionReuseLedgers(ref attempt);
            }
        }

        attempt.Timing.SubmitQueue +=
            Stopwatch.GetElapsedTime(stageStartTimestamp);
        attempt.SubmitCompletedTimestamp = Stopwatch.GetTimestamp();
        if (!receipt.SubmissionAccepted)
        {
            return HandleInteractiveResizeOverlaySubmitFailure(
                ref attempt,
                receipt.Result);
        }

        ReleaseCollectForDesktopFrame(ref attempt);
        return EDesktopFrameFlow.Continue;
    }

    private EDesktopFrameFlow HandleInteractiveResizeOverlaySubmitFailure(
        ref VulkanFrameAttempt attempt,
        Result submitResult)
    {
        if (submitResult == Result.ErrorDeviceLost)
        {
            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss);
            attempt.Stop(
                EDesktopFrameReason.SubmitFailed,
                EDesktopFrameRecoveryAction.DeviceLost);
            throw CreateDeviceLostException(
                "Interactive resize overlay QueueSubmit",
                submitResult);
        }

        VulkanImGuiFrameSnapshot? snapshot =
            attempt.InteractiveResizeImGuiSnapshot;
        if (TryRecoverRejectedDesktopImage(
                ref attempt,
                commandBufferDirtyFlagSet: false,
                commandBuffersDirtiedAfterSceneRecord: false,
                recordedSwapchainWriteCount: 0,
                rejectionStage: "InteractiveResizeOverlaySubmitRejected",
                rejectedSubmitResult: submitResult,
                recoveryOverlaySnapshot: snapshot))
        {
            attempt.Reason = EDesktopFrameReason.SubmitFailed;
            attempt.Flow = EDesktopFrameFlow.Completed;
            throw new InvalidOperationException(
                $"Failed to submit interactive resize overlay ({submitResult}); acquired image ownership was recovered.");
        }

        _ = ConsumeDesktopAcquireForRecovery(
            ref attempt,
            $"InteractiveResizeOverlaySubmitRejected:{submitResult}");
        ResolveDesktopAcquireBySwapchainRecreation(
            ref attempt,
            $"Interactive resize overlay submit failed with {submitResult}");
        CompleteDesktopFrameSlot(ref attempt);
        attempt.Stop(
            EDesktopFrameReason.SubmitFailed,
            EDesktopFrameRecoveryAction.RecreateSwapchain);
        throw new InvalidOperationException(
            $"Failed to submit interactive resize overlay ({submitResult}).");
    }
}
