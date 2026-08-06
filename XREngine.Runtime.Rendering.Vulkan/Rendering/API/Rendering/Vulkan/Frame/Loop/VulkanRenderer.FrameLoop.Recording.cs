using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private EDesktopFrameFlow RecordDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            VulkanImGuiFrameSnapshot? imguiOverlaySnapshot = null;
            bool hasPendingImGuiOverlay = false;
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.SnapshotImGuiOverlay"))
            {
                if (CanRecordImGuiOverlayCommandBuffer(attempt.ImageIndex))
                {
                    hasPendingImGuiOverlay =
                        TryConsumeRenderableImGuiOverlaySnapshot(
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
                FrameOp[] dynamicTextOverlayOps;
                ulong dynamicTextOverlaySignature;
                PrimaryCommandArtifactOwner? dynamicTextOverlayVariant;

                stageStartTimestamp = Stopwatch.GetTimestamp();
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.FrameLifecycle.RecordCommandBuffer"))
                {
                    long allocationStart =
                        GC.GetAllocatedBytesForCurrentThread();
                    try
                    {
                        attempt.SceneCommandBuffer =
                            EnsureCommandBufferRecorded(
                                attempt.ImageIndex,
                                attempt.PreserveSwapchainForImGuiOverlay,
                                out string recordingDeferredReason,
                                out dynamicTextSecondaryCommandBuffer,
                                out dynamicTextOverlayOpCount,
                                out dynamicTextOverlayOps,
                                out dynamicTextOverlaySignature,
                                out dynamicTextOverlayVariant,
                                out attempt.TextureUploadCommandBuffer,
                                out attempt.TextureUploadCommandPool,
                                out attempt.SwapchainLayoutAfterScene,
                                out attempt.SceneCommandBufferDirtyGeneration);
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
                        when (IsTransientResourceRetirementRecordingFailure(ex))
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
                attempt.PresentationSource = _windowPresentSource.Capture();
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

                if (attempt.PreserveSwapchainForImGuiOverlay &&
                    attempt.HasImGuiOverlayCommandBuffer &&
                    dynamicTextOverlayOpCount > 0)
                {
                    RecordDesktopDynamicTextOverlay(
                        ref attempt,
                        dynamicTextSecondaryCommandBuffer,
                        dynamicTextOverlayOpCount,
                        dynamicTextOverlayOps,
                        dynamicTextOverlaySignature,
                        dynamicTextOverlayVariant);
                }

                attempt.AdvanceTo(EDesktopFramePhase.Recorded);
                return ValidateDesktopRecording(ref attempt);
            }
            finally
            {
                _imguiDrawData.Recycle(imguiOverlaySnapshot);
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
                IsSwapchainResourceRetirementRecordingFailure(reason);
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
            MarkCommandBuffersDirty(
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
                        TryRecordImGuiOverlayCommandBuffer(
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
            int overlayOpCount,
            FrameOp[] overlayOps,
            ulong overlaySignature,
            PrimaryCommandArtifactOwner? overlayVariant)
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
                        TryRecordDynamicUiBatchTextOverlayCommandBuffer(
                            attempt.ImageIndex,
                            secondaryCommandBuffer,
                            overlayOpCount,
                            ImageLayout.PresentSrcKhr,
                            attempt.ImGuiOverlayCommandBuffer,
                            overlayVariant,
                            overlayOps,
                            overlaySignature,
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

        private EDesktopFrameFlow ValidateDesktopRecording(
            ref VulkanFrameAttempt attempt)
        {
            if (!TryValidatePresentationSourceForSubmission(
                    attempt.PresentationSource,
                    attempt.SceneCommandBuffer,
                    attempt.ImageIndex,
                    out string presentationSourceFailure))
            {
                MarkCommandBuffersDirty(presentationSourceFailure);
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
                            Phase524bInjectedDesktopRejectionStage,
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
                HaveCommandBuffersDirtiedSince(
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
                _windowPresentSource.Capture();
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

            bool generationsCurrent =
                GetCurrentVulkanResourceGeneration(
                    ObjectType.Image,
                    source.Image.Handle) ==
                    source.ImageAllocationGeneration &&
                GetCurrentVulkanResourceGeneration(
                    ObjectType.ImageView,
                    source.ImageView.Handle) ==
                    source.ImageViewGeneration &&
                GetCurrentVulkanResourceGeneration(
                    ObjectType.Sampler,
                    source.Sampler.Handle) ==
                    source.SamplerGeneration &&
                GetCurrentVulkanResourceGeneration(
                    ObjectType.DescriptorSet,
                    source.DescriptorSet.Handle) ==
                    source.DescriptorSetGeneration &&
                ResolveCommandBufferRecordingGeneration(
                    source.OwningCommandArtifact) ==
                    source.OwningCommandArtifactGeneration;
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
