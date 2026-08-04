using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private void ResetCommandBufferLifecycle(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context)
        {
            _lastEnsureCommandBufferRecordedPrimary = false;
            context.RecordingDeferredReason = string.Empty;
            context.DynamicUiSecondaryCommandBuffer = default;
            context.DynamicUiOverlayOperationCount = 0;
            context.DynamicUiOverlayOperations = Array.Empty<FrameOp>();
            context.DynamicUiOverlaySignature = 0;
            context.DynamicUiOverlayVariant = null;
            context.TextureUploadCommandBuffer = default;
            context.TextureUploadCommandPool = default;
            context.SwapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;
            context.CommandBufferDirtyGenerationAfterRecord =
                SnapshotCommandBufferDirtyGeneration();
        }

        private bool TryInitializeCommandBufferLifecycle(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            out CommandBufferLifecycleState state)
        {
            state = default;
            if (!IsDeviceOperational)
            {
                context.RecordingDeferredReason =
                    $"Vulkan device state is {DeviceState}";
                return false;
            }

            if (!TryEnsureCommandBuffersForSwapchain())
                throw new InvalidOperationException(
                    "Command buffers are unavailable because swapchain framebuffers are not initialised.");

            if (_commandBuffers is null)
                throw new InvalidOperationException(
                    "Command buffers have not been allocated yet.");

            uint imageIndex = context.ImageIndex;
            if (imageIndex >= _commandBuffers.Length)
                throw new InvalidOperationException(
                    $"Command buffer index {imageIndex} is out of range for {_commandBuffers.Length} allocated command buffers.");

            if (_commandBufferDirtyFlags is null ||
                imageIndex >= _commandBufferDirtyFlags.Length)
            {
                throw new InvalidOperationException(
                    "Command buffer dirty flags are not initialised correctly.");
            }

            if (_commandBufferFrameOpSignatures is null ||
                imageIndex >= _commandBufferFrameOpSignatures.Length)
            {
                throw new InvalidOperationException(
                    "Command buffer frame-op signatures are not initialised correctly.");
            }

            if (_commandBufferPlannerRevisions is null ||
                imageIndex >= _commandBufferPlannerRevisions.Length)
            {
                throw new InvalidOperationException(
                    "Command buffer planner revisions are not initialised correctly.");
            }

            if (_primaryCommandPlans is null ||
                imageIndex >= _primaryCommandPlans.Length)
            {
                throw new InvalidOperationException(
                    "Primary command plans are not initialised correctly.");
            }

            state = new CommandBufferLifecycleState(
                imageIndex,
                context.PreserveSwapchainForOverlay)
            {
                ImageForcedDirty = _commandBufferDirtyFlags[imageIndex],
                EnsureStartDirtyGeneration =
                    SnapshotCommandBufferDirtyGeneration(),
                CommandBufferImageSlot =
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                SwapchainImageEverPresentedAtRecord =
                    IsSwapchainImageEverPresented(imageIndex),
                GpuPipelineProfilingActive =
                    IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                    RenderPipelineGpuProfiler.Instance.IsProfilingActive,
                PrimaryCommandPlan = _primaryCommandPlans[imageIndex],
            };
            state.GpuProfilerCommandBufferStateDirty =
                IsVulkanGpuProfilerCommandBufferStateDirty(
                    imageIndex,
                    state.GpuPipelineProfilingActive,
                    state.CommandBufferImageSlot);

            if (state.GpuProfilerCommandBufferStateDirty)
            {
                ClearVulkanGpuProfilerPendingQueries();
                MarkCommandBufferVariantsDirty(
                    imageIndex,
                    "gpu-profiler-command-buffer-state");
            }

            return true;
        }

        private void PrepareCommandBufferFrameOperations(
            scoped ref CommandBufferLifecycleState state)
        {
            using (VulkanCpuStageScope cpuStage =
                   new(EVulkanCpuStage.FrameOpPreparation))
            {
                using (VulkanCpuStageScope drainStage =
                       new(EVulkanCpuStage.FrameOpDrain))
                {
                    state.FrameOperations = DrainFrameOpsExcludingTextureUploads(
                        out state.RawFrameOpsSignature,
                        computeSignature: FrameOpSignatureDiffDiagnosticsEnabled);
                    state.FrameOperations =
                        FilterDiagnosticSkippedFrameOps(state.FrameOperations);
                    VulkanSwapchainContextCoalescer.Coalesce(state.FrameOperations);
                }
            }

            state.HasFrameOperations = state.FrameOperations.Length > 0;
            state.FrameOperationsSignature = state.RawFrameOpsSignature;
            state.Scratch = _commandBufferRecordingScratch.Value!;

            if (!state.HasFrameOperations)
                return;

            using (VulkanCpuStageScope cpuStage =
                   new(EVulkanCpuStage.FrameOpPreparation))
            {
                using (VulkanCpuStageScope schedulingStage =
                       new(EVulkanCpuStage.FrameOpScheduling))
                {
                    using (VulkanCpuStageScope sortStage =
                           new(EVulkanCpuStage.FrameOpSort))
                    {
                        state.FrameOperations = _frameOperationScheduler
                            .SortFrameOpsCore(
                                state.FrameOperations,
                                CompiledRenderGraph);
                    }
                    using (VulkanCpuStageScope cohortStage =
                           new(EVulkanCpuStage.FrameOpCohort))
                    {
                        RecordVisibleMeshDrawCohort(
                            state.FrameOperations,
                            state.Scratch.VisibleMaterialIdentities);
                    }
                    FrameOp[] staticOperations;
                    using (VulkanCpuStageScope splitStage =
                           new(EVulkanCpuStage.FrameOpSplit))
                    {
                        SplitDynamicUiBatchTextFrameOps(
                            state.FrameOperations,
                            out staticOperations,
                            out state.DynamicUiOperations);
                    }
                    state.FrameOperations = staticOperations;
                    using (VulkanCpuStageScope signatureStage =
                           new(EVulkanCpuStage.FrameOpSignature))
                    {
                        NormalizePrimaryPlanPassIndices(state.FrameOperations);
                        state.FrameOperationsSignature =
                            ComputeFrameOpsSignature(state.FrameOperations);
                        state.DynamicUiSignature = state.HasDynamicUiOperations
                            ? ComputeFrameOpsSignature(state.DynamicUiOperations)
                            : 0;
                    }

                    using (VulkanCpuStageScope planStage =
                           new(EVulkanCpuStage.FrameOpPlan))
                    {
                        VulkanPrimaryCommandPlan primaryPlan =
                            state.PrimaryCommandPlan;
                        primaryPlan.Build(
                            state.FrameOperations,
                            state.FrameOperationsSignature,
                            new VulkanPrimaryPlanTerminalContext(
                                state.PreserveSwapchainForOverlay,
                                TransitionSwapchainToPresent: true,
                                ReleaseExternalImageOwnership: false),
                            BarrierPlanner);

                        FrameOpSignatureHasher primaryReuseIdentity = new();
                        primaryReuseIdentity.Add(
                            state.FrameOperationsSignature);
                        primaryReuseIdentity.Add(primaryPlan.Identity);
                        state.FrameOperationsSignature =
                            primaryReuseIdentity.ToHash();
                    }
                }
            }

            if (FrameOpSignatureDiffDiagnosticsEnabled &&
                state.RawFrameOpsSignature != state.FrameOperationsSignature)
            {
                Debug.VulkanEvery(
                    $"Vulkan.FrameOpSignature.Normalized.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Frame-op signature changed after recorder normalization: raw=0x{0:X16} normalized=0x{1:X16} ops={2}",
                    state.RawFrameOpsSignature,
                    state.FrameOperationsSignature,
                    state.FrameOperations.Length);
            }
        }

        private bool TryRegisterCommandBufferFrameDataManifest(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state)
        {
            bool registered;
            string failureReason;
            using (VulkanCpuStageScope cpuStage =
                   new(EVulkanCpuStage.FrameDataManifest))
            {
                registered = TryRegisterFrameWideMeshFrameDataRequirements(
                    state.FrameOperations,
                    state.DynamicUiOperations,
                    state.CommandBufferImageSlot,
                    sealAfterRegister: true,
                    state.Scratch.MeshDrawSlotsByRenderer,
                    state.Scratch,
                    state.Scratch.ReusableMeshFrameDataFamilyBases,
                    out _,
                    out failureReason);
            }

            return registered ||
                DeferCommandBufferLifecycle(
                    ref context,
                    ref state,
                    failureReason);
        }

        private CommandBuffer SchedulePreparedCommandBufferLifecycle(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state)
        {
            FrameOp[] plannerPreparationOperations =
                state.HasStaticFrameOperations
                    ? state.FrameOperations
                    : state.DynamicUiOperations;
            using FrameOpResourcePlannerPreparationScope plannerPreparationScope =
                new(this, plannerPreparationOperations);

            ClassifyPreparedCommandBufferFrameOperations(ref state);
            if (!TryPrepareCommandBufferResources(
                    ref context,
                    ref state,
                    in plannerPreparationScope))
            {
                return default;
            }

            CaptureCommandBufferDependencies(ref state);
            RecordCommandBufferTextureUploads(ref context, ref state);
            CaptureCommandBufferReuseInputs(ref state);

            if (TryReuseLastSwapchainWriter(
                    ref context,
                    ref state,
                    out CommandBuffer reusedCommandBuffer))
            {
                return reusedCommandBuffer;
            }

            if (TryReusePreparedCommandChain(
                    ref context,
                    ref state,
                    out reusedCommandBuffer))
            {
                return reusedCommandBuffer;
            }

            BuildCommandBufferCommandChainSchedule(ref state);
            SelectCommandBufferVariant(ref state);
            EvaluateCommandBufferVariantDirtyState(ref state);

            if (!state.Dirty &&
                TryRefreshReusableCommandBufferVariant(ref state) &&
                TryPrepareReusableCommandBufferQueries(ref state) &&
                TryPrepareReusableCommandBufferDynamicUi(ref state))
            {
                return PublishReusedCommandBufferVariant(
                    ref context,
                    ref state);
            }

            if (!TryPrepareCommandBufferVariantForRecording(
                    ref context,
                    ref state))
            {
                return default;
            }

            RecordCommandBufferCacheMiss(ref state);
            if (!TryRecordCommandBufferVariant(ref context, ref state))
                return default;

            return PublishRecordedCommandBufferVariant(
                ref context,
                ref state);
        }

        private void ClassifyPreparedCommandBufferFrameOperations(
            scoped ref CommandBufferLifecycleState state)
        {
            state.HasQueryFrameOperations =
                HasQueryFrameOps(state.FrameOperations) ||
                HasQueryFrameOps(state.DynamicUiOperations);
            state.RequiresTrackedPresentSourceRefresh =
                !state.HasStaticFrameOperations &&
                HasLastWindowPresentSourceForSwapchainRefresh();
        }

        private bool TryPrepareCommandBufferResources(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state,
            in FrameOpResourcePlannerPreparationScope plannerPreparationScope)
        {
            FrameOp[] plannerOperations = state.HasStaticFrameOperations
                ? state.FrameOperations
                : state.DynamicUiOperations;
            ulong plannerFrameOperationsSignature =
                state.HasStaticFrameOperations
                    ? state.FrameOperationsSignature
                    : state.DynamicUiSignature;

            using VulkanCpuStageScope cpuStage =
                new(EVulkanCpuStage.ResourcePlanning);
            bool hasPlannerOperations = plannerOperations.Length > 0;
            if (hasPlannerOperations &&
                TryDescribeRecentResourceAllocationFailure(
                    out string prePlanFailureReason))
            {
                return DeferCommandBufferLifecycle(
                    ref context,
                    ref state,
                    prePlanFailureReason);
            }

            if (hasPlannerOperations &&
                TryReusePreparedFrameOpResourcePlannerStates(
                    plannerFrameOperationsSignature,
                    out state.PlannerRevision))
            {
                // Planner allocations are immutable for an exact plan hit, but the
                // shared Vulkan wrappers are not. External readback scopes may have
                // rebound them to another cached allocator since this plan was last
                // used, so restore their descriptors and attachment views before
                // recording against the reused allocation plan.
                FrameOpContext plannerContext =
                    ActiveLastActiveFrameOpContext ?? SelectPrimaryPlannerContext(plannerOperations);
                string refreshReason = state.HasStaticFrameOperations
                    ? "Vulkan command-chain prepared-plan wrapper refresh"
                    : "Vulkan command-chain dynamic UI prepared-plan wrapper refresh";
                if (!TryRefreshFrameOpResourceWrappers(
                        plannerOperations,
                        plannerContext,
                        refreshReason,
                        AllowSynchronousResourceUploads,
                        out string refreshFailureReason))
                {
                    return DeferCommandBufferLifecycle(
                        ref context,
                        ref state,
                        refreshFailureReason);
                }

                return true;
            }

            if (state.HasStaticFrameOperations)
            {
                FrameOpContext plannerContext =
                    PrepareResourcePlannerForFrameOps(
                        state.FrameOperations,
                        state.FrameOperationsSignature);
                if (TryDescribeRecentResourceAllocationFailure(
                        out string postPlanFailureReason))
                {
                    return DeferCommandBufferLifecycle(
                        ref context,
                        ref state,
                        postPlanFailureReason);
                }

                if (!TryRefreshFrameOpResourceWrappers(
                        state.FrameOperations,
                        plannerContext,
                        "Vulkan command-chain resource planner refresh",
                        AllowSynchronousResourceUploads,
                        out string refreshFailureReason))
                {
                    return DeferCommandBufferLifecycle(
                        ref context,
                        ref state,
                        refreshFailureReason);
                }
            }
            else if (state.HasDynamicUiOperations)
            {
                FrameOpContext plannerContext =
                    PrepareResourcePlannerForFrameOps(
                        state.DynamicUiOperations,
                        state.DynamicUiSignature);
                if (TryDescribeRecentResourceAllocationFailure(
                        out string postDynamicPlanFailureReason))
                {
                    return DeferCommandBufferLifecycle(
                        ref context,
                        ref state,
                        postDynamicPlanFailureReason);
                }

                if (!TryRefreshFrameOpResourceWrappers(
                        state.DynamicUiOperations,
                        plannerContext,
                        "Vulkan command-chain dynamic UI resource planner refresh",
                        AllowSynchronousResourceUploads,
                        out string refreshFailureReason))
                {
                    return DeferCommandBufferLifecycle(
                        ref context,
                        ref state,
                        refreshFailureReason);
                }
            }

            plannerPreparationScope.PublishCurrentState();
            state.PlannerRevision = state.HasStaticFrameOperations
                ? PrepareFrameOpResourcePlannerStatesForFrameOps(
                    state.FrameOperations,
                    state.FrameOperationsSignature)
                : state.HasDynamicUiOperations
                    ? PrepareFrameOpResourcePlannerStatesForFrameOps(
                        state.DynamicUiOperations,
                        state.DynamicUiSignature)
                    : ResourcePlannerRevision;

            if (TryDescribeRecentResourceAllocationFailure(
                    out string plannerFailureReason))
            {
                return DeferCommandBufferLifecycle(
                    ref context,
                    ref state,
                    plannerFailureReason);
            }

            if (hasPlannerOperations)
            {
                RememberPreparedFrameOpResourcePlannerStates(
                    plannerFrameOperationsSignature,
                    state.PlannerRevision);
            }

            return true;
        }

        private void CaptureCommandBufferDependencies(
            scoped ref CommandBufferLifecycleState state)
        {
            using VulkanCpuStageScope cpuStage =
                new(EVulkanCpuStage.DependencySnapshot);
            state.FallbackContext = state.HasStaticFrameOperations
                ? state.FrameOperations[0].Context
                : state.HasDynamicUiOperations
                    ? state.DynamicUiOperations[0].Context
                    : CaptureFrameOpContext();
            state.FrameOpContextFingerprint =
                ComputeCommandBufferFrameOpContextFingerprint(
                    state.FrameOperations,
                    state.DynamicUiOperations,
                    state.FallbackContext);
            state.FrameOpContextId = ResolveCommandBufferFrameOpContextId(
                state.FrameOperations,
                state.DynamicUiOperations,
                state.FallbackContext);
            state.CurrentGenerations = CaptureCommandBufferGenerationDomains(
                state.ImageIndex,
                state.FrameOperationsSignature,
                state.FrameOperations,
                state.DynamicUiOperations,
                state.DynamicUiSignature,
                state.FallbackContext,
                state.FrameOpContextFingerprint,
                state.GpuPipelineProfilingActive,
                state.CommandBufferImageSlot);

            // Take the synchronized cache-publication generation once for this
            // prepared frame. Every primary/range signature consumes this snapshot.
            ulong sharedGraphicsPipelineGeneration =
                SharedGraphicsPipelineGeneration;
            state.CurrentDependencySignature =
                CaptureCommandRecordingDependencySignature(
                    state.ImageIndex,
                    state.CommandBufferImageSlot,
                    state.PlannerRevision,
                    state.DynamicUiSignature,
                    state.FallbackContext,
                    state.CurrentGenerations,
                    state.FrameOperations,
                    sharedGraphicsPipelineGeneration);

            System.Diagnostics.Debug.Assert(
                !state.HasStaticFrameOperations ||
                state.PrimaryCommandPlan
                    .HasEquivalentEmissionAndDependencies(
                        state.CurrentDependencySignature),
                "Typed primary-plan command/dependency emission no longer matches the direct recorder.");
        }

        private void RecordCommandBufferTextureUploads(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state)
        {
            BeginRecordedTextureUploadSubmitBatch();
            FrameOp[] textureUploadOperations = DrainTextureUploadFrameOps();
            if (textureUploadOperations.Length == 0)
                return;

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.RecordCommandBuffer.RecordTextureUploads"))
            {
                if (TryRecordTextureUploadCommandBuffer(
                        state.ImageIndex,
                        textureUploadOperations,
                        out context.TextureUploadCommandBuffer,
                        out context.TextureUploadCommandPool))
                {
                    return;
                }

                context.TextureUploadCommandBuffer = default;
                context.TextureUploadCommandPool = default;
            }
        }

        private void CaptureCommandBufferReuseInputs(
            scoped ref CommandBufferLifecycleState state)
        {
            if (!state.ImageForcedDirty &&
                HaveCommandBuffersDirtiedSince(
                    state.EnsureStartDirtyGeneration))
            {
                state.ImageForcedDirty = true;
            }

            using VulkanCpuStageScope cpuStage =
                new(EVulkanCpuStage.ImageLayoutSnapshot);
            state.ImageLayoutStartSignature =
                ComputeImageLayoutStateSignature();
        }

        private bool DeferCommandBufferLifecycle(
            scoped ref VulkanCommandSchedulingContext<CommandBufferCacheVariant> context,
            scoped ref CommandBufferLifecycleState state,
            string reason)
        {
            context.RecordingDeferredReason = reason;
            FailUnsubmittedSubmissionMarkers(
                state.FrameOperations,
                state.DynamicUiOperations);
            return false;
        }
    }
}

