using System;
using System.Runtime.ExceptionServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool PresentRejectedDesktopImageAndFinalize(
            ref DesktopFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageHasValidPresentedContent,
            int recordedSwapchainWriteCount,
            string rejectionStage,
            Result? rejectedSubmitResult,
            bool commandBufferDirtyFlagSet,
            bool commandBuffersDirtiedAfterSceneRecord,
            int clearedLayoutCount,
            bool recoveryFrameWritten)
        {
            VulkanDesktopPresentDispatchOutcome dispatch =
                QueueDesktopPresent(
                    ref attempt,
                    "Vulkan.FrameLifecycle.DirtyAbortQueuePresent",
                    $"presenting rejected Vulkan frame {attempt.FrameNumber} ({rejectionStage})");
            Result presentResult = dispatch.Result;
            if (!dispatch.Dispatched &&
                presentResult != Result.ErrorDeviceLost)
            {
                RecordDesktopPresentBookkeeping(
                    ref attempt,
                    presentResult,
                    presentAccepted: false,
                    hasValidFrameContent: false);
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Rejected-frame presentation dispatch failed before vkQueuePresent");
                CompleteDesktopFrameSlot(ref attempt);
                attempt.AdvanceTo(EDesktopFramePhase.Recovered);
                attempt.Flow = EDesktopFrameFlow.Completed;
                throw dispatch.AuxiliaryFailure ??
                    new InvalidOperationException(
                        "Rejected-frame presentation dispatch failed before vkQueuePresent.");
            }

            bool accepted =
                presentResult is Result.Success or Result.SuboptimalKhr;
            RecordDesktopPresentBookkeeping(
                ref attempt,
                presentResult,
                accepted,
                hasValidFrameContent:
                    imageHasValidPresentedContent ||
                    recoveryFrameWritten);
            if (presentResult == Result.ErrorDeviceLost)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
                throw CreateDeviceLostException(
                    "Dirty abort QueuePresent",
                    presentResult);
            }

            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership.ResolvedByPresentation);
            Exception? policyFailure = ApplyDesktopPresentPolicy(
                ref attempt,
                presentResult,
                "Dirty abort QueuePresent");
            CompleteDesktopFrameSlot(ref attempt);
            attempt.AdvanceTo(EDesktopFramePhase.Recovered);
            attempt.Flow = EDesktopFrameFlow.Completed;

            RecordCompletedRejectedDesktopRecovery(
                ref attempt,
                in policy,
                imageHasValidPresentedContent ||
                    recoveryFrameWritten,
                recordedSwapchainWriteCount +
                    attempt.RecoverySwapchainWriteCount,
                presentResult,
                rejectionStage,
                rejectedSubmitResult,
                commandBufferDirtyFlagSet,
                commandBuffersDirtiedAfterSceneRecord,
                clearedLayoutCount,
                accepted);

            if (dispatch.AuxiliaryFailure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(dispatch.AuxiliaryFailure)
                    .Throw();
            }
            if (policyFailure is not null)
                ExceptionDispatchInfo.Capture(policyFailure).Throw();

            return true;
        }
    }
}
