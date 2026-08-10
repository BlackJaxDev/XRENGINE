using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        internal EDesktopFrameFlow RecordDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            VulkanImGuiFrameSnapshot? imguiOverlaySnapshot = null;
            bool hasPendingImGuiOverlay = false;
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.SnapshotImGuiOverlay"))
            {
                if (ImGuiOverlayAdmission.CanRecord(attempt.ImageIndex))
                {
                    hasPendingImGuiOverlay =
                        ImGuiOverlayAdmission.TryConsumeRenderableSnapshot(
                            DesktopWsiOutput.IsInteractiveResizeInProgress,
                            out imguiOverlaySnapshot);
                }
            }

            attempt.Timing.SnapshotImGuiOverlay +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            attempt.PreserveSwapchainForImGuiOverlay =
                hasPendingImGuiOverlay &&
                UseDynamicRenderingRenderTargets;

            try
            {
                ThrowIfDesktopFrameFaultInjected(
                    EVulkanDesktopFrameFaultPoint.SceneRecording);
                CommandBuffer dynamicTextSecondaryCommandBuffer;
                int dynamicTextOverlayOpCount;

                stageStartTimestamp = Stopwatch.GetTimestamp();
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.RecordCommandBuffer"))
                {
                    long allocationStart =
                        GC.GetAllocatedBytesForCurrentThread();
                    try
                    {
                        VulkanPrimaryCommandRecordingResult recordingResult =
                            RecordPreparedDesktopPrimary(
                                attempt.ImageIndex,
                                attempt.PreserveSwapchainForImGuiOverlay);
                        string recordingDeferredReason =
                            recordingResult.Succeeded
                                ? string.Empty
                                : recordingResult.Reason ??
                                  "primary command recording was deferred";
                        attempt.SceneCommandBuffer =
                            recordingResult.CommandBuffer;
                        dynamicTextSecondaryCommandBuffer =
                            recordingResult.DynamicUiSecondaryCommandBuffer;
                        dynamicTextOverlayOpCount =
                            recordingResult.DynamicUiOverlayOperationCount;
                        attempt.TextureUploadCommandBuffer =
                            recordingResult.TextureUploadCommandBuffer;
                        attempt.TextureUploadCommandPool =
                            recordingResult.TextureUploadCommandPool;
                        attempt.SwapchainLayoutAfterScene =
                            recordingResult.SwapchainLayoutAfterCommandBuffer;
                        attempt.SceneCommandBufferDirtyGeneration =
                            recordingResult.CommandBufferDirtyGeneration;
                        _lastEnsureCommandBufferRecordedPrimary =
                            recordingResult.Disposition ==
                            EVulkanPrimaryCommandRecordingDisposition.Recorded;
                        if (attempt.TextureUploadCommandBuffer.Handle != 0)
                        {
                            attempt.TransitionUploadOwnership(
                                EVulkanDesktopUploadOwnership.Recorded);
                        }

                        attempt.SceneSwapchainWriteCount =
                            ResolveRecordedDesktopSwapchainWriteCount(
                                ref attempt,
                                attempt.SceneCommandBuffer);

                        if (!string.IsNullOrEmpty(recordingDeferredReason))
                        {
                            return HandleDesktopRecordingDeferred(
                                ref attempt,
                                recordingDeferredReason,
                                imguiOverlaySnapshot);
                        }
                    }
                    catch (InvalidOperationException ex)
                        when (VulkanRecordingFailurePolicy.IsTransientResourceRetirement(ex))
                    {
                        return HandleDesktopRecordingResourceRetired(
                            ref attempt,
                            ex.Message);
                    }
                    catch (Exception ex)
                    {
                        RecoverDesktopRecordingException(
                            ref attempt,
                            "command buffer recording failed",
                            EDesktopFrameReason.RecordingFailed,
                            ex);
                        throw;
                    }
                    finally
                    {
                        TimeSpan elapsed =
                            Stopwatch.GetElapsedTime(stageStartTimestamp);
                        attempt.Timing.RecordSceneCommandBuffer += elapsed;
                        attempt.Timing.RecordCommandBuffer += elapsed;
                        long allocatedBytes =
                            GC.GetAllocatedBytesForCurrentThread() -
                            allocationStart;
                        if (_lastEnsureCommandBufferRecordedPrimary)
                        {
                            RuntimeEngine.Rendering.Stats.Vulkan
                                .RecordVulkanRecordCommandBufferAllocation(
                                    allocatedBytes);
                        }
                    }
                }

                attempt.ScenePrimaryRecordedThisFrame =
                    _lastEnsureCommandBufferRecordedPrimary;
                VulkanPresentationSourceTuple presentationSource =
                    _windowPresentSource.CaptureForDescriptorSlot(
                        checked((int)attempt.ImageIndex));
                if (presentationSource.HasLogicalSource)
                {
                    _ = _windowPresentSource.TryBindCommandArtifact(
                        presentationSource.LogicalEpoch,
                        checked((int)attempt.ImageIndex),
                        attempt.SceneCommandBuffer,
                        _commandRuntime.CommandBuffers.ResolveRecordingGeneration(
                            attempt.SceneCommandBuffer),
                        out presentationSource);
                }
                attempt.PresentationSource = presentationSource;
                if (RecordDesktopImGuiOverlay(
                        ref attempt,
                        imguiOverlaySnapshot) !=
                    EDesktopFrameFlow.Continue)
                {
                    return attempt.Flow;
                }

                if (dynamicTextOverlayOpCount > 0 &&
                    VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.DynamicUiText.LateOverlayDecision.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Dynamic UI text late-overlay decision: preserveForImGui={0} hasImGui={1} ops={2} secondary=0x{3:X}",
                        attempt.PreserveSwapchainForImGuiOverlay,
                        attempt.HasImGuiOverlayCommandBuffer,
                        dynamicTextOverlayOpCount,
                        dynamicTextSecondaryCommandBuffer.Handle);
                }

                if (dynamicTextOverlayOpCount > 0)
                {
                    RecordDesktopDynamicTextOverlay(
                        ref attempt,
                        dynamicTextSecondaryCommandBuffer,
                        dynamicTextOverlayOpCount);
                }

                attempt.AdvanceTo(EDesktopFramePhase.Recorded);
                return ValidateDesktopRecording(ref attempt);
            }
            finally
            {
                _outputRuntime._imguiDrawData.Recycle(imguiOverlaySnapshot);
            }
        }

        private EDesktopFrameFlow HandleDesktopRecordingDeferred(
            ref VulkanFrameAttempt attempt,
            string reason,
            VulkanImGuiFrameSnapshot? recoveryOverlaySnapshot)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecordDeferredReason",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Scene command-buffer recording deferred; a separately recorded texture-upload batch will remain eligible for the recovery submit. {0}",
                reason);
            bool swapchainAttachmentRetired =
                VulkanRecordingFailurePolicy.IsSwapchainResourceRetirement(reason);
            if (TryRecoverRejectedDesktopImage(
                    ref attempt,
                    commandBufferDirtyFlagSet: false,
                    commandBuffersDirtiedAfterSceneRecord: true,
                    recordedSwapchainWriteCount:
                        attempt.SceneSwapchainWriteCount,
                    rejectionStage: "RecordDeferred",
                    rejectedSubmitResult: null,
                    recoveryOverlaySnapshot:
                        recoveryOverlaySnapshot))
            {
                if (swapchainAttachmentRetired)
                {
                    ScheduleSwapchainRecreate(
                        "A generation-bound swapchain attachment retired during command recording");
                }

                attempt.Reason =
                    EDesktopFrameReason.RecordingDeferred;
                return EDesktopFrameFlow.Completed;
            }

            _ = ConsumeDesktopAcquireForRecovery(
                ref attempt,
                "RecordDeferred");
            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecordDeferred",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Command buffer recording deferred before vkBeginCommandBuffer; retrying the output on its next frame. {0}",
                reason);
            ResolveDesktopAcquireBySwapchainRecreation(
                ref attempt,
                "Deferred-recording fallback could not return acquired image ownership");
            CompleteDesktopFrameSlot(ref attempt);
            attempt.Stop(
                EDesktopFrameReason.RecordingDeferred,
                EDesktopFrameRecoveryAction.RecreateSwapchain);
            return EDesktopFrameFlow.Stop;
        }

        private EDesktopFrameFlow HandleDesktopRecordingResourceRetired(
            ref VulkanFrameAttempt attempt,
            string reason)
        {
            ReleaseUnsubmittedDesktopUpload(
                ref attempt,
                "command buffer resource generation retired during recording");
            _commandRuntime.CommandBuffers.MarkDirty(
                "command buffer resource generation retired during recording");

            if (TryRecoverRejectedDesktopImage(
                    ref attempt,
                    commandBufferDirtyFlagSet: true,
                    commandBuffersDirtiedAfterSceneRecord: true,
                    recordedSwapchainWriteCount: 0,
                    rejectionStage: "RecordResourceRetired",
                    rejectedSubmitResult: null))
            {
                attempt.Reason =
                    EDesktopFrameReason.RecordingResourceRetired;
                return EDesktopFrameFlow.Completed;
            }

            _ = ConsumeDesktopAcquireForRecovery(
                ref attempt,
                "RecordResourceRetired");
            ResolveDesktopAcquireBySwapchainRecreation(
                ref attempt,
                "Retired-resource recording fallback could not return acquired image ownership");
            CompleteDesktopFrameSlot(ref attempt);
            attempt.Stop(
                EDesktopFrameReason.RecordingResourceRetired,
                EDesktopFrameRecoveryAction.RecreateSwapchain);
            return EDesktopFrameFlow.Stop;
        }

        private EDesktopFrameFlow RecordDesktopImGuiOverlay(
            ref VulkanFrameAttempt attempt,
            VulkanImGuiFrameSnapshot? snapshot)
        {
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            try
            {
                ThrowIfDesktopFrameFaultInjected(
                    EVulkanDesktopFrameFaultPoint.OverlayRecording);
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.RecordImGuiOverlay"))
                {
                    attempt.HasImGuiOverlayCommandBuffer =
                        snapshot is not null &&
                        TryRecordImGuiOverlay(
                            attempt.ImageIndex,
                            snapshot,
                            attempt.SwapchainLayoutAfterScene,
                            attempt.SceneCommandBuffer,
                            out attempt.ImGuiOverlayCommandBuffer);
                    if (attempt.PreserveSwapchainForImGuiOverlay &&
                        !attempt.HasImGuiOverlayCommandBuffer)
                    {
                        throw new InvalidOperationException(
                            "Scene primary preserved the swapchain for ImGui, but the overlay command buffer was not recorded.");
                    }
                }
            }
            catch (Exception ex)
            {
                RecoverDesktopRecordingException(
                    ref attempt,
                    "ImGui overlay command buffer recording failed",
                    EDesktopFrameReason.OverlayRecordingFailed,
                    ex);
                throw;
            }
            finally
            {
                TimeSpan elapsed =
                    Stopwatch.GetElapsedTime(stageStartTimestamp);
                attempt.Timing.RecordImGuiOverlay += elapsed;
                attempt.Timing.RecordCommandBuffer += elapsed;
            }

            long elapsedTicks =
                Stopwatch.GetTimestamp() - stageStartTimestamp;
            RecordOverlayFrameOutput(
                EFrameOutputKind.ImGuiOverlay,
                "Vulkan ImGui overlay command buffer",
                attempt.HasImGuiOverlayCommandBuffer,
                attempt.HasImGuiOverlayCommandBuffer ? 1 : 0,
                elapsedTicks);
            return EDesktopFrameFlow.Continue;
        }

        private void RecordDesktopDynamicTextOverlay(
            ref VulkanFrameAttempt attempt,
            CommandBuffer secondaryCommandBuffer,
            int overlayOpCount)
        {
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            try
            {
                ThrowIfDesktopFrameFaultInjected(
                    EVulkanDesktopFrameFaultPoint.OverlayRecording);
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.RecordDynamicUiTextOverlay"))
                {
                    attempt.HasDynamicTextOverlayCommandBuffer =
                        TryRecordDesktopDynamicTextOverlayCommandBuffer(
                            attempt.ImageIndex,
                            secondaryCommandBuffer,
                            overlayOpCount,
                            attempt.HasImGuiOverlayCommandBuffer
                                ? ImageLayout.PresentSrcKhr
                                : attempt.SwapchainLayoutAfterScene,
                            attempt.HasImGuiOverlayCommandBuffer
                                ? attempt.ImGuiOverlayCommandBuffer
                                : attempt.SceneCommandBuffer,
                            out attempt.DynamicTextOverlayCommandBuffer);
                }
            }
            catch (Exception ex)
            {
                RecoverDesktopRecordingException(
                    ref attempt,
                    "dynamic UI text overlay command buffer recording failed",
                    EDesktopFrameReason.OverlayRecordingFailed,
                    ex);
                throw;
            }
            finally
            {
                TimeSpan elapsed =
                    Stopwatch.GetElapsedTime(stageStartTimestamp);
                attempt.Timing.RecordDynamicUiTextOverlay += elapsed;
                attempt.Timing.RecordCommandBuffer += elapsed;
            }

            long elapsedTicks =
                Stopwatch.GetTimestamp() - stageStartTimestamp;
            RecordOverlayFrameOutput(
                EFrameOutputKind.DynamicTextOverlay,
                "Vulkan dynamic text overlay command buffer",
                attempt.HasDynamicTextOverlayCommandBuffer,
                attempt.HasDynamicTextOverlayCommandBuffer ? 1 : 0,
                elapsedTicks);
        }

        private bool TryRecordDesktopDynamicTextOverlayCommandBuffer(
            uint imageIndex,
            CommandBuffer secondaryCommandBuffer,
            int operationCount,
            ImageLayout initialSwapchainLayout,
            CommandBuffer predecessorCommandBuffer,
            out CommandBuffer overlayCommandBuffer)
        {
            overlayCommandBuffer = default;
            CommandBuffer[]? overlays = _commandRuntime.CommandBuffers.DynamicUiOverlays;
            if (overlays is null || imageIndex >= overlays.Length ||
                !_outputRuntime.TryCaptureDynamicUiOverlayTarget(imageIndex, out VulkanDynamicUiOverlayTarget target))
            {
                return false;
            }

            VulkanDynamicUiBatchTextOverlayRecordingInput input = new(
                overlays[imageIndex],
                secondaryCommandBuffer,
                operationCount,
                initialSwapchainLayout,
                predecessorCommandBuffer,
                _outputRuntime.Desktop.StreamlineFrameGenerationActive,
                target);
            bool recorded = _commandRuntime.DynamicUiOverlayRecorder.TryRecord(
                new VulkanTrackedCommandEncoder(_commandRuntime),
                _telemetry,
                in input,
                out overlayCommandBuffer,
                out bool streamlineUiInitialized);
            if (streamlineUiInitialized)
                _outputRuntime.MarkStreamlineUiImageInitialized(imageIndex);
            return recorded;
        }

        private EDesktopFrameFlow ValidateDesktopRecording(
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
                    $"recording validation failed: {presentationSourceFailure}");
                return HandleDesktopRecordingDeferred(
                    ref attempt,
                    presentationSourceFailure,
                    recoveryOverlaySnapshot: null);
            }

            FrameOpContext? phase524bContext =
                attempt.PresentationSource.LogicalEpoch != 0
                    ? attempt.PresentationSource.Context
                    : _lastWindowPresentFrameOpContext ??
                ActiveLastActiveFrameOpContext;
            if (phase524bContext.HasValue &&
                TryPreparePhase524bInjectedDesktopRejection(
                    phase524bContext.Value,
                    attempt.ImageIndex))
            {
                if (TryRecoverRejectedDesktopImage(
                        ref attempt,
                        commandBufferDirtyFlagSet: false,
                        commandBuffersDirtiedAfterSceneRecord: false,
                        recordedSwapchainWriteCount:
                            attempt.SceneSwapchainWriteCount,
                        rejectionStage:
                            VulkanRejectedDesktopFramePolicy.InjectedRejectionStage,
                        rejectedSubmitResult: null))
                {
                    return EDesktopFrameFlow.Completed;
                }

                throw new InvalidOperationException(
                    "The controlled Phase 5.2.4b desktop rejection could not apply its last-completed-image policy.");
            }

            bool dirtyFlag =
                _commandBufferDirtyFlags is not null &&
                attempt.ImageIndex <
                (uint)_commandBufferDirtyFlags.Length &&
                _commandBufferDirtyFlags[attempt.ImageIndex];
            bool generationChanged =
                _commandRuntime.CommandBuffers.HaveDirtiedSince(
                    attempt.SceneCommandBufferDirtyGeneration);
            if (attempt.ScenePrimaryRecordedThisFrame &&
                dirtyFlag &&
                !generationChanged)
            {
                _commandBufferDirtyFlags![attempt.ImageIndex] = false;
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.FreshPrimaryDirtiedBeforeSubmit",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Continuing with freshly recorded command buffer for image {0} after clearing its pre-existing dirty flag. Cached reuse remains disabled for the affected variant.",
                    attempt.ImageIndex);
            }
            else if (dirtyFlag || generationChanged)
            {
                SettleRejectedDesktopCommandArtifacts(
                    ref attempt,
                    $"command buffer dirtied before submit: flag={dirtyFlag} generationChanged={generationChanged}");
                if (TryRecoverRejectedDesktopImage(
                        ref attempt,
                        dirtyFlag,
                        generationChanged,
                        attempt.SceneSwapchainWriteCount,
                        "CommandBufferDirtiedBeforeSubmit",
                        rejectedSubmitResult: null))
                {
                    return EDesktopFrameFlow.Completed;
                }

                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.DirtyBeforeSubmitFallback",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Command buffer for image {0} was dirtied after recording and before submit, and skipped-frame present failed. Recreating swapchain to recover. flag={1} generationChanged={2}",
                    attempt.ImageIndex,
                    dirtyFlag,
                    generationChanged);
                _ = ConsumeDesktopAcquireForRecovery(
                    ref attempt,
                    "CommandBufferDirtiedBeforeSubmit");
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Command buffer dirtied before submit - recovering timeline/present state");
                CompleteDesktopFrameSlot(ref attempt);
                attempt.Stop(
                    EDesktopFrameReason.RecordingDirtied,
                    EDesktopFrameRecoveryAction.RecreateSwapchain);
                return EDesktopFrameFlow.Stop;
            }

            attempt.AdvanceTo(EDesktopFramePhase.Validated);
            return EDesktopFrameFlow.Continue;
        }

        private bool TryValidatePresentationSourceForSubmission(
            in VulkanPresentationSourceTuple source,
            CommandBuffer sceneCommandBuffer,
            uint descriptorSlot,
            out string failureReason)
        {
            failureReason = string.Empty;
            VulkanPresentationSourceTuple published =
                _windowPresentSource.CaptureForDescriptorSlot(
                    checked((int)descriptorSlot));
            if (!source.Equals(published))
            {
                failureReason =
                    $"final presentation source publication changed before submit (recorded epoch={source.LogicalEpoch}, current epoch={published.LogicalEpoch})";
                return false;
            }

            if (source.DescriptorResourceEpoch != published.DescriptorResourceEpoch ||
                source.DescriptorPublicationGeneration != published.DescriptorPublicationGeneration)
            {
                failureReason =
                    $"final presentation descriptor publication changed before submit (epoch={source.LogicalEpoch})";
                return false;
            }

            if (!source.HasLogicalSource)
                return true;

            if (!source.IsComplete)
            {
                failureReason =
                    $"final presentation source epoch {source.LogicalEpoch} is incomplete";
                return false;
            }

            if (source.OwningCommandArtifact.Handle != sceneCommandBuffer.Handle)
            {
                failureReason =
                    $"final presentation source epoch {source.LogicalEpoch} was not recorded by the selected scene primary";
                return false;
            }

            if (source.DescriptorSlot != checked((int)descriptorSlot))
            {
                failureReason =
                    $"final presentation source epoch {source.LogicalEpoch} uses descriptor slot {source.DescriptorSlot}, not acquired slot {descriptorSlot}";
                return false;
            }

            ulong currentImageGeneration = ResourceRuntime.GetPublishedGeneration(
                ObjectType.Image,
                source.Image.Handle);
            ulong currentImageViewGeneration = ResourceRuntime.GetPublishedGeneration(
                ObjectType.ImageView,
                source.ImageView.Handle);
            ulong currentSamplerGeneration = ResourceRuntime.GetPublishedGeneration(
                ObjectType.Sampler,
                source.Sampler.Handle);
            ulong currentDescriptorSetGeneration = ResourceRuntime.GetPublishedGeneration(
                ObjectType.DescriptorSet,
                source.DescriptorSet.Handle);
            ulong currentCommandGeneration = _commandRuntime.CommandBuffers
                .ResolveRecordingGeneration(source.OwningCommandArtifact);
            bool generationsCurrent =
                currentImageGeneration == source.ImageAllocationGeneration &&
                currentImageViewGeneration == source.ImageViewGeneration &&
                currentSamplerGeneration == source.SamplerGeneration &&
                currentDescriptorSetGeneration == source.DescriptorSetGeneration &&
                currentCommandGeneration == source.OwningCommandArtifactGeneration;
            if (generationsCurrent)
                return true;

            failureReason =
                $"final presentation source epoch {source.LogicalEpoch} references a superseded native generation";
            return false;
        }

        private void RecoverDesktopRecordingException(
            ref VulkanFrameAttempt attempt,
            string operation,
            EDesktopFrameReason reason,
            Exception exception)
        {
            ReleaseUnsubmittedDesktopUpload(ref attempt, operation);
            _ = ConsumeDesktopAcquireForRecovery(ref attempt, operation);
            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] {0}. Recreating swapchain ownership before propagating the failure. {1}",
                operation,
                exception.Message);
            ResolveDesktopAcquireBySwapchainRecreation(
                ref attempt,
                $"{operation} - recovering timeline/present state");
            CompleteDesktopFrameSlot(ref attempt);
            attempt.Stop(
                reason,
                EDesktopFrameRecoveryAction.RecreateSwapchain);
        }
    }
}
