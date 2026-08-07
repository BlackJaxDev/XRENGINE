using System;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private RejectedDesktopFramePolicyDecision ResolveRejectedDesktopRecoveryPolicy(
            ref VulkanFrameAttempt attempt,
            string rejectionStage,
            out bool imageWasEverPresented,
            out bool imageHasValidPresentedContent,
            out bool acquireAvailable)
        {
            imageWasEverPresented =
                OutputRuntime.Desktop.IsImageEverPresented(attempt.ImageIndex);
            imageHasValidPresentedContent =
                OutputRuntime.Desktop.ImageHasValidPresentedContent is not null &&
                attempt.ImageIndex <
                OutputRuntime.Desktop.ImageHasValidPresentedContent.Length &&
                OutputRuntime.Desktop.ImageHasValidPresentedContent[attempt.ImageIndex];
            acquireAvailable =
                attempt.AcquireOwnership ==
                    EVulkanDesktopAcquireOwnership.AcquiredUnresolved &&
                attempt.AcquireSemaphore.Handle != 0;
            RejectedDesktopFramePolicyDecision policy =
                VulkanRejectedDesktopFramePolicy.Resolve(
                    acquireAvailable,
                    _deviceLost,
                    imageWasEverPresented,
                    imageHasValidPresentedContent);

            if (policy.ShouldPresent ||
                !acquireAvailable ||
                _deviceLost ||
                !string.Equals(
                    rejectionStage,
                    "RecordDeferred",
                    StringComparison.Ordinal))
            {
                return policy;
            }

            return new RejectedDesktopFramePolicyDecision(
                ERejectedDesktopFrameDisposition
                    .PresentInitializationClear,
                ERejectedDesktopFramePolicyReason
                    .DeferredInitializationClear);
        }

        private void RecordRejectedDesktopSkip(
            ref VulkanFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            string rejectionStage,
            Result? rejectedSubmitResult,
            bool commandBufferDirtyFlagSet,
            bool commandBuffersDirtiedAfterSceneRecord,
            bool imageWasEverPresented,
            bool imageHasValidPresentedContent)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.RejectedDesktopFrame.{policy.Reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan][FrameFailure][RejectedDesktopFrame] policy=SkipPresent reason={0} frame={1} image={2} rejectionStage={3} submitResult={4} dirtyFlag={5} generationChanged={6} imageEverPresented={7} validPriorContent={8}",
                policy.Reason,
                attempt.FrameNumber,
                attempt.ImageIndex,
                rejectionStage,
                rejectedSubmitResult?.ToString() ?? "not-submitted",
                commandBufferDirtyFlagSet,
                commandBuffersDirtiedAfterSceneRecord,
                imageWasEverPresented,
                imageHasValidPresentedContent);
            RecordInjectedRejectedDesktopRecovery(
                ref attempt,
                in policy,
                rejectionStage,
                presentAccepted: false);
        }

        private void RecordCompletedRejectedDesktopRecovery(
            ref VulkanFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageHasValidPresentedContent,
            int recordedSwapchainWriteCount,
            Result presentResult,
            string rejectionStage,
            Result? rejectedSubmitResult,
            bool commandBufferDirtyFlagSet,
            bool commandBuffersDirtiedAfterSceneRecord,
            int clearedLayoutCount,
            bool presentAccepted)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.DirtyBeforeSubmit",
                TimeSpan.FromSeconds(1),
                "[Vulkan][FrameFailure][RejectedDesktopFrame] policy={0} reason={1} frame={2} image={3} finalTargetValid={4} swapchainWrites={5} presentResult={6} rejectionStage={7} submitResult={8} dirtyFlag={9} generationChanged={10} clearedLayouts={11}.",
                policy.Disposition,
                policy.Reason,
                attempt.FrameNumber,
                attempt.ImageIndex,
                imageHasValidPresentedContent,
                recordedSwapchainWriteCount,
                presentResult,
                rejectionStage,
                rejectedSubmitResult?.ToString() ?? "not-submitted",
                commandBufferDirtyFlagSet,
                commandBuffersDirtiedAfterSceneRecord,
                clearedLayoutCount);
            RecordInjectedRejectedDesktopRecovery(
                ref attempt,
                in policy,
                rejectionStage,
                presentAccepted);
        }

        private void RecordInjectedRejectedDesktopRecovery(
            ref VulkanFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            string rejectionStage,
            bool presentAccepted)
        {
            if (!string.Equals(
                    rejectionStage,
                    VulkanRejectedDesktopFramePolicy.InjectedRejectionStage,
                    StringComparison.Ordinal))
            {
                return;
            }

            FrameOpContext? rejectionContext =
                _lastWindowPresentFrameOpContext ??
                ActiveLastActiveFrameOpContext;
            if (!rejectionContext.HasValue)
                return;

            _outputRuntime.RecordPhase524bInjectedDesktopRejection(
                rejectionContext.Value,
                in policy,
                presentAccepted,
                renderFrameId: attempt.FrameNumber);
        }
    }
}
