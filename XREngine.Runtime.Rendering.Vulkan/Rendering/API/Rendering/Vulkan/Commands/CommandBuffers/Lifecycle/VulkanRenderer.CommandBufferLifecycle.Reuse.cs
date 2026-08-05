using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool TryReuseLastSwapchainWriter(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state,
            out CommandBuffer commandBuffer)
        {
            commandBuffer = default;
            if (state.RequiresTrackedPresentSourceRefresh ||
                state.HasStaticFrameOperations ||
                state.HasDynamicUiOperations ||
                state.PreserveSwapchainForOverlay ||
                state.ImageForcedDirty)
            {
                return false;
            }

            if (!TryReuseLastSwapchainWriterVariant(
                    state.ImageIndex,
                    state.FrameOpContextFingerprint,
                    state.FrameOpContextId,
                    state.PlannerRevision,
                    state.ImageLayoutStartSignature,
                    state.SwapchainImageEverPresentedAtRecord,
                    state.GpuPipelineProfilingActive,
                    state.CommandBufferImageSlot,
                    out commandBuffer,
                    out context.SwapchainLayoutAfterCommandBuffer))
            {
                return false;
            }

            context.CommandBufferDirtyGenerationAfterRecord =
                SnapshotCommandBufferDirtyGeneration();
            return true;
        }

        private bool TryReusePreparedCommandChain(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state,
            out CommandBuffer commandBuffer)
        {
            commandBuffer = default;
            bool hasMutableGpuDrivenFrameOperations =
                state.HasStaticFrameOperations &&
                HasMutableGpuDrivenFrameOps(state.FrameOperations);
            bool hasMutableFrameSourceBindings =
                state.HasStaticFrameOperations &&
                HasMutableFrameSourceSamplerBindings(state.FrameOperations);
            if (!VulkanPrimaryCommandBufferReuseEnabled ||
                hasMutableGpuDrivenFrameOperations ||
                hasMutableFrameSourceBindings ||
                state.ImageForcedDirty ||
                state.GpuPipelineProfilingActive)
            {
                return false;
            }

            if (!TryReuseCleanCommandChainPrimaryVariant(
                    state.ImageIndex,
                    state.FrameOperationsSignature,
                    state.FrameOpContextFingerprint,
                    state.FrameOpContextId,
                    state.DynamicUiSignature,
                    state.DynamicUiOperations.Length,
                    state.PlannerRevision,
                    state.ImageLayoutStartSignature,
                    state.GpuPipelineProfilingActive,
                    state.CommandBufferImageSlot,
                    state.CurrentGenerations,
                    state.CurrentDependencySignature,
                    state.FrameOperations,
                    state.DynamicUiOperations,
                    state.DelayDynamicUiOverlayRecording,
                    state.PreserveSwapchainForOverlay,
                    state.RequiresTrackedPresentSourceRefresh,
                    state.SwapchainImageEverPresentedAtRecord,
                    out commandBuffer,
                    out context.DynamicUiSecondaryCommandBuffer,
                    out context.DynamicUiOverlayOperationCount,
                    out CommandBufferCacheVariant? dynamicUiOverlayVariant,
                    out context.SwapchainLayoutAfterCommandBuffer,
                    out state.PreparedCommandChainFastScheduleSignature,
                    out state.HasPreparedCommandChainFastScheduleSignature))
            {
                return false;
            }

            if (state.DelayDynamicUiOverlayRecording)
            {
                context.DynamicUiOverlayOperations =
                    state.DynamicUiOperations;
                context.DynamicUiOverlaySignature =
                    state.DynamicUiSignature;
                context.DynamicUiOverlayVariant =
                    dynamicUiOverlayVariant;
            }

            context.CommandBufferDirtyGenerationAfterRecord =
                SnapshotCommandBufferDirtyGeneration();
            return true;
        }

        /// <summary>
        /// Mutable frame-source publications can select a different descriptor
        /// command-chain variant after startup. They must pass through the common
        /// schedule/variant dirty evaluation before a thin primary is reused; the
        /// early clean-reuse shortcut publishes variant metadata and returns before
        /// that validation phase.
        /// </summary>
        private static bool HasMutableFrameSourceSamplerBindings(FrameOp[] ops)
        {
            for (int index = 0; index < ops.Length; index++)
                if (ops[index] is MeshDrawOp meshDraw &&
                    meshDraw.Draw.ProgramBindingSnapshot is
                        { HasMutableFrameSourceSamplerBindings: true })
                {
                    return true;
                }

            return false;
        }

        private void BuildCommandBufferCommandChainSchedule(
            scoped ref CommandBufferLifecycleState state)
        {
            CommandChainLoweringStats loweringStats = default;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordCommandBuffer.CommandChainLowering"))
            {
                using VulkanCpuStageScope cpuStage =
                    new(EVulkanCpuStage.PacketConstruction);
                FrameOp[] scheduledDynamicUiOperations =
                    state.PreserveSwapchainForOverlay
                        ? Array.Empty<FrameOp>()
                        : state.DynamicUiOperations;
                ulong scheduledDynamicUiSignature =
                    state.PreserveSwapchainForOverlay
                        ? 0
                        : state.DynamicUiSignature;

                state.CommandChainSchedule = TryBuildCommandChainSchedule(
                    state.ImageIndex,
                    state.FrameOperations,
                    scheduledDynamicUiOperations,
                    state.FrameOperationsSignature,
                    scheduledDynamicUiSignature,
                    state.PlannerRevision,
                    allowExternalSwapchainTarget: false,
                    out loweringStats,
                    state.HasPreparedCommandChainFastScheduleSignature
                        ? state.PreparedCommandChainFastScheduleSignature
                        : null);
            }

            if (state.CommandChainSchedule is not null)
            {
                RuntimeEngine.Rendering.Stats.Vulkan
                    .RecordVulkanCommandChainMetrics(
                        chainsScheduled: loweringStats.ChainsScheduled,
                        chainsRecorded: loweringStats.ChainsRecorded,
                        chainsReused: loweringStats.ChainsReused,
                        chainsFrameDataRefreshed:
                            loweringStats.ChainsFrameDataRefreshed,
                        volatileChainsRecorded:
                            loweringStats.VolatileChainsRecorded,
                        visibilityPackets: loweringStats.VisibilityPackets,
                        renderPackets: loweringStats.RenderPackets,
                        secondaryCommandBuffers:
                            loweringStats.SecondaryCommandBuffers,
                        firstStructuralDirtyReason:
                            loweringStats.FirstStructuralDirtyReason,
                        firstDescriptorGenerationMismatch:
                            loweringStats.FirstDescriptorGenerationMismatch,
                        firstResourcePlanRevisionMismatch:
                            loweringStats.FirstResourcePlanRevisionMismatch);
            }

            state.CommandChainCache =
                state.CommandChainSchedule is null
                    ? null
                    : GetCommandChainCache(state.ImageIndex);
            state.CommandChainPrimaryIdentityComponents =
                state.CommandChainSchedule is null ||
                state.CommandChainCache is null
                    ? default
                    : ComputePrimaryCommandBufferGroupIdentity(
                        state.CommandChainSchedule,
                        state.CommandChainCache);
            state.CommandChainPrimaryGroupSignature =
                state.CommandChainSchedule is null
                    ? 0
                    : state.CommandChainPrimaryIdentityComponents.Combined;
            state.CommandChainPrimaryGroupCount =
                state.CommandChainSchedule?.Groups.Length ?? 0;
            state.AllPreparedDrawBindingsUseSecondaryBuffers =
                state.CommandChainSchedule is not null &&
                AreAllPreparedDrawBindingsSecondaryOwned(
                    state.CommandChainSchedule,
                    state.FrameOperations);
            if (state.CommandChainSchedule is not null)
            {
                state.CurrentDependencySignature =
                    CaptureCommandChainPrimaryPreparedBindingDependencies(
                        state.CurrentDependencySignature,
                        state.FrameOperations);
            }
            state.CommandChainPrimarySkeletonSignature =
                state.CommandChainSchedule is null
                    ? ulong.MaxValue
                    : ComputeCommandChainPrimarySkeletonSignature(
                        state.FrameOperations);
        }

        private void SelectCommandBufferVariant(
            scoped ref CommandBufferLifecycleState state)
        {
            state.Variant = GetOrCreateCommandBufferVariant(
                state.ImageIndex,
                state.FrameOperationsSignature,
                state.DynamicUiSignature,
                state.DynamicUiOperations.Length,
                state.CommandChainSchedule,
                state.CommandChainPrimaryGroupSignature,
                state.CommandChainPrimaryGroupCount,
                state.PreserveSwapchainForOverlay,
                state.CurrentDependencySignature,
                state.FrameOperations);
            if (state.ImageForcedDirty)
            {
                MarkCommandBufferVariantsDirty(
                    state.ImageIndex,
                    "image-forced-dirty");
            }

            state.ForcedVariantDirtyReason = state.Variant.DirtyReason;
            state.Dirty = state.ImageForcedDirty || state.Variant.Dirty;
            state.ForcedDirty = state.Dirty;
            state.HasTextureUploadFrameOperations =
                state.HasStaticFrameOperations &&
                HasTextureUploadFrameOps(state.FrameOperations);

            // GPU-written indirect argument/count contents are data publication,
            // not command topology. Immutable signatures already key the buffers,
            // offsets, capacities, pipelines, descriptors, targets, and frame slot.
            state.DependencyMismatch = state.UsingCommandChains
                ? state.Variant.RecordedDependencySignature
                    .CompareCommandChainPrimary(
                        state.CurrentDependencySignature)
                : state.Variant.RecordedDependencySignature.Compare(
                    state.CurrentDependencySignature);
        }

        private void EvaluateCommandBufferVariantDirtyState(
            scoped ref CommandBufferLifecycleState state)
        {
            CommandBufferCacheVariant variant = state.Variant;
            bool frameOperationsRequireFreshPrimary =
                _commandScheduler.RequiresFreshPrimary(
                    state.HasStaticFrameOperations,
                    VulkanPrimaryCommandBufferReuseEnabled);
            bool scheduleRequiresFreshPrimary =
                state.CommandChainSchedule?.RequiresFreshPrimary == true;
            bool mutableSwapchainFrameSourceRequiresFreshPrimary =
                state.UsingCommandChains &&
                HasMutableFrameSourceSamplerBindings(state.FrameOperations);

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordCommandBuffer.DirtyEvaluation"))
            {
                if (!state.Dirty && scheduleRequiresFreshPrimary)
                {
                    state.Dirty = true;
                    state.PrimaryFrameStateDirty = true;
                    state.PrimaryFrameStateDirtyReason =
                        "command-chain-inline-publication";
                }

                // The desktop compositor samples a mutable frame source while
                // targeting an acquired swapchain image. Secondary scene and
                // shadow command buffers remain reusable, but the thin primary
                // also owns the acquired-image render scope and present-cycle
                // transitions. Re-record it until that native present state is
                // represented by a complete reusable-primary identity; reusing
                // the current logical identity can leave the diagnostic clear
                // visible even though the final source texture is valid.
                if (!state.Dirty &&
                    mutableSwapchainFrameSourceRequiresFreshPrimary)
                {
                    state.Dirty = true;
                    state.PrimaryFrameStateDirty = true;
                    state.PrimaryFrameStateDirtyReason =
                        "mutable-swapchain-frame-source";
                }

                if (!state.Dirty && frameOperationsRequireFreshPrimary)
                {
                    state.Dirty = true;
                    state.PrimaryFrameStateDirty = true;
                    state.PrimaryFrameStateDirtyReason = "reuse-disabled";
                }

                if (state.GpuProfilerCommandBufferStateDirty)
                    state.ProfilerDirty = true;

                if (!state.Dirty &&
                    state.DependencyMismatch.RequiresRecording)
                {
                    state.Dirty = true;
                    if (state.DependencyMismatch.InvalidationClass ==
                        CommandRecordingInvalidationClass.Structural)
                    {
                        state.FrameOpSignatureDirty = true;
                    }
                    else
                    {
                        state.PlannerDirty = true;
                    }
                }

                if (!state.Dirty &&
                    _commandScheduler.HasOperationSignatureChanged(
                        state.HasFrameOperations,
                        variant.FrameOpsSignature,
                        state.FrameOperationsSignature))
                {
                    LogFrameOpSignatureDiff(
                        state.ImageIndex,
                        variant,
                        state.FrameOperationsSignature,
                        state.FrameOperations);
                    state.Dirty = true;
                    state.FrameOpSignatureDirty = true;
                }

                if (!state.Dirty &&
                    _commandScheduler.HasPlannerGenerationChanged(
                        state.UsingCommandChains,
                        variant.PlannerRevision,
                        state.PlannerRevision))
                {
                    state.Dirty = true;
                    state.PlannerDirty = true;
                }

                // An inline desktop primary owns the swapchain writer and must be
                // re-recorded for output-camera transitions. A mixed command-chain
                // primary can refresh inline and secondary per-draw camera data in
                // place; its exact inline structure remains separately validated.
                if (!state.Dirty &&
                    _commandScheduler.HasCameraGenerationChanged(
                        state.UsingCommandChains,
                        variant.RecordedGenerations.CameraPose,
                        state.CurrentGenerations.CameraPose))
                {
                    state.Dirty = true;
                    state.FrameDataDirty = true;
                    _lastReusableFrameDataRefreshFailureReason =
                        "inline primary camera pose changed";
                }

                if (!state.Dirty &&
                    IsCommandBufferVariantImageLayoutStateDirty(
                        variant,
                        state.ImageLayoutStartSignature,
                        out state.PrimaryImageEntryStateMismatch))
                {
                    state.Dirty = true;
                    state.PrimaryFrameStateDirty = true;
                    state.PrimaryFrameStateDirtyReason =
                        variant.RecordedImageLayoutEndState is null
                            ? "missing-layout-state"
                            : "image-layout-entry-state";
                    RecordPrimaryImageEntryStateMismatch(
                        state.PrimaryImageEntryStateMismatch);
                }

                if (!state.Dirty &&
                    _commandScheduler.HasSwapchainLifecycleChanged(
                        variant.RecordedSwapchainImageEverPresented,
                        state.SwapchainImageEverPresentedAtRecord,
                        state.RequiresTrackedPresentSourceRefresh,
                        variant.RecordedSwapchainRefreshFromLastPresentSource))
                {
                    state.Dirty = true;
                    state.SwapchainLifecycleDirty = true;
                }

                if (!state.Dirty &&
                    !state.UsingCommandChains &&
                    IsCommandBufferVariantGpuProfilerStateDirty(
                        variant,
                        state.GpuPipelineProfilingActive,
                        state.CommandBufferImageSlot))
                {
                    state.Dirty = true;
                    state.ProfilerDirty = true;
                }

                if (!state.Dirty &&
                    !state.DelayDynamicUiOverlayRecording &&
                    IsDynamicUiBatchTextSecondaryDirty(
                        variant,
                        state.DynamicUiSignature))
                {
                    state.Dirty = true;
                    state.DynamicUiDirty = true;
                }

                if (!state.Dirty &&
                    state.UsingCommandChains &&
                    IsDynamicUiBatchTextPrimaryStructureDirty(
                        variant,
                        state.DynamicUiOperations.Length))
                {
                    state.Dirty = true;
                    state.DynamicUiDirty = true;
                }

                if (!state.Dirty &&
                    state.CommandChainSchedule is not null)
                {
                    state.CommandChainPrimaryDirtyReason =
                        EvaluatePrimaryCommandBufferDirtyReason(
                            state.CommandChainSchedule,
                            variant.CommandChainScheduleSignature,
                            variant.CommandChainPrimaryGroupSignature,
                            variant.CommandChainPrimaryGroupCount,
                            state.CommandChainPrimaryGroupSignature,
                            variant.GpuProfilerActive,
                            variant.GpuProfilerFrameSlot,
                            state.GpuPipelineProfilingActive,
                            state.CommandBufferImageSlot);

                    if (state.CommandChainCache is null ||
                        !variant.RecordedSecondaryArtifactSequence
                            .MatchesCurrentArtifacts(state.CommandChainCache))
                    {
                        state.CommandChainPrimaryDirtyReason |=
                            PrimaryCommandBufferDirtyReason.SecondaryArtifactSequence;
                    }

                    if (state.CommandChainPrimaryDirtyReason !=
                        PrimaryCommandBufferDirtyReason.None)
                    {
                        state.Dirty = true;
                        state.CommandChainPrimaryDirty = true;
                    }
                }
            }
        }

        private bool TryRefreshReusableCommandBufferVariant(
            scoped ref CommandBufferLifecycleState state)
        {
            bool refreshedReusableFrameData = true;
            state.DynamicUiFrameDataNeedsRerecord = false;
            _lastReusableFrameDataRefreshFailureReason = null;
            ReadOnlySpan<CommandChainKey> scheduledCommandChainKeys =
                state.CommandChainSchedule is not null &&
                state.CommandChainCache is not null
                    ? PrepareReusableCommandChainKeysByOpIndex(
                        state.CommandChainSchedule,
                        state.CommandChainCache,
                        state.FrameOperations.Length,
                        state.Scratch)
                    : ReadOnlySpan<CommandChainKey>.Empty;
            using (VulkanCpuStageScope cpuStage =
                   new(EVulkanCpuStage.FrameDataRefresh))
            {
                refreshedReusableFrameData =
                    !state.HasStaticFrameOperations ||
                    TryRefreshReusableCommandBufferFrameData(
                        state.ImageIndex,
                        state.Scratch
                            .PrimaryReusableFrameDataRefreshRequests,
                        state.Scratch
                            .PrimaryReusableFrameDataOwnerWorkRequests,
                        state.Scratch
                            .PrimaryReusableFrameDataRefreshBatchInfo,
                        state.Variant.PrimaryFrameDataRefreshState,
                        dynamicUi: false,
                        descriptorResourcesCapturedByFrameSignature:
                            state.UsingCommandChains &&
                            state.AllPreparedDrawBindingsUseSecondaryBuffers,
                        commandChainCache: state.CommandChainCache,
                        scheduledCommandChainKeys:
                            scheduledCommandChainKeys);

                if (refreshedReusableFrameData &&
                    state.HasDynamicUiOperations)
                {
                    state.DynamicUiFrameDataNeedsRerecord =
                        !TryRefreshReusableCommandBufferFrameData(
                            state.ImageIndex,
                            state.Scratch
                                .DynamicUiReusableFrameDataRefreshRequests,
                            state.Scratch
                                .DynamicUiReusableFrameDataOwnerWorkRequests,
                            state.Scratch
                                .DynamicUiReusableFrameDataRefreshBatchInfo,
                            state.Variant.DynamicUiFrameDataRefreshState,
                            dynamicUi: true,
                            commandChainCache: null,
                            scheduledCommandChainKeys:
                                ReadOnlySpan<CommandChainKey>.Empty);

                    // The scene primary only executes the dynamic-text
                    // secondary's stable handle. If that secondary cannot be
                    // refreshed in place, rebuild the isolated secondary in the
                    // next lifecycle phase instead of invalidating the scene and
                    // shadow primary.
                    if (state.DynamicUiFrameDataNeedsRerecord)
                        _lastReusableFrameDataRefreshFailureReason = null;
                }
            }

            if (refreshedReusableFrameData)
                return true;

            state.Dirty = true;
            state.FrameDataDirty = true;
            return false;
        }

        private bool TryPrepareReusableCommandBufferQueries(
            scoped ref CommandBufferLifecycleState state)
        {
            if (!state.HasQueryFrameOperations)
                return true;

            CommandBuffer primaryCommandBuffer =
                state.Variant.PrimaryCommandBuffer;
            if (PrepareQueryFrameOpsForCommandBufferReuse(
                    primaryCommandBuffer,
                    state.FrameOperations) &&
                PrepareQueryFrameOpsForCommandBufferReuse(
                    primaryCommandBuffer,
                    state.DynamicUiOperations))
            {
                return true;
            }

            state.Dirty = true;
            state.PrimaryFrameStateDirty = true;
            state.PrimaryFrameStateDirtyReason = "query-pool-prepare";
            return false;
        }

        private bool TryPrepareReusableCommandBufferDynamicUi(
            scoped ref CommandBufferLifecycleState state)
        {
            state.DynamicUiSecondaryReady = true;
            if (!state.DelayDynamicUiOverlayRecording)
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.RecordCommandBuffer.RecordDynamicUiSecondary"))
                using (VulkanCpuStageScope cpuStage =
                       new(EVulkanCpuStage.SecondaryRecording))
                {
                    state.DynamicUiSecondaryReady =
                        RecordDynamicUiBatchTextSecondaryCommandBuffer(
                            state.ImageIndex,
                            state.Variant,
                            state.DynamicUiOperations,
                            state.DynamicUiSignature,
                            forceRecord:
                                state.DynamicUiFrameDataNeedsRerecord);
                }
            }
            else
            {
                state.Variant.DynamicUiSecondaryRecorded = false;
            }

            if (!state.HasDynamicUiOperations ||
                state.DelayDynamicUiOverlayRecording ||
                state.DynamicUiSecondaryReady)
            {
                return true;
            }

            state.Dirty = true;
            state.DynamicUiDirty = true;
            return false;
        }

        private CommandBuffer PublishReusedCommandBufferVariant(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state)
        {
            CommandBufferCacheVariant variant = state.Variant;
            StoreFrameOpSignatureDebugParts(
                variant,
                state.FrameOperations);
            if (state.CommandChainSchedule is not null)
            {
                variant.CommandChainScheduleSignature =
                    state.CommandChainSchedule.StructuralSignature;
                variant.CommandChainPrimaryGroupSignature =
                    state.CommandChainPrimaryGroupSignature;
                variant.CommandChainPrimaryIdentityComponents =
                    state.CommandChainPrimaryIdentityComponents;
                variant.CommandChainPrimarySkeletonSignature =
                    state.CommandChainPrimarySkeletonSignature;
                variant.CommandChainPrimaryGroupCount =
                    state.CommandChainPrimaryGroupCount;
            }

            variant.PreserveSwapchainForOverlay =
                state.PreserveSwapchainForOverlay;
            variant.RecordedGenerations = state.CurrentGenerations;
            variant.RecordedDependencySignature =
                state.CurrentDependencySignature;
            variant.RecordedFrameOpContextFingerprint =
                state.FrameOpContextFingerprint;
            variant.RecordedFrameOpContextId = state.FrameOpContextId;
            variant.LastUsedFrameId = VulkanFrameCounter;
            variant.DirtyReason = null;
            SetActiveCommandBufferVariant(state.ImageIndex, variant);
            RestoreRecordedImageLayoutEndState(variant);
            PrepareVulkanGpuProfilerReusableSubmission(
                state.CommandBufferImageSlot,
                variant,
                state.GpuPipelineProfilingActive);
            UpdateVulkanGpuProfilerCommandBufferState(
                state.ImageIndex,
                state.GpuPipelineProfilingActive,
                state.CommandBufferImageSlot);

            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanCommandBufferCacheOutcome(
                    reusedClean: true,
                    recorded: false,
                    forcedDirty: false,
                    frameOpSignatureDirty: false,
                    plannerDirty: false,
                    profilerDirty: false,
                    dirtyReason: null,
                    structuralSignature:
                        state.CurrentGenerations.Structural,
                    descriptorGeneration:
                        state.CurrentGenerations.Descriptor,
                    swapchainSlot: state.CommandBufferImageSlot);
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanCommandChainMetrics(
                    primaryCommandBuffersReused: 1);

            context.SwapchainLayoutAfterCommandBuffer =
                variant.RecordedSwapchainFinalLayout;
            PublishDynamicUiSchedulingOutputs(
                ref context,
                ref state,
                variant,
                state.DynamicUiSecondaryReady);
            EnsureCommandBufferVariantContextBeforeSubmit(
                state.ImageIndex,
                variant,
                state.FrameOpContextFingerprint,
                state.FrameOpContextId,
                state.UsingCommandChains
                    ? "primary-command-chain"
                    : "primary");
            PrepareSubmissionMarkersForCommandBufferReuse(
                variant.PrimaryCommandBuffer,
                state.FrameOperations,
                state.DynamicUiOperations);
            return variant.PrimaryCommandBuffer;
        }

        private static void PublishDynamicUiSchedulingOutputs(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state,
            CommandBufferCacheVariant variant,
            bool dynamicUiSecondaryReady)
        {
            if (!dynamicUiSecondaryReady)
                return;

            context.DynamicUiSecondaryCommandBuffer =
                variant.DynamicUiSecondaryCommandBuffer;
            context.DynamicUiOverlayOperationCount =
                state.DynamicUiOperations.Length;
            if (!state.DelayDynamicUiOverlayRecording)
                return;

            context.DynamicUiOverlayOperations =
                state.DynamicUiOperations;
            context.DynamicUiOverlaySignature =
                state.DynamicUiSignature;
            context.DynamicUiOverlayVariant = variant;
        }
    }
}

