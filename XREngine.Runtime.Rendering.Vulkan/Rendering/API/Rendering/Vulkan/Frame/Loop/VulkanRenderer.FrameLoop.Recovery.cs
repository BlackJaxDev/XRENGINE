using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private void ResolveDesktopAcquireBySwapchainRecreation(
            ref VulkanFrameAttempt attempt,
            string reason)
        {
            if (!TryRecreateSwapchainNow(reason))
            {
                throw new InvalidOperationException(
                    $"Swapchain recreation could not settle acquired desktop image ownership. Reason={reason}");
            }

            if (attempt.AcquireOwnership is
                EVulkanDesktopAcquireOwnership.AcquiredUnresolved or
                EVulkanDesktopAcquireOwnership
                    .ConsumedByRecoveryImagePendingPresent or
                EVulkanDesktopAcquireOwnership
                    .ConsumedBySubmissionImagePendingPresent)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .ResolvedBySwapchainInvalidation);
            }
        }

        internal void SettleDesktopAcquireAfterUnexpectedFailure(
            ref VulkanFrameAttempt attempt,
            Exception primaryFailure)
        {
            EVulkanDesktopPostAcquireFailureStage failureStage =
                attempt.AcquireOwnership is
                    EVulkanDesktopAcquireOwnership
                        .ConsumedBySubmissionImagePendingPresent or
                    EVulkanDesktopAcquireOwnership
                        .ConsumedByRecoveryImagePendingPresent
                    ? EVulkanDesktopPostAcquireFailureStage
                        .PostSubmitAuxiliary
                    : EVulkanDesktopPostAcquireFailureStage.Recording;
            VulkanDesktopRecoveryOutcome recoveryOutcome =
                VulkanDesktopFramePolicy.ResolvePostAcquireFailure(
                    failureStage,
                    _deviceLost,
                    recreateSwapchainAfterResolution: false);
            if (_deviceLost)
            {
                AbandonDesktopOwnershipAfterDeviceLoss(ref attempt);
                return;
            }

            try
            {
                switch (attempt.AcquireOwnership)
                {
                    case EVulkanDesktopAcquireOwnership.AcquiredUnresolved:
                        SettleUnsubmittedDesktopAcquire(ref attempt);
                        break;
                    case EVulkanDesktopAcquireOwnership
                        .ConsumedBySubmissionImagePendingPresent
                        when recoveryOutcome.MustSettlePresentation:
                        ReleaseCollectForFailureSettlement(ref attempt);
                        _ = PresentSubmittedDesktopFrame(ref attempt);
                        break;
                    case EVulkanDesktopAcquireOwnership
                        .ConsumedByRecoveryImagePendingPresent
                        when recoveryOutcome.MustSettlePresentation:
                        ReleaseCollectForFailureSettlement(ref attempt);
                        PresentRecoveredDesktopFrameAfterUnexpectedFailure(
                            ref attempt);
                        break;
                }
            }
            catch (Exception recoveryFailure)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Desktop frame recovery also failed after {0}: {1}",
                    primaryFailure.GetType().Name,
                    recoveryFailure.Message);

                if (!_deviceLost &&
                    attempt.AcquireOwnership is
                        EVulkanDesktopAcquireOwnership
                            .ConsumedBySubmissionImagePendingPresent or
                        EVulkanDesktopAcquireOwnership
                            .ConsumedByRecoveryImagePendingPresent)
                {
                    try
                    {
                        ResolveDesktopAcquireBySwapchainRecreation(
                            ref attempt,
                            "Desktop post-submit failure fallback");
                        CompleteDesktopFrameSlot(ref attempt);
                    }
                    catch (Exception invalidationFailure)
                    {
                        Debug.VulkanWarning(
                            "[Vulkan] Desktop swapchain invalidation also failed after {0}: {1}",
                            primaryFailure.GetType().Name,
                            invalidationFailure.Message);
                    }
                }
            }
        }

        private void AbandonDesktopOwnershipAfterDeviceLoss(
            ref VulkanFrameAttempt attempt)
        {
            if (!VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(
                    attempt.AcquireOwnership))
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
            }

            if (!VulkanDesktopFramePolicy.IsUploadFinalizationLegal(
                    attempt.UploadOwnership))
            {
                attempt.TransitionUploadOwnership(
                    EVulkanDesktopUploadOwnership
                        .AbandonedAfterDeviceLoss);
            }
        }

        private void SettleUnsubmittedDesktopAcquire(
            ref VulkanFrameAttempt attempt)
        {
            ReleaseUnsubmittedDesktopUpload(
                ref attempt,
                "unexpected desktop frame phase failure");
            if (TryRecoverRejectedDesktopImage(
                    ref attempt,
                    commandBufferDirtyFlagSet: true,
                    commandBuffersDirtiedAfterSceneRecord: true,
                    recordedSwapchainWriteCount:
                        attempt.SceneSwapchainWriteCount,
                    rejectionStage: "UnexpectedPhaseFailure",
                    rejectedSubmitResult: null))
            {
                return;
            }

            _ = ConsumeDesktopAcquireForRecovery(
                ref attempt,
                "UnexpectedPhaseFailure");
            ResolveDesktopAcquireBySwapchainRecreation(
                ref attempt,
                "Unexpected desktop phase failure requires swapchain ownership recovery");
            CompleteDesktopFrameSlot(ref attempt);
        }

        private void ReleaseCollectForFailureSettlement(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.CollectReleased)
                return;

            try
            {
                RuntimeRenderingHostServices.Scheduling
                    .MarkRenderFrameReadyForCollect(XRWindow);
                attempt.CollectReleased = true;
            }
            catch (Exception collectFailure)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to release frame collect before failure settlement: {0}",
                    collectFailure.Message);
            }
        }

        private void PresentRecoveredDesktopFrameAfterUnexpectedFailure(
            ref VulkanFrameAttempt attempt)
        {
            VulkanDesktopPresentDispatchOutcome dispatch =
                DesktopWsiOutput.PresentFrameTarget(
                    this,
                    ref attempt,
                    "Vulkan.FrameLifecycle.RecoveryFailureQueuePresent",
                    "settling a recovered desktop frame after an auxiliary failure");
            Result result = dispatch.Result;
            if (!dispatch.Dispatched && result != Result.ErrorDeviceLost)
            {
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Recovery presentation dispatch was rejected before vkQueuePresent");
                CompleteDesktopFrameSlot(ref attempt);
                return;
            }

            bool accepted =
                result is Result.Success or Result.SuboptimalKhr;
            RecordDesktopPresentBookkeeping(
                ref attempt,
                result,
                accepted,
                hasValidFrameContent: false);
            if (result == Result.ErrorDeviceLost)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
                throw CreateDeviceLostException(
                    "Recovery failure QueuePresent",
                    result);
            }

            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership.ResolvedByPresentation);
            Exception? policyFailure = ApplyDesktopPresentPolicy(
                ref attempt,
                result,
                "Recovery failure QueuePresent");
            CompleteDesktopFrameSlot(ref attempt);
            if (attempt.Phase < EDesktopFramePhase.Recovered)
                attempt.AdvanceTo(EDesktopFramePhase.Recovered);

            if (dispatch.AuxiliaryFailure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(dispatch.AuxiliaryFailure)
                    .Throw();
            }
            if (policyFailure is not null)
                ExceptionDispatchInfo.Capture(policyFailure).Throw();
        }

        private void ReleaseUnsubmittedDesktopUpload(
            ref VulkanFrameAttempt attempt,
            string reason)
        {
            CancelRecordedTextureUploadSubmitBatch(reason);

            if (attempt.TextureUploadCommandBuffer.Handle == 0 ||
                attempt.UploadOwnership is
                    EVulkanDesktopUploadOwnership.SubmittedDeferredFree or
                    EVulkanDesktopUploadOwnership.Retired or
                    EVulkanDesktopUploadOwnership.CancelledFreed)
            {
                return;
            }

            CommandBuffer uploadCommandBuffer =
                attempt.TextureUploadCommandBuffer;
            if (attempt.TextureUploadCommandPool.Handle != 0 && !_deviceLost)
            {
                FreeVulkanCommandBufferTracked(
                    attempt.TextureUploadCommandPool,
                    ref uploadCommandBuffer,
                    "FrameLoop.UploadAbort");
            }

            RemoveCommandBufferBindState(
                attempt.TextureUploadCommandBuffer);
            attempt.TextureUploadCommandBuffer = default;
            attempt.TextureUploadCommandPool = default;
            attempt.TransitionUploadOwnership(
                EVulkanDesktopUploadOwnership.CancelledFreed);
        }

        private bool ConsumeDesktopAcquireForRecovery(
            ref VulkanFrameAttempt attempt,
            string reason)
        {
            if (attempt.AcquireOwnership !=
                    EVulkanDesktopAcquireOwnership.AcquiredUnresolved ||
                attempt.AcquireSemaphore.Handle == 0 ||
                _deviceLost)
            {
                return attempt.AcquireOwnership !=
                       EVulkanDesktopAcquireOwnership.AcquiredUnresolved;
            }

            ulong signalValue = Math.Max(
                _commandRuntime.Synchronization._graphicsTimelineValue + 1,
                attempt.AcquireTimelineValue + 1);
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            Result result;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.AcquireAbortBridgeSubmit"))
            {
                result = SubmitAcquireSemaphoreBridge(
                    attempt.AcquireSemaphore,
                    signalValue);
            }

            attempt.Timing.AcquireBridgeSubmit +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            if (result == Result.Success)
            {
                _commandRuntime.Synchronization._graphicsTimelineValue = Math.Max(
                    _commandRuntime.Synchronization._graphicsTimelineValue,
                    signalValue);
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .ConsumedByRecoveryImagePendingPresent);
                return true;
            }

            if (result == Result.ErrorDeviceLost)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
                attempt.RecoveryAction =
                    EDesktopFrameRecoveryAction.DeviceLost;
                throw CreateDeviceLostException(
                    "Acquire recovery QueueSubmit",
                    result);
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.AcquireAbortBridgeFailed.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Failed to consume acquired swapchain semaphore after aborted frame ({0}): {1}.",
                reason,
                result);
            return false;
        }

        private int ResolveRecordedDesktopSwapchainWriteCount(
            ref VulkanFrameAttempt attempt,
            CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0 ||
                _primaryCommandArtifactOwners is null ||
                attempt.ImageIndex >= _primaryCommandArtifactOwners.Length)
            {
                return 0;
            }

            PrimaryCommandArtifactOwner owner = _primaryCommandArtifactOwners[attempt.ImageIndex];
            return owner.PrimaryCommandBuffer.Handle == commandBuffer.Handle
                ? owner.RecordedSwapchainWriteCount
                : 0;
        }

        private bool TryRecoverRejectedDesktopImage(
            ref VulkanFrameAttempt attempt,
            bool commandBufferDirtyFlagSet,
            bool commandBuffersDirtiedAfterSceneRecord,
            int recordedSwapchainWriteCount,
            string rejectionStage,
            Result? rejectedSubmitResult,
            VulkanImGuiFrameSnapshot? recoveryOverlaySnapshot = null)
        {
            RejectedDesktopFramePolicyDecision policy =
                ResolveRejectedDesktopRecoveryPolicy(
                    ref attempt,
                    rejectionStage,
                    out bool imageWasEverPresented,
                    out bool imageHasValidPresentedContent,
                    out bool acquireAvailable);

            if (!policy.ShouldPresent)
            {
                if (acquireAvailable && !_deviceLost)
                {
                    ReleaseUnsubmittedDesktopUpload(
                        ref attempt,
                        "desktop frame rejected without a legal recovery submit");
                }

                RecordRejectedDesktopSkip(
                    ref attempt,
                    in policy,
                    rejectionStage,
                    rejectedSubmitResult,
                    commandBufferDirtyFlagSet,
                    commandBuffersDirtiedAfterSceneRecord,
                    imageWasEverPresented,
                    imageHasValidPresentedContent);
                return false;
            }

            // Recording deferral and pre-dispatch submission rejection do not
            // execute the rejected command buffers. Submitted image state and
            // reusable command-buffer journals therefore remain authoritative.
            // Clearing either side would make the next valid primary require
            // entry states that recovery had just erased, creating a permanent
            // reject/recover loop.
            const int clearedLayoutCount = 0;
            CommandPool abortCommandPool = default;
            CommandBuffer abortCommandBuffer = default;
            CommandBuffer recoveryOverlayCommandBuffer = default;
            bool abortSubmitted = false;
            try
            {
                PrepareRejectedDesktopAbortCommand(
                    ref attempt,
                    in policy,
                    imageWasEverPresented,
                    out abortCommandPool,
                    out abortCommandBuffer,
                    out bool replayedPresentationSource);
                bool hasRecoveryOverlay =
                    TryRecordRejectedDesktopRecoveryOverlay(
                        ref attempt,
                        recoveryOverlaySnapshot,
                        abortCommandBuffer,
                        out recoveryOverlayCommandBuffer);
                attempt.HasImGuiOverlayCommandBuffer = hasRecoveryOverlay;
                attempt.ImGuiOverlayCommandBuffer =
                    hasRecoveryOverlay
                        ? recoveryOverlayCommandBuffer
                        : default;
                attempt.RecoverySwapchainWriteCount =
                    replayedPresentationSource || policy.ShouldClearBeforePresent || hasRecoveryOverlay
                        ? 1
                        : 0;
                if (!TrySubmitRejectedDesktopAbort(
                        ref attempt,
                        abortCommandPool,
                        abortCommandBuffer,
                        recoveryOverlayCommandBuffer,
                        ref abortSubmitted))
                {
                    ReleaseUnsubmittedDesktopUpload(
                        ref attempt,
                        "rejected desktop recovery submit failed");
                    return false;
                }

                return PresentRejectedDesktopImageAndFinalize(
                    ref attempt,
                    in policy,
                    imageHasValidPresentedContent,
                    recordedSwapchainWriteCount,
                    rejectionStage,
                    rejectedSubmitResult,
                    commandBufferDirtyFlagSet,
                    commandBuffersDirtiedAfterSceneRecord,
                    clearedLayoutCount,
                    attempt.RecoverySwapchainWriteCount > 0);
            }
            finally
            {
                if (!abortSubmitted &&
                    !_deviceLost &&
                    attempt.UploadOwnership ==
                        EVulkanDesktopUploadOwnership.Recorded)
                {
                    ReleaseUnsubmittedDesktopUpload(
                        ref attempt,
                        "rejected desktop recovery was not submitted");
                }

                ReleaseUnsubmittedRejectedDesktopAbortCommand(
                    abortCommandPool,
                    ref abortCommandBuffer,
                    abortSubmitted);
            }
        }
    }
}
