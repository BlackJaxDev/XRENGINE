using System;
using System.Runtime.ExceptionServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private bool PresentRejectedDesktopImageAndFinalize(
            ref VulkanFrameAttempt attempt,
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
            VulkanDesktopPresentDispatchOutcome dispatch = QueueDesktopPresentCore(
                ref attempt,
                "Vulkan.FrameLifecycle.DirtyAbortQueuePresent",
                $"presenting rejected Vulkan frame {attempt.FrameNumber} ({rejectionStage})");
            Result presentResult = dispatch.Result;
            if (!dispatch.Dispatched &&
                presentResult != Result.ErrorDeviceLost)
            {
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Rejected-frame presentation dispatch failed before vkQueuePresent");
                RecordDesktopPresentBookkeeping(
                    ref attempt,
                    presentResult,
                    presentAccepted: false,
                    hasValidFrameContent: false);
                CompleteDesktopFrameSlot(ref attempt);
                attempt.AdvanceTo(EDesktopFramePhase.Recovered);
                attempt.Flow = EDesktopFrameFlow.Completed;
                throw dispatch.AuxiliaryFailure ??
                    new InvalidOperationException(
                        "Rejected-frame presentation dispatch failed before vkQueuePresent.");
            }

            bool accepted =
                presentResult is Result.Success or Result.SuboptimalKhr;
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
            RecordDesktopPresentBookkeeping(
                ref attempt,
                presentResult,
                accepted,
                hasValidFrameContent:
                    imageHasValidPresentedContent ||
                    recoveryFrameWritten);
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
