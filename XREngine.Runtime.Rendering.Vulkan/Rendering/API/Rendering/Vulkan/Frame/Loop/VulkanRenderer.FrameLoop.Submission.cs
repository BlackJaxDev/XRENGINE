using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private const uint DesktopSubmitCommandBufferCapacity = 4;

        private EDesktopFrameFlow SubmitDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.Submission);
            _graphicsTimelineValue = Math.Max(
                _graphicsTimelineValue + 1,
                attempt.AcquireTimelineValue + 1);
            attempt.GraphicsSignalValue = _graphicsTimelineValue;

            ulong* waitTimelineValues = stackalloc ulong[1] { 0UL };
            ulong* signalTimelineValues = stackalloc ulong[2]
            {
                attempt.GraphicsSignalValue,
                0UL,
            };
            Semaphore* waitSemaphores = stackalloc Semaphore[1]
            {
                attempt.AcquireSemaphore,
            };
            PipelineStageFlags* waitStages =
                stackalloc PipelineStageFlags[1]
                {
                    PipelineStageFlags.ColorAttachmentOutputBit,
                };
            CommandBuffer* commandBuffers =
                stackalloc CommandBuffer[
                    (int)DesktopSubmitCommandBufferCapacity];
            uint commandBufferCount = 0;
            if (attempt.TextureUploadCommandBuffer.Handle != 0)
            {
                AppendDesktopSubmitCommandBuffer(
                    commandBuffers,
                    ref commandBufferCount,
                    attempt.TextureUploadCommandBuffer);
            }

            AppendDesktopSubmitCommandBuffer(
                commandBuffers,
                ref commandBufferCount,
                attempt.SceneCommandBuffer);
            if (attempt.HasImGuiOverlayCommandBuffer &&
                attempt.ImGuiOverlayCommandBuffer.Handle != 0)
            {
                AppendDesktopSubmitCommandBuffer(
                    commandBuffers,
                    ref commandBufferCount,
                    attempt.ImGuiOverlayCommandBuffer);
            }

            if (attempt.HasDynamicTextOverlayCommandBuffer &&
                attempt.DynamicTextOverlayCommandBuffer.Handle != 0)
            {
                AppendDesktopSubmitCommandBuffer(
                    commandBuffers,
                    ref commandBufferCount,
                    attempt.DynamicTextOverlayCommandBuffer);
            }

            Semaphore* signalSemaphores = stackalloc Semaphore[2]
            {
                _graphicsTimelineSemaphore,
                attempt.PresentSemaphore,
            };
            TimelineSemaphoreSubmitInfo timelineSubmitInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = waitTimelineValues,
                SignalSemaphoreValueCount = 2,
                PSignalSemaphoreValues = signalTimelineValues,
            };
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineSubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBuffers,
                SignalSemaphoreCount = 2,
                PSignalSemaphores = signalSemaphores,
            };

            try
            {
                MarkDlssFrameGenerationPclMarker(
                    NvidiaDlssManager.Native.StreamlinePclMarker
                        .RenderSubmitStart);
            }
            catch
            {
                _ = TryRecoverRejectedDesktopImage(
                    ref attempt,
                    commandBufferDirtyFlagSet: true,
                    commandBuffersDirtiedAfterSceneRecord: false,
                    recordedSwapchainWriteCount:
                        attempt.SceneSwapchainWriteCount,
                    rejectionStage: "RenderSubmitStart",
                    rejectedSubmitResult: null);
                throw;
            }

            long stageStartTimestamp = Stopwatch.GetTimestamp();
            Result submitResult;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.Submit"))
            using (VulkanCpuStageScope cpuStage =
                   new(EVulkanCpuStage.Submission))
            {
                lock (_oneTimeSubmitLock)
                {
                    ulong frameOpsSignature =
                        _commandBufferFrameOpSignatures is not null &&
                        attempt.ImageIndex <
                        (uint)_commandBufferFrameOpSignatures.Length
                            ? _commandBufferFrameOpSignatures[
                                attempt.ImageIndex]
                            : 0UL;
                    _ = TryGetCommandBufferDiagnosticMetadata(
                        attempt.ImageIndex,
                        attempt.SceneCommandBuffer,
                        out ulong plannerRevision,
                        out ulong frameOpContextId,
                        out ulong resourceGeneration,
                        out ulong descriptorGeneration);
                    VulkanSubmissionDiagnosticContext diagnosticContext =
                        CreateSwapchainSubmissionDiagnosticContext(
                            "SwapchainDraw",
                            attempt.ImageIndex,
                            attempt.FrameNumber,
                            attempt.FrameSlot,
                            0UL,
                            attempt.GraphicsSignalValue,
                            attempt.SceneCommandBufferDirtyGeneration,
                            frameOpsSignature,
                            plannerRevision,
                            frameOpContextId,
                            resourceGeneration,
                            descriptorGeneration);
                    submitResult = SubmitToQueueTracked(
                        graphicsQueue,
                        ref submitInfo,
                        default,
                        diagnosticContext,
                        caller: nameof(RenderFrameCallback));
                }
            }

            attempt.Timing.SubmitQueue +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            if (submitResult != Result.Success)
                return HandleDesktopSubmitFailure(
                    ref attempt,
                    submitResult);

            // Commit every ownership transition immediately after the queue
            // accepts the submit. Auxiliary marker, trim, and telemetry failures
            // cannot strand a submitted image.
            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership
                    .ConsumedBySubmissionImagePendingPresent);
            attempt.Submitted = true;
            attempt.AdvanceTo(EDesktopFramePhase.Submitted);
            MarkFrameTimingSubmitted(
                unchecked((int)Math.Min(
                    attempt.ImageIndex,
                    int.MaxValue)));
            _frameSlotTimelineValues![attempt.FrameSlot] =
                attempt.GraphicsSignalValue;
            if (_swapchainImageTimelineValues is not null &&
                attempt.ImageIndex <
                _swapchainImageTimelineValues.Length)
            {
                _swapchainImageTimelineValues[attempt.ImageIndex] =
                    attempt.GraphicsSignalValue;
            }

            CommitSubmittedDesktopTextureUpload(
                ref attempt,
                attempt.GraphicsSignalValue,
                "graphics frame");
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.PostSubmitAuxiliary);

            try
            {
                MarkDlssFrameGenerationPclMarker(
                    NvidiaDlssManager.Native.StreamlinePclMarker
                        .RenderSubmitEnd);
            }
            catch (Exception ex)
            {
                attempt.DeferredFailure ??= ex;
            }

            RuntimeRenderingHostServices.Scheduling
                .MarkRenderFrameReadyForCollect(XRWindow);
            attempt.CollectReleased = true;

            stageStartTimestamp = Stopwatch.GetTimestamp();
            try
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.TrimStaging"))
                {
                    _stagingManager.Trim(this);
                }
            }
            catch (Exception ex)
            {
                attempt.DeferredFailure ??= ex;
            }
            finally
            {
                attempt.Timing.TrimStaging +=
                    Stopwatch.GetElapsedTime(stageStartTimestamp);
            }

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.Submit",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Frame={0} SubmittedImage={1}",
                    attempt.FrameNumber,
                    attempt.ImageIndex);
            }

            return EDesktopFrameFlow.Continue;
        }

        private static void AppendDesktopSubmitCommandBuffer(
            CommandBuffer* commandBuffers,
            ref uint commandBufferCount,
            CommandBuffer commandBuffer)
        {
            if (commandBufferCount >=
                DesktopSubmitCommandBufferCapacity)
            {
                throw new InvalidOperationException(
                    "Desktop Vulkan submit command-buffer capacity was exceeded.");
            }

            commandBuffers[commandBufferCount++] = commandBuffer;
        }

        /// <summary>
        /// Transfers a successfully submitted texture-upload batch to timeline
        /// publication and deferred command-buffer reclamation.
        /// </summary>
        private void CommitSubmittedDesktopTextureUpload(
            ref VulkanFrameAttempt attempt,
            ulong signalValue,
            string uploadSource)
        {
            if (attempt.TextureUploadCommandBuffer.Handle == 0)
                return;

            attempt.TransitionUploadOwnership(
                EVulkanDesktopUploadOwnership.SubmittedDeferredFree);
            QueueRecordedTextureUploadsForTimeline(
                signalValue,
                uploadSource);
            DeferSecondaryCommandBufferFree(
                attempt.ImageIndex,
                attempt.TextureUploadCommandPool,
                attempt.TextureUploadCommandBuffer);
            attempt.TextureUploadCommandBuffer = default;
            attempt.TextureUploadCommandPool = default;
            attempt.TransitionUploadOwnership(
                EVulkanDesktopUploadOwnership.Retired);
        }

        private EDesktopFrameFlow HandleDesktopSubmitFailure(
            ref VulkanFrameAttempt attempt,
            Result submitResult)
        {
            if (submitResult == Result.ErrorDeviceLost)
            {
                if (attempt.UploadOwnership ==
                    EVulkanDesktopUploadOwnership.Recorded)
                {
                    attempt.TransitionUploadOwnership(
                        EVulkanDesktopUploadOwnership
                            .AbandonedAfterDeviceLoss);
                }
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
                attempt.Stop(
                    EDesktopFrameReason.SubmitFailed,
                    EDesktopFrameRecoveryAction.DeviceLost);
                throw CreateDeviceLostException(
                    "Draw QueueSubmit",
                    submitResult);
            }

            ReleaseUnsubmittedDesktopUpload(
                ref attempt,
                $"graphics frame submit failed with {submitResult}");
            MarkCommandBuffersDirty(
                $"graphics frame submit rejected with {submitResult}");
            if (TryRecoverRejectedDesktopImage(
                    ref attempt,
                    commandBufferDirtyFlagSet: true,
                    commandBuffersDirtiedAfterSceneRecord: true,
                    recordedSwapchainWriteCount:
                        attempt.SceneSwapchainWriteCount,
                    rejectionStage: "DrawSubmitRejected",
                    rejectedSubmitResult: submitResult))
            {
                attempt.Reason = EDesktopFrameReason.SubmitFailed;
                attempt.Flow = EDesktopFrameFlow.Completed;
                throw new InvalidOperationException(
                    $"Failed to submit draw command buffer ({submitResult}); acquired image ownership was recovered before propagating the failure.");
            }

            _ = ConsumeDesktopAcquireForRecovery(
                ref attempt,
                $"DrawSubmitFailed:{submitResult}");
            ResolveDesktopAcquireBySwapchainRecreation(
                ref attempt,
                $"Draw submit failed with {submitResult} - recovering acquired image state");
            CompleteDesktopFrameSlot(ref attempt);
            attempt.Stop(
                EDesktopFrameReason.SubmitFailed,
                EDesktopFrameRecoveryAction.RecreateSwapchain);
            throw new InvalidOperationException(
                $"Failed to submit draw command buffer ({submitResult}).");
        }
    }
}
