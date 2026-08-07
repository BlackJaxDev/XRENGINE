using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private const uint DesktopSubmitCommandBufferCapacity = 4;

        internal EDesktopFrameFlow SubmitDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            if (!TryValidatePresentationSourceForSubmission(
                    attempt.PresentationSource,
                    attempt.SceneCommandBuffer,
                    attempt.ImageIndex,
                    out string presentationSourceFailure))
            {
                _commandRuntime.CommandBuffers.MarkDirty(presentationSourceFailure);
                SettleRejectedDesktopCommandArtifacts(
                    ref attempt,
                    $"submit precondition failed: {presentationSourceFailure}");
                return HandleDesktopRecordingDeferred(
                    ref attempt,
                    $"submit precondition failed: {presentationSourceFailure}",
                    recoveryOverlaySnapshot: null);
            }

            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.Submission);
            _commandRuntime.Synchronization._graphicsTimelineValue = Math.Max(
                _commandRuntime.Synchronization._graphicsTimelineValue + 1,
                attempt.AcquireTimelineValue + 1);
            attempt.GraphicsSignalValue = _commandRuntime.Synchronization._graphicsTimelineValue;

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
                _commandRuntime.Synchronization._graphicsTimelineSemaphore,
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

            VulkanMappedFrameArena? mappedFrameArena = MappedFrameArena;
            ulong mappedFrameGeneration = mappedFrameArena?.Generation ?? 0UL;
            bool mappedFrameSlotPrepared;
            try
            {
                mappedFrameSlotPrepared = mappedFrameArena is null ||
                    mappedFrameArena.TryPrepareFrameSlotForSubmission(
                        attempt.ImageIndex,
                        mappedFrameGeneration);
            }
            catch
            {
                CompleteMappedFrameArenaDeviceLossObservation();
                throw;
            }
            if (!mappedFrameSlotPrepared)
            {
                CompleteMappedFrameArenaDeviceLossObservation();
                _commandRuntime.CommandBuffers.MarkDirty(
                    $"mapped frame-data slot {attempt.ImageIndex} was not writable/flushable for generation {mappedFrameGeneration}");
                SettleRejectedDesktopCommandArtifacts(
                    ref attempt,
                    "mapped frame-data submission preparation failed");
                return HandleDesktopRecordingDeferred(
                    ref attempt,
                    "mapped frame-data submission preparation failed",
                    recoveryOverlaySnapshot: null);
            }

            try
            {
                MarkDlssFrameGenerationPclMarker(
                    NvidiaDlssManager.Native.StreamlinePclMarker
                        .RenderSubmitStart);
            }
            catch
            {
                _ = mappedFrameArena?.TryCancelFrameSlotSubmission(
                    attempt.ImageIndex,
                    mappedFrameGeneration);
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
            try
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.Submit"))
                using (VulkanCpuStageScope cpuStage =
                       new(_frameTelemetry, EVulkanCpuStage.Submission))
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
                        _ = _commandRuntime.CommandBuffers.TryGetDiagnosticMetadata(
                            attempt.ImageIndex,
                            attempt.SceneCommandBuffer,
                            out ulong plannerRevision,
                            out ulong frameOpContextId,
                            out ulong resourceGeneration,
                            out ulong descriptorGeneration);
                        VulkanSubmissionDiagnosticContext diagnosticContext =
                            CreateDesktopSubmissionDiagnosticContext(
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
                            _deviceContext.GraphicsQueue,
                            ref submitInfo,
                            default,
                            diagnosticContext,
                            caller: "RenderFrameCallback");
                        if (submitResult == Result.Success)
                        {
                            // The queue owns this frame as soon as vkQueueSubmit accepts it. Set
                            // settlement flags before profiling/telemetry scopes can unwind.
                            attempt.Submitted = true;
                            attempt.CommandArtifactsSettled = true;
                            attempt.TransitionAcquireOwnership(
                                EVulkanDesktopAcquireOwnership
                                    .ConsumedBySubmissionImagePendingPresent);
                            attempt.AdvanceTo(EDesktopFramePhase.Submitted);
                            mappedFrameArena?.MarkFrameSlotSubmitted(
                                attempt.ImageIndex,
                                mappedFrameGeneration);
                        }
                    }
                }
            }
            catch
            {
                if (!attempt.Submitted)
                {
                    _ = mappedFrameArena?.TryCancelFrameSlotSubmission(
                        attempt.ImageIndex,
                        mappedFrameGeneration);
                }
                CompleteMappedFrameArenaDeviceLossObservation();
                throw;
            }

            attempt.Timing.SubmitQueue +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            if (submitResult != Result.Success)
            {
                _ = mappedFrameArena?.TryCancelFrameSlotSubmission(
                    attempt.ImageIndex,
                    mappedFrameGeneration);
                return HandleDesktopSubmitFailure(
                    ref attempt,
                    submitResult);
            }

            _frameTelemetry.MarkFrameTimingSubmitted(
                unchecked((int)Math.Min(
                    attempt.ImageIndex,
                    int.MaxValue)));
            _commandRuntime.Synchronization._frameSlotTimelineValues![attempt.FrameSlot] =
                attempt.GraphicsSignalValue;
            if (OutputRuntime.Desktop.ImageTimelineValues is not null &&
                attempt.ImageIndex <
                OutputRuntime.Desktop.ImageTimelineValues.Length)
            {
                OutputRuntime.Desktop.ImageTimelineValues[attempt.ImageIndex] =
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
                .MarkRenderFrameReadyForCollect(DesktopWsiOutput.Window);
            attempt.CollectReleased = true;

            stageStartTimestamp = Stopwatch.GetTimestamp();
            try
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.TrimStaging"))
                {
                    ResourceRuntime.Allocations.Staging.Trim(OutputRuntime);
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
            ResourceRuntime.Uploads.PublicationState.QueueRecordedForTimeline(
                signalValue,
                uploadSource);
            _commandRuntime.DeferSecondaryCommandBufferFree(
                Api,
                _deviceContext.Device,
                ResourceRuntime,
                attempt.FrameSlot,
                attempt.ImageIndex,
                attempt.TextureUploadCommandPool,
                attempt.TextureUploadCommandBuffer,
                "FrameLoop.TextureUploadSecondary");
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
            _commandRuntime.CommandBuffers.MarkDirty(
                $"graphics frame submit rejected with {submitResult}");
            SettleRejectedDesktopCommandArtifacts(
                ref attempt,
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

        /// <summary>
        /// Transfers every unsubmitted artifact owned by this attempt to the exact
        /// invalidation/reset queue. The queue retains ownership when a worker or CPU
        /// recording lease has not settled yet; otherwise the immediate drain resets
        /// the artifact and releases its recorded resource generations now.
        /// </summary>
        private void SettleRejectedDesktopCommandArtifacts(
            ref VulkanFrameAttempt attempt,
            string reason)
        {
            if (attempt.CommandArtifactsSettled)
                return;

            Span<ulong> handles = stackalloc ulong[3];
            int count = 0;
            count = AddRejectedArtifactHandle(
                handles,
                count,
                attempt.SceneCommandBuffer);
            if (attempt.HasImGuiOverlayCommandBuffer)
            {
                count = AddRejectedArtifactHandle(
                    handles,
                    count,
                    attempt.ImGuiOverlayCommandBuffer);
            }
            if (attempt.HasDynamicTextOverlayCommandBuffer)
            {
                count = AddRejectedArtifactHandle(
                    handles,
                    count,
                    attempt.DynamicTextOverlayCommandBuffer);
            }

            if (count != 0)
            {
                _ = _commandRuntime.InvalidateCachedCommandBuffers(
                    handles[..count],
                    reason,
                    OutputRuntime,
                    _frameTelemetry);
                if (!IsDeviceLost)
                    _commandRuntime.DrainInvalidatedCommandBufferRecordings(
                        Api,
                        ResourceRuntime,
                        count);
            }

            attempt.CommandArtifactsSettled = true;
        }

        private static int AddRejectedArtifactHandle(
            Span<ulong> handles,
            int count,
            CommandBuffer commandBuffer)
        {
            ulong handle = unchecked((ulong)commandBuffer.Handle);
            if (handle == 0)
                return count;
            for (int i = 0; i < count; i++)
                if (handles[i] == handle)
                    return count;
            handles[count] = handle;
            return count + 1;
        }
    }
}
