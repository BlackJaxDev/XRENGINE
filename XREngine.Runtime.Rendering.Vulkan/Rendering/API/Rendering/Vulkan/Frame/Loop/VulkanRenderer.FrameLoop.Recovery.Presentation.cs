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
            bool presentationReleaseEnqueued =
                dispatch.Dispatched &&
                VulkanWsiPresentResult.EnqueuesPresentationRelease(presentResult);
            if (presentResult == Result.ErrorDeviceLost)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
                throw CreateDeviceLostException(
                    "Dirty abort QueuePresent",
                    presentResult);
            }

            if (!presentationReleaseEnqueued)
            {
                if (VulkanWsiPresentResult.RequiresOutputQuarantine(
                        dispatch.Dispatched,
                        presentResult))
                {
                    QuarantineDesktopFrameAdmission(
                        ref attempt,
                        $"Dirty-abort QueuePresent returned an indeterminate WSI result: {presentResult}.");
                }
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Rejected-frame presentation did not enqueue WSI release work");
                RecordDesktopPresentBookkeeping(
                    ref attempt,
                    presentResult,
                    presentAccepted: false,
                    hasValidFrameContent: false);
                CompleteDesktopFrameSlot(ref attempt);
                attempt.AdvanceTo(EDesktopFramePhase.Recovered);
                attempt.Flow = EDesktopFrameFlow.Completed;
                Exception? nonEnqueuePolicyFailure = ApplyDesktopPresentPolicy(
                    ref attempt,
                    presentResult,
                    "Dirty abort QueuePresent");
                throw dispatch.AuxiliaryFailure ?? nonEnqueuePolicyFailure ??
                    new InvalidOperationException(
                        $"Rejected-frame presentation did not enqueue WSI release work ({presentResult}).");
            }

            bool accepted =
                presentResult is Result.Success or Result.SuboptimalKhr;
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
