using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>
    /// Attempts exact desktop primary reuse from current producer payloads before
    /// a new frame plan is lowered. The recorded source-order permutation is the
    /// authority that keeps dynamic UBO slot ordinals identical to the cached
    /// secondary command buffers.
    /// </summary>
    internal bool TryReusePreparedPrimary(
        in VulkanPreparedPrimaryReuseInput input,
        out VulkanPrimaryCommandRecordingResult result)
    {
        result = default;
        VulkanPreparedPrimaryAuthority authority = input.Authority;
        if (input.PrimaryCommandBuffer.Handle == 0 ||
            !authority.RecordingTarget.IsValid ||
            input.RenderFrameId == 0UL ||
            authority.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan.Revision !=
                authority.ResourcePlanStamp.ResourcePlannerRevision ||
            authority.Policy.FreshSerialRecording ||
            !VulkanPrimaryCommandBufferReuseEnabled ||
            !CommandChainsEnabledForCurrentRecording ||
            input.CommandChainSchedule.RequiresFreshPrimary)
        {
            return false;
        }

        CommandChainScheduleCacheIdentity scheduleIdentity =
            input.CommandChainSchedule.CacheIdentity;
        int scheduledDynamicOperationCount =
            authority.Policy.PreserveSwapchainForOverlay
                ? 0
                : input.DynamicUiOperations.Length;
        ulong scheduledDynamicOperationSignature =
            authority.Policy.PreserveSwapchainForOverlay
                ? 0UL
                : input.DynamicUiOperationSignature;
        if (scheduleIdentity.StaticOperationCount !=
                input.StaticOperations.Length ||
            scheduleIdentity.DynamicOperationCount !=
                scheduledDynamicOperationCount ||
            scheduleIdentity.StaticOperationSignature !=
                input.StaticOperationSignature ||
            scheduleIdentity.DynamicOperationSignature !=
                scheduledDynamicOperationSignature ||
            scheduleIdentity.ResourcePlanRevision !=
                authority.ResourcePlanStamp.ResourcePlannerRevision ||
            scheduleIdentity.RecordingTarget !=
                authority.RecordingTargetSnapshot)
        {
            return false;
        }

        PrimaryCommandArtifactOwner? owner = ResolvePreparedPrimaryOwner(
            input.PrimaryCommandBuffer);
        if (owner is null || owner.Dirty ||
            owner.RecordedCommandChainScheduleCacheIdentity != scheduleIdentity ||
            !owner.TryProjectRecordedOperationOrder(
                input.StaticOperations,
                input.DynamicUiOperations,
                out FrameOp[] orderedStaticOperations,
                out FrameOp[] orderedDynamicUiOperations))
        {
            return false;
        }

        ReadOnlySpan<FrameOp> reuseStaticOperations =
            orderedStaticOperations;
        FrameOpContext fallbackContext = reuseStaticOperations.Length > 0
            ? reuseStaticOperations[0].Context
            : orderedDynamicUiOperations.Length > 0
                ? orderedDynamicUiOperations[0].Context
                : authority.PresentationSource.Context;
        ulong frameOpContextFingerprint =
            ComputeCommandBufferFrameOpContextFingerprint(
                reuseStaticOperations,
                orderedDynamicUiOperations,
                in fallbackContext);
        if (owner.FrameOpsSignature != input.StaticOperationSignature ||
            owner.DynamicUiSignature != input.DynamicUiOperationSignature ||
            owner.DynamicUiOpCount != orderedDynamicUiOperations.Length ||
            owner.PlannerRevision !=
                authority.ResourcePlanStamp.ResourcePlannerRevision ||
            owner.RecordedFrameOpContextFingerprint != frameOpContextFingerprint)
        {
            return false;
        }

        FrameOperationSequence staticOperations =
            new(orderedStaticOperations);
        FrameOperationSequence dynamicUiOperations =
            new(orderedDynamicUiOperations);
        VulkanComputePreparationResult computePreparation =
            PrepareComputeFrameOpsForRecording(
                input.ImageIndex,
                staticOperations);
        if (computePreparation.Succeeded)
        {
            computePreparation = PrepareComputeFrameOpsForRecording(
                input.ImageIndex,
                dynamicUiOperations);
        }
        if (!computePreparation.Succeeded)
            return false;

        ulong cohortGeneration = unchecked((ulong)Interlocked.Increment(
            ref _primaryReuseCohortGeneration));
        if (cohortGeneration == 0UL)
        {
            cohortGeneration = unchecked((ulong)Interlocked.Increment(
                ref _primaryReuseCohortGeneration));
        }
        if (!TryPreparePrimaryReuseFrameDataCohort(
                input.ImageIndex,
                input.ImageIndex,
                cohortGeneration,
                input.RenderFrameId,
                input.CommandChainSchedule,
                staticOperations,
                dynamicUiOperations))
        {
            return false;
        }

        ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
            reuseStaticOperations,
            orderedDynamicUiOperations,
            in fallbackContext);
        ulong imageLayoutStartSignature =
            ComputePreparedImageLayoutStartSignature(
                authority.RecordingTarget,
                authority.TrackedTargetLayout,
                authority.ResourcePlanStamp.ResourceAllocationSignature);
        if (!TryReuseCleanCommandChainPrimaryVariant(
                input.ImageIndex,
                cohortGeneration,
                input.RenderFrameId,
                input.ImageIndex,
                input.StaticOperationSignature,
                frameOpContextFingerprint,
                frameOpContextId,
                input.DynamicUiOperationSignature,
                orderedDynamicUiOperations.Length,
                authority.ResourcePlanStamp.ResourcePlannerRevision,
                imageLayoutStartSignature,
                gpuPipelineProfilingActive: false,
                commandBufferImageSlot: checked((int)input.ImageIndex),
                reuseStaticOperations,
                orderedDynamicUiOperations,
                dynamicUiOperations,
                delayDynamicUiSecondaryRecording:
                    authority.Policy.PreserveSwapchainForOverlay,
                authority.Policy.PreserveSwapchainForOverlay,
                requiresTrackedPresentSourceRefresh:
                    authority.PresentationSource.HasLogicalSource,
                authority.RecordingTarget.ImageEverPresentedAtRecordStart,
                input.CommandChainSchedule,
                authority.RecordingTarget,
                authority.Policy,
                out CommandBuffer reusedPrimary,
                out CommandBuffer reusedDynamicUi,
                out int reusedDynamicUiCount,
                out _,
                out ImageLayout reusedFinalLayout))
        {
            return false;
        }

        owner.LastUsedFrameId = unchecked((ulong)Interlocked.Increment(
            ref _recordedPrimaryFrameCounter));
        result = new VulkanPrimaryCommandRecordingResult(
            EVulkanPrimaryCommandRecordingDisposition.Reused,
            reusedPrimary,
            reusedDynamicUi,
            reusedDynamicUiCount,
            TextureUploadCommandBuffer: default,
            TextureUploadCommandPool: default,
            reusedFinalLayout,
            owner.RecordedSwapchainWriteCount,
            Volatile.Read(ref CommandBuffers.DirtyGeneration),
            Reason: null);
        return true;
    }

    /// <summary>
    /// Records one primary command buffer exclusively from a sealed frame plan
    /// and frozen output/planner observations.
    /// </summary>
    internal VulkanPrimaryCommandRecordingResult RecordPrimary(
        in VulkanPreparedPrimaryCommandInput input)
    {
        if (!TryValidatePreparedPrimaryInput(in input, out string reason))
            return VulkanPrimaryCommandRecordingResult.ReplanRequired(reason);

        Interlocked.Increment(ref _recordedPrimaryFrameCounter);
        bool gpuPipelineProfilingActive =
            VulkanFrameTelemetry.IsGpuProfilerCommandBufferInstrumentationEnabled &&
            FrameTelemetry._vulkanGpuProfilerEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;

        CommandBuffer uploadCommandBuffer = default;
        CommandPool uploadCommandPool = default;
        FrameOp[] uploadOperations = input.TextureUploadOperations ?? [];
        if (uploadOperations.Length > 0 &&
            !TryRecordTextureUploadCommandBuffer(
                input.ImageIndex,
                uploadOperations,
                out uploadCommandBuffer,
                out uploadCommandPool))
        {
            return VulkanPrimaryCommandRecordingResult.Deferred(
                "texture-upload command recording was deferred");
        }

        FrameOperationSequence operations = input.LogicalViewId == 0
            ? input.FramePlan.GetNativeStaticOperationsForRecording()
            : input.FramePlan.GetNativeStaticOperationsForLogicalView(
                input.LogicalViewId,
                input.NativeOperationsOverride ?? []);

        PrimaryCommandArtifactOwner? owner = ResolvePreparedPrimaryOwner(
            input.PrimaryCommandBuffer);
        FrameOp[] dynamicUiOperations = input.DynamicUiOperations ?? [];
        FrameOperationSequence sealedDynamicUiOperations =
            input.FramePlan.GetNativeDynamicOverlayOperationsForRecording();
        ReadOnlySpan<FrameOp> reuseStaticOperations = operations.AsSpan();
        if (owner is not null &&
            VulkanPrimaryCommandBufferReuseEnabled &&
            CommandChainsEnabledForCurrentRecording &&
            input.LogicalViewId == 0 &&
            input.CommandChainSchedule is not null &&
            !input.CommandChainSchedule.RequiresFreshPrimary &&
            !input.Policy.FreshSerialRecording &&
            TryPreparePrimaryReuseFrameDataCohort(
                in input,
                operations,
                sealedDynamicUiOperations))
        {
            FrameOpContext fallbackContext = reuseStaticOperations.Length > 0
                ? reuseStaticOperations[0].Context
                : dynamicUiOperations.Length > 0
                    ? dynamicUiOperations[0].Context
                    : input.PresentationSource.Context;
            ulong frameOpContextFingerprint =
                ComputeCommandBufferFrameOpContextFingerprint(
                    reuseStaticOperations,
                    dynamicUiOperations,
                    in fallbackContext);
            ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
                reuseStaticOperations,
                dynamicUiOperations,
                in fallbackContext);
            ulong imageLayoutStartSignature = ComputePreparedImageLayoutStartSignature(in input);
            if (TryReuseCleanCommandChainPrimaryVariant(
                    input.ImageIndex,
                    input.FramePlan.Generation,
                    input.FramePlan.RenderFrameId,
                    input.FrameDataImageIndexOverride ?? input.ImageIndex,
                    input.FramePlan.StaticOperationSignature,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    input.FramePlan.DynamicOverlaySignature,
                    input.FramePlan.DynamicOverlayOperationCount,
                    input.ResourcePlanStamp.ResourcePlannerRevision,
                    imageLayoutStartSignature,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot: checked((int)input.ImageIndex),
                    reuseStaticOperations,
                    dynamicUiOperations,
                    sealedDynamicUiOperations,
                    delayDynamicUiSecondaryRecording:
                        input.Policy.PreserveSwapchainForOverlay,
                    input.Policy.PreserveSwapchainForOverlay,
                    requiresTrackedPresentSourceRefresh: input.PresentationSource.HasLogicalSource,
                    input.RecordingTarget.ImageEverPresentedAtRecordStart,
                    input.CommandChainSchedule,
                    input.RecordingTarget,
                    input.Policy,
                    out CommandBuffer reusedPrimary,
                    out CommandBuffer reusedDynamicUi,
                    out int reusedDynamicUiCount,
                    out _,
                    out ImageLayout reusedFinalLayout))
            {
                return new VulkanPrimaryCommandRecordingResult(
                    EVulkanPrimaryCommandRecordingDisposition.Reused,
                    reusedPrimary,
                    reusedDynamicUi,
                    reusedDynamicUiCount,
                    uploadCommandBuffer,
                    uploadCommandPool,
                    reusedFinalLayout,
                    owner.RecordedSwapchainWriteCount,
                    Volatile.Read(ref CommandBuffers.DirtyGeneration),
                    Reason: null);
            }
        }

        if (owner is not null && dynamicUiOperations.Length > 0 &&
            !RecordDynamicUiBatchTextSecondaryCommandBuffer(
                input.ImageIndex,
                owner,
                sealedDynamicUiOperations,
                input.FramePlan.DynamicOverlaySignature,
                forceRecord: input.Policy.FreshSerialRecording,
                includeDepthAttachment: !input.Policy.PreserveSwapchainForOverlay,
                input.RecordingTarget,
                input.Policy))
        {
            return AttachUploadArtifacts(
                VulkanPrimaryCommandRecordingResult.Deferred(
                    "dynamic UI secondary command recording was deferred"),
                uploadCommandBuffer,
                uploadCommandPool);
        }
        VulkanCommandRecordingContext context = new(
            input.ImageIndex,
            input.PrimaryCommandBuffer,
            input.DynamicUiSecondaryCommandBuffer,
            operations,
            input.Policy.PreserveSwapchainForOverlay
                ? 0
                : input.FramePlan.DynamicOverlayOperationCount,
            input.CommandChainSchedule,
            input.Policy.PreserveSwapchainForOverlay,
            input.Policy.TransitionSwapchainToPresent,
            input.PrimaryCommandPlan,
            input.FrameDataImageIndexOverride,
            input.OpenXrTargetContext,
            input.ExcludeDesktopSwapchainBarriers,
            input.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan,
            input.FramePlan,
            input.RecordingTarget,
            input.PresentationSource,
            input.Policy,
            input.ResourcePlanStamp,
            input.ClearState);

        if (!Recorder.Prepare(ref context))
            return AttachUploadArtifacts(
                ClassifyPrimaryRecordingFailure(ref context),
                uploadCommandBuffer,
                uploadCommandPool);

        Recorder.EnterRecordingScope();
        bool recorded;
        try
        {
            recorded = RecordCommandBufferLifecycle(ref context);
        }
        finally
        {
            Recorder.ExitRecordingScope();
        }
        if (!recorded)
            return AttachUploadArtifacts(
                ClassifyPrimaryRecordingFailure(ref context),
                uploadCommandBuffer,
                uploadCommandPool);

        if (owner is not null)
            PublishRecordedPrimaryOwner(owner, in input, operations, dynamicUiOperations, ref context);

        return new VulkanPrimaryCommandRecordingResult(
            EVulkanPrimaryCommandRecordingDisposition.Recorded,
            input.PrimaryCommandBuffer,
            input.DynamicUiSecondaryCommandBuffer,
            input.FramePlan.DynamicOverlayOperationCount,
            uploadCommandBuffer,
            uploadCommandPool,
            context.RecordedSwapchainFinalLayout,
            context.RecordedSwapchainWriteCount,
            Volatile.Read(ref CommandBuffers.DirtyGeneration),
            Reason: null);
    }

    /// <summary>
    /// Builds the current frame's immutable mesh refresh cohort before primary
    /// reuse examines it. The scratch object is recorder-thread-local rather than
    /// swapchain-image-owned, so a cohort left by the last fresh recording cannot
    /// be treated as current merely because the primary artifact itself is clean.
    /// </summary>
    private bool TryPreparePrimaryReuseFrameDataCohort(
        in VulkanPreparedPrimaryCommandInput input,
        FrameOperationSequence operations,
        FrameOperationSequence dynamicUiOperations)
        => TryPreparePrimaryReuseFrameDataCohort(
            input.ImageIndex,
            input.FrameDataImageIndexOverride ?? input.ImageIndex,
            input.FramePlan.Generation,
            input.FramePlan.RenderFrameId,
            input.CommandChainSchedule!,
            operations,
            dynamicUiOperations);

    private bool TryPreparePrimaryReuseFrameDataCohort(
        uint imageIndex,
        uint frameDataImageIndex,
        ulong cohortGeneration,
        ulong renderFrameId,
        CommandChainSchedule commandChainSchedule,
        FrameOperationSequence operations,
        FrameOperationSequence dynamicUiOperations)
    {
        CommandBufferRecordingScratch scratch =
            _commandBufferRecordingScratch.Value!;
        int commandBufferImageSlot = unchecked((int)Math.Min(
            frameDataImageIndex,
            int.MaxValue));
        bool registered;
        string failureReason;
        CommandChainScheduleCacheIdentity scheduleIdentity =
            commandChainSchedule.CacheIdentity;
        ulong primaryReusableBatchSignature =
            ComputeReusableFrameDataBatchSignature(
                in scheduleIdentity,
                dynamicUi: false);
        ulong dynamicUiReusableBatchSignature =
            ComputeReusableFrameDataBatchSignature(
                in scheduleIdentity,
                dynamicUi: true);
        using (VulkanCpuStageScope cpuStage =
               new(_frameTelemetry, EVulkanCpuStage.FrameDataManifest))
        {
            registered = TryRegisterFrameWideMeshFrameDataRequirements(
                operations,
                dynamicUiOperations,
                commandBufferImageSlot,
                sealAfterRegister: true,
                scratch.MeshDrawSlotsByRenderer,
                scratch,
                scratch.ReusableMeshFrameDataFamilyBases,
                primaryReusableBatchSignature,
                dynamicUiReusableBatchSignature,
                out _,
                out failureReason);
        }

        if (!registered)
        {
            TraceCommandChainPrimaryReuseRejection(
                imageIndex,
                "FrameDataCohort",
                failureReason);
            return false;
        }

        scratch.PublishReusableFrameDataRefreshCohort(
            cohortGeneration,
            renderFrameId,
            frameDataImageIndex);
        return true;
    }

    /// <summary>
    /// Reuses the schedule publication already validated by primary-command
    /// reuse as the stable mesh-cohort identity. This avoids rehashing every
    /// draw's immutable program and descriptor layout a second time.
    /// </summary>
    private static ulong ComputeReusableFrameDataBatchSignature(
        in CommandChainScheduleCacheIdentity scheduleIdentity,
        bool dynamicUi)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x5246555345424154UL);
        hash.Add(dynamicUi);
        hash.Add(
            dynamicUi
                ? scheduleIdentity.DynamicOperationCount
                : scheduleIdentity.StaticOperationCount);
        hash.Add(
            dynamicUi
                ? scheduleIdentity.DynamicOperationSignature
                : scheduleIdentity.StaticOperationSignature);
        hash.Add(scheduleIdentity.ResourcePlanRevision);
        hash.Add(scheduleIdentity.ResourceVersionSignature);
        hash.Add(scheduleIdentity.DescriptorVersionSignature);
        return hash.ToHash();
    }

    private void PublishRecordedPrimaryOwner(
        PrimaryCommandArtifactOwner owner,
        in VulkanPreparedPrimaryCommandInput input,
        FrameOperationSequence operations,
        FrameOp[] dynamicUiOperations,
        scoped ref VulkanCommandRecordingContext context)
    {
        FrameOpContext fallbackContext = operations.Length > 0
            ? operations[0].Context
            : dynamicUiOperations.Length > 0
                ? dynamicUiOperations[0].Context
                : input.PresentationSource.Context;
        FrameOpSignatureHasher contextHash = new();
        contextHash.Add(0x434D444354584654UL);
        contextHash.Add(operations.Length);
        for (int operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            contextHash.Add(operations[operationIndex].Context.RecordingFingerprint);
            contextHash.Add((int)operations[operationIndex].Context.ContextKind);
        }
        contextHash.Add(dynamicUiOperations.Length);
        for (int operationIndex = 0; operationIndex < dynamicUiOperations.Length; operationIndex++)
        {
            contextHash.Add(dynamicUiOperations[operationIndex].Context.RecordingFingerprint);
            contextHash.Add((int)dynamicUiOperations[operationIndex].Context.ContextKind);
        }
        if (operations.Length == 0 && dynamicUiOperations.Length == 0)
            contextHash.Add(fallbackContext.RecordingFingerprint);

        owner.Dirty = context.FrameOpsRequireRerecord;
        owner.DirtyReason = context.FrameOpsRequireRerecord
            ? "recorded frame omitted transient operations and requires completion"
            : null;
        owner.FrameOpsSignature = input.FramePlan.StaticOperationSignature;
        owner.DynamicUiSignature = input.FramePlan.DynamicOverlaySignature;
        owner.DynamicUiOpCount = input.FramePlan.DynamicOverlayOperationCount;
        owner.PreserveSwapchainForOverlay = input.Policy.PreserveSwapchainForOverlay;
        owner.RecordedFrameOpContextFingerprint = contextHash.ToHash();
        owner.RecordedFrameOpContextId = operations.Length > 0
            ? operations[0].Context.ContextId
            : dynamicUiOperations.Length > 0
                ? dynamicUiOperations[0].Context.ContextId
                : fallbackContext.ContextId;
        owner.RecordedSwapchainImageEverPresented =
            input.RecordingTarget.ImageEverPresentedAtRecordStart;
        owner.RecordedSwapchainFinalLayout = context.RecordedSwapchainFinalLayout;
        owner.RecordedSwapchainWriteCount = context.RecordedSwapchainWriteCount;
        owner.RecordedSwapchainRefreshFromLastPresentSource =
            input.PresentationSource.HasLogicalSource;
        owner.RecordedImageLayoutStartSignature =
            ComputePreparedImageLayoutStartSignature(in input);
        owner.CommandChainScheduleSignature =
            input.CommandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        owner.PlannerRevision = input.ResourcePlanStamp.ResourcePlannerRevision;
        owner.GpuProfilerActive = FrameTelemetry._vulkanGpuProfilerRecordingActive;
        owner.GpuProfilerFrameSlot = owner.GpuProfilerActive
            ? FrameTelemetry._vulkanGpuProfilerRecordingFrameSlot
            : -1;
        CaptureVulkanGpuProfilerVariantScopes(owner.GpuProfilerFrameSlot, owner);
        owner.LastUsedFrameId = input.FramePlan.RenderFrameId;
        if (input.LogicalViewId == 0UL)
            owner.CaptureRecordedOperationOrder(input.FramePlan);
        else
            owner.ClearRecordedOperationOrder();
        ReadOnlySpan<FrameOp> staticOperations = operations.AsSpan();
        CommandBufferGenerationDomains generations = CaptureCommandBufferGenerationDomains(
            input.ImageIndex,
            input.FramePlan.StaticOperationSignature,
            staticOperations,
            dynamicUiOperations,
            input.FramePlan.DynamicOverlaySignature,
            in fallbackContext,
            owner.RecordedFrameOpContextFingerprint,
            profilerActive: owner.GpuProfilerActive,
            profilerFrameSlot: owner.GpuProfilerFrameSlot);
        owner.RecordedGenerations = generations;
        owner.RecordedResourceGeneration = generations.ResourceAllocation;
        owner.RecordedDescriptorGeneration = generations.Descriptor;
        CommandRecordingDependencySignature dependencySignature =
            CaptureCommandRecordingDependencySignature(
                input.ImageIndex,
                input.ResourcePlanStamp.ResourceAllocationSignature,
                input.FramePlan.DynamicOverlaySignature,
                in fallbackContext,
                in generations,
                staticOperations,
                SharedGraphicsPipelineGeneration);
        if (input.CommandChainSchedule is { } schedule)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache =
                GetCommandChainCache(input.ImageIndex);
            VulkanCommandIdentityComponents groupIdentity =
                ComputePrimaryCommandBufferGroupIdentity(schedule, commandChainCache);
            owner.CommandChainPrimaryIdentityComponents = groupIdentity;
            owner.CommandChainPrimaryGroupSignature = groupIdentity.Combined;
            owner.RecordedCommandChainScheduleCacheIdentity =
                schedule.CacheIdentity;
            owner.CommandChainPrimaryGroupCount = schedule.Groups.Length;
            owner.CommandChainPrimarySkeletonSignature =
                ComputeCommandChainPrimarySkeletonSignature(staticOperations);
            owner.AllPreparedDrawBindingsUseSecondaryBuffers =
                AreAllPreparedDrawBindingsSecondaryOwned(schedule, staticOperations);
            owner.RecordedDependencySignature =
                CaptureCommandChainPrimaryPreparedBindingDependencies(
                    dependencySignature,
                    staticOperations) with
                {
                    RenderTargetSnapshot = schedule.DependencySignature.RenderTargetSnapshot,
                    RecordedPacketKey = schedule.DependencySignature.RecordedPacketKey,
                };
            owner.RecordedSecondaryArtifactSequence.CopyFrom(
                CommandBuffers.RecordingScratch.Value!
                    .ExecutedCommandChainSecondaryArtifactSequence);
            long artifactMutationGeneration =
                CommandChains.SnapshotArtifactMutationGeneration();
            schedule.PublishArtifactMutationGeneration(
                artifactMutationGeneration);
            owner.RecordedCommandChainArtifactMutationGeneration =
                artifactMutationGeneration;
        }
        else
        {
            owner.RecordedDependencySignature = dependencySignature;
            owner.CommandChainPrimaryGroupSignature = ulong.MaxValue;
            owner.RecordedCommandChainScheduleCacheIdentity = default;
            owner.CommandChainPrimaryGroupCount = -1;
            owner.CommandChainPrimarySkeletonSignature = ulong.MaxValue;
            owner.RecordedSecondaryArtifactSequence.Clear();
            owner.RecordedCommandChainArtifactMutationGeneration = -1;
            owner.AllPreparedDrawBindingsUseSecondaryBuffers = false;
        }
        StoreFrameOpSignatureDebugParts(owner, operations);
        CaptureCommandBufferVariantImageLayoutEndState(owner);
        CommandBuffers.ActiveBuffers?[input.ImageIndex] = owner.PrimaryCommandBuffer;
    }

    private static ulong ComputePreparedImageLayoutStartSignature(
        in VulkanPreparedPrimaryCommandInput input)
        => ComputePreparedImageLayoutStartSignature(
            input.RecordingTarget,
            input.TrackedTargetLayout,
            input.ResourcePlanStamp.ResourceAllocationSignature);

    private static ulong ComputePreparedImageLayoutStartSignature(
        in SwapchainRecordingTarget recordingTarget,
        ImageLayout trackedTargetLayout,
        ulong resourceAllocationSignature)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(recordingTarget.Image.Handle);
        hash.Add((int)trackedTargetLayout);
        hash.Add(resourceAllocationSignature);
        hash.Add(recordingTarget.ImageEverPresentedAtRecordStart);
        return hash.ToHash();
    }

    private PrimaryCommandArtifactOwner? ResolvePreparedPrimaryOwner(
        CommandBuffer primaryCommandBuffer)
    {
        PrimaryCommandArtifactOwner[]? desktopOwners = CommandBuffers.PrimaryOwners;
        if (desktopOwners is not null)
            for (int index = 0; index < desktopOwners.Length; index++)
                if (desktopOwners[index].PrimaryCommandBuffer.Handle == primaryCommandBuffer.Handle)
                    return desktopOwners[index];

        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
            foreach (PrimaryCommandArtifactOwner owner in CommandBuffers.OpenXrPrimaryOwners.Values)
                if (owner.PrimaryCommandBuffer.Handle == primaryCommandBuffer.Handle)
                    return owner;
        return null;
    }

    private static VulkanPrimaryCommandRecordingResult AttachUploadArtifacts(
        VulkanPrimaryCommandRecordingResult result,
        CommandBuffer uploadCommandBuffer,
        CommandPool uploadCommandPool)
        => result with
        {
            TextureUploadCommandBuffer = uploadCommandBuffer,
            TextureUploadCommandPool = uploadCommandPool,
        };

    private static bool TryValidatePreparedPrimaryInput(
        in VulkanPreparedPrimaryCommandInput input,
        out string reason)
    {
        bool allowsTargetlessExternalOperations =
            input.Policy.IsExternalSwapchainTarget &&
            input.ExcludeDesktopSwapchainBarriers &&
            !input.Policy.TransitionSwapchainToPresent &&
            input.FramePlan.OperationCount > 0;
        if (!input.RecordingTarget.IsValid && !allowsTargetlessExternalOperations)
        {
            reason = "frame-plan precondition failed: the frozen output target is no longer recordable";
            return false;
        }
        if (input.PrimaryCommandBuffer.Handle == 0)
        {
            reason = "frame-plan precondition failed: the prepared primary command buffer is null";
            return false;
        }
        if (!input.FramePlan.IsSealed)
        {
            reason = "frame-plan precondition failed: the prepared frame plan is not sealed";
            return false;
        }
        if (!input.PrimaryCommandPlan.IsFrozen)
        {
            reason = "frame-plan precondition failed: the prepared primary command plan is not frozen";
            return false;
        }
        if (input.FramePlan.PlannerRevision !=
            input.ResourcePlanStamp.ResourcePlannerRevision)
        {
            reason =
                "frame-plan precondition failed: the sealed frame plan and resource stamp have different revisions";
            return false;
        }
        if (input.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan.Revision !=
            input.ResourcePlanStamp.ResourcePlannerRevision)
        {
            reason =
                "frame-plan precondition failed: the frozen render graph and resource stamp have different revisions";
            return false;
        }

        IReadOnlyList<RenderGraph.VulkanBarrierPlanner.PlannedImageBarrier> imageBarriers =
            input.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan.Barriers.ImageBarriers;
        for (int barrierIndex = 0; barrierIndex < imageBarriers.Count; barrierIndex++)
            if (imageBarriers[barrierIndex].NativeImage.Handle == 0)
            {
                reason =
                    $"frame-plan precondition failed: frozen barrier resource '{imageBarriers[barrierIndex].ResourceName}' is not allocated";
                return false;
            }

        IReadOnlyList<RenderGraph.VulkanBarrierPlanner.PlannedBufferBarrier> bufferBarriers =
            input.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan.Barriers.BufferBarriers;
        for (int barrierIndex = 0; barrierIndex < bufferBarriers.Count; barrierIndex++)
        {
            RenderGraph.VulkanBarrierPlanner.PlannedBufferBarrier barrier =
                bufferBarriers[barrierIndex];
            if (barrier.NativeBuffer.Handle != 0 && barrier.NativeSize != 0)
                continue;

            reason =
                $"frame-plan precondition failed: frozen buffer barrier resource '{barrier.ResourceName}' has no native binding";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static VulkanPrimaryCommandRecordingResult ClassifyPrimaryRecordingFailure(
        scoped ref VulkanCommandRecordingContext context)
    {
        string reason = string.IsNullOrWhiteSpace(context.RecordingDeferredReason)
            ? "primary command recording was deferred"
            : context.RecordingDeferredReason;
        return context.FailureKind == EVulkanCommandRecordingFailureKind.ReplanRequired
            ? VulkanPrimaryCommandRecordingResult.ReplanRequired(reason)
            : VulkanPrimaryCommandRecordingResult.Deferred(reason);
    }
}
