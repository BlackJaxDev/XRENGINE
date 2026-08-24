using System;
using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private unsafe bool TrySubmitRejectedDesktopAbort(
            ref VulkanFrameAttempt attempt,
            CommandPool commandPool,
            CommandBuffer commandBuffer,
            CommandBuffer overlayCommandBuffer,
            CommandBuffer dynamicTextCommandBuffer,
            ref bool submitted)
        {
            CommandBuffer* submittedCommandBuffers =
                stackalloc CommandBuffer[4];
            uint submittedCommandBufferCount = 0;
            if (attempt.TextureUploadCommandBuffer.Handle != 0)
            {
                submittedCommandBuffers[submittedCommandBufferCount++] =
                    attempt.TextureUploadCommandBuffer;
            }

            submittedCommandBuffers[submittedCommandBufferCount++] =
                commandBuffer;
            if (overlayCommandBuffer.Handle != 0)
            {
                submittedCommandBuffers[submittedCommandBufferCount++] =
                    overlayCommandBuffer;
            }
            if (dynamicTextCommandBuffer.Handle != 0)
            {
                submittedCommandBuffers[submittedCommandBufferCount++] =
                    dynamicTextCommandBuffer;
            }
            ulong signalValue;
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            VulkanSubmissionReceipt submitReceipt;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.DirtyAbortPresentSubmit"))
            {
                submitReceipt = SubmitAcquireSemaphoreBridge(
                    attempt.AcquireSemaphore,
                    attempt.AcquireTimelineValue + 1UL,
                    attempt.PresentSemaphore,
                    submittedCommandBuffers,
                    submittedCommandBufferCount,
                    out signalValue);
            }

            attempt.Timing.AcquireBridgeSubmit +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            Result submitResult = submitReceipt.Result;
            if (!submitReceipt.SubmissionAccepted)
            {
                if (submitResult == Result.ErrorDeviceLost)
                {
                    attempt.TransitionAcquireOwnership(
                        EVulkanDesktopAcquireOwnership
                            .IndeterminateAfterDeviceLoss);
                    throw CreateDeviceLostException(
                        "Dirty abort QueueSubmit",
                        submitResult);
                }

                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.DirtyAbortPresentSubmitFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Failed to submit skipped-frame present for image {0}: {1}.",
                    attempt.ImageIndex,
                    submitResult);
                return false;
            }

            submitted = true;
            // Capture every post-acceptance obligation before executing any
            // fallible publication. Terminal settlement can then retry debt
            // publication without reopening the accepted native submission.
            attempt.RecoverySubmissionAccepted = true;
            attempt.RecoveryCommandPool = commandPool;
            attempt.RecoveryCommandBuffer = commandBuffer;
            attempt.GraphicsSignalValue = signalValue;
            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership
                    .ConsumedByRecoveryImagePendingPresent);
            if (attempt.UploadOwnership == EVulkanDesktopUploadOwnership.Recorded)
                attempt.TransitionUploadOwnership(
                    EVulkanDesktopUploadOwnership.SubmittedDeferredFree);
            SettleAcceptedDesktopRecoverySubmissionDebt(ref attempt);
            RuntimeRenderingHostServices.Scheduling
                .MarkRenderFrameReadyForCollect(DesktopWsiOutput.Window);
            attempt.CollectReleased = true;
            return true;
        }

        /// <summary>
        /// Publishes all managed debt for an accepted recovery submission.
        /// Each publication is independently idempotent so settlement can retry
        /// after an auxiliary failure without re-submitting native work.
        /// </summary>
        private void SettleAcceptedDesktopRecoverySubmissionDebt(
            ref VulkanFrameAttempt attempt)
        {
            if (!attempt.RecoverySubmissionAccepted)
                return;

            PublishAcceptedRecoverySubmissionReuseLedgers(ref attempt);
            CommitSubmittedDesktopTextureUpload(
                ref attempt,
                attempt.GraphicsSignalValue,
                "rejected desktop recovery frame");
            if (attempt.RecoveryCommandRetirementQueued)
                return;

            _commandRuntime.DeferSecondaryCommandBufferFree(
                Api,
                _deviceContext.Device,
                ResourceRuntime,
                attempt.FrameSlot,
                attempt.ImageIndex,
                attempt.RecoveryCommandPool,
                attempt.RecoveryCommandBuffer,
                "FrameLoop.RecoverySecondary");
            attempt.RecoveryCommandRetirementQueued = true;
            attempt.RecoveryCommandPool = default;
            attempt.RecoveryCommandBuffer = default;
        }

        private void PublishAcceptedRecoverySubmissionReuseLedgers(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.SubmissionReuseLedgersPublished)
                return;

            _commandRuntime.Synchronization._frameSlotTimelineValues![attempt.FrameSlot] =
                attempt.GraphicsSignalValue;
            if (OutputRuntime.Desktop.ImageTimelineValues is not null &&
                attempt.ImageIndex < OutputRuntime.Desktop.ImageTimelineValues.Length)
            {
                OutputRuntime.Desktop.ImageTimelineValues[attempt.ImageIndex] =
                    attempt.GraphicsSignalValue;
            }

            attempt.SubmissionReuseLedgersPublished = true;
        }
    }
}
