using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool TryPrepareCommandBufferVariantForRecording(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            if (TryPrepareComputeFrameOpsForRecording(
                    state.ImageIndex,
                    state.FrameOperations,
                    out string preparationFailureReason) &&
                TryPrepareComputeFrameOpsForRecording(
                    state.ImageIndex,
                    state.DynamicUiOperations,
                    out preparationFailureReason))
            {
                return true;
            }

            return DeferCommandBufferLifecycle(
                ref context,
                ref state,
                preparationFailureReason);
        }

        private void RecordCommandBufferCacheMiss(
            scoped ref CommandBufferLifecycleState state)
        {
            string? dirtyReason = VulkanFrameDiagnosticsTraceEnabled
                ? DescribePrimaryReuseMiss(
                    state.Variant,
                    state.CurrentGenerations,
                    state.DependencyMismatch,
                    state.ForcedDirty,
                    state.ImageForcedDirty,
                    state.ForcedVariantDirtyReason,
                    state.FrameOpSignatureDirty,
                    state.PlannerDirty,
                    state.ProfilerDirty,
                    state.FrameDataDirty,
                    state.DynamicUiDirty,
                    state.SwapchainLifecycleDirty,
                    state.CommandChainPrimaryDirty,
                    state.CommandChainPrimaryDirtyReason,
                    state.CommandChainSchedule?.StructuralSignature ??
                        ulong.MaxValue,
                    state.CommandChainPrimaryGroupSignature,
                    state.CommandChainPrimaryIdentityComponents,
                    state.CommandChainPrimaryGroupCount,
                    state.PrimaryFrameStateDirty,
                    state.PrimaryFrameStateDirtyReason,
                    state.PrimaryImageEntryStateMismatch,
                    state.PlannerRevision,
                    state.ImageLayoutStartSignature,
                    state.SwapchainImageEverPresentedAtRecord)
                : null;

            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanCommandBufferCacheOutcome(
                    reusedClean: false,
                    recorded: true,
                    state.ForcedDirty,
                    state.FrameOpSignatureDirty,
                    state.PlannerDirty,
                    state.ProfilerDirty,
                    dirtyReason,
                    detailReasons:
                        (state.FrameDataDirty
                            ? EVulkanCommandBufferDecisionReason.FrameData
                            : 0) |
                        (state.DynamicUiDirty
                            ? EVulkanCommandBufferDecisionReason.DynamicOverlay
                            : 0) |
                        (state.SwapchainLifecycleDirty
                            ? EVulkanCommandBufferDecisionReason
                                .SwapchainLifecycle
                            : 0) |
                        (state.CommandChainPrimaryDirty
                            ? EVulkanCommandBufferDecisionReason
                                .CommandChainPrimary
                            : 0) |
                        (state.PrimaryFrameStateDirty
                            ? EVulkanCommandBufferDecisionReason
                                .PrimaryFrameState
                            : 0) |
                        (state.Variant.RecordedGenerations.Descriptor !=
                         state.CurrentGenerations.Descriptor
                            ? EVulkanCommandBufferDecisionReason
                                .DescriptorGeneration
                            : 0) |
                        (state.Variant.RecordedGenerations.ResourceAllocation !=
                         state.CurrentGenerations.ResourceAllocation
                            ? EVulkanCommandBufferDecisionReason
                                .ResourceAllocation
                            : 0),
                    structuralSignature:
                        state.CurrentGenerations.Structural,
                    descriptorGeneration:
                        state.CurrentGenerations.Descriptor,
                    swapchainSlot: state.CommandBufferImageSlot);
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanCommandChainMetrics(
                    primaryCommandBuffersRecorded: 1);
        }

        private bool TryRecordCommandBufferVariant(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            _lastEnsureCommandBufferRecordedPrimary = true;
            _commandRecorder.EnterRecordingScope();
            state.RecordedDynamicUiSecondaryReady =
                state.DelayDynamicUiOverlayRecording;
            state.RecordedSwapchainWriteCount = 0;
            state.QueryFrameOperationsRequireRerecord = false;

            try
            {
                RecordDirtyCommandBufferDynamicUiSecondary(ref state);
                return TryRecordDirtyPrimaryCommandBuffer(
                    ref context,
                    ref state);
            }
            catch
            {
                CancelRecordedTextureUploadSubmitBatch(
                    "command buffer recording failed before upload submit");
                _ = TryAbandonCommandBufferRecording(
                    state.Variant.PrimaryCommandBuffer);
                state.Variant.Dirty = true;
                state.Variant.DirtyReason =
                    "primary command-buffer recording threw before completion";
                FailUnsubmittedSubmissionMarkers(
                    state.FrameOperations,
                    state.DynamicUiOperations);
                throw;
            }
            finally
            {
                _commandRecorder.ExitRecordingScope();
            }
        }

        private void RecordDirtyCommandBufferDynamicUiSecondary(
            scoped ref CommandBufferLifecycleState state)
        {
            if (!state.DelayDynamicUiOverlayRecording)
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.RecordCommandBuffer.RecordDynamicUiSecondary"))
                using (VulkanCpuStageScope cpuStage =
                       new(EVulkanCpuStage.SecondaryRecording))
                {
                    state.RecordedDynamicUiSecondaryReady =
                        RecordDynamicUiBatchTextSecondaryCommandBuffer(
                            state.ImageIndex,
                            state.Variant,
                            state.DynamicUiOperations,
                            state.DynamicUiSignature);
                }

                return;
            }

            state.Variant.DynamicUiSecondaryRecorded = false;
        }

        private bool TryRecordDirtyPrimaryCommandBuffer(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordCommandBuffer.RecordPrimary"))
            {
                using VulkanCpuStageScope cpuStage =
                    new(EVulkanCpuStage.PrimaryRecording);
                for (int recordingAttempt = 0;
                     recordingAttempt < _commandScheduler.RecordingAttemptLimit;
                     recordingAttempt++)
                {
                    bool primaryRecorded = TryRecordCommandBuffer(
                        state.ImageIndex,
                        state.Variant.PrimaryCommandBuffer,
                        state.Variant.DynamicUiSecondaryCommandBuffer,
                        state.FrameOperations,
                        state.RecordedDynamicUiSecondaryReady &&
                        !state.PreserveSwapchainForOverlay
                            ? state.DynamicUiOperations.Length
                            : 0,
                        state.CommandChainSchedule,
                        state.PreserveSwapchainForOverlay,
                        state.PrimaryCommandPlan,
                        out state.RecordedSwapchainWriteCount,
                        out context.SwapchainLayoutAfterCommandBuffer,
                        out context.RecordingDeferredReason,
                        out state.QueryFrameOperationsRequireRerecord,
                        framePlan: state.SealedFramePlan);
                    if (primaryRecorded)
                    {
                        bool primaryImageEntryValid =
                            TryValidateRecordedPrimaryImageEntryDependencies(
                                ref context,
                                ref state);
                        bool commandChainDependenciesValid =
                            primaryImageEntryValid &&
                            TryValidateRecordedCommandChainDependencies(
                                ref context,
                                ref state);
                        if (primaryImageEntryValid &&
                            commandChainDependenciesValid)
                        {
                            _lastEnsureCommandBufferRecordedPrimary = true;
                            context.RecordingDeferredReason = string.Empty;
                            return true;
                        }

                        // The native primary reached vkEndCommandBuffer, so it owns
                        // recorded dependency pins even though it has not been
                        // published to a submit path. Settle that exact recording
                        // before any retry or return; otherwise its lifetime lease
                        // survives without an outer handle that can release it.
                        DiscardRejectedPrimaryCommandBuffer(
                            state.Variant.PrimaryCommandBuffer);

                        if (recordingAttempt + 1 >=
                            _commandScheduler.RecordingAttemptLimit)
                        {
                            break;
                        }

                        // Lazy material publication can update an ordinary
                        // descriptor set while later command-chain groups are
                        // still being prepared. Vulkan invalidates every older
                        // secondary that recorded that set. Re-record those
                        // exact chains and the thin primary now, after the
                        // publication phase has settled, rather than dropping
                        // the complete scene frame and visibly flashing the
                        // rejected-frame recovery content.
                        Debug.VulkanWarningEvery(
                            $"Vulkan.Primary.RetryRecordedDependency.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Retrying primary command recording because a recorded dependency changed during command encoding: {0}",
                            context.RecordingDeferredReason);
                        continue;
                    }

                    if (recordingAttempt + 1 <
                            _commandScheduler.RecordingAttemptLimit &&
                        IsPlanPreconditionRecordingFailure(
                            context.RecordingDeferredReason) &&
                        TryReplanCommandBufferAfterPreconditionFailure(
                            ref context,
                            ref state))
                    {
                        state.ResealFramePlan();
                        CaptureCommandBufferDependencies(ref state);
                        Debug.VulkanWarningEvery(
                            $"Vulkan.Primary.RetryPlanPrecondition.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Rebuilt the immutable context-local frame plan after recording precondition failure: {0}",
                            context.RecordingDeferredReason);
                        context.RecordingDeferredReason = string.Empty;
                        continue;
                    }

                    if (!_commandScheduler.ShouldRetryRecording(
                            recordingAttempt,
                            IsTransientResourceRetirementRecordingFailure(
                                context.RecordingDeferredReason),
                            IsSwapchainResourceRetirementRecordingFailure(
                                context.RecordingDeferredReason)))
                    {
                        break;
                    }

                    // Texture uploads use an independent primary command buffer.
                    // Keep that batch alive while retrying the scene primary so a
                    // recovery submit can still make streaming progress.
                    VulkanSwapchainDepthResources? currentDepth =
                        CurrentSwapchainDepthResources;
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Primary.RetryRetiredResource.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Retrying primary command recording immediately because a resource generation retired during the first attempt: {0} CurrentSwapchainDepth=0x{1:X}/generation={2}",
                        context.RecordingDeferredReason,
                        currentDepth?.Image.Handle ?? 0,
                        GetCurrentVulkanResourceGeneration(
                            ObjectType.Image,
                            currentDepth?.Image.Handle ?? 0));
                }
            }

            _lastEnsureCommandBufferRecordedPrimary = false;
            state.Variant.Dirty = true;
            state.Variant.DirtyReason =
                context.RecordingDeferredReason;
            // A scene-recording deferral does not invalidate the separately
            // recorded texture-upload command buffer. Its caller either submits
            // that upload with the recovery frame or explicitly cancels it.
            FailUnsubmittedSubmissionMarkers(
                state.FrameOperations,
                state.DynamicUiOperations);
            return false;
        }

        private void DiscardRejectedPrimaryCommandBuffer(CommandBuffer commandBuffer)
        {
            if (!TryAbandonCommandBufferRecording(commandBuffer))
            {
                throw new InvalidOperationException(
                    $"Recorded primary command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} could not be abandoned after dependency validation failed.");
            }

            if (ResetVulkanCommandBufferTracked(commandBuffer) != Result.Success)
            {
                throw new InvalidOperationException(
                    $"Recorded primary command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} could not be reset after dependency validation failed.");
            }
        }

        private bool TryReplanCommandBufferAfterPreconditionFailure(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            FrameOp[] plannerOperations = state.HasStaticFrameOperations
                ? state.FrameOperations
                : state.DynamicUiOperations;
            if (plannerOperations.Length == 0)
                return false;

            // The native attempt has already been abandoned by the primary
            // recorder. Temporarily leave the logical recording scope so the
            // planner may publish a replacement snapshot, then re-enter before
            // the next bounded encoding attempt.
            _commandRecorder.ExitRecordingScope();
            try
            {
                using FrameOpResourcePlannerPreparationScope preparationScope =
                    new(this, plannerOperations);
                return TryPrepareCommandBufferResources(
                    ref context,
                    ref state,
                    in preparationScope);
            }
            finally
            {
                _commandRecorder.EnterRecordingScope();
            }
        }

        private static bool IsPlanPreconditionRecordingFailure(string? reason)
            => reason?.StartsWith(
                "frame-plan precondition failed",
                StringComparison.Ordinal) == true;

        private bool TryValidateRecordedCommandChainDependencies(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            if (state.CommandChainSchedule is null ||
                state.CommandChainCache is null)
            {
                return true;
            }

            VulkanPrimarySecondaryArtifactSequence executedArtifactSequence =
                _commandBufferRecordingScratch.Value!
                    .ExecutedCommandChainSecondaryArtifactSequence;
            if (!executedArtifactSequence.MatchesCurrentArtifacts(
                    state.CommandChainCache,
                    out string? artifactMismatch))
            {
                context.RecordingDeferredReason =
                    $"Recorded primary command buffer secondary artifact changed during command encoding: {artifactMismatch}.";
                state.Variant.Dirty = true;
                state.Variant.DirtyReason = context.RecordingDeferredReason;
                _commandBufferDirtyFlags![state.ImageIndex] = true;
                _lastEnsureCommandBufferRecordedPrimary = false;
                return false;
            }

            // Dirty secondaries can be replaced while the primary is built.
            // Publish the exact artifact generations the completed primary executes.
            if (!TryValidatePrimaryCommandBufferGroupSharedDependencies(
                    state.CommandChainSchedule,
                    state.CommandChainCache,
                    out CommandRecordingDependencyMismatch dependencyMismatch))
            {
                int invalidatedSecondaryCount =
                    InvalidatePrimaryCommandBufferGroupSharedDependencyMismatches(
                        state.CommandChainSchedule,
                        state.CommandChainCache);
                context.RecordingDeferredReason =
                    $"Recorded primary command buffer referenced {invalidatedSecondaryCount} " +
                    $"stale secondary artifact(s). Field={dependencyMismatch.Field} " +
                    $"Class={dependencyMismatch.InvalidationClass}.";
                state.Variant.Dirty = true;
                state.Variant.DirtyReason =
                    context.RecordingDeferredReason;
                _commandBufferDirtyFlags![state.ImageIndex] = true;
                _lastEnsureCommandBufferRecordedPrimary = false;
                return false;
            }

            state.CommandChainPrimaryIdentityComponents =
                ComputePrimaryCommandBufferGroupIdentity(
                    state.CommandChainSchedule,
                    state.CommandChainCache);
            state.CommandChainPrimaryGroupSignature =
                state.CommandChainPrimaryIdentityComponents.Combined;
            return true;
        }

        private bool TryValidateRecordedPrimaryImageEntryDependencies(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            if (!TryGetRecordedImageEntryStateMismatch(
                    state.Variant.PrimaryCommandBuffer,
                    out VulkanImageEntryStateMismatch mismatch,
                    includeIncompleteState: false))
            {
                return true;
            }

            context.RecordingDeferredReason =
                $"Recorded primary command buffer requires unavailable submitted image state. " +
                $"Kind={mismatch.Kind} Image=0x{mismatch.ImageHandle:X} " +
                $"Mip={mismatch.MipLevel} Layer={mismatch.ArrayLayer} " +
                $"Aspect={mismatch.Aspect} Expected={mismatch.Expected}.";
            state.Variant.Dirty = true;
            state.Variant.DirtyReason = context.RecordingDeferredReason;
            _commandBufferDirtyFlags![state.ImageIndex] = true;
            _lastEnsureCommandBufferRecordedPrimary = false;
            RecordPrimaryImageEntryStateMismatch(mismatch);
            return false;
        }

        private CommandBuffer PublishRecordedCommandBufferVariant(
            scoped ref VulkanCommandSchedulingContext<PrimaryCommandArtifactOwner> context,
            scoped ref CommandBufferLifecycleState state)
        {
            PrimaryCommandArtifactOwner variant = state.Variant;
            _commandBufferDirtyFlags![state.ImageIndex] = false;
            variant.Dirty = false;
            variant.DirtyReason = null;
            variant.FrameOpsSignature =
                state.FrameOperationsSignature;
            variant.DynamicUiSignature =
                state.RecordedDynamicUiSecondaryReady &&
                !state.DelayDynamicUiOverlayRecording
                    ? state.DynamicUiSignature
                    : 0;
            variant.DynamicUiOpCount =
                state.RecordedDynamicUiSecondaryReady
                    ? state.DynamicUiOperations.Length
                    : 0;
            variant.PreserveSwapchainForOverlay =
                state.PreserveSwapchainForOverlay;
            variant.RecordedFrameOpContextFingerprint =
                state.FrameOpContextFingerprint;
            variant.RecordedFrameOpContextId =
                state.FrameOpContextId;
            variant.RecordedResourceGeneration =
                state.FallbackContext.ResourceGeneration;
            variant.RecordedDescriptorGeneration =
                state.FallbackContext.DescriptorGeneration;
            variant.RecordedGenerations =
                state.CurrentGenerations;
            variant.RecordedDependencySignature =
                state.CurrentDependencySignature;
            variant.RecordedSwapchainImageEverPresented =
                state.SwapchainImageEverPresentedAtRecord;
            variant.RecordedSwapchainFinalLayout =
                context.SwapchainLayoutAfterCommandBuffer;
            variant.RecordedSwapchainWriteCount =
                state.RecordedSwapchainWriteCount;
            variant.RecordedSwapchainRefreshFromLastPresentSource =
                state.RequiresTrackedPresentSourceRefresh &&
                state.RecordedSwapchainWriteCount > 0;
            variant.RecordedImageLayoutStartSignature =
                state.ImageLayoutStartSignature;
            CaptureCommandBufferVariantImageLayoutEndState(variant);
            variant.CommandChainScheduleSignature =
                state.CommandChainSchedule?.StructuralSignature ??
                ulong.MaxValue;
            variant.CommandChainPrimaryGroupSignature =
                state.CommandChainSchedule is null ||
                state.CommandChainCache is null
                    ? ulong.MaxValue
                    : state.CommandChainPrimaryGroupSignature;
            variant.CommandChainPrimaryIdentityComponents =
                state.CommandChainPrimaryIdentityComponents;
            variant.RecordedSecondaryArtifactSequence.CopyFrom(
                _commandBufferRecordingScratch.Value!
                    .ExecutedCommandChainSecondaryArtifactSequence);
            variant.CommandChainPrimarySkeletonSignature =
                state.CommandChainPrimarySkeletonSignature;
            variant.CommandChainPrimaryGroupCount =
                state.CommandChainSchedule is null
                    ? -1
                    : state.CommandChainPrimaryGroupCount;
            variant.PlannerRevision = state.PlannerRevision;
            variant.GpuProfilerActive =
                state.GpuPipelineProfilingActive;
            variant.GpuProfilerFrameSlot =
                state.GpuPipelineProfilingActive
                    ? state.CommandBufferImageSlot
                    : -1;
            CaptureVulkanGpuProfilerVariantScopes(
                state.CommandBufferImageSlot,
                variant);
            variant.LastUsedFrameId = VulkanFrameCounter;
            StoreFrameOpSignatureDebugParts(
                variant,
                state.FrameOperations);
            SetActivePrimaryCommandArtifactOwner(state.ImageIndex, variant);
            UpdateVulkanGpuProfilerCommandBufferState(
                state.ImageIndex,
                state.GpuPipelineProfilingActive,
                state.CommandBufferImageSlot);

            if (state.HasTextureUploadFrameOperations)
            {
                MarkPrimaryCommandArtifactOwnerTransient(
                    variant,
                    "transient texture upload");
            }

            if (state.QueryFrameOperationsRequireRerecord)
            {
                MarkPrimaryCommandArtifactOwnerTransient(
                    variant,
                    "query draw was not recorded");
            }

            PublishDynamicUiSchedulingOutputs(
                ref context,
                ref state,
                variant,
                state.RecordedDynamicUiSecondaryReady);
            context.CommandBufferDirtyGenerationAfterRecord =
                SnapshotCommandBufferDirtyGeneration();
            if (HaveCommandBuffersDirtiedSince(
                    state.EnsureStartDirtyGeneration))
            {
                MarkPrimaryCommandArtifactOwnerDirtyAfterConcurrentInvalidation(
                    variant);
            }

            EnsureCommandBufferVariantContextBeforeSubmit(
                state.ImageIndex,
                variant,
                state.FrameOpContextFingerprint,
                state.FrameOpContextId,
                state.UsingCommandChains
                    ? "recorded-primary-command-chain"
                    : "recorded-primary");
            return variant.PrimaryCommandBuffer;
        }
    }
}

