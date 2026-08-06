using System;
using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool TrySubmitRejectedDesktopAbort(
            ref VulkanFrameAttempt attempt,
            CommandPool commandPool,
            CommandBuffer commandBuffer,
            CommandBuffer overlayCommandBuffer,
            ref bool submitted)
        {
            CommandBuffer* submittedCommandBuffers =
                stackalloc CommandBuffer[3];
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
            ulong signalValue = Math.Max(
                _commandRuntime.Synchronization._graphicsTimelineValue + 1,
                attempt.AcquireTimelineValue + 1);
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            Result submitResult;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.DirtyAbortPresentSubmit"))
            {
                submitResult = SubmitAcquireSemaphoreBridge(
                    attempt.AcquireSemaphore,
                    signalValue,
                    attempt.PresentSemaphore,
                    submittedCommandBuffers,
                    submittedCommandBufferCount);
            }

            attempt.Timing.AcquireBridgeSubmit +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            if (submitResult != Result.Success)
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
            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership
                    .ConsumedByRecoveryImagePendingPresent);
            _commandRuntime.Synchronization._graphicsTimelineValue = Math.Max(
                _commandRuntime.Synchronization._graphicsTimelineValue,
                signalValue);
            _commandRuntime.Synchronization._frameSlotTimelineValues![attempt.FrameSlot] =
                signalValue;
            if (OutputRuntime.Desktop.ImageTimelineValues is not null &&
                attempt.ImageIndex <
                OutputRuntime.Desktop.ImageTimelineValues.Length)
            {
                OutputRuntime.Desktop.ImageTimelineValues[attempt.ImageIndex] =
                    signalValue;
            }

            CommitSubmittedDesktopTextureUpload(
                ref attempt,
                signalValue,
                "rejected desktop recovery frame");
            DeferSecondaryCommandBufferFree(
                attempt.ImageIndex,
                commandPool,
                commandBuffer);
            RuntimeRenderingHostServices.Scheduling
                .MarkRenderFrameReadyForCollect(XRWindow);
            attempt.CollectReleased = true;
            return true;
        }
    }
}
