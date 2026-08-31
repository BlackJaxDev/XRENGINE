using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private const uint DesktopSubmitCommandBufferCapacity = 4;

        internal VulkanDesktopFramePhaseResult SubmitDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            long allocationBefore =
                GC.GetAllocatedBytesForCurrentThread();
            try
            {
                EDesktopFrameFlow flow = SubmitDesktopFrameCore(ref attempt);
                return attempt.CompletePhase(
                    EVulkanFrameStage.QueueSubmit,
                    flow);
            }
            finally
            {
                if (!attempt.Submitted)
                    _gpuDiagnosticReadbackSidecar?.CancelPrimarySubmission(
                        attempt.SceneCommandBuffer);
                // Flush consumes accepted-attempt requests. Anything left here
                // belongs to a rejected or abandoned recording attempt.
                DiscardPendingGpuRenderStatsReadbacks();
                _commandRuntime.DiscardDeferredLightingObjectReadback();
                VulkanFrameHotPathTelemetry.RecordSubmission(
                    allocationBefore);
            }
        }

        private unsafe EDesktopFrameFlow SubmitDesktopFrameCore(
            ref VulkanFrameAttempt attempt)
        {
            using (VulkanCpuStageScope validationStage = new(
                       _frameTelemetry,
                       EVulkanCpuStage.SubmissionDiagnostics,
                       enabled: false))
            {
                _ = attempt.CompletePhase(
                    EVulkanFrameStage.SubmitPrepare,
                    EDesktopFrameFlow.Continue);
                if (attempt.OutputExecutionPlan is not { } outputPlan ||
                    (!outputPlan.HasExecutableOutput(EFrameOutputKind.DesktopScene) &&
                     !outputPlan.HasExecutableOutput(EFrameOutputKind.EditorScenePanel)))
                {
                    const string reason =
                        "immutable output DAG did not admit desktop submission";
                    SettleRejectedDesktopCommandArtifacts(ref attempt, reason);
                    return attempt.WorkClass == ERenderOutputWorkClass.PresentNow
                        ? HandleDesktopPresentNowFailureAfterAcquire(
                            ref attempt,
                            EVulkanPresentNowReadinessStage.FramePlanSeal,
                            reason,
                            recoveryOverlaySnapshot: null,
                            recoveryDynamicTextSecondaryCommandBuffer: default,
                            recoveryDynamicTextOperationCount: 0)
                        : HandleDesktopRecordingDeferred(
                            ref attempt,
                            reason,
                            recoveryOverlaySnapshot: null);
                }
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
                    string reason =
                        $"submit precondition failed: {presentationSourceFailure}";
                    return attempt.WorkClass == ERenderOutputWorkClass.PresentNow
                        ? HandleDesktopPresentNowFailureAfterAcquire(
                            ref attempt,
                            EVulkanPresentNowReadinessStage.FramePlanSeal,
                            reason,
                            recoveryOverlaySnapshot: null,
                            recoveryDynamicTextSecondaryCommandBuffer: default,
                            recoveryDynamicTextOperationCount: 0)
                        : HandleDesktopRecordingDeferred(
                            ref attempt,
                            reason,
                            recoveryOverlaySnapshot: null);
                }

                ThrowIfDesktopFrameFaultInjected(
                    EVulkanDesktopFrameFaultPoint.Submission);
            }
            CommandBuffer* commandBuffers =
                stackalloc CommandBuffer[
                    (int)DesktopSubmitCommandBufferCapacity];
            uint commandBufferCount = 0;
            VulkanMappedFrameArena? mappedFrameArena = null;
            ulong mappedFrameGeneration = 0UL;
            VulkanFrameDataArena? frameDataArena = null;
            ulong frameDataGeneration = 0UL;
            using (VulkanCpuStageScope preparationStage = new(
                       _frameTelemetry,
                       EVulkanCpuStage.SubmissionPreparation,
                       enabled: false))
            {
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

                mappedFrameArena = MappedFrameArena;
                mappedFrameGeneration = mappedFrameArena?.Generation ?? 0UL;
                frameDataArena = FrameDataArena;
                frameDataGeneration = frameDataArena?.Generation ?? 0UL;
                bool mappedFrameSlotPrepared = false;
                bool frameDataSlotPrepared = false;
                try
                {
                    mappedFrameSlotPrepared = mappedFrameArena is null ||
                        mappedFrameArena.TryPrepareFrameSlotForSubmission(
                            attempt.ImageIndex,
                            mappedFrameGeneration);
                    frameDataSlotPrepared = frameDataArena is null ||
                        frameDataArena.TryPrepareFrameSlotForSubmission(
                            checked((uint)attempt.FrameSlot),
                            frameDataGeneration);
                }
                catch
                {
                    if (mappedFrameSlotPrepared)
                        _ = mappedFrameArena?.TryCancelFrameSlotSubmission(
                            attempt.ImageIndex,
                            mappedFrameGeneration);
                    if (frameDataSlotPrepared)
                        _ = frameDataArena?.TryCancelFrameSlotSubmission(
                            checked((uint)attempt.FrameSlot),
                            frameDataGeneration);
                    CompleteMappedFrameArenaDeviceLossObservation();
                    throw;
                }
                if (!mappedFrameSlotPrepared || !frameDataSlotPrepared)
                {
                    if (mappedFrameSlotPrepared)
                        _ = mappedFrameArena?.TryCancelFrameSlotSubmission(
                            attempt.ImageIndex,
                            mappedFrameGeneration);
                    if (frameDataSlotPrepared)
                        _ = frameDataArena?.TryCancelFrameSlotSubmission(
                            checked((uint)attempt.FrameSlot),
                            frameDataGeneration);
                    CompleteMappedFrameArenaDeviceLossObservation();
                    _commandRuntime.CommandBuffers.MarkDirty(
                        $"mapped frame-data preparation failed for image slot {attempt.ImageIndex} generation {mappedFrameGeneration} or frame slot {attempt.FrameSlot} generation {frameDataGeneration}");
                    SettleRejectedDesktopCommandArtifacts(
                        ref attempt,
                        "mapped frame-data submission preparation failed");
                    const string reason =
                        "mapped frame-data submission preparation failed";
                    return attempt.WorkClass == ERenderOutputWorkClass.PresentNow
                        ? HandleDesktopPresentNowFailureAfterAcquire(
                            ref attempt,
                            EVulkanPresentNowReadinessStage.FramePlanSeal,
                            reason,
                            recoveryOverlaySnapshot: null,
                            recoveryDynamicTextSecondaryCommandBuffer: default,
                            recoveryDynamicTextOperationCount: 0)
                        : HandleDesktopRecordingDeferred(
                            ref attempt,
                            reason,
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
                    _ = frameDataArena?.TryCancelFrameSlotSubmission(
                        checked((uint)attempt.FrameSlot),
                        frameDataGeneration);
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
            }
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            attempt.SubmitStartedTimestamp = stageStartTimestamp;
            Result submitResult;
            VulkanSubmissionReceipt submitReceipt =
                VulkanSubmissionReceipt.Rejected(Result.ErrorUnknown);
            try
            {
                using (VulkanCpuStageScope cpuStage =
                       new(
                           _frameTelemetry,
                           EVulkanCpuStage.Submission,
                           enabled: false))
                {
                    // SubmitToQueueTrackedWithDisposition is the sole queue
                    // gateway. Keep diagnostic preparation and completion-ledger
                    // publication outside its narrow native queue lease.
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
                        _ = attempt.CompletePhase(
                            EVulkanFrameStage.QueueSubmit,
                            EDesktopFrameFlow.Continue);
                        submitReceipt = SubmitFrameTargetLease(
                            in attempt.FrameTargetLease,
                            commandBuffers,
                            commandBufferCount,
                            signalGraphicsTimeline: true,
                            minimumGraphicsTimelineSignalValue:
                                attempt.AcquireTimelineValue + 1UL,
                            out attempt.GraphicsSignalValue,
                            in diagnosticContext,
                            caller: "RenderFrameCallback");
                        submitResult = submitReceipt.Result;
                        attempt.SubmitResult = submitResult;
                        attempt.Timing.QueueSubmitAdmission +=
                            submitReceipt.QueueAdmissionWait;
                        attempt.Timing.NativeQueueSubmit +=
                            submitReceipt.NativeDispatchElapsed;
                        attempt.Timing.RecordCausalWait(
                            new VulkanFrameCausalWait(
                                EVulkanFrameWaitReason.QueueSubmitAdmission,
                                submitReceipt.QueueAdmissionWait,
                                attempt.FrameNumber,
                                attempt.FrameSlot,
                                unchecked((int)attempt.ImageIndex),
                                SemaphoreTargetValue: attempt.AcquireTimelineValue,
                                SemaphoreCompletedValue: attempt.AcquireTimelineValue,
                                QueueFamily: _deviceContext.QueueFamilies.GraphicsFamilyIndex ?? 0U,
                                PendingCommandCount: checked((int)commandBufferCount),
                                ConcurrentWorkerActivity: Volatile.Read(
                                    ref _commandRuntime.Workers.ActiveWorkerCount),
                                Stage: EVulkanFrameStage.QueueSubmit));
                        attempt.Timing.RecordCausalWait(
                            new VulkanFrameCausalWait(
                                EVulkanFrameWaitReason.NativeQueueSubmit,
                                submitReceipt.NativeDispatchElapsed,
                                attempt.FrameNumber,
                                attempt.FrameSlot,
                                unchecked((int)attempt.ImageIndex),
                                SemaphoreTargetValue: attempt.GraphicsSignalValue,
                                SemaphoreCompletedValue: 0UL,
                                QueueFamily: _deviceContext.QueueFamilies.GraphicsFamilyIndex ?? 0U,
                                PendingCommandCount: checked((int)commandBufferCount),
                                ConcurrentWorkerActivity: Volatile.Read(
                                    ref _commandRuntime.Workers.ActiveWorkerCount),
                                Stage: EVulkanFrameStage.QueueSubmit));
                        if (submitReceipt.SubmissionAccepted)
                        {
                            // The queue owns this frame as soon as vkQueueSubmit accepts it. Set
                            // settlement flags before profiling/telemetry scopes can unwind.
                            attempt.Submitted = true;
                            _gpuDiagnosticReadbackSidecar?.MarkPrimarySubmissionAccepted(
                                attempt.SceneCommandBuffer,
                                attempt.GraphicsSignalValue);
                            attempt.CommandArtifactsSettled = true;
                            mappedFrameArena?.MarkFrameSlotSubmitted(
                                attempt.ImageIndex,
                                mappedFrameGeneration);
                            frameDataArena?.MarkFrameSlotSubmitted(
                                checked((uint)attempt.FrameSlot),
                                frameDataGeneration);
                            attempt.TransitionAcquireOwnership(
                                EVulkanDesktopAcquireOwnership
                                    .ConsumedBySubmissionImagePendingPresent);
                            attempt.AdvanceTo(EDesktopFramePhase.Submitted);
                            PublishAcceptedDesktopSubmissionReuseLedgers(ref attempt);
                            PublishDesktopReadbackReceipts(in attempt);
                            if (attempt.UploadOwnership == EVulkanDesktopUploadOwnership.Recorded)
                                attempt.TransitionUploadOwnership(
                                    EVulkanDesktopUploadOwnership.SubmittedDeferredFree);
                            if (attempt.WorkClass == ERenderOutputWorkClass.PresentNow &&
                                attempt.GraphicsSignalValue == 0UL)
                            {
                                TimeSpan elapsed =
                                    Stopwatch.GetElapsedTime(attempt.StartTimestamp);
                                VulkanPresentNowReadinessException failure = new(
                                    attempt.FrameNumber,
                                    EVulkanPresentNowReadinessStage.QueueSubmission,
                                    "graphics-submission-serial",
                                    "DesktopScene -> recorded primary -> graphics queue submission",
                                    elapsed,
                                    TimeSpan.Zero,
                                    "The queue accepted new work without publishing a nonzero submission serial.");
                                _presentNowTerminalFailure ??= failure;
                                attempt.DeferredFailure ??= failure;
                                Debug.VulkanError(
                                    $"[Vulkan][PresentNow][RendererPaused] {failure.Message}");
                            }
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
                    _ = frameDataArena?.TryCancelFrameSlotSubmission(
                        checked((uint)attempt.FrameSlot),
                        frameDataGeneration);
                }
                CompleteMappedFrameArenaDeviceLossObservation();
                throw;
            }
            using (VulkanCpuStageScope publicationStage = new(
                       _frameTelemetry,
                       EVulkanCpuStage.SubmissionPublication,
                       enabled: false))
            {
                attempt.Timing.SubmitQueue +=
                    Stopwatch.GetElapsedTime(stageStartTimestamp);
                attempt.SubmitCompletedTimestamp = Stopwatch.GetTimestamp();
                if (submitResult != Result.Success)
                {
                    _ = mappedFrameArena?.TryCancelFrameSlotSubmission(
                        attempt.ImageIndex,
                        mappedFrameGeneration);
                    _ = frameDataArena?.TryCancelFrameSlotSubmission(
                        checked((uint)attempt.FrameSlot),
                        frameDataGeneration);
                    return HandleDesktopSubmitFailure(
                        ref attempt,
                        submitResult);
                }

                _frameTelemetry.MarkFrameTimingSubmitted(
                    unchecked((int)Math.Min(
                        attempt.ImageIndex,
                        int.MaxValue)),
                    RuntimeEngine.Rendering.State.RenderFrameId);
                CommitSubmittedDesktopTextureUpload(
                    ref attempt,
                    attempt.GraphicsSignalValue,
                    "graphics frame");
                FlushPendingGpuRenderStatsReadbacks();
                _commandRuntime.FlushDeferredLightingObjectReadback(attempt.SceneCommandBuffer);
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

                WaitForNextDesktopFrameSlotBeforeCollect(ref attempt);
                ReleaseCollectForDesktopFrame(ref attempt);

                stageStartTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                               "Vulkan.FrameLifecycle.TrimStaging"))
                    {
                        ResourceRuntime.Allocations.Staging.Trim(
                            ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                                "The Vulkan backend object context is not initialized."));
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

        private static unsafe void AppendDesktopSubmitCommandBuffer(
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
            if (attempt.TextureUploadCommandBuffer.Handle == 0 &&
                attempt.UploadOwnership != EVulkanDesktopUploadOwnership.SubmittedDeferredFree)
                return;

            if (attempt.UploadOwnership == EVulkanDesktopUploadOwnership.Recorded)
                attempt.TransitionUploadOwnership(
                    EVulkanDesktopUploadOwnership.SubmittedDeferredFree);

            if (!attempt.TextureUploadTimelinePublished)
            {
                ResourceRuntime.Uploads.PublicationState.QueueRecordedForTimeline(
                    signalValue,
                    uploadSource);
                attempt.TextureUploadTimelinePublished = true;
            }
            if (!attempt.TextureUploadRetirementQueued)
            {
                _commandRuntime.DeferSecondaryCommandBufferFree(
                    Api,
                    _deviceContext.Device,
                    ResourceRuntime,
                    attempt.FrameSlot,
                    attempt.ImageIndex,
                    attempt.TextureUploadCommandPool,
                    attempt.TextureUploadCommandBuffer,
                    "FrameLoop.TextureUploadSecondary");
                attempt.TextureUploadRetirementQueued = true;
                attempt.TextureUploadCommandBuffer = default;
                attempt.TextureUploadCommandPool = default;
            }
            if (attempt.UploadOwnership == EVulkanDesktopUploadOwnership.SubmittedDeferredFree &&
                attempt.TextureUploadTimelinePublished &&
                attempt.TextureUploadRetirementQueued)
            {
                attempt.TransitionUploadOwnership(EVulkanDesktopUploadOwnership.Retired);
            }
        }

        /// <summary>Publishes reuse-safety values immediately after native queue acceptance.</summary>
        private void PublishAcceptedDesktopSubmissionReuseLedgers(ref VulkanFrameAttempt attempt)
        {
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
                    reason);
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
