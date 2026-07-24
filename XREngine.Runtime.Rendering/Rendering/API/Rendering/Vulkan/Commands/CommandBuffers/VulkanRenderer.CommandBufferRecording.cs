using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly struct PendingRecordedTextureUploadPublication(
            VulkanImportedTexturePendingUpload upload,
            ulong timelineValue,
            string uploadSource)
        {
            public VulkanImportedTexturePendingUpload Upload { get; } = upload;
            public ulong TimelineValue { get; } = timelineValue;
            public string UploadSource { get; } = uploadSource;
        }

        private readonly List<VulkanImportedTexturePendingUpload> _recordedTextureUploadsForSubmit = new();
        private readonly List<PendingRecordedTextureUploadPublication> _pendingRecordedTextureUploadPublications = new();

        private CommandBuffer EnsureCommandBufferRecorded(
            uint imageIndex,
            bool preserveSwapchainForOverlay,
            out string recordingDeferredReason,
            out CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            out int dynamicUiBatchTextOverlayOpCount,
            out FrameOp[] dynamicUiBatchTextOverlayOps,
            out ulong dynamicUiBatchTextOverlaySignature,
            out CommandBufferCacheVariant? dynamicUiBatchTextOverlayVariant,
            out CommandBuffer textureUploadCommandBuffer,
            out CommandPool textureUploadCommandPool,
            out ImageLayout swapchainLayoutAfterCommandBuffer,
            out long commandBufferDirtyGenerationAfterRecord)
        {
            _lastEnsureCommandBufferRecordedPrimary = false;
            recordingDeferredReason = string.Empty;
            dynamicUiBatchTextSecondaryCommandBuffer = default;
            dynamicUiBatchTextOverlayOpCount = 0;
            dynamicUiBatchTextOverlayOps = Array.Empty<FrameOp>();
            dynamicUiBatchTextOverlaySignature = 0;
            dynamicUiBatchTextOverlayVariant = null;
            textureUploadCommandBuffer = default;
            textureUploadCommandPool = default;
            swapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;
            commandBufferDirtyGenerationAfterRecord = SnapshotCommandBufferDirtyGeneration();

            if (!IsDeviceOperational)
            {
                recordingDeferredReason = $"Vulkan device state is {DeviceState}";
                return default;
            }

            if (!TryEnsureCommandBuffersForSwapchain())
                throw new InvalidOperationException("Command buffers are unavailable because swapchain framebuffers are not initialised.");

            if (_commandBuffers is null)
                throw new InvalidOperationException("Command buffers have not been allocated yet.");

            if (imageIndex >= _commandBuffers.Length)
                throw new InvalidOperationException($"Command buffer index {imageIndex} is out of range for {_commandBuffers.Length} allocated command buffers.");

            if (_commandBufferDirtyFlags is null || imageIndex >= _commandBufferDirtyFlags.Length)
                throw new InvalidOperationException("Command buffer dirty flags are not initialised correctly.");

            if (_commandBufferFrameOpSignatures is null || imageIndex >= _commandBufferFrameOpSignatures.Length)
                throw new InvalidOperationException("Command buffer frame-op signatures are not initialised correctly.");

            if (_commandBufferPlannerRevisions is null || imageIndex >= _commandBufferPlannerRevisions.Length)
                throw new InvalidOperationException("Command buffer planner revisions are not initialised correctly.");

            bool imageForcedDirty = _commandBufferDirtyFlags[imageIndex];
            long ensureStartDirtyGeneration = SnapshotCommandBufferDirtyGeneration();
            bool frameOpSignatureDirty = false;
            bool plannerDirty = false;
            bool profilerDirty = false;
            bool frameDataDirty = false;
            bool dynamicUiDirty = false;
            bool swapchainLifecycleDirty = false;
            bool commandChainPrimaryDirty = false;
            bool primaryFrameStateDirty = false;
            string? primaryFrameStateDirtyReason = null;
            PrimaryCommandBufferDirtyReason commandChainPrimaryDirtyReason = PrimaryCommandBufferDirtyReason.None;
            int commandBufferImageSlot = unchecked((int)Math.Min(imageIndex, int.MaxValue));
            bool swapchainImageEverPresentedAtRecord = IsSwapchainImageEverPresented(imageIndex);
            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            bool gpuProfilerCommandBufferStateDirty = IsVulkanGpuProfilerCommandBufferStateDirty(
                imageIndex,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            if (gpuProfilerCommandBufferStateDirty)
            {
                ClearVulkanGpuProfilerPendingQueries();
                MarkCommandBufferVariantsDirty(imageIndex, "gpu-profiler-command-buffer-state");
            }

            FrameOp[] ops;
            ulong rawFrameOpsSignature;
            using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.FrameOpPreparation))
            {
                ops = DrainFrameOpsExcludingTextureUploads(
                    out rawFrameOpsSignature,
                    computeSignature: FrameOpSignatureDiffDiagnosticsEnabled);
                ops = FilterDiagnosticSkippedFrameOps(ops);
            }

            FrameOp[] dynamicUiBatchTextOps = Array.Empty<FrameOp>();
            bool hasFrameOps = ops.Length > 0;
            ulong frameOpsSignature = rawFrameOpsSignature;
            ulong dynamicUiBatchTextSignature = 0;

            if (hasFrameOps)
            {
                using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.FrameOpPreparation))
                {
                    // EVulkanCpuStage.FrameOpPreparation already measures this hot section.
                    // Interface-returned profiler scopes box their value-type implementation,
                    // so a second profiler scope here would manufacture managed allocations every frame.
                    ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                    SplitDynamicUiBatchTextFrameOps(ops, out FrameOp[] staticOps, out dynamicUiBatchTextOps);
                    ops = staticOps;
                    frameOpsSignature = ComputeFrameOpsSignature(ops);
                    dynamicUiBatchTextSignature = dynamicUiBatchTextOps.Length == 0
                        ? 0
                        : ComputeFrameOpsSignature(dynamicUiBatchTextOps);
                }

                if (FrameOpSignatureDiffDiagnosticsEnabled && rawFrameOpsSignature != frameOpsSignature)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.FrameOpSignature.Normalized.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Frame-op signature changed after recorder normalization: raw=0x{0:X16} normalized=0x{1:X16} ops={2}",
                        rawFrameOpsSignature,
                        frameOpsSignature,
                        ops.Length);
                }
            }

            CommandBufferRecordingScratch frameDataScratch = _commandBufferRecordingScratch.Value!;
            if (!TryRegisterFrameWideMeshFrameDataRequirements(
                    ops,
                    dynamicUiBatchTextOps,
                    commandBufferImageSlot,
                    sealAfterRegister: true,
                    frameDataScratch.MeshDrawSlotsByRenderer,
                    frameDataScratch,
                    frameDataScratch.ReusableMeshFrameDataFamilyBases,
                    out _,
                    out string frameDataManifestReason))
            {
                recordingDeferredReason = frameDataManifestReason;
                FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                return default;
            }

            FrameOp[] plannerPreparationOps = ops.Length > 0 ? ops : dynamicUiBatchTextOps;
            using FrameOpResourcePlannerPreparationScope frameOpResourcePlannerPreparationScope =
                new(this, plannerPreparationOps);

            bool hasStaticFrameOps = ops.Length > 0;
            bool hasQueryFrameOps = HasQueryFrameOps(ops) || HasQueryFrameOps(dynamicUiBatchTextOps);
            bool delayDynamicUiBatchTextOverlayRecording =
                preserveSwapchainForOverlay &&
                dynamicUiBatchTextOps.Length > 0;
            bool requiresTrackedPresentSourceRefresh =
                !hasStaticFrameOps &&
                HasLastWindowPresentSourceForSwapchainRefresh();

            ulong plannerRevision;
            using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.ResourcePlanning))
            {
                if (hasStaticFrameOps)
                {
                    if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                    {
                        recordingDeferredReason = prePlanFailureReason;
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }

                    FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(ops, frameOpsSignature);
                    if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                    {
                        recordingDeferredReason = postPlanFailureReason;
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }

                    if (!TryRefreshFrameOpResourceWrappers(
                            ops,
                            plannerContext,
                            "Vulkan command-chain resource planner refresh",
                            AllowSynchronousResourceUploads,
                            out string refreshFailureReason))
                    {
                        recordingDeferredReason = refreshFailureReason;
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }
                }
                else if (dynamicUiBatchTextOps.Length > 0)
                {
                    if (TryDescribeRecentResourceAllocationFailure(out string preDynamicPlanFailureReason))
                    {
                        recordingDeferredReason = preDynamicPlanFailureReason;
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }

                    FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(dynamicUiBatchTextOps, dynamicUiBatchTextSignature);
                    if (TryDescribeRecentResourceAllocationFailure(out string postDynamicPlanFailureReason))
                    {
                        recordingDeferredReason = postDynamicPlanFailureReason;
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }

                    if (!TryRefreshFrameOpResourceWrappers(
                        dynamicUiBatchTextOps,
                        plannerContext,
                        "Vulkan command-chain dynamic UI resource planner refresh",
                        AllowSynchronousResourceUploads,
                        out string refreshFailureReason))
                    {
                        recordingDeferredReason = refreshFailureReason;
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }
                }

                frameOpResourcePlannerPreparationScope.PublishCurrentState();
                plannerRevision = hasStaticFrameOps
                    ? PrepareFrameOpResourcePlannerStatesForFrameOps(ops, frameOpsSignature)
                    : dynamicUiBatchTextOps.Length > 0
                        ? PrepareFrameOpResourcePlannerStatesForFrameOps(dynamicUiBatchTextOps, dynamicUiBatchTextSignature)
                        : ResourcePlannerRevision;
                if (TryDescribeRecentResourceAllocationFailure(out string frameOpPlannerFailureReason))
                {
                    recordingDeferredReason = frameOpPlannerFailureReason;
                    FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                    return default;
                }
            }

            FrameOpContext commandBufferFallbackContext = hasStaticFrameOps
                ? ops[0].Context
                : dynamicUiBatchTextOps.Length > 0
                    ? dynamicUiBatchTextOps[0].Context
                    : CaptureFrameOpContext();
            ulong frameOpContextFingerprint = ComputeCommandBufferFrameOpContextFingerprint(
                ops,
                dynamicUiBatchTextOps,
                commandBufferFallbackContext);
            ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
                ops,
                dynamicUiBatchTextOps,
                commandBufferFallbackContext);
            CommandBufferGenerationDomains currentGenerations = CaptureCommandBufferGenerationDomains(
                imageIndex,
                frameOpsSignature,
                ops,
                dynamicUiBatchTextOps,
                dynamicUiBatchTextSignature,
                commandBufferFallbackContext,
                frameOpContextFingerprint,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            // Take the synchronized cache-publication generation once for this prepared frame.
            // All primary/range signature construction below consumes this immutable snapshot.
            ulong sharedGraphicsPipelineGeneration = SharedGraphicsPipelineGeneration;
            CommandRecordingDependencySignature currentDependencySignature = CaptureCommandRecordingDependencySignature(
                imageIndex,
                commandBufferImageSlot,
                plannerRevision,
                frameOpsSignature,
                dynamicUiBatchTextSignature,
                commandBufferFallbackContext,
                currentGenerations,
                ops,
                sharedGraphicsPipelineGeneration);

            BeginRecordedTextureUploadSubmitBatch();
            FrameOp[] textureUploadOps = DrainTextureUploadFrameOps();
            if (textureUploadOps.Length > 0)
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.RecordTextureUploads"))
                {
                    if (!TryRecordTextureUploadCommandBuffer(
                            imageIndex,
                            textureUploadOps,
                            out textureUploadCommandBuffer,
                            out textureUploadCommandPool))
                    {
                        textureUploadCommandBuffer = default;
                        textureUploadCommandPool = default;
                    }
                }
            }

            if (!imageForcedDirty && HaveCommandBuffersDirtiedSince(ensureStartDirtyGeneration))
                imageForcedDirty = true;

            ulong imageLayoutStartSignature = ComputeImageLayoutStateSignature();
            bool hasMutableGpuDrivenFrameOps = hasStaticFrameOps && HasMutableGpuDrivenFrameOps(ops);
            if (!requiresTrackedPresentSourceRefresh &&
                !hasStaticFrameOps &&
                dynamicUiBatchTextOps.Length == 0 &&
                !preserveSwapchainForOverlay &&
                !imageForcedDirty &&
                TryReuseLastSwapchainWriterVariant(
                    imageIndex,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    plannerRevision,
                    imageLayoutStartSignature,
                    swapchainImageEverPresentedAtRecord,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot,
                    out CommandBuffer lastSwapchainWriterCommandBuffer,
                    out swapchainLayoutAfterCommandBuffer))
            {
                commandBufferDirtyGenerationAfterRecord = SnapshotCommandBufferDirtyGeneration();
                return lastSwapchainWriterCommandBuffer;
            }

            if (VulkanPrimaryCommandBufferReuseEnabled &&
                !hasMutableGpuDrivenFrameOps &&
                !imageForcedDirty &&
                !gpuPipelineProfilingActive &&
                TryReuseCleanCommandChainPrimaryVariant(
                    imageIndex,
                    frameOpsSignature,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    dynamicUiBatchTextSignature,
                    dynamicUiBatchTextOps.Length,
                    plannerRevision,
                    imageLayoutStartSignature,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot,
                    currentGenerations,
                    currentDependencySignature,
                    ops,
                    dynamicUiBatchTextOps,
                    delayDynamicUiBatchTextOverlayRecording,
                    preserveSwapchainForOverlay,
                    requiresTrackedPresentSourceRefresh,
                    swapchainImageEverPresentedAtRecord,
                    out CommandBuffer reusableCommandBuffer,
                    out dynamicUiBatchTextSecondaryCommandBuffer,
                    out dynamicUiBatchTextOverlayOpCount,
                    out CommandBufferCacheVariant? reusableDynamicUiBatchTextOverlayVariant,
                    out swapchainLayoutAfterCommandBuffer))
            {
                if (delayDynamicUiBatchTextOverlayRecording)
                {
                    dynamicUiBatchTextOverlayOps = dynamicUiBatchTextOps;
                    dynamicUiBatchTextOverlaySignature = dynamicUiBatchTextSignature;
                    dynamicUiBatchTextOverlayVariant = reusableDynamicUiBatchTextOverlayVariant;
                }

                commandBufferDirtyGenerationAfterRecord = SnapshotCommandBufferDirtyGeneration();
                return reusableCommandBuffer;
            }

            CommandChainSchedule? commandChainSchedule = null;
            CommandChainLoweringStats commandChainStats = default;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.CommandChainLowering"))
            {
                using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.PacketConstruction);
                FrameOp[] scheduledDynamicUiBatchTextOps = preserveSwapchainForOverlay
                    ? Array.Empty<FrameOp>()
                    : dynamicUiBatchTextOps;
                ulong scheduledDynamicUiBatchTextSignature = preserveSwapchainForOverlay
                    ? 0
                    : dynamicUiBatchTextSignature;

                commandChainSchedule = TryBuildCommandChainSchedule(
                    imageIndex,
                    ops,
                    scheduledDynamicUiBatchTextOps,
                    frameOpsSignature,
                    scheduledDynamicUiBatchTextSignature,
                    plannerRevision,
                    allowExternalSwapchainTarget: false,
                    out commandChainStats);
            }

            if (commandChainSchedule is not null)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
                    chainsScheduled: commandChainStats.ChainsScheduled,
                    chainsRecorded: commandChainStats.ChainsRecorded,
                    chainsReused: commandChainStats.ChainsReused,
                    chainsFrameDataRefreshed: commandChainStats.ChainsFrameDataRefreshed,
                    volatileChainsRecorded: commandChainStats.VolatileChainsRecorded,
                    visibilityPackets: commandChainStats.VisibilityPackets,
                    renderPackets: commandChainStats.RenderPackets,
                    secondaryCommandBuffers: commandChainStats.SecondaryCommandBuffers,
                    chainWorkerRecordTime: commandChainStats.WorkerRecordTime,
                    renderThreadWaitForWorkersTime: commandChainStats.WaitForWorkersTime,
                    firstStructuralDirtyReason: commandChainStats.FirstStructuralDirtyReason,
                    firstDescriptorGenerationMismatch: commandChainStats.FirstDescriptorGenerationMismatch,
                    firstResourcePlanRevisionMismatch: commandChainStats.FirstResourcePlanRevisionMismatch);
            }

            Dictionary<CommandChainKey, CommandChain>? commandChainCache = commandChainSchedule is null
                ? null
                : GetCommandChainCache(imageIndex);
            ulong commandChainPrimaryGroupSignature = commandChainSchedule is null || commandChainCache is null
                ? 0
                : ComputePrimaryCommandBufferGroupSignature(commandChainSchedule, commandChainCache);
            int commandChainPrimaryGroupCount = commandChainSchedule?.Groups.Length ?? 0;

            CommandBufferCacheVariant variant = GetOrCreateCommandBufferVariant(
                imageIndex,
                frameOpsSignature,
                dynamicUiBatchTextSignature,
                dynamicUiBatchTextOps.Length,
                commandChainSchedule,
                commandChainPrimaryGroupSignature,
                commandChainPrimaryGroupCount,
                preserveSwapchainForOverlay,
                ops);
            if (imageForcedDirty)
                MarkCommandBufferVariantsDirty(imageIndex, "image-forced-dirty");

            string? forcedVariantDirtyReason = variant.DirtyReason;
            bool dirty = imageForcedDirty || variant.Dirty;
            bool forcedDirty = dirty;
            bool usingCommandChains = commandChainSchedule is not null;
            bool hasTextureUploadFrameOps = hasStaticFrameOps && HasTextureUploadFrameOps(ops);
            bool frameOpsRequireFreshPrimary = hasStaticFrameOps &&
                (!VulkanPrimaryCommandBufferReuseEnabled || hasMutableGpuDrivenFrameOps);
            CommandRecordingDependencyMismatch dependencyMismatch =
                variant.RecordedDependencySignature.Compare(currentDependencySignature);

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.DirtyEvaluation"))
            {
                if (!dirty && frameOpsRequireFreshPrimary)
                {
                    dirty = true;
                    primaryFrameStateDirty = true;
                    primaryFrameStateDirtyReason = hasMutableGpuDrivenFrameOps
                        ? "mutable-gpu-driven-frame-ops"
                        : "reuse-disabled";
                }

                if (gpuProfilerCommandBufferStateDirty)
                    profilerDirty = true;

                if (!dirty && dependencyMismatch.RequiresRecording)
                {
                    dirty = true;
                    if (dependencyMismatch.InvalidationClass == CommandRecordingInvalidationClass.Structural)
                        frameOpSignatureDirty = true;
                    else
                        plannerDirty = true;
                }

                if (!dirty && !usingCommandChains && hasFrameOps && variant.FrameOpsSignature != frameOpsSignature)
                {
                    LogFrameOpSignatureDiff(imageIndex, variant, frameOpsSignature, ops);
                    dirty = true;
                    frameOpSignatureDirty = true;
                }

                if (!dirty && usingCommandChains && variant.FrameOpsSignature != frameOpsSignature)
                {
                    LogFrameOpSignatureDiff(imageIndex, variant, frameOpsSignature, ops);
                    dirty = true;
                    frameOpSignatureDirty = true;
                }

                if (!dirty && !usingCommandChains && variant.PlannerRevision != plannerRevision)
                {
                    dirty = true;
                    plannerDirty = true;
                }

                // An inline desktop primary owns the swapchain writer and must be re-recorded
                // for the output viewport's camera transitions. Command-chain primaries only
                // contain their thin scheduling/volatile ranges; their per-draw camera data is
                // refreshed through the reusable secondary ranges instead.
                if (!dirty &&
                    !usingCommandChains &&
                    variant.RecordedGenerations.CameraPose != currentGenerations.CameraPose)
                {
                    dirty = true;
                    frameDataDirty = true;
                    _lastReusableFrameDataRefreshFailureReason = "inline primary camera pose changed";
                }

                if (!dirty && IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature))
                {
                    dirty = true;
                    primaryFrameStateDirty = true;
                    primaryFrameStateDirtyReason = variant.RecordedImageLayoutEndState is null
                        ? "missing-layout-state"
                        : "image-layout-start";
                }

                if (!dirty && variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresentedAtRecord)
                {
                    dirty = true;
                    swapchainLifecycleDirty = true;
                }

                if (!dirty &&
                    requiresTrackedPresentSourceRefresh &&
                    !variant.RecordedSwapchainRefreshFromLastPresentSource)
                {
                    dirty = true;
                    swapchainLifecycleDirty = true;
                }

                if (!dirty &&
                    !usingCommandChains &&
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    dirty = true;
                    profilerDirty = true;
                }

                if (!dirty &&
                    !delayDynamicUiBatchTextOverlayRecording &&
                    IsDynamicUiBatchTextSecondaryDirty(variant, dynamicUiBatchTextSignature))
                {
                    dirty = true;
                    dynamicUiDirty = true;
                }

                if (!dirty && usingCommandChains && IsDynamicUiBatchTextPrimaryStructureDirty(variant, dynamicUiBatchTextOps.Length))
                {
                    dirty = true;
                    dynamicUiDirty = true;
                }

                if (!dirty && commandChainSchedule is not null)
                {
                    commandChainPrimaryDirtyReason = EvaluatePrimaryCommandBufferDirtyReason(
                        commandChainSchedule,
                        variant.CommandChainScheduleSignature,
                        variant.CommandChainPrimaryGroupSignature,
                        variant.CommandChainPrimaryGroupCount,
                        commandChainPrimaryGroupSignature,
                        variant.PlannerRevision,
                        variant.GpuProfilerActive,
                        variant.GpuProfilerFrameSlot,
                        gpuPipelineProfilingActive,
                        commandBufferImageSlot);

                    if (commandChainPrimaryDirtyReason != PrimaryCommandBufferDirtyReason.None)
                    {
                        dirty = true;
                        commandChainPrimaryDirty = true;
                    }
                }
            }

            if (!dirty)
            {
                bool refreshedReusableFrameData = true;
                _lastReusableFrameDataRefreshFailureReason = null;
                using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.FrameDataRefresh))
                {
                    refreshedReusableFrameData = !hasStaticFrameOps ||
                        TryRefreshReusableCommandBufferFrameData(imageIndex, ops, EVulkanMeshFrameDataStreamKind.Primary);
                    if (refreshedReusableFrameData && dynamicUiBatchTextOps.Length > 0)
                        refreshedReusableFrameData = TryRefreshReusableCommandBufferFrameData(imageIndex, dynamicUiBatchTextOps, EVulkanMeshFrameDataStreamKind.DynamicUi);
                }

                if (!refreshedReusableFrameData)
                {
                    dirty = true;
                    frameDataDirty = true;
                }
                else
                {
                    if (hasQueryFrameOps &&
                        (!PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops) ||
                         !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, dynamicUiBatchTextOps)))
                    {
                        dirty = true;
                        primaryFrameStateDirty = true;
                        primaryFrameStateDirtyReason = "query-pool-prepare";
                    }

                    if (dirty)
                        goto ReuseRejected;

                    bool dynamicUiSecondaryReady = true;
                    if (!delayDynamicUiBatchTextOverlayRecording)
                    {
                        using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.RecordDynamicUiSecondary"))
                        using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording))
                            dynamicUiSecondaryReady = RecordDynamicUiBatchTextSecondaryCommandBuffer(
                                imageIndex,
                                variant,
                                dynamicUiBatchTextOps,
                                dynamicUiBatchTextSignature);
                    }
                    else
                    {
                        variant.DynamicUiSecondaryRecorded = false;
                    }

                    if (dynamicUiBatchTextOps.Length > 0 &&
                        !delayDynamicUiBatchTextOverlayRecording &&
                        !dynamicUiSecondaryReady)
                    {
                        dirty = true;
                        dynamicUiDirty = true;
                    }
                    else
                    {
                        StoreFrameOpSignatureDebugParts(variant, ops);
                        if (commandChainSchedule is not null)
                        {
                            variant.CommandChainScheduleSignature = commandChainSchedule.StructuralSignature;
                            variant.CommandChainPrimaryGroupSignature = commandChainPrimaryGroupSignature;
                            variant.CommandChainPrimaryGroupCount = commandChainPrimaryGroupCount;
                        }
                        variant.PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
                        variant.RecordedGenerations = currentGenerations;
                        variant.RecordedDependencySignature = currentDependencySignature;
                        variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
                        variant.RecordedFrameOpContextId = frameOpContextId;
                        variant.LastUsedFrameId = VulkanFrameCounter;
                        variant.DirtyReason = null;
                        SetActiveCommandBufferVariant(imageIndex, variant);
                        RestoreRecordedImageLayoutEndState(variant);
                        PrepareVulkanGpuProfilerReusableSubmission(
                            commandBufferImageSlot,
                            variant,
                            gpuPipelineProfilingActive);
                        UpdateVulkanGpuProfilerCommandBufferState(
                            imageIndex,
                            gpuPipelineProfilingActive,
                            commandBufferImageSlot);
                        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                            reusedClean: true,
                            recorded: false,
                            forcedDirty: false,
                            frameOpSignatureDirty: false,
                            plannerDirty: false,
                            profilerDirty: false,
                            dirtyReason: null,
                            structuralSignature: currentGenerations.Structural,
                            descriptorGeneration: currentGenerations.Descriptor,
                            swapchainSlot: commandBufferImageSlot);
                        if (commandChainSchedule is not null)
                        {
                            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
                                primaryCommandBuffersReused: 1);
                        }
                        swapchainLayoutAfterCommandBuffer = variant.RecordedSwapchainFinalLayout;
                        if (dynamicUiSecondaryReady)
                        {
                            dynamicUiBatchTextSecondaryCommandBuffer = variant.DynamicUiSecondaryCommandBuffer;
                            dynamicUiBatchTextOverlayOpCount = dynamicUiBatchTextOps.Length;
                            if (delayDynamicUiBatchTextOverlayRecording)
                            {
                                dynamicUiBatchTextOverlayOps = dynamicUiBatchTextOps;
                                dynamicUiBatchTextOverlaySignature = dynamicUiBatchTextSignature;
                                dynamicUiBatchTextOverlayVariant = variant;
                            }
                        }
                        EnsureCommandBufferVariantContextBeforeSubmit(
                            imageIndex,
                            variant,
                            frameOpContextFingerprint,
                            frameOpContextId,
                            usingCommandChains ? "primary-command-chain" : "primary");
                        return variant.PrimaryCommandBuffer;
                    }
                }
            }

        ReuseRejected:

            string? dirtyReason = VulkanFrameDiagnosticsTraceEnabled
                ? DescribePrimaryReuseMiss(
                    variant,
                    currentGenerations,
                    dependencyMismatch,
                    forcedDirty,
                    imageForcedDirty,
                    forcedVariantDirtyReason,
                    frameOpSignatureDirty,
                    plannerDirty,
                    profilerDirty,
                    frameDataDirty,
                    dynamicUiDirty,
                    swapchainLifecycleDirty,
                    commandChainPrimaryDirty,
                    commandChainPrimaryDirtyReason,
                    commandChainSchedule?.StructuralSignature ?? ulong.MaxValue,
                    commandChainPrimaryGroupSignature,
                    commandChainPrimaryGroupCount,
                    primaryFrameStateDirty,
                    primaryFrameStateDirtyReason,
                    plannerRevision,
                    imageLayoutStartSignature,
                    swapchainImageEverPresentedAtRecord)
                : null;

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                reusedClean: false,
                recorded: true,
                forcedDirty,
                frameOpSignatureDirty,
                plannerDirty,
                profilerDirty,
                dirtyReason,
                detailReasons:
                    (frameDataDirty ? EVulkanCommandBufferDecisionReason.FrameData : 0) |
                    (dynamicUiDirty ? EVulkanCommandBufferDecisionReason.DynamicOverlay : 0) |
                    (swapchainLifecycleDirty ? EVulkanCommandBufferDecisionReason.SwapchainLifecycle : 0) |
                    (commandChainPrimaryDirty ? EVulkanCommandBufferDecisionReason.CommandChainPrimary : 0) |
                    (primaryFrameStateDirty ? EVulkanCommandBufferDecisionReason.PrimaryFrameState : 0) |
                    (variant.RecordedGenerations.Descriptor != currentGenerations.Descriptor ? EVulkanCommandBufferDecisionReason.DescriptorGeneration : 0) |
                    (variant.RecordedGenerations.ResourceAllocation != currentGenerations.ResourceAllocation ? EVulkanCommandBufferDecisionReason.ResourceAllocation : 0),
                structuralSignature: currentGenerations.Structural,
                descriptorGeneration: currentGenerations.Descriptor,
                swapchainSlot: commandBufferImageSlot);
            if (commandChainSchedule is not null)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
                    primaryCommandBuffersRecorded: 1);
            }

            _lastEnsureCommandBufferRecordedPrimary = true;
            _isRecordingCommandBuffer = true;
            bool recordedDynamicUiSecondaryReady = delayDynamicUiBatchTextOverlayRecording;
            int recordedSwapchainWriteCount = 0;
            bool queryFrameOpsRequireRerecord = false;
            try
            {
                if (!delayDynamicUiBatchTextOverlayRecording)
                {
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.RecordDynamicUiSecondary"))
                    using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording))
                        recordedDynamicUiSecondaryReady = RecordDynamicUiBatchTextSecondaryCommandBuffer(
                            imageIndex,
                            variant,
                            dynamicUiBatchTextOps,
                            dynamicUiBatchTextSignature);
                }
                else
                {
                    variant.DynamicUiSecondaryRecorded = false;
                }

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.RecordPrimary"))
                {
                    using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.PrimaryRecording);
                    bool primaryRecorded = false;
                    for (int recordingAttempt = 0; recordingAttempt < 2; recordingAttempt++)
                    {
                        primaryRecorded = TryRecordCommandBuffer(
                            imageIndex,
                            variant.PrimaryCommandBuffer,
                            variant.DynamicUiSecondaryCommandBuffer,
                            ops,
                            recordedDynamicUiSecondaryReady && !preserveSwapchainForOverlay ? dynamicUiBatchTextOps.Length : 0,
                            commandChainSchedule,
                            preserveSwapchainForOverlay,
                            out recordedSwapchainWriteCount,
                            out swapchainLayoutAfterCommandBuffer,
                            out recordingDeferredReason,
                            out queryFrameOpsRequireRerecord);
                        if (primaryRecorded)
                            break;

                        if (recordingAttempt != 0 ||
                            !IsTransientResourceRetirementRecordingFailure(recordingDeferredReason) ||
                            IsSwapchainResourceRetirementRecordingFailure(recordingDeferredReason))
                        {
                            break;
                        }

                        CancelRecordedTextureUploadSubmitBatch(
                            "command buffer resource generation retired during recording retry");
                        VulkanSwapchainDepthResources? currentDepth = CurrentSwapchainDepthResources;
                        Debug.VulkanWarningEvery(
                            $"Vulkan.Primary.RetryRetiredResource.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Retrying primary command recording immediately because a resource generation retired during the first attempt: {0} CurrentSwapchainDepth=0x{1:X}/generation={2}",
                            recordingDeferredReason,
                            currentDepth?.Image.Handle ?? 0,
                            GetCurrentVulkanResourceGeneration(ObjectType.Image, currentDepth?.Image.Handle ?? 0));
                    }

                    if (!primaryRecorded)
                    {
                        _lastEnsureCommandBufferRecordedPrimary = false;
                        CancelRecordedTextureUploadSubmitBatch(
                            "command buffer recording deferred before or during recording");
                        FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                        return default;
                    }
                }
            }
            catch
            {
                CancelRecordedTextureUploadSubmitBatch("command buffer recording failed before upload submit");
                FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps);
                throw;
            }
            finally
            {
                _isRecordingCommandBuffer = false;
            }
            _commandBufferDirtyFlags[imageIndex] = false;
            variant.Dirty = false;
            variant.DirtyReason = null;
            variant.FrameOpsSignature = frameOpsSignature;
            variant.DynamicUiSignature = recordedDynamicUiSecondaryReady && !delayDynamicUiBatchTextOverlayRecording
                ? dynamicUiBatchTextSignature
                : 0;
            variant.DynamicUiOpCount = recordedDynamicUiSecondaryReady ? dynamicUiBatchTextOps.Length : 0;
            variant.PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
            variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
            variant.RecordedFrameOpContextId = frameOpContextId;
            variant.RecordedResourceGeneration = commandBufferFallbackContext.ResourceGeneration;
            variant.RecordedDescriptorGeneration = commandBufferFallbackContext.DescriptorGeneration;
            variant.RecordedGenerations = currentGenerations;
            variant.RecordedDependencySignature = currentDependencySignature;
            variant.RecordedSwapchainImageEverPresented = swapchainImageEverPresentedAtRecord;
            variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
            variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
            variant.RecordedSwapchainRefreshFromLastPresentSource =
                requiresTrackedPresentSourceRefresh &&
                recordedSwapchainWriteCount > 0;
            variant.RecordedImageLayoutStartSignature = imageLayoutStartSignature;
            CaptureCommandBufferVariantImageLayoutEndState(variant);
            variant.CommandChainScheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
            variant.CommandChainPrimaryGroupSignature = commandChainSchedule is null || commandChainCache is null
                ? ulong.MaxValue
                : ComputePrimaryCommandBufferGroupSignature(commandChainSchedule, commandChainCache);
            variant.CommandChainPrimaryGroupCount = commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount;
            variant.PlannerRevision = plannerRevision;
            variant.GpuProfilerActive = gpuPipelineProfilingActive;
            variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;
            CaptureVulkanGpuProfilerVariantScopes(commandBufferImageSlot, variant);
            variant.LastUsedFrameId = VulkanFrameCounter;
            StoreFrameOpSignatureDebugParts(variant, ops);
            SetActiveCommandBufferVariant(imageIndex, variant);
            UpdateVulkanGpuProfilerCommandBufferState(
                imageIndex,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            if (hasTextureUploadFrameOps)
                MarkCommandBufferVariantTransient(variant, "transient texture upload");
            if (queryFrameOpsRequireRerecord)
                MarkCommandBufferVariantTransient(variant, "query draw was not recorded");
            if (recordedDynamicUiSecondaryReady)
            {
                dynamicUiBatchTextSecondaryCommandBuffer = variant.DynamicUiSecondaryCommandBuffer;
                dynamicUiBatchTextOverlayOpCount = dynamicUiBatchTextOps.Length;
                if (delayDynamicUiBatchTextOverlayRecording)
                {
                    dynamicUiBatchTextOverlayOps = dynamicUiBatchTextOps;
                    dynamicUiBatchTextOverlaySignature = dynamicUiBatchTextSignature;
                    dynamicUiBatchTextOverlayVariant = variant;
                }
            }
            commandBufferDirtyGenerationAfterRecord = SnapshotCommandBufferDirtyGeneration();
            if (HaveCommandBuffersDirtiedSince(ensureStartDirtyGeneration))
                MarkCommandBufferVariantDirtyAfterConcurrentInvalidation(variant);
            EnsureCommandBufferVariantContextBeforeSubmit(
                imageIndex,
                variant,
                frameOpContextFingerprint,
                frameOpContextId,
                usingCommandChains ? "recorded-primary-command-chain" : "recorded-primary");
            return variant.PrimaryCommandBuffer;
        }

        private bool TryReuseLastSwapchainWriterVariant(
            uint imageIndex,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            ulong plannerRevision,
            ulong imageLayoutStartSignature,
            bool swapchainImageEverPresented,
            bool gpuPipelineProfilingActive,
            int commandBufferImageSlot,
            out CommandBuffer commandBuffer,
            out ImageLayout swapchainLayoutAfterCommandBuffer)
        {
            commandBuffer = default;
            swapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;

            if (_commandBufferVariants is null ||
                imageIndex >= _commandBufferVariants.Length)
            {
                return false;
            }

            List<CommandBufferCacheVariant> variants = _commandBufferVariants[imageIndex];
            CommandBufferCacheVariant? best = null;
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.Dirty ||
                    variant.RecordedSwapchainWriteCount <= 0 ||
                    variant.RecordedSwapchainFinalLayout != ImageLayout.PresentSrcKhr ||
                    !TryValidateCommandBufferVariantContext(
                        imageIndex,
                        variant,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        "last-swapchain-writer") ||
                    variant.PlannerRevision != plannerRevision ||
                    IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature) ||
                    variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented ||
                    variant.PreserveSwapchainForOverlay ||
                    variant.PrimaryCommandBuffer.Handle == 0 ||
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    continue;
                }

                if (best is null || variant.LastUsedFrameId > best.LastUsedFrameId)
                    best = variant;
            }

            if (best is null)
                return false;

            best.LastUsedFrameId = VulkanFrameCounter;
            best.DirtyReason = null;
            SetActiveCommandBufferVariant(imageIndex, best);
            RestoreRecordedImageLayoutEndState(best);
            PrepareVulkanGpuProfilerReusableSubmission(
                commandBufferImageSlot,
                best,
                gpuPipelineProfilingActive);
            UpdateVulkanGpuProfilerCommandBufferState(
                imageIndex,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                reusedClean: true,
                recorded: false,
                forcedDirty: false,
                frameOpSignatureDirty: false,
                plannerDirty: false,
                profilerDirty: false,
                dirtyReason: null);

            EnsureCommandBufferVariantContextBeforeSubmit(
                imageIndex,
                best,
                frameOpContextFingerprint,
                frameOpContextId,
                "last-swapchain-writer");
            commandBuffer = best.PrimaryCommandBuffer;
            swapchainLayoutAfterCommandBuffer = best.RecordedSwapchainFinalLayout;
            return true;
        }

        private static bool HasQueryFrameOps(FrameOp[] ops)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (ops[i] is QueryOp)
                    return true;
            }

            return false;
        }

        private static void MarkCommandBufferVariantTransient(CommandBufferCacheVariant variant, string reason)
        {
            // The command buffer recorded immediately before this call is still the current
            // submit candidate. Transient means "record again next time"; erasing its recorded
            // context/dependency metadata here makes the current pre-submit guard reject a
            // command buffer that was just recorded successfully.
            variant.Dirty = true;
            variant.DirtyReason = reason;
        }

        private static void MarkCommandBufferVariantDirtyAfterConcurrentInvalidation(CommandBufferCacheVariant variant)
        {
            variant.Dirty = true;
            variant.DirtyReason = "concurrent invalidation during primary record";
        }

        private CommandBufferGenerationDomains CaptureCommandBufferGenerationDomains(
            uint imageIndex,
            ulong structuralSignature,
            FrameOp[] staticOps,
            FrameOp[] volatileOps,
            ulong overlaySignature,
            in FrameOpContext context,
            ulong frameOpContextFingerprint,
            bool profilerActive,
            int profilerFrameSlot)
            => new(
                Structural: structuralSignature,
                FrameData: ComputeFrameDataGeneration(staticOps, volatileOps),
                CameraPose: ResolveCameraPoseReplayGeneration(
                    frameOpContextFingerprint,
                    ComputeCameraPoseGeneration(staticOps, volatileOps, context)),
                TargetSlot: imageIndex + 1UL,
                Descriptor: context.DescriptorGeneration,
                ResourceAllocation: context.ResourceGeneration,
                Query: ComputeQueryGeneration(staticOps, volatileOps),
                Overlay: overlaySignature,
                Profiler: ((profilerActive ? 1UL : 0UL) << 32) | unchecked((uint)(profilerFrameSlot + 1)));

        private static CommandRecordingDependencySignature CaptureCommandRecordingDependencySignature(
            uint imageIndex,
            int frameSlot,
            ulong resourcePlanGeneration,
            ulong staticStructuralSignature,
            ulong volatileSuffixSignature,
            in FrameOpContext context,
            in CommandBufferGenerationDomains generations,
            FrameOp[] preparedStaticOps,
            ulong sharedGraphicsPipelineGeneration)
        {
            FrameOpSignatureHasher renderAreaHash = new();
            renderAreaHash.Add(context.DisplayWidth);
            renderAreaHash.Add(context.DisplayHeight);
            renderAreaHash.Add(context.InternalWidth);
            renderAreaHash.Add(context.InternalHeight);

            uint viewMask = context.MultiviewEnabled
                ? 0x3u
                : 0x1u;
            FrameOpSignatureHasher inheritanceHash = new();
            inheritanceHash.Add((int)context.ContextKind);
            inheritanceHash.Add(context.PipelineIdentity);
            inheritanceHash.Add(context.ViewportIdentity);
            inheritanceHash.Add(context.OutputFrameBufferIdentity);
            inheritanceHash.Add(context.StereoEnabled);
            inheritanceHash.Add(context.MultiviewEnabled);
            inheritanceHash.Add(ComputePassMetadataSignature(context.PassMetadata));
            ulong inheritanceSignature = inheritanceHash.ToHash();

            FrameOpSignatureHasher descriptorBindingHash = new();
            descriptorBindingHash.Add(ResolveFrameOpContextResourceRegistrySignature(context));
            descriptorBindingHash.Add(ComputePassMetadataSignature(context.PassMetadata));
            ulong descriptorBindingIdentity = descriptorBindingHash.ToHash();

            CapturePreparedBindingIdentities(
                preparedStaticOps,
                out ulong meshBindingIdentity,
                out ulong indexBufferBindingIdentity,
                out ulong vertexBufferBindingIdentity,
                out ulong preparedProgramIdentity);

            return new CommandRecordingDependencySignature(
                OutputPassAttachment: staticStructuralSignature,
                RenderArea: renderAreaHash.ToHash(),
                ViewMask: viewMask,
                QueueFamily: context.SubmissionQueueFamily,
                DynamicRenderingInheritance: inheritanceSignature,
                PipelineGeneration: sharedGraphicsPipelineGeneration,
                PipelineLayoutGeneration: preparedProgramIdentity,
                MeshBindingIdentity: meshBindingIdentity,
                IndexBufferBindingIdentity: indexBufferBindingIdentity,
                VertexBufferBindingIdentity: vertexBufferBindingIdentity,
                BufferAllocationGeneration: generations.ResourceAllocation,
                ImageAllocationGeneration: unchecked((ulong)(uint)ResolveFrameOpContextResourceRegistrySignature(context)),
                ImageViewGeneration: unchecked((ulong)(uint)context.OutputFrameBufferIdentity),
                // Immutable descriptor-set and sampler identity remain separate from
                // publication generation. The dependency classifier still treats a
                // publication change as binding state because ordinary descriptor
                // writes invalidate recorded command buffers.
                SamplerAllocationGeneration: descriptorBindingIdentity,
                DescriptorLayoutGeneration: unchecked((ulong)(uint)ComputePassMetadataSignature(context.PassMetadata)),
                DescriptorSetGeneration: descriptorBindingIdentity,
                ResourcePlanGeneration: resourcePlanGeneration,
                ExternalTargetVariant: imageIndex,
                FrameSlotVariant: frameSlot,
                DescriptorPublicationGeneration: generations.Descriptor,
                DataPublicationGeneration: generations.FrameData,
                VolatileSuffixGeneration: volatileSuffixSignature);
        }

        private static void CapturePreparedBindingIdentities(
            FrameOp[] ops,
            out ulong meshIdentity,
            out ulong indexIdentity,
            out ulong vertexIdentity,
            out ulong programIdentity)
        {
            FrameOpSignatureHasher meshHash = new();
            FrameOpSignatureHasher indexHash = new();
            FrameOpSignatureHasher vertexHash = new();
            FrameOpSignatureHasher programHash = new();
            for (int i = 0; i < ops.Length; i++)
            {
                PendingMeshDraw draw = ops[i] switch
                {
                    MeshDrawOp direct => direct.Draw,
                    IndirectDrawOp indirect => indirect.Draw,
                    _ => default,
                };
                if (draw.Renderer is not { } renderer)
                    continue;

                int rendererIdentity = RuntimeHelpers.GetHashCode(renderer);
                int meshObjectIdentity = renderer.Mesh is null ? 0 : RuntimeHelpers.GetHashCode(renderer.Mesh);
                meshHash.Add(rendererIdentity);
                meshHash.Add(meshObjectIdentity);
                indexHash.Add(meshObjectIdentity);
                indexHash.Add((int)(renderer.Mesh?.Type ?? EPrimitiveType.Triangles));
                vertexHash.Add(rendererIdentity);
                vertexHash.Add(renderer.Mesh?.Buffers is null ? 0 : RuntimeHelpers.GetHashCode(renderer.Mesh.Buffers));
                programHash.Add(draw.PreparedProgramIdentity);
                programHash.Add(draw.PreparedProgram?.BindingId ?? 0u);
            }

            meshIdentity = meshHash.ToHash();
            indexIdentity = indexHash.ToHash();
            vertexIdentity = vertexHash.ToHash();
            programIdentity = programHash.ToHash();
        }

        private ulong ResolveCameraPoseReplayGeneration(ulong contextFingerprint, ulong rawPoseGeneration)
        {
            if (rawPoseGeneration == 0)
                return 0;

            ref CameraPoseReuseState? state = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _cameraPoseReuseStates,
                contextFingerprint,
                out bool exists);
            state ??= new CameraPoseReuseState
            {
                RawPoseGeneration = rawPoseGeneration,
                LastObservedFrame = VulkanFrameCounter,
            };

            if (!exists)
                return CombineCameraPoseReplayGeneration(rawPoseGeneration, state.ReplayGeneration);

            ulong frame = VulkanFrameCounter;
            if (state.LastObservedFrame == frame)
                return CombineCameraPoseReplayGeneration(state.RawPoseGeneration, state.ReplayGeneration);

            state.LastObservedFrame = frame;
            if (state.RawPoseGeneration != rawPoseGeneration)
            {
                state.RawPoseGeneration = rawPoseGeneration;
                state.ReplayGeneration++;
                state.SettleInvalidationPending = true;
            }
            else if (state.SettleInvalidationPending)
            {
                // Previous-camera matrices and temporal history converge on the first frame after
                // input stops. Advance the replay generation once more so no inline primary from
                // the final moving frame can be selected for that boundary frame.
                state.ReplayGeneration++;
                state.SettleInvalidationPending = false;
            }

            return CombineCameraPoseReplayGeneration(state.RawPoseGeneration, state.ReplayGeneration);
        }

        private static ulong CombineCameraPoseReplayGeneration(ulong rawPoseGeneration, ulong replayGeneration)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(rawPoseGeneration);
            hash.Add(replayGeneration);
            return hash.ToHash();
        }

        private static ulong ComputeCameraPoseGeneration(
            FrameOp[] staticOps,
            FrameOp[] volatileOps,
            in FrameOpContext outputContext)
        {
            // A primary-cache camera transition only concerns the camera that owns the output
            // viewport. Shadow/capture cameras can legitimately move while the desktop view is
            // stationary; including them makes the swapchain primary re-record every frame.
            // Visibility and query-probe work can also change draw order/count without moving
            // that camera, so preserve only a deduplicated, order-independent pose set.
            Span<ulong> uniqueCameraPoseSignatures = stackalloc ulong[128];
            int uniqueCameraPoseCount = 0;
            bool exceededInlineCapacity = false;
            AddCameraPoseGenerationParts(
                staticOps,
                outputContext.ViewportIdentity,
                uniqueCameraPoseSignatures,
                ref uniqueCameraPoseCount,
                ref exceededInlineCapacity);
            AddCameraPoseGenerationParts(
                volatileOps,
                outputContext.ViewportIdentity,
                uniqueCameraPoseSignatures,
                ref uniqueCameraPoseCount,
                ref exceededInlineCapacity);

            if (uniqueCameraPoseCount == 0)
                return 0UL;

            if (exceededInlineCapacity)
            {
                return ComputeCameraPoseGenerationConservatively(
                    staticOps,
                    volatileOps,
                    outputContext.ViewportIdentity);
            }

            SortCameraPoseSignatures(uniqueCameraPoseSignatures, uniqueCameraPoseCount);
            FrameOpSignatureHasher hash = new();
            hash.Add(uniqueCameraPoseCount);
            for (int i = 0; i < uniqueCameraPoseCount; i++)
                hash.Add(uniqueCameraPoseSignatures[i]);
            return hash.ToHash();
        }

        private static void AddCameraPoseGenerationParts(
            FrameOp[] ops,
            int outputViewportIdentity,
            Span<ulong> uniqueCameraPoseSignatures,
            ref int uniqueCameraPoseCount,
            ref bool exceededInlineCapacity)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (!TryGetPrimaryViewportCameraPoseDraw(
                        ops[i],
                        outputViewportIdentity,
                        out PendingMeshDraw draw))
                {
                    continue;
                }

                ulong signature = ComputeCameraPoseSignature(draw);
                bool alreadyCaptured = false;
                for (int poseIndex = 0; poseIndex < uniqueCameraPoseCount; poseIndex++)
                {
                    if (uniqueCameraPoseSignatures[poseIndex] != signature)
                        continue;

                    alreadyCaptured = true;
                    break;
                }

                if (alreadyCaptured)
                    continue;

                if (uniqueCameraPoseCount >= uniqueCameraPoseSignatures.Length)
                {
                    exceededInlineCapacity = true;
                    continue;
                }

                uniqueCameraPoseSignatures[uniqueCameraPoseCount++] = signature;
            }
        }

        private static ulong ComputeCameraPoseGenerationConservatively(
            FrameOp[] staticOps,
            FrameOp[] volatileOps,
            int outputViewportIdentity)
        {
            FrameOpSignatureHasher hash = new();
            int cameraDrawCount = 0;
            AddCameraPoseGenerationPartsConservatively(
                ref hash,
                staticOps,
                outputViewportIdentity,
                ref cameraDrawCount);
            AddCameraPoseGenerationPartsConservatively(
                ref hash,
                volatileOps,
                outputViewportIdentity,
                ref cameraDrawCount);
            return cameraDrawCount == 0 ? 0UL : hash.ToHash();
        }

        private static void AddCameraPoseGenerationPartsConservatively(
            ref FrameOpSignatureHasher hash,
            FrameOp[] ops,
            int outputViewportIdentity,
            ref int cameraDrawCount)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (!TryGetPrimaryViewportCameraPoseDraw(
                        ops[i],
                        outputViewportIdentity,
                        out PendingMeshDraw draw))
                {
                    continue;
                }

                cameraDrawCount++;
                hash.Add(ComputeCameraPoseSignature(draw));
            }
        }

        private static bool TryGetPrimaryViewportCameraPoseDraw(
            FrameOp op,
            int outputViewportIdentity,
            out PendingMeshDraw draw)
        {
            switch (op)
            {
                case MeshDrawOp meshDraw when IsCameraAttachedToOutputViewport(
                    meshDraw.Draw.Camera,
                    outputViewportIdentity):
                    draw = meshDraw.Draw;
                    return true;
                case IndirectDrawOp indirectDraw when IsCameraAttachedToOutputViewport(
                    indirectDraw.Draw.Camera,
                    outputViewportIdentity):
                    draw = indirectDraw.Draw;
                    return true;
                default:
                    draw = default;
                    return false;
            }
        }

        private static bool IsCameraAttachedToOutputViewport(
            XRCamera? camera,
            int outputViewportIdentity)
        {
            if (camera is null)
                return false;

            if (outputViewportIdentity == 0)
                return true;

            int viewportCount = camera.Viewports.Count;
            if (viewportCount == 0)
                return true;

            for (int i = 0; i < viewportCount; i++)
            {
                XRViewport viewport = camera.Viewports[i];
                if (RuntimeHelpers.GetHashCode(viewport) != outputViewportIdentity)
                    continue;

                return viewport.RenderPipeline?.IsShadowPass != true;
            }

            return false;
        }

        private static ulong ComputeCameraPoseSignature(in PendingMeshDraw draw)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(draw.Camera is null ? 0 : RuntimeHelpers.GetHashCode(draw.Camera));
            hash.Add(draw.StereoRightEyeCamera is null ? 0 : RuntimeHelpers.GetHashCode(draw.StereoRightEyeCamera));
            hash.Add(draw.IsStereoPass);
            hash.Add(draw.UseUnjitteredProjection);
            AddVector3Signature(ref hash, draw.CameraPosition);
            AddVector3Signature(ref hash, draw.CameraForward);
            AddVector3Signature(ref hash, draw.CameraUp);
            AddVector3Signature(ref hash, draw.CameraRight);
            return hash.ToHash();
        }

        private static void SortCameraPoseSignatures(Span<ulong> signatures, int count)
        {
            for (int i = 1; i < count; i++)
            {
                ulong value = signatures[i];
                int insertionIndex = i - 1;
                while (insertionIndex >= 0 && signatures[insertionIndex] > value)
                {
                    signatures[insertionIndex + 1] = signatures[insertionIndex];
                    insertionIndex--;
                }

                signatures[insertionIndex + 1] = value;
            }
        }

        private static ulong ComputeFrameDataGeneration(FrameOp[] staticOps, FrameOp[] volatileOps)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(staticOps.Length);
            for (int i = 0; i < staticOps.Length; i++)
                hash.Add(ComputeFrameOpFrameDataSignature(staticOps[i], i));
            hash.Add(volatileOps.Length);
            for (int i = 0; i < volatileOps.Length; i++)
                hash.Add(ComputeFrameOpFrameDataSignature(volatileOps[i], i));
            return hash.ToHash();
        }

        private static ulong ComputeQueryGeneration(FrameOp[] staticOps, FrameOp[] volatileOps)
        {
            FrameOpSignatureHasher hash = new();
            int queryCount = 0;
            hash.Add(staticOps.Length);
            AddQueryGenerationParts(ref hash, staticOps, ref queryCount);
            hash.Add(volatileOps.Length);
            AddQueryGenerationParts(ref hash, volatileOps, ref queryCount);
            return queryCount == 0 ? 0UL : hash.ToHash();
        }

        private static void AddQueryGenerationParts(
            ref FrameOpSignatureHasher hash,
            FrameOp[] ops,
            ref int queryCount)
        {
            int queryBracketDepth = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                if (op is QueryOp query)
                {
                    queryCount++;
                    hash.Add(i);
                    hash.Add(ComputeFrameOpStructuralSignature(
                        query,
                        i,
                        RenderPacketVolatility.FrameDataOnly));

                    if (query.Operation == ERenderQueryOperation.Begin)
                        queryBracketDepth++;
                    else if (query.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                        queryBracketDepth--;
                    continue;
                }

                // Query frame ops are intentionally omitted from command-chain groups and
                // stay inline in the primary. Include the exact bracket position and every
                // enclosed draw in the primary-cache identity so a previous layout cannot
                // attribute a proxy draw to the wrong query object.
                if (queryBracketDepth > 0)
                {
                    hash.Add(i);
                    hash.Add(ComputeFrameOpStructuralSignature(
                        op,
                        i,
                        RenderPacketVolatility.FrameDataOnly));
                }
            }
        }

        private string DescribePrimaryReuseMiss(
            CommandBufferCacheVariant variant,
            in CommandBufferGenerationDomains current,
            in CommandRecordingDependencyMismatch dependencyMismatch,
            bool forcedDirty,
            bool imageForcedDirty,
            string? forcedVariantDirtyReason,
            bool frameOpSignatureDirty,
            bool plannerDirty,
            bool profilerDirty,
            bool frameDataDirty,
            bool dynamicUiDirty,
            bool swapchainLifecycleDirty,
            bool commandChainPrimaryDirty,
            PrimaryCommandBufferDirtyReason commandChainPrimaryDirtyReason,
            ulong commandChainScheduleSignature,
            ulong commandChainPrimaryGroupSignature,
            int commandChainPrimaryGroupCount,
            bool primaryFrameStateDirty,
            string? primaryFrameStateDirtyReason,
            ulong plannerRevision,
            ulong imageLayoutStartSignature,
            bool swapchainImageEverPresented)
        {
            CommandBufferGenerationDomains previous = variant.RecordedGenerations;
            if (dependencyMismatch.RequiresRecording)
                return $"dependency-signature field={dependencyMismatch.Field} class={dependencyMismatch.InvalidationClass}";
            if (forcedDirty)
            {
                string reason = FormatForcedCommandBufferDirtyReason(
                    imageForcedDirty,
                    variant.Dirty,
                    forcedVariantDirtyReason);
                return $"cache-state old={(variant.Dirty ? "dirty" : "clean")} new=record-required reason={reason}";
            }
            if (frameOpSignatureDirty)
                return $"structural-generation old=0x{previous.Structural:X16} new=0x{current.Structural:X16}";
            if (plannerDirty)
                return $"resource-plan-generation old={variant.PlannerRevision} new={plannerRevision}";
            if (profilerDirty)
                return $"profiler-generation old=0x{previous.Profiler:X16} new=0x{current.Profiler:X16}";
            if (frameDataDirty)
                return $"frame-data-generation old=0x{previous.FrameData:X16} new=0x{current.FrameData:X16} refresh={_lastReusableFrameDataRefreshFailureReason ?? "failed"}";
            if (dynamicUiDirty)
                return $"overlay-generation old=0x{previous.Overlay:X16} new=0x{current.Overlay:X16}";
            if (swapchainLifecycleDirty)
                return $"target-slot-state slot={current.TargetSlot} presented={variant.RecordedSwapchainImageEverPresented}->{swapchainImageEverPresented}";
            if (commandChainPrimaryDirty)
                return DescribePrimaryCommandChainReuseMiss(
                    variant,
                    commandChainPrimaryDirtyReason,
                    commandChainScheduleSignature,
                    commandChainPrimaryGroupSignature,
                    commandChainPrimaryGroupCount,
                    plannerRevision,
                    current.Profiler);
            if (primaryFrameStateDirty)
            {
                if (string.Equals(primaryFrameStateDirtyReason, "query-pool-prepare", StringComparison.Ordinal))
                    return $"query-generation old=0x{previous.Query:X16} new=0x{current.Query:X16}";
                if (string.Equals(primaryFrameStateDirtyReason, "image-layout-start", StringComparison.Ordinal))
                    return $"image-layout-generation old=0x{variant.RecordedImageLayoutStartSignature:X16} new=0x{imageLayoutStartSignature:X16}";
                return $"primary-frame-state old=cached new=record-required field={primaryFrameStateDirtyReason ?? "unknown"}";
            }

            if (previous.Descriptor != current.Descriptor)
                return $"descriptor-generation old={previous.Descriptor} new={current.Descriptor}";
            if (previous.ResourceAllocation != current.ResourceAllocation)
                return $"resource-allocation-generation old={previous.ResourceAllocation} new={current.ResourceAllocation}";
            return "cache-state old=unknown new=record-required reason=unclassified";
        }

        private static string DescribePrimaryCommandChainReuseMiss(
            CommandBufferCacheVariant variant,
            PrimaryCommandBufferDirtyReason reasons,
            ulong scheduleSignature,
            ulong groupSignature,
            int groupCount,
            ulong plannerRevision,
            ulong profilerGeneration)
        {
            if ((reasons & PrimaryCommandBufferDirtyReason.ScheduleStructure) != 0)
                return $"primary-chain-schedule old=0x{variant.CommandChainScheduleSignature:X16} new=0x{scheduleSignature:X16}";
            if ((reasons & PrimaryCommandBufferDirtyReason.GroupStructure) != 0)
                return $"primary-chain-groups old=0x{variant.CommandChainPrimaryGroupSignature:X16}/{variant.CommandChainPrimaryGroupCount} new=0x{groupSignature:X16}/{groupCount}";
            if ((reasons & PrimaryCommandBufferDirtyReason.ResourcePlan) != 0)
                return $"primary-chain-resource-plan old={variant.PlannerRevision} new={plannerRevision}";
            if ((reasons & PrimaryCommandBufferDirtyReason.ProfilerMode) != 0)
                return $"primary-chain-profiler old=0x{variant.RecordedGenerations.Profiler:X16} new=0x{profilerGeneration:X16}";
            return $"primary-chain-state old=clean new=record-required field={PrimaryCommandBufferDirtyReason.None}";
        }

        private static ulong ComputeCommandBufferFrameOpContextFingerprint(
            FrameOp[] ops,
            FrameOp[] dynamicUiBatchTextOps,
            in FrameOpContext fallbackContext)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(0x434D444354584654UL);
            AddFrameOpContextFingerprints(ref hash, ops);
            AddFrameOpContextFingerprints(ref hash, dynamicUiBatchTextOps);
            if (ops.Length == 0 && dynamicUiBatchTextOps.Length == 0)
                hash.Add(fallbackContext.RecordingFingerprint);

            return hash.ToHash();
        }

        private static void AddFrameOpContextFingerprints(ref FrameOpSignatureHasher hash, FrameOp[] ops)
        {
            hash.Add(ops.Length);
            for (int i = 0; i < ops.Length; i++)
            {
                hash.Add(ops[i].Context.RecordingFingerprint);
                hash.Add((int)ops[i].Context.ContextKind);
            }
        }

        private static ulong ResolveCommandBufferFrameOpContextId(FrameOp[] ops, FrameOp[] dynamicUiBatchTextOps, in FrameOpContext fallbackContext)
        {
            if (ops.Length > 0)
                return ops[0].Context.ContextId;
            if (dynamicUiBatchTextOps.Length > 0)
                return dynamicUiBatchTextOps[0].Context.ContextId;
            return fallbackContext.ContextId;
        }

        private static bool IsCommandBufferVariantFrameOpContextDirty(
            CommandBufferCacheVariant variant,
            ulong frameOpContextFingerprint)
            => variant.RecordedFrameOpContextFingerprint != frameOpContextFingerprint;

        private bool TryValidateCommandBufferVariantContext(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            string reusePath)
        {
            if (!IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
                return true;

            LogCommandBufferFrameOpContextMismatch(
                imageIndex,
                variant,
                frameOpContextFingerprint,
                frameOpContextId,
                reusePath);
            return false;
        }

        private void EnsureCommandBufferVariantContextBeforeSubmit(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            string submitPath)
        {
            if (!IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
                return;

            LogCommandBufferFrameOpContextMismatch(
                imageIndex,
                variant,
                frameOpContextFingerprint,
                frameOpContextId,
                submitPath);
            throw new InvalidOperationException(
                $"Vulkan command buffer frame-op context mismatch before submit in {submitPath}. " +
                $"Image={imageIndex} RecordedContextId={variant.RecordedFrameOpContextId} " +
                $"Recorded=0x{variant.RecordedFrameOpContextFingerprint:X16} CurrentContextId={frameOpContextId} " +
                $"Current=0x{frameOpContextFingerprint:X16}.");
        }

        private void LogCommandBufferFrameOpContextMismatch(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            string reusePath)
        {
            string policy = ShouldFailFastOnFrameOpContextMismatch()
                ? "diagnostic-fail-fast-before-submit"
                : "discard-rerecord";
            Debug.VulkanWarningEvery(
                $"Vulkan.CommandBuffer.FrameOpContextMismatch.{GetHashCode()}.{imageIndex}.{reusePath}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] frame-op context mismatch in {0}; rejecting cached primary before submit. Image={1} Policy={2} RecordedContextId={3} Recorded=0x{4:X16} CurrentContextId={5} Current=0x{6:X16}",
                reusePath,
                imageIndex,
                policy,
                variant.RecordedFrameOpContextId,
                variant.RecordedFrameOpContextFingerprint,
                frameOpContextId,
                frameOpContextFingerprint);
        }

        private bool ShouldFailFastOnFrameOpContextMismatch()
            => _diagnosticOptions.EnableValidationLayers ||
               _diagnosticOptions.EnableCrashBreadcrumbs ||
               _diagnosticOptions.Preset == EVulkanDiagnosticPreset.CrashDiagnostics;

        private bool TryReuseCleanCommandChainPrimaryVariant(
            uint imageIndex,
            ulong frameOpsSignature,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            ulong dynamicUiBatchTextSignature,
            int dynamicUiBatchTextOpCount,
            ulong plannerRevision,
            ulong imageLayoutStartSignature,
            bool gpuPipelineProfilingActive,
            int commandBufferImageSlot,
            in CommandBufferGenerationDomains currentGenerations,
            in CommandRecordingDependencySignature currentDependencySignature,
            FrameOp[] ops,
            FrameOp[] dynamicUiBatchTextOps,
            bool delayDynamicUiSecondaryRecording,
            bool preserveSwapchainForOverlay,
            bool requiresTrackedPresentSourceRefresh,
            bool swapchainImageEverPresented,
            out CommandBuffer commandBuffer,
            out CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            out int dynamicUiBatchTextOverlayOpCount,
            out CommandBufferCacheVariant? dynamicUiBatchTextOverlayVariant,
            out ImageLayout swapchainLayoutAfterCommandBuffer)
        {
            commandBuffer = default;
            dynamicUiBatchTextSecondaryCommandBuffer = default;
            dynamicUiBatchTextOverlayOpCount = 0;
            dynamicUiBatchTextOverlayVariant = null;
            swapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;
            if (!CommandChainsEnabledForCurrentRecording ||
                _commandBufferVariants is null ||
                imageIndex >= _commandBufferVariants.Length)
            {
                return false;
            }

            ulong fastScheduleSignature = ComputeCommandChainFastScheduleSignature(
                imageIndex,
                ops,
                dynamicUiBatchTextOps,
                plannerRevision);
            if (!TryGetCachedCommandChainSchedule(
                    imageIndex,
                    fastScheduleSignature,
                    out CommandChainSchedule? cachedSchedule,
                    out _))
            {
                return false;
            }
            if (cachedSchedule is null)
                return false;

            Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(imageIndex);
            ulong currentPrimaryGroupSignature = ComputePrimaryCommandBufferGroupSignature(cachedSchedule, commandChainCache);
            int currentPrimaryGroupCount = cachedSchedule.Groups.Length;

            List<CommandBufferCacheVariant> variants = _commandBufferVariants[imageIndex];
            bool hasDynamicUiBatchTextOverlay = dynamicUiBatchTextOpCount > 0;
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                CommandRecordingDependencyMismatch dependencyMismatch =
                    variant.RecordedDependencySignature.Compare(currentDependencySignature);
                if (dependencyMismatch.RequiresRecording && VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.PrimaryReuse.DependencyMiss.{GetHashCode()}.{imageIndex}.{dependencyMismatch.Field}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Cached primary dependency mismatch. Image={0} Field={1} Class={2}",
                        imageIndex,
                        dependencyMismatch.Field,
                        dependencyMismatch.InvalidationClass);
                }
                if (variant.Dirty ||
                    dependencyMismatch.RequiresRecording ||
                    variant.CommandChainScheduleSignature != cachedSchedule.StructuralSignature ||
                    variant.CommandChainPrimaryGroupSignature != currentPrimaryGroupSignature ||
                    variant.CommandChainPrimaryGroupCount != currentPrimaryGroupCount ||
                    // Query brackets stay inline and are deliberately omitted from the
                    // command-chain schedule. They therefore need their own primary-cache
                    // identity; otherwise a primary recorded for query A can be replayed
                    // while the current frame refreshes proxy data for query B.
                    variant.RecordedGenerations.Query != currentGenerations.Query ||
                    variant.PlannerRevision != plannerRevision ||
                    IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature) ||
                    variant.PreserveSwapchainForOverlay != preserveSwapchainForOverlay ||
                    (requiresTrackedPresentSourceRefresh && !variant.RecordedSwapchainRefreshFromLastPresentSource) ||
                    variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented ||
                    (variant.DynamicUiOpCount > 0) != hasDynamicUiBatchTextOverlay ||
                    (!delayDynamicUiSecondaryRecording &&
                        IsDynamicUiBatchTextSecondaryDirty(variant, dynamicUiBatchTextSignature)) ||
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    continue;
                }

                bool refreshedReusableFrameData;
                using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.FrameDataRefresh))
                {
                    refreshedReusableFrameData = ops.Length == 0 ||
                        TryRefreshReusableCommandBufferFrameData(imageIndex, ops, EVulkanMeshFrameDataStreamKind.Primary);
                    if (refreshedReusableFrameData && dynamicUiBatchTextOps.Length > 0)
                        refreshedReusableFrameData = TryRefreshReusableCommandBufferFrameData(imageIndex, dynamicUiBatchTextOps, EVulkanMeshFrameDataStreamKind.DynamicUi);
                }
                if (!refreshedReusableFrameData)
                    return false;

                if ((HasQueryFrameOps(ops) && !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops)) ||
                    (HasQueryFrameOps(dynamicUiBatchTextOps) && !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, dynamicUiBatchTextOps)))
                {
                    return false;
                }

                bool dynamicUiSecondaryReady = true;
                if (!delayDynamicUiSecondaryRecording)
                {
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordCommandBuffer.FastReuse.RecordDynamicUiSecondary"))
                    using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording))
                        dynamicUiSecondaryReady = RecordDynamicUiBatchTextSecondaryCommandBuffer(
                            imageIndex,
                            variant,
                            dynamicUiBatchTextOps,
                            dynamicUiBatchTextSignature);
                }
                else
                {
                    variant.DynamicUiSecondaryRecorded = false;
                }

                if (dynamicUiBatchTextOpCount > 0 &&
                    !delayDynamicUiSecondaryRecording &&
                    !dynamicUiSecondaryReady)
                {
                    return false;
                }

                variant.DynamicUiSignature = delayDynamicUiSecondaryRecording
                    ? 0
                    : dynamicUiBatchTextSignature;
                variant.DynamicUiOpCount = dynamicUiBatchTextOpCount;
                variant.PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
                variant.PlannerRevision = plannerRevision;
                variant.GpuProfilerActive = gpuPipelineProfilingActive;
                variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;
                variant.RecordedGenerations = currentGenerations;
                variant.RecordedDependencySignature = currentDependencySignature;
                variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
                variant.RecordedFrameOpContextId = frameOpContextId;
                variant.LastUsedFrameId = VulkanFrameCounter;
                variant.DirtyReason = null;
                StoreFrameOpSignatureDebugParts(variant, ops);
                SetActiveCommandBufferVariant(imageIndex, variant);
                RestoreRecordedImageLayoutEndState(variant);
                PrepareVulkanGpuProfilerReusableSubmission(
                    commandBufferImageSlot,
                    variant,
                    gpuPipelineProfilingActive);
                UpdateVulkanGpuProfilerCommandBufferState(
                    imageIndex,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                    reusedClean: true,
                    recorded: false,
                    forcedDirty: false,
                    frameOpSignatureDirty: false,
                    plannerDirty: false,
                    profilerDirty: false,
                    dirtyReason: null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersReused: 1);
                EnsureCommandBufferVariantContextBeforeSubmit(
                    imageIndex,
                    variant,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    "command-chain-primary");
                commandBuffer = variant.PrimaryCommandBuffer;
                if (dynamicUiSecondaryReady)
                {
                    dynamicUiBatchTextSecondaryCommandBuffer = variant.DynamicUiSecondaryCommandBuffer;
                    dynamicUiBatchTextOverlayOpCount = dynamicUiBatchTextOpCount;
                    if (delayDynamicUiSecondaryRecording)
                        dynamicUiBatchTextOverlayVariant = variant;
                }
                swapchainLayoutAfterCommandBuffer = variant.RecordedSwapchainFinalLayout;
                return true;
            }

            return false;
        }

        private bool TryRefreshReusableCommandBufferFrameData(
            uint imageIndex,
            FrameOp[] ops,
            EVulkanMeshFrameDataStreamKind streamKind = EVulkanMeshFrameDataStreamKind.Primary,
            bool refreshMaterialUniforms = true)
        {
            if (ops.Length == 0)
                return true;

            long descriptorSetContentUpdateGeneration =
                SnapshotDescriptorSetContentUpdateGeneration();
            CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily =
                recordingScratch.ReusableMeshDrawSlotsByRendererFamily;
            meshDrawSlotsByRendererFamily.Clear();
            meshDrawSlotsByRendererFamily.EnsureCapacity(recordingScratch.ReusableMeshDrawSlotCapacityHint);
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> familyBases =
                recordingScratch.ReusableMeshFrameDataFamilyBases;
            int frameDataSlot = unchecked((int)Math.Min(imageIndex, int.MaxValue));

            int packetStart = 0;
            while (packetStart < ops.Length)
            {
                FrameOpContext packetContext = ops[packetStart].Context;
                FrameOpPlannerStateKey packetPlannerKey = BuildFrameOpPlannerStateKey(packetContext);
                int packetEnd = packetStart + 1;
                while (packetEnd < ops.Length &&
                       BuildFrameOpPlannerStateKey(ops[packetEnd].Context) == packetPlannerKey)
                {
                    packetEnd++;
                }

                // Resource-planner state is packet state, not draw state. Keep one
                // readback scope for a contiguous compatible range so warmed primary
                // reuse does not serialize a full planner save/restore around every op.
                using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(packetContext);
                for (int i = packetStart; i < packetEnd; i++)
                {
                    FrameOp op = ops[i];
                    switch (op)
                    {
                        case MeshDrawOp drawOp:
                        {
                            int drawUniformSlot = GetFrameWideMeshDrawUniformSlot(
                                meshDrawSlotsByRendererFamily,
                                familyBases,
                                drawOp.Draw.Renderer,
                                frameDataSlot,
                                streamKind,
                                drawOp.Context,
                                drawOp.Draw);
                            if (!drawOp.Draw.Renderer.TryRefreshReusableCommandBufferFrameData(imageIndex, drawOp.Draw, drawUniformSlot, out string reason, refreshMaterialUniforms))
                            {
                                _lastReusableFrameDataRefreshFailureReason =
                                    $"mesh op={i}/{ops.Length} mesh='{drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(drawOp.Draw.MaterialOverride ?? drawOp.Draw.Renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' slot={drawUniformSlot}: {reason}";
                                if (FrameDataReuseDiagnosticsEnabled)
                                {
                                    Debug.VulkanEvery(
                                        $"Vulkan.FrameDataReuse.Mesh.{GetHashCode()}",
                                        TimeSpan.FromSeconds(1),
                                        "[Vulkan] Reusable command-buffer frame-data refresh failed image={0} op={1}/{2} mesh='{3}' material='{4}' drawSlot={5}: {6}",
                                        imageIndex,
                                        i,
                                        ops.Length,
                                        drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                                        (drawOp.Draw.MaterialOverride ?? drawOp.Draw.Renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                                        drawUniformSlot,
                                        reason);
                                }
                                return false;
                            }
                            break;
                        }
                        case IndirectDrawOp indirectDrawOp:
                        {
                            int drawUniformSlot = GetFrameWideMeshDrawUniformSlot(
                                meshDrawSlotsByRendererFamily,
                                familyBases,
                                indirectDrawOp.MeshRenderer,
                                frameDataSlot,
                                streamKind,
                                indirectDrawOp.Context,
                                indirectDrawOp.Draw);
							bool refreshed = indirectDrawOp.MeshRenderer.TryRefreshReusableCommandBufferFrameData(
									imageIndex,
									indirectDrawOp.Draw,
									drawUniformSlot,
									out string reason,
									refreshMaterialUniforms);
                            if (!refreshed)
                            {
                                _lastReusableFrameDataRefreshFailureReason =
                                    $"indirect op={i}/{ops.Length} mesh='{indirectDrawOp.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(indirectDrawOp.Draw.MaterialOverride ?? indirectDrawOp.MeshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' slot={drawUniformSlot}: {reason}";
                                return false;
                            }
                            break;
                        }
                        case ComputeDispatchOp computeOp:
                        {
                            ulong reusableDescriptorKey = ComputeReusableComputeDescriptorBindingKey(computeOp, i);
                            if (!computeOp.Program.TryRefreshReusableComputeDispatchFrameData(imageIndex, computeOp.Snapshot, reusableDescriptorKey))
                            {
                                _lastReusableFrameDataRefreshFailureReason =
                                    $"compute op={i}/{ops.Length} program='{computeOp.Program.Data?.Name ?? "<unnamed program>"}'";
                                if (FrameDataReuseDiagnosticsEnabled)
                                {
                                    Debug.VulkanEvery(
                                        $"Vulkan.FrameDataReuse.Compute.{GetHashCode()}",
                                        TimeSpan.FromSeconds(1),
                                        "[Vulkan] Reusable command-buffer compute frame-data refresh failed image={0} op={1}/{2} program='{3}' groups={4}x{5}x{6}.",
                                        imageIndex,
                                        i,
                                        ops.Length,
                                        computeOp.Program.Data?.Name ?? "<unnamed program>",
                                        computeOp.GroupsX,
                                        computeOp.GroupsY,
                                        computeOp.GroupsZ);
                                }
                                return false;
                            }
                            break;
                        }
                    }
                }

                packetStart = packetEnd;
            }

            recordingScratch.ReusableMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRendererFamily.Count);
            if (HaveDescriptorSetContentsUpdatedSince(descriptorSetContentUpdateGeneration))
            {
                _lastReusableFrameDataRefreshFailureReason =
                    "descriptor contents changed without UPDATE_AFTER_BIND; command recording is required";
                return false;
            }

            return true;
        }

        private static string FormatForcedCommandBufferDirtyReason(
            bool imageForcedDirty,
            bool variantDirty,
            string? variantDirtyReason)
        {
            if (imageForcedDirty && variantDirty)
            {
                return string.IsNullOrWhiteSpace(variantDirtyReason)
                    ? "forced:image+variant"
                    : $"forced:image+variant:{variantDirtyReason}";
            }

            if (imageForcedDirty)
                return "forced:image";

            return string.IsNullOrWhiteSpace(variantDirtyReason)
                ? "forced:variant"
                : $"forced:variant:{variantDirtyReason}";
        }

        private static bool IsDynamicUiBatchTextSecondaryDirty(
            CommandBufferCacheVariant variant,
            ulong dynamicUiBatchTextSignature)
            => variant.DynamicUiSignature != dynamicUiBatchTextSignature ||
               (dynamicUiBatchTextSignature != 0 && !variant.DynamicUiSecondaryRecorded);

        private static bool IsDynamicUiBatchTextPrimaryStructureDirty(
            CommandBufferCacheVariant variant,
            int dynamicUiBatchTextOpCount)
            => (variant.DynamicUiOpCount > 0) != (dynamicUiBatchTextOpCount > 0);

        private static string GetRecordPrimaryFrameOpProfileScopeName(FrameOp op)
            => op switch
            {
                BlitOp => "Vulkan.RecordPrimary.Op.Blit",
                ClearOp => "Vulkan.RecordPrimary.Op.Clear",
                TransformFeedbackOp => "Vulkan.RecordPrimary.Op.TransformFeedback",
                MeshDrawOp => "Vulkan.RecordPrimary.Op.MeshDraw",
                IndirectDrawOp => "Vulkan.RecordPrimary.Op.IndirectDraw",
                MeshTaskDispatchIndirectCountOp => "Vulkan.RecordPrimary.Op.MeshTaskDispatch",
                ComputeDispatchOp => "Vulkan.RecordPrimary.Op.ComputeDispatch",
                ComputeDispatchIndirectOp => "Vulkan.RecordPrimary.Op.ComputeDispatchIndirect",
                BufferCopyOp => "Vulkan.RecordPrimary.Op.BufferCopy",
                SubmissionMarkerOp => "Vulkan.RecordPrimary.Op.SubmissionMarker",
                MemoryBarrierOp => "Vulkan.RecordPrimary.Op.MemoryBarrier",
                PublishFramebufferForSamplingOp => "Vulkan.RecordPrimary.Op.PublishFramebufferForSampling",
                DlssUpscaleOp => "Vulkan.RecordPrimary.Op.DlssUpscale",
                DlssFrameGenerationOp => "Vulkan.RecordPrimary.Op.DlssFrameGeneration",
                TextureUploadFrameOp => "Vulkan.RecordPrimary.Op.TextureUpload",
                _ => "Vulkan.RecordPrimary.Op.Unknown"
            };

        internal static void CollectMeshFrameDataRequirementsForRecording(
            FrameOp[] ops,
            int frameDataSlot,
            EVulkanMeshFrameDataStreamKind streamKind,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> rendererFamilyDrawSlots,
            Dictionary<VulkanMeshFrameDataFamilyKey, int> familyStrides,
            bool append = false)
        {
            if (!append)
            {
                rendererFamilyDrawSlots.Clear();
                familyStrides.Clear();
            }

            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                VkMeshRenderer? renderer;
                PendingMeshDraw draw;
                switch (op)
                {
                    case MeshDrawOp drawOp:
                        renderer = drawOp.Draw.Renderer;
                        draw = drawOp.Draw;
                        break;
                    case IndirectDrawOp indirectDrawOp:
                        renderer = indirectDrawOp.MeshRenderer;
                        draw = indirectDrawOp.Draw;
                        break;
                    default:
                        continue;
                }

                VulkanMeshFrameDataFamilyKey family =
                    VulkanMeshFrameDataFamilyKey.From(frameDataSlot, streamKind, op.Context, draw);
                VulkanMeshFrameDataRendererFamilyKey rendererFamily = new(renderer, family);
                rendererFamilyDrawSlots.TryGetValue(rendererFamily, out int count);
                int requiredDrawSlots = count + 1;
                rendererFamilyDrawSlots[rendererFamily] = requiredDrawSlots;
                if (!familyStrides.TryGetValue(family, out int stride) || stride < requiredDrawSlots)
                    familyStrides[family] = requiredDrawSlots;
            }
        }

        private static int GetFrameWideMeshDrawUniformSlot(
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> slotsByRendererFamily,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> familyBases,
            VkMeshRenderer renderer,
            int frameDataSlot,
            EVulkanMeshFrameDataStreamKind streamKind,
            in FrameOpContext context,
            in PendingMeshDraw draw)
        {
            VulkanMeshFrameDataFamilyKey family =
                VulkanMeshFrameDataFamilyKey.From(frameDataSlot, streamKind, context, draw);
            VulkanMeshFrameDataRendererFamilyKey rendererFamily = new(renderer, family);
            if (!familyBases.TryGetValue(rendererFamily, out int baseSlot))
            {
                throw new InvalidOperationException(
                    $"Mesh frame-data output family {family} was not published before draw-slot resolution.");
            }

            ref int ordinalRef = ref CollectionsMarshal.GetValueRefOrAddDefault(
                slotsByRendererFamily,
                rendererFamily,
                out _);
            int slot = checked(baseSlot + ordinalRef);
            ordinalRef++;
            return slot;
        }

        private bool TryRegisterFrameWideMeshFrameDataRequirements(
            FrameOp[] primaryOps,
            FrameOp[] secondaryOps,
            int frameDataSlot,
            bool sealAfterRegister,
            Dictionary<VkMeshRenderer, int> requirements,
            CommandBufferRecordingScratch scratch,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> resolvedFamilyBases,
            out ulong manifestGeneration,
            out string reason)
        {
            CollectMeshFrameDataRequirementsForRecording(
                primaryOps,
                frameDataSlot,
                EVulkanMeshFrameDataStreamKind.Primary,
                scratch.MeshDrawSlotsByRendererFamily,
                scratch.MeshFrameDataFamilyStrides);
            if (secondaryOps.Length > 0)
            {
                CollectMeshFrameDataRequirementsForRecording(
                    secondaryOps,
                    frameDataSlot,
                    EVulkanMeshFrameDataStreamKind.DynamicUi,
                    scratch.MeshDrawSlotsByRendererFamily,
                    scratch.MeshFrameDataFamilyStrides,
                    append: true);
            }

            bool registered = _frameWideMeshFrameDataManifest.TryRegister(
                RuntimeEngine.Rendering.State.RenderFrameId,
                requirements,
                scratch.MeshDrawSlotsByRendererFamily,
                scratch.MeshFrameDataFamilyStrides,
                resolvedFamilyBases,
                sealAfterRegister,
                out manifestGeneration,
                out bool manifestLayoutChanged,
                out reason);
            if (registered && manifestLayoutChanged)
                ObserveMeshFrameDataManifestGeneration(manifestGeneration);
            PublishFrameWideMeshFrameDataManifestGauges();
            return registered;
        }

        /// <summary>
        /// Invalidates command buffers whose baked dynamic offsets predate a frame-data
        /// family relocation. Append-only publications preserve existing offsets, so this
        /// runs only when an existing family base actually moves.
        /// </summary>
        private void ObserveMeshFrameDataManifestGeneration(ulong generation)
        {
            if (generation == 0)
                return;

            long generationValue = unchecked((long)generation);
            long previous;
            while (true)
            {
                previous = Volatile.Read(ref _observedMeshFrameDataManifestGeneration);
                if (unchecked((ulong)previous) >= generation)
                    return;
                if (Interlocked.CompareExchange(
                        ref _observedMeshFrameDataManifestGeneration,
                        generationValue,
                        previous) == previous)
                {
                    break;
                }
            }

            // The first publication has no older offsets to invalidate.
            if (previous == 0)
                return;

            int secondaryCount = InvalidateCommandChainSecondaryCommandBuffersForFrameDataLayoutChange();
            MarkOpenXrPrimaryCommandBufferVariantsDirty();
            MarkCommandBuffersDirty("mesh frame-data layout generation changed");
            Debug.Vulkan(
                "[Vulkan] Mesh frame-data layout generation advanced from {0} to {1}; invalidated {2} cached command-chain secondaries with baked dynamic offsets.",
                unchecked((ulong)previous),
                generation,
                secondaryCount);
        }

        private void PublishFrameWideMeshFrameDataManifestGauges()
            => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameWideMeshFrameDataManifestGauges(
                MeshFrameDataManifestGeneration,
                MeshFrameDataManifestPublicationCount,
                MeshFrameDataManifestLateRegistrationCount,
                MeshFrameDataManifestRendererCount,
                MeshFrameDataManifestFamilyCount,
                MeshFrameDataManifestIsSealed);

        private bool HasLastWindowPresentSourceForSwapchainRefresh()
        {
            XRFrameBuffer? sourceFrameBuffer = _lastWindowPresentFrameBuffer;
            return sourceFrameBuffer is not null &&
                   sourceFrameBuffer.Width > 0 &&
                   sourceFrameBuffer.Height > 0;
        }

        private bool IsSwapchainImageEverPresented(uint imageIndex)
            => _swapchainImageEverPresented is not null &&
               imageIndex < _swapchainImageEverPresented.Length &&
               _swapchainImageEverPresented[imageIndex];

        private bool TryRecordTextureUploadCommandBuffer(
            uint imageIndex,
            FrameOp[] textureUploadOps,
            out CommandBuffer commandBuffer,
            out CommandPool commandPool)
        {
            commandBuffer = default;
            commandPool = default;
            if (textureUploadOps.Length == 0)
                return false;

            bool commandBufferBegun = false;
            try
            {
                commandPool = GetThreadCommandPool();
                commandBuffer = AllocateCommandBuffer(
                    CommandBufferLevel.Primary,
                    "texture upload command buffer",
                    commandPool);
                RegisterCommandBufferImageIndex(commandBuffer, imageIndex);

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };

                if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin texture upload command buffer.");

                commandBufferBegun = true;
                ResetCommandBufferBindState(commandBuffer);

                bool uploadBatchLabelActive = CmdBeginLabel(commandBuffer, "TextureUploads");
                int queuedBefore = _recordedTextureUploadsForSubmit.Count;
                try
                {
                    for (int i = 0; i < textureUploadOps.Length; i++)
                    {
                        if (textureUploadOps[i] is not TextureUploadFrameOp textureUploadOp)
                            continue;

                        bool uploadLabelActive = CmdBeginLabel(commandBuffer, "TextureUpload");
                        try
                        {
                            RecordTextureUploadOp(commandBuffer, textureUploadOp.Upload);
                        }
                        finally
                        {
                            if (uploadLabelActive)
                                CmdEndLabel(commandBuffer);
                        }
                    }
                }
                finally
                {
                    if (uploadBatchLabelActive)
                        CmdEndLabel(commandBuffer);
                }

                if (EndCommandBufferTracked(commandBuffer) != Result.Success)
                    throw new Exception("Failed to end texture upload command buffer.");

                if (_recordedTextureUploadsForSubmit.Count == queuedBefore)
                {
                    FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "TextureUpload.RecordFailure");
                    RemoveCommandBufferBindState(commandBuffer);
                    commandBuffer = default;
                    commandPool = default;
                    return false;
                }

                return true;
            }
            catch
            {
                CancelRecordedTextureUploadSubmitBatch("texture upload command buffer recording failed");

                if (commandBuffer.Handle != 0 && commandPool.Handle != 0 && !_deviceLost)
                {
                    if (!commandBufferBegun)
                    {
                        FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "TextureUpload.RecordException");
                    }
                    else
                    {
                        FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "TextureUpload.RecordDeviceLoss");
                    }
                }

                if (commandBuffer.Handle != 0)
                    RemoveCommandBufferBindState(commandBuffer);

                commandBuffer = default;
                commandPool = default;
                throw;
            }
        }

        private void RecordTextureUploadOp(CommandBuffer commandBuffer, VulkanImportedTexturePendingUpload upload)
        {
            VulkanImportedTextureUploadRequest request = upload.Request;
            if (!upload.ShouldPublish())
            {
                _textureUploadService.RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Canceled,
                    "request became stale or canceled before command recording");
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                InvokeTextureUploadCanceled(upload);
                return;
            }

            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.UploadRecording,
                $"recording {upload.StagingResources.Length} mip copies token={upload.PublicationToken}");

            if (!upload.TryValidateCopyRegions(out string? validationFailure))
            {
                _textureUploadService.RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Failed,
                    validationFailure);
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                InvokeTextureUploadError(upload, new InvalidOperationException(validationFailure ?? "Invalid Vulkan imported texture upload copy regions."));
                return;
            }

            upload.MarkRecordStarted();
            TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                request.TextureName,
                request.SourcePath,
                request.StreamingGeneration,
                upload.PublicationToken,
                "decodeCompleteToUploadRecord",
                TextureRuntimeDiagnostics.ElapsedMilliseconds(upload.PreparedTimestamp));

            ImageSubresourceRange range = new()
            {
                AspectMask = upload.AspectMask,
                BaseMipLevel = 0,
                LevelCount = upload.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            ImageMemoryBarrier uploadBeginBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = upload.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &uploadBeginBarrier);

            for (int i = 0; i < upload.StagingResources.Length; i++)
            {
                VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[i];
                BufferImageCopy copyRegion = staging.CopyRegion;
                CmdCopyBufferToImageTracked(
                    commandBuffer,
                    staging.Buffer,
                    upload.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    ref copyRegion);
            }

            ImageMemoryBarrier uploadEndBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = upload.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &uploadEndBarrier);

            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.Uploaded,
                $"recorded {upload.StagingResources.Length} mip copies");
            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.DescriptorPublishPending,
                $"publicationToken={upload.PublicationToken}; waiting for recorded command buffer completion");

            QueueRecordedTextureUploadForSubmit(upload);
        }

        private void BeginRecordedTextureUploadSubmitBatch()
            => _recordedTextureUploadsForSubmit.Clear();

        private void QueueRecordedTextureUploadForSubmit(VulkanImportedTexturePendingUpload upload)
            => _recordedTextureUploadsForSubmit.Add(upload);

        private void QueueRecordedTextureUploadsForTimeline(ulong timelineValue, string uploadSource)
        {
            if (_recordedTextureUploadsForSubmit.Count == 0)
                return;

            for (int i = 0; i < _recordedTextureUploadsForSubmit.Count; i++)
            {
                _pendingRecordedTextureUploadPublications.Add(
                    new PendingRecordedTextureUploadPublication(
                        _recordedTextureUploadsForSubmit[i],
                        timelineValue,
                        uploadSource));
            }

            _recordedTextureUploadsForSubmit.Clear();
        }

        private void PublishRecordedTextureUploadsAfterCompletedSubmit(string uploadSource)
        {
            if (_recordedTextureUploadsForSubmit.Count == 0)
                return;

            for (int i = 0; i < _recordedTextureUploadsForSubmit.Count; i++)
                PublishRecordedTextureUploadAfterGpuCompletion(_recordedTextureUploadsForSubmit[i], uploadSource);

            _recordedTextureUploadsForSubmit.Clear();
        }

        private void MoveRecordedTextureUploadsForSubmitTo(List<VulkanImportedTexturePendingUpload> destination)
        {
            if (_recordedTextureUploadsForSubmit.Count == 0)
                return;

            destination.AddRange(_recordedTextureUploadsForSubmit);
            _recordedTextureUploadsForSubmit.Clear();
        }

        private void PublishRecordedTextureUploadsAfterCompletedSubmit(
            List<VulkanImportedTexturePendingUpload> uploads,
            string uploadSource)
        {
            if (uploads.Count == 0)
                return;

            for (int i = 0; i < uploads.Count; i++)
                PublishRecordedTextureUploadAfterGpuCompletion(uploads[i], uploadSource);

            uploads.Clear();
        }

        private void CancelRecordedTextureUploadSubmitBatch(string reason)
        {
            if (_recordedTextureUploadsForSubmit.Count == 0)
                return;

            for (int i = 0; i < _recordedTextureUploadsForSubmit.Count; i++)
                CancelRecordedTextureUpload(_recordedTextureUploadsForSubmit[i], reason);

            _recordedTextureUploadsForSubmit.Clear();
        }

        private void CancelRecordedTextureUploads(
            List<VulkanImportedTexturePendingUpload> uploads,
            string reason)
        {
            if (uploads.Count == 0)
                return;

            // Recorded primary/secondary command buffers may contain copies from
            // these staging resources. They cannot remain reusable after the
            // canceled upload retires those buffers, images, or descriptors.
            _ = InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();
            MarkOpenXrPrimaryCommandBufferVariantsDirty();
            MarkCommandBuffersDirty(reason);

            for (int i = 0; i < uploads.Count; i++)
                CancelRecordedTextureUpload(uploads[i], reason);

            uploads.Clear();
        }

        private void CancelRecordedTextureUploadPublications(string reason)
        {
            CancelRecordedTextureUploadSubmitBatch(reason);

            if (_pendingRecordedTextureUploadPublications.Count == 0)
                return;

            for (int i = 0; i < _pendingRecordedTextureUploadPublications.Count; i++)
                CancelRecordedTextureUpload(_pendingRecordedTextureUploadPublications[i].Upload, reason);

            _pendingRecordedTextureUploadPublications.Clear();
        }

        private void DrainCompletedRecordedTextureUploadPublications()
        {
            if (_pendingRecordedTextureUploadPublications.Count == 0 || IsDeviceLost)
                return;

            for (int i = _pendingRecordedTextureUploadPublications.Count - 1; i >= 0; i--)
            {
                PendingRecordedTextureUploadPublication pending = _pendingRecordedTextureUploadPublications[i];
                bool completed;
                try
                {
                    completed = HasTimelineValueCompleted(_graphicsTimelineSemaphore, pending.TimelineValue);
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                if (!completed)
                    continue;

                _pendingRecordedTextureUploadPublications.RemoveAt(i);
                PublishRecordedTextureUploadAfterGpuCompletion(pending.Upload, pending.UploadSource);
            }
        }

        private void CancelRecordedTextureUpload(
            VulkanImportedTexturePendingUpload upload,
            string reason)
        {
            VulkanImportedTextureUploadRequest request = upload.Request;
            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.Canceled,
                reason);

            if (!IsDeviceLost)
                upload.Texture.ReleasePreparedImportedUploadResources(upload);

            InvokeTextureUploadCanceled(upload);
        }

        private void PublishRecordedTextureUploadAfterGpuCompletion(
            VulkanImportedTexturePendingUpload upload,
            string uploadSource)
        {
            VulkanImportedTextureUploadRequest request = upload.Request;
            if (!upload.ShouldPublish())
            {
                upload.Texture.ReleasePreparedImportedUploadResources(upload);
                _textureUploadService.RecordState(
                    request,
                    VulkanTextureUploadGenerationState.Canceled,
                    $"request became stale before {uploadSource} descriptor publication");
                InvokeTextureUploadCanceled(upload);
                return;
            }

            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.Uploaded,
                $"{uploadSource} recorded upload completed");
            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.DescriptorPublishPending,
                $"publicationToken={upload.PublicationToken}");

            long publicationStart = TextureRuntimeDiagnostics.StartTiming();
            upload.Texture.PublishSynchronizedImportedTextureUpload(upload);
            upload.MarkPublished();
            RetireTextureUploadStagingResources(upload);
            TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                request.TextureName,
                request.SourcePath,
                request.StreamingGeneration,
                upload.PublicationToken,
                "uploadRecordToDescriptorPublication",
                upload.RecordTimestamp == 0L ? 0.0 : TextureRuntimeDiagnostics.ElapsedMilliseconds(upload.RecordTimestamp));
            TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                RuntimeRenderingHostServices.FrameTiming.LastRenderTimestampTicks,
                request.TextureName,
                request.SourcePath,
                request.StreamingGeneration,
                upload.PublicationToken,
                "publicationToOldResourceRetirementEnqueue",
                TextureRuntimeDiagnostics.ElapsedMilliseconds(publicationStart));

            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.Published,
                $"publicationToken={upload.PublicationToken}");
            _textureUploadService.RecordState(
                request,
                VulkanTextureUploadGenerationState.Retired,
                "old texture and staging resources enqueued for frame-slot retirement");
            InvokeTextureUploadFinished(upload);
        }

        private void RetireTextureUploadStagingResources(VulkanImportedTexturePendingUpload upload)
        {
            for (int i = 0; i < upload.StagingResources.Length; i++)
            {
                VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[i];
                RetireBuffer(staging.Buffer, staging.Memory);
            }
        }

        private static void InvokeTextureUploadFinished(VulkanImportedTexturePendingUpload upload)
        {
            if (!upload.TryGetTexture(out XRTexture2D? texture) || texture is null)
                return;

            try
            {
                upload.OnFinished?.Invoke(texture);
            }
            catch (Exception ex)
            {
                upload.OnError?.Invoke(ex);
            }
        }

        private static void InvokeTextureUploadCanceled(VulkanImportedTexturePendingUpload upload)
        {
            try
            {
                upload.OnCanceled?.Invoke();
            }
            catch (Exception ex)
            {
                upload.OnError?.Invoke(ex);
            }
        }

        private static void InvokeTextureUploadError(VulkanImportedTexturePendingUpload upload, Exception exception)
        {
            try
            {
                upload.OnError?.Invoke(exception);
            }
            catch
            {
                // Error callbacks are diagnostics-only; avoid recursive failure loops.
            }
        }

        private readonly record struct SwapchainRecordingTarget(
            Image Image,
            ImageView ImageView,
            Format ImageFormat,
            Extent2D Extent,
            Image DepthImage,
            ImageView DepthView,
            Format DepthFormat,
            ImageAspectFlags DepthAspect,
            ImageLayout InitialColorLayout,
            bool ImageEverPresentedAtRecordStart)
        {
            public bool IsValid =>
                Image.Handle != 0 &&
                ImageView.Handle != 0 &&
                Extent.Width != 0 &&
                Extent.Height != 0 &&
                DepthImage.Handle != 0 &&
                DepthView.Handle != 0;
        }

        private ImageLayout ResolveTrackedSwapchainTargetColorLayout(Image image)
        {
            if (image.Handle == 0)
                return ImageLayout.Undefined;

            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            return TryGetTrackedImageLayout(image, colorRange, out ImageLayout trackedLayout)
                ? trackedLayout
                : ImageLayout.Undefined;
        }

        private SwapchainRecordingTarget ResolveSwapchainRecordingTarget(
            uint imageIndex,
            OpenXrEyeRenderTargetContext? openXrTargetContext)
        {
            if (openXrTargetContext is { } openXrTarget && openXrTarget.IsValid)
            {
                ImageLayout initialColorLayout = ResolveTrackedSwapchainTargetColorLayout(openXrTarget.Image);
                return new SwapchainRecordingTarget(
                    openXrTarget.Image,
                    openXrTarget.ImageView,
                    openXrTarget.ImageFormat,
                    openXrTarget.Extent,
                    openXrTarget.DepthImage,
                    openXrTarget.DepthView,
                    openXrTarget.DepthFormat,
                    openXrTarget.DepthAspect,
                    initialColorLayout,
                    ImageEverPresentedAtRecordStart: false);
            }

            if (swapChainImages is null ||
                swapChainImageViews is null ||
                imageIndex >= swapChainImages.Length ||
                imageIndex >= swapChainImageViews.Length)
            {
                return default;
            }

            Image swapchainImage = swapChainImages[imageIndex];
            bool imageEverPresented = IsSwapchainImageEverPresented(imageIndex);
            ImageLayout initialSwapchainLayout = ResolveTrackedSwapchainTargetColorLayout(swapchainImage);
            if (initialSwapchainLayout == ImageLayout.Undefined && imageEverPresented)
                initialSwapchainLayout = ImageLayout.PresentSrcKhr;

            VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;
            return new SwapchainRecordingTarget(
                swapchainImage,
                swapChainImageViews[imageIndex],
                swapChainImageFormat,
                swapChainExtent,
                depth?.Image ?? default,
                depth?.View ?? default,
                depth?.Format ?? default,
                depth?.Aspect ?? default,
                initialSwapchainLayout,
                imageEverPresented);
        }

        private bool TryResolveGraphicsPipelinePrewarmTarget(
            XRFrameBuffer? target,
            int passIndex,
            in FrameOpContext context,
            in SwapchainRecordingTarget swapchainTarget,
            out bool useDynamicRendering,
            out RenderPass renderPass,
            out DynamicRenderingFormatSignature dynamicRenderingFormats,
            out bool depthStencilReadOnly,
            out string reason)
        {
            useDynamicRendering = false;
            renderPass = default;
            dynamicRenderingFormats = default;
            depthStencilReadOnly = false;
            reason = string.Empty;

            if (target is null)
            {
                useDynamicRendering = UseDynamicRenderingRenderTargets && swapchainTarget.IsValid;
                if (useDynamicRendering)
                {
                    dynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(
                        swapchainTarget.ImageFormat,
                        swapchainTarget.DepthFormat);
                    return true;
                }

                renderPass = _renderPass;
                if (renderPass.Handle != 0)
                    return true;

                reason = "legacy swapchain render pass is unavailable";
                return false;
            }

            VkFrameBuffer? vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target);
            if (vkFrameBuffer is null)
            {
                reason = $"target '{target.Name ?? "<unnamed>"}' has no Vulkan framebuffer";
                return false;
            }

            vkFrameBuffer.EnsureCurrent();
            ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, vkFrameBuffer);
            FrameBufferAttachmentSignature[] attachmentSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                CompiledRenderGraph.Synchronization,
                preserveTrackedClearLoads: false);
            depthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(attachmentSignature);

            if (UseDynamicRenderingRenderTargets)
            {
                useDynamicRendering = true;
                uint viewMask = vkFrameBuffer.MultiviewViewMask;
                dynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                    attachmentSignature,
                    viewMask,
                    ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, viewMask));
                return true;
            }

            renderPass = vkFrameBuffer.ResolveRenderPassForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                CompiledRenderGraph.Synchronization,
                preserveTrackedClearLoads: false);
            if (renderPass.Handle != 0)
                return true;

            reason = $"target '{target.Name ?? "<unnamed>"}' has no compatible legacy render pass";
            return false;
        }

        private bool TryRecordCommandBuffer(
            uint imageIndex,
            CommandBuffer commandBuffer,
            CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            FrameOp[] ops,
            int dynamicUiBatchTextOpCount,
            CommandChainSchedule? commandChainSchedule,
            bool preserveSwapchainForOverlay,
            out int recordedSwapchainWriteCount,
            out ImageLayout recordedSwapchainFinalLayout,
            out string recordingDeferredReason,
            out bool queryFrameOpsRequireRerecord,
            bool transitionSwapchainToPresent = true,
            uint? frameDataImageIndexOverride = null,
            OpenXrEyeRenderTargetContext? openXrTargetContext = null,
            bool excludeDesktopSwapchainBarriers = false)
        {
            using DesktopSwapchainBarrierExclusionScope desktopSwapchainBarrierExclusion =
                new(excludeDesktopSwapchainBarriers);
            recordedSwapchainWriteCount = 0;
            recordedSwapchainFinalLayout = ImageLayout.Undefined;
            recordingDeferredReason = string.Empty;
            queryFrameOpsRequireRerecord = false;
            bool queryFrameOpsRequireRerecordLocal = false;
            int droppedDrawOps = 0;
            int droppedComputeOps = 0;
            int droppedFrameOps = 0;
            FrameOpFailureSnapshot? firstFailure = null;
            uint frameDataImageIndex = frameDataImageIndexOverride ?? imageIndex;
            int commandBufferImageSlot = unchecked((int)Math.Min(frameDataImageIndex, int.MaxValue));
            // The strict-SPS mirror recorder targets an engine-owned layered FBO
            // and intentionally has no OpenXR image target context. Do not let its
            // frame-data index alias desktop swapchain image 0. Direct per-eye XR
            // recording supplies an explicit target context and remains valid.
            SwapchainRecordingTarget swapchainTarget =
                IsRenderingExternalSwapchainTarget && openXrTargetContext is null
                    ? default
                    : ResolveSwapchainRecordingTarget(imageIndex, openXrTargetContext);
            Extent2D swapchainRecordExtent = swapchainTarget.IsValid ? swapchainTarget.Extent : swapChainExtent;
            bool imageWasEverPresentedAtRecordStart = swapchainTarget.ImageEverPresentedAtRecordStart;
            ImageLayout initialSwapchainColorLayout = swapchainTarget.IsValid
                ? swapchainTarget.InitialColorLayout
                : ImageLayout.Undefined;
            CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
            HashSet<nint> executedCommandChainSecondaryHandles = recordingScratch.ExecutedCommandChainSecondaryHandles;
            executedCommandChainSecondaryHandles.Clear();

            // Publish the complete command-stream reservation before vkBeginCommandBuffer.
            // Arena offsets are stable, but descriptor slabs and CPU view tables must also be
            // materialized at this legal boundary so a draw cannot grow shared state midway
            // through recording.
            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = recordingScratch.MeshDrawSlotsByRenderer;
            meshDrawSlotsByRenderer.Clear();
            meshDrawSlotsByRenderer.EnsureCapacity(Math.Max(1, recordingScratch.RecordMeshDrawSlotCapacityHint));
            VulkanMeshFrameDataReservationManifest frameDataManifest = recordingScratch.MeshFrameDataManifest;
            ulong frameDataGeneration = MeshFrameDataReservationGeneration;
            frameDataManifest.Begin(frameDataGeneration, recordingScratch.RecordMeshDrawSlotCapacityHint);
            if (!TryRegisterFrameWideMeshFrameDataRequirements(
                    ops,
                    Array.Empty<FrameOp>(),
                    commandBufferImageSlot,
                    sealAfterRegister: true,
                    meshDrawSlotsByRenderer,
                    recordingScratch,
                    recordingScratch.PrimaryMeshFrameDataFamilyBases,
                    out _,
                    out string frameWideReason))
            {
                frameDataManifest.End();
                recordingDeferredReason =
                    $"Frame-wide mesh frame-data manifest deferred command recording: {frameWideReason}";
                return false;
            }
            foreach (KeyValuePair<VkMeshRenderer, int> reservation in meshDrawSlotsByRenderer)
            {
                if (frameDataManifest.TryReserve(reservation.Key, reservation.Value))
                    continue;
                frameDataManifest.End();
                recordingDeferredReason =
                    $"Unable to reserve {reservation.Value} mesh frame-data slots before command recording.";
                return false;
            }
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily =
                recordingScratch.PrimaryMeshDrawSlotsByRendererFamily;
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshFrameDataFamilyBases =
                recordingScratch.PrimaryMeshFrameDataFamilyBases;
            meshDrawSlotsByRendererFamily.Clear();
            HashSet<int> optionalPipelineDeferredOpIndices = recordingScratch.OptionalPipelineDeferredOpIndices;
            optionalPipelineDeferredOpIndices.Clear();
            EMeshSubmissionStrategy submissionStrategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
            VulkanPipelineVariantManifest pipelineVariantManifest = GetOrBuildPipelineVariantManifest(
                CompiledRenderGraph.Plan,
                ops,
                submissionStrategy,
                UseDynamicRenderingRenderTargets,
                ComputeFrameOpsSignature(ops));
            bool warmupPreviouslyCompleted = pipelineVariantManifest.WarmupCompleted;
            bool graphicsPipelinesReady = true;
            string firstGraphicsPipelinePendingReason = string.Empty;
            foreach (VulkanPipelineVariantRequirement requirement in pipelineVariantManifest.Requirements)
            {
                int opIndex = requirement.OpIndex;
                PendingMeshDraw pendingDraw = ops[opIndex] switch
                {
                    MeshDrawOp direct => direct.Draw,
                    IndirectDrawOp indirect => indirect.Draw,
                    _ => default,
                };
                VkMeshRenderer? meshRenderer = pendingDraw.Renderer;
                if (meshRenderer is null)
                {
                    if (requirement.Required)
                    {
                        graphicsPipelinesReady = false;
                        firstGraphicsPipelinePendingReason = firstGraphicsPipelinePendingReason.Length == 0
                            ? $"op={opIndex} has no prepared mesh renderer"
                            : firstGraphicsPipelinePendingReason;
                    }
                    else
                    {
                        optionalPipelineDeferredOpIndices.Add(opIndex);
                    }
                    continue;
                }
                XRFrameBuffer? target = ops[opIndex].Target;

                int drawSlot = GetFrameWideMeshDrawUniformSlot(
                    meshDrawSlotsByRendererFamily,
                    meshFrameDataFamilyBases,
                    meshRenderer,
                    commandBufferImageSlot,
                    EVulkanMeshFrameDataStreamKind.Primary,
                    ops[opIndex].Context,
                    pendingDraw);
                using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                    ops[opIndex].Context.PipelineInstance);
                using var plannerScope =
                    EnterFrameOpResourcePlannerReadbackScope(ops[opIndex].Context);
                if (!meshRenderer.TryPrewarmFrameDataForRecording(
                        pendingDraw,
                        drawSlot,
                        commandBufferImageSlot,
                        out string prewarmReason))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.MeshFrameData.PreRecordReservationFailed.{meshRenderer.GetHashCode()}.{drawSlot}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Mesh frame-data reservation failed before command recording for mesh='{0}' slot={1}: {2}",
                        meshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                        drawSlot,
                        prewarmReason);
                    frameDataManifest.End();
                    recordingDeferredReason =
                        $"Mesh frame-data reservation deferred before command recording for " +
                        $"mesh '{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}', slot {drawSlot}: {prewarmReason}";
                    return false;
                }

                int pipelinePassIndex = EnsureValidPassIndex(
                    ops[opIndex].PassIndex,
                    ops[opIndex].GetType().Name,
                    ops[opIndex].Context.PassMetadata);
                if (pipelinePassIndex == int.MinValue)
                    continue;

                if (!TryResolveGraphicsPipelinePrewarmTarget(
                        target,
                        pipelinePassIndex,
                        ops[opIndex].Context,
                        swapchainTarget,
                        out bool useDynamicRendering,
                        out RenderPass prewarmRenderPass,
                        out DynamicRenderingFormatSignature prewarmDynamicRenderingFormats,
                        out bool depthStencilReadOnly,
                        out string targetReason))
                {
                    if (!requirement.Required)
                    {
                        optionalPipelineDeferredOpIndices.Add(opIndex);
                        Debug.VulkanEvery(
                            $"Vulkan.OptionalPipelineNodeDeferred.{GetHashCode()}.{requirement.PassIndex}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Optional pipeline node deferred without rejecting the frame. Pass={0} Variant={1} Reason={2}",
                            requirement.PassName,
                            requirement.SubmissionStrategy,
                            targetReason);
                        continue;
                    }

                    graphicsPipelinesReady = false;
                    if (firstGraphicsPipelinePendingReason.Length == 0)
                    {
                        firstGraphicsPipelinePendingReason =
                            $"op={opIndex} target='{target?.Name ?? "<swapchain>"}': {targetReason}";
                    }
                    continue;
                }

                if (meshRenderer.TryPrewarmGraphicsPipelinesForRecording(
                        pendingDraw,
                        prewarmRenderPass,
                        useDynamicRendering,
                        prewarmDynamicRenderingFormats,
                        pipelinePassIndex,
                        ops[opIndex].Context.PassMetadata,
                        depthStencilReadOnly,
                        ops[opIndex].Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                        out string pipelineReason))
                {
                    continue;
                }

                if (!requirement.Required)
                {
                    optionalPipelineDeferredOpIndices.Add(opIndex);
                    Debug.VulkanEvery(
                        $"Vulkan.OptionalPipelineNodeDeferred.{GetHashCode()}.{requirement.PassIndex}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Optional pipeline node deferred without rejecting the frame. Pass={0} Variant={1} Dynamic={2} Stereo={3} Multiview={4} Reason={5}",
                        requirement.PassName,
                        requirement.SubmissionStrategy,
                        requirement.DynamicRendering,
                        requirement.Stereo,
                        requirement.Multiview,
                        pipelineReason);
                    continue;
                }

                graphicsPipelinesReady = false;
                if (firstGraphicsPipelinePendingReason.Length == 0)
                {
                    firstGraphicsPipelinePendingReason =
                        $"op={opIndex} mesh='{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}': {pipelineReason}";
                }
            }
            recordingScratch.RecordMeshDrawSlotCapacityHint = Math.Max(
                recordingScratch.RecordMeshDrawSlotCapacityHint,
                meshDrawSlotsByRendererFamily.Count);
            meshDrawSlotsByRendererFamily.Clear();

            if (!graphicsPipelinesReady)
            {
                frameDataManifest.End();
                recordingDeferredReason = warmupPreviouslyCompleted
                    ? $"Required graphics pipeline became pending after declared warmup: {firstGraphicsPipelinePendingReason}"
                    : $"Graphics pipeline prewarm deferred before vkBeginCommandBuffer: {firstGraphicsPipelinePendingReason}";
                Debug.VulkanWarningEvery(
                    $"Vulkan.Primary.PipelinePrewarmPending.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Primary command recording deferred before vkBeginCommandBuffer because required graphics pipelines are pending. detail={0}",
                    firstGraphicsPipelinePendingReason);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
                    EVulkanPipelineTelemetryEvent.RequiredPipelineRecordDeferred);
                return false;
            }

            pipelineVariantManifest.MarkWarmupCompleted();

            if (!frameDataManifest.TrySeal(
                    MeshFrameDataReservationGeneration,
                    MeshFrameDataReservedBytes))
            {
                frameDataManifest.End();
                recordingDeferredReason =
                    "Mesh frame-data generation changed while the command-stream reservation manifest was being materialized.";
                return false;
            }
            using VulkanMeshFrameDataManifestRecordingScope frameDataManifestScope = new(frameDataManifest);

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.ResetAndBegin"))
            {
                ReleaseDeferredSecondaryCommandBuffers(frameDataImageIndex);
                ResetVulkanCommandBufferTracked(commandBuffer);
                ResetSubmissionMarkersForCommandBuffer(commandBuffer);
                CleanupComputeTransientResources(frameDataImageIndex);

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                };

                if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin recording command buffer.");

                BeginFrameTimingQueries(commandBuffer, commandBufferImageSlot);
                BeginVulkanGpuProfilerQueries(commandBuffer, commandBufferImageSlot);

                ResetCommandBufferBindState(commandBuffer);
                recordingScratch.PreparedInlineQueries.Clear();
                recordingScratch.BegunInlineQueries.Clear();

                if (CanRecordCommandBufferDebugLabels)
                {
                    CmdBeginLabel(commandBuffer, frameDataImageIndex == imageIndex
                        ? $"FrameCmd[{imageIndex}]"
                        : $"FrameCmd[target={imageIndex} frame={frameDataImageIndex}]");
                }
            }

            // Global pending barriers are deferred until the first pass boundary to
            // maintain pass-scoped ordering.  Any remaining global mask is emitted
            // before the first pass barrier group via EmitPassBarriers.

            FrameOpContext initialContext = ops.Length > 0
                ? ops[0].Context
                : CaptureFrameOpContext();

            // Coalesce swapchain-targeting ops into a single context to avoid
            // render-pass restarts across pipeline boundaries.  Context changes
            // between pipelines that all render to the swapchain cause
            // EndActiveRenderPass + BeginRenderPassForTarget cycles that can lose
            // composited content (e.g. the skybox turns black).  FBO-targeting ops
            // keep their original context for correct barrier/resource planning.
            IReadOnlyList<VulkanRenderGraphCompiler.SecondaryRecordingBucket> secondaryBuckets;
            Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket>? secondaryBucketByStart = null;
            CommandChainKey[]? scheduledCommandChainKeysByOpIndex = null;
            Dictionary<CommandChainKey, CommandChain>? scheduledCommandChainCache = null;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.SortAndSecondaryBuckets"))
            {
                if (commandChainSchedule is null)
                {
                    // Always sort frame ops by (PassOrder, safe draw order, OriginalIndex)
                    // and then normalize same-target clears before first same-target use.
                    // Render graph pass order preserves cross-pass dependencies, while same-pass
                    // compute/barrier/indirect operations stay in enqueue order so GPU-produced
                    // counters are written before the draw commands that consume them.
                    ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                }

                secondaryBuckets = _renderGraphCompiler.BuildSecondaryRecordingBuckets(ops);
                if (secondaryBuckets.Count > 8)
                {
                    secondaryBucketByStart = recordingScratch.SecondaryBucketByStart;
                    secondaryBucketByStart.Clear();
                    secondaryBucketByStart.EnsureCapacity(Math.Max(recordingScratch.SecondaryBucketByStartCapacityHint, secondaryBuckets.Count));
                    foreach (VulkanRenderGraphCompiler.SecondaryRecordingBucket bucket in secondaryBuckets)
                        secondaryBucketByStart[bucket.StartIndex] = bucket;
                    recordingScratch.SecondaryBucketByStartCapacityHint = Math.Max(1, secondaryBucketByStart.Count);
                }

                if (commandChainSchedule is not null &&
                    TryGetCommandChainScheduleFrameSlot(commandChainSchedule, out int commandChainScheduleFrameSlot))
                {
                    scheduledCommandChainCache = GetCommandChainCache(unchecked((uint)commandChainScheduleFrameSlot));
                    if (CommandChainValidationEnabled)
                        ValidatePrimaryCommandChainSchedule(
                            commandChainSchedule,
                            ops,
                            dynamicUiBatchTextOpCount,
                            scheduledCommandChainCache);
                    if (recordingScratch.ScheduledCommandChainKeysByOpIndex.Length < ops.Length)
                    {
                        int capacity = Math.Max(ops.Length, Math.Max(recordingScratch.ScheduledCommandChainKeysByOpIndex.Length * 2, 16));
                        recordingScratch.ScheduledCommandChainKeysByOpIndex = new CommandChainKey[capacity];
                    }
                    scheduledCommandChainKeysByOpIndex = recordingScratch.ScheduledCommandChainKeysByOpIndex;
                    PopulateCommandChainKeysByFrameOpIndex(
                        commandChainSchedule,
                        scheduledCommandChainCache,
                        scheduledCommandChainKeysByOpIndex.AsSpan(),
                        ops.Length);
                }
            }

            initialContext = ops.Length > 0
                ? ops[0].Context
                : initialContext;
            using FrameOpResourcePlannerRecordingScope frameOpResourcePlannerRecordingScope = EnterFrameOpResourcePlannerRecordingScope();
            _ = TryActivateFrameOpResourcePlannerState(initialContext);

            if (commandChainSchedule is not null)
                _lastReusableFrameDataRefreshFailureReason = null;

            int swapchainPresentTransitions = 0;
            bool usedSwapchainDynamicRendering = false;
            bool swapchainInColorAttachmentLayout = false;
            ImageLayout swapchainFinalTargetLayout = transitionSwapchainToPresent
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.ColorAttachmentOptimal;
            ImageLayout swapchainFinalLayout = initialSwapchainColorLayout;

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.FrameStartBarriers"))
            {
                CmdBeginLabel(commandBuffer, "SwapchainBarriers");
                if (swapchainTarget.IsValid)
                {
                    var plannedSwapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    var swapchainImageBarriers = BarrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    var swapchainBufferBarriers = BarrierPlanner.GetBufferBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    EmitPlannedSwapchainBarriers(commandBuffer, plannedSwapchainBarriers);
                    EmitPlannedImageBarriers(commandBuffer, swapchainImageBarriers);
                    EmitPlannedBufferBarriers(commandBuffer, swapchainBufferBarriers);
                }
                CmdEndLabel(commandBuffer);

                // Transition any freshly-allocated physical images from UNDEFINED to
                // a safe initial layout so that render passes never see UNDEFINED.
                EmitInitialImageBarriersForUnknownPass(
                    commandBuffer,
                    skipDesktopSwapchainImages: excludeDesktopSwapchainBarriers);
            }

            int clearCount = 0;
            int drawCount = 0;
            int meshDrawCount = 0;
            int indirectDrawCount = 0;
            int meshTaskDispatchCount = 0;
            int blitCount = 0;
            int computeCount = 0;
            int swapchainWriteCount = 0;
            int swapchainClearWrites = 0;
            int swapchainDrawWrites = 0;
            int swapchainBlitWrites = 0;
            int sceneSwapchainWriters = 0;
            int overlaySwapchainWriters = 0;
            int forcedDiagnosticSwapchainWriters = 0;
            int fboOnlyDrawOps = 0;
            int fboOnlyBlitOps = 0;
            string swapchainLastWriter = "None";
            int swapchainLastWriterPass = int.MinValue;
            int swapchainLastWriterOpIndex = -1;

            // Per-pipeline context identity tracking for swapchain writes
            Dictionary<int, int> swapchainWritesByPipeline = recordingScratch.SwapchainWritesByPipeline;
            Dictionary<int, string> swapchainWriterLabelByPipeline = recordingScratch.SwapchainWriterLabelByPipeline;
            Dictionary<int, string> swapchainWriterDetailByPipeline = recordingScratch.SwapchainWriterDetailByPipeline;
            Dictionary<int, FrameOp> swapchainWriterOpByPipeline = recordingScratch.SwapchainWriterOpByPipeline;
            Dictionary<int, int> swapchainWriterDynamicUiDrawCountByPipeline = recordingScratch.SwapchainWriterDynamicUiDrawCountByPipeline;
            Dictionary<int, int> swapchainWriterPassByPipeline = recordingScratch.SwapchainWriterPassByPipeline;
            Dictionary<int, int> swapchainWriterOpIndexByPipeline = recordingScratch.SwapchainWriterOpIndexByPipeline;
            Dictionary<int, string> pipelineNameByIdentity = recordingScratch.PipelineNameByIdentity;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.ScratchAndUniformSlots"))
            {
                swapchainWritesByPipeline.Clear();
                swapchainWriterLabelByPipeline.Clear();
                swapchainWriterDetailByPipeline.Clear();
                swapchainWriterOpByPipeline.Clear();
                swapchainWriterDynamicUiDrawCountByPipeline.Clear();
                swapchainWriterPassByPipeline.Clear();
                swapchainWriterOpIndexByPipeline.Clear();
                pipelineNameByIdentity.Clear();
                meshDrawSlotsByRendererFamily.Clear();
                int writerCapacityHint = Math.Max(1, recordingScratch.RecordSwapchainWriterCapacityHint);
                swapchainWritesByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterLabelByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterDetailByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterOpByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterDynamicUiDrawCountByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterPassByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterOpIndexByPipeline.EnsureCapacity(writerCapacityHint);
                pipelineNameByIdentity.EnsureCapacity(Math.Max(1, recordingScratch.RecordPipelineNameCapacityHint));
                meshDrawSlotsByRendererFamily.EnsureCapacity(Math.Max(1, recordingScratch.RecordMeshDrawSlotCapacityHint));
				meshDrawSlotsByRendererFamily.Clear();
            }

            void RememberPipelineName(in FrameOpContext context)
            {
                if (!pipelineNameByIdentity.ContainsKey(context.PipelineIdentity))
                {
                    string? name = context.PipelineInstance?.Pipeline?.GetType().Name;
                    if (string.IsNullOrWhiteSpace(name))
                        name = "UnknownPipeline";
                    pipelineNameByIdentity[context.PipelineIdentity] = name;
                }
            }

            static string DescribeFrameOpTraceDetails(FrameOp op)
                => op switch
                {
                    ComputeDispatchOp compute =>
                        $" compute='{compute.Program.Data.Name ?? "<unnamed program>"}' groups={compute.GroupsX},{compute.GroupsY},{compute.GroupsZ} uniforms={compute.Snapshot.Uniforms.Count} samplers={compute.Snapshot.Samplers.Count + compute.Snapshot.SamplersByName.Count} images={compute.Snapshot.Images.Count} buffers={compute.Snapshot.Buffers.Count}",
                    ComputeDispatchIndirectOp computeIndirect =>
                        $" computeIndirect='{computeIndirect.Program.Data.Name ?? "<unnamed program>"}' args=0x{computeIndirect.ArgumentBuffer.Handle:X}+{computeIndirect.ArgumentOffset}",
                    BufferCopyOp copy =>
                        $" copy=0x{copy.SourceBuffer.Handle:X}+{copy.SourceOffset}->0x{copy.DestinationBuffer.Handle:X}+{copy.DestinationOffset} bytes={copy.ByteCount}",
                    SubmissionMarkerOp marker => $" marker='{marker.Label}'",
                    IndirectDrawOp indirect =>
                        $" renderer='{indirect.MeshRenderer.MeshRenderer?.Name ?? "<unnamed renderer>"}' draws={indirect.DrawCount} stride={indirect.Stride} offset={indirect.ByteOffset} countOffset={indirect.CountByteOffset} useCount={indirect.UseCount}",
                    QueryOp query =>
                        $" query={query.Operation} descriptor={query.Descriptor}",
                    BlitOp blit =>
                        $" in='{blit.InFbo?.Name ?? "<swapchain>"}' out='{blit.OutFbo?.Name ?? "<swapchain>"}' color={blit.ColorBit} depth={blit.DepthBit} stencil={blit.StencilBit}",
                    _ => string.Empty
                };

            int GetMeshDrawUniformSlot(
                VkMeshRenderer renderer,
                in FrameOpContext context,
                in PendingMeshDraw draw)
            {
                return GetFrameWideMeshDrawUniformSlot(
                    meshDrawSlotsByRendererFamily,
                    meshFrameDataFamilyBases,
                    renderer,
                    commandBufferImageSlot,
                    EVulkanMeshFrameDataStreamKind.Primary,
                    context,
                    draw);
            }

            void MarkSwapchainWriterCore(string writerLabel, int passIndex, int opIndex, int pipelineIdentity)
            {
                swapchainLastWriter = writerLabel;
                swapchainLastWriterPass = passIndex;
                swapchainLastWriterOpIndex = opIndex;
                swapchainWritesByPipeline.TryGetValue(pipelineIdentity, out int count);
                swapchainWritesByPipeline[pipelineIdentity] = count + 1;
                swapchainWriterLabelByPipeline[pipelineIdentity] = writerLabel;
                swapchainWriterPassByPipeline[pipelineIdentity] = passIndex;
                swapchainWriterOpIndexByPipeline[pipelineIdentity] = opIndex;
            }

            void MarkSwapchainFrameOpWriter(string writerLabel, FrameOp op, int passIndex, int opIndex, int pipelineIdentity)
            {
                MarkSwapchainWriterCore(writerLabel, passIndex, opIndex, pipelineIdentity);
                swapchainWriterOpByPipeline[pipelineIdentity] = op;
                swapchainWriterDetailByPipeline.Remove(pipelineIdentity);
                swapchainWriterDynamicUiDrawCountByPipeline.Remove(pipelineIdentity);
            }

            void MarkSwapchainStaticWriter(string writerLabel, string writerDetail, int passIndex, int opIndex, int pipelineIdentity)
            {
                MarkSwapchainWriterCore(writerLabel, passIndex, opIndex, pipelineIdentity);
                swapchainWriterDetailByPipeline[pipelineIdentity] = writerDetail;
                swapchainWriterOpByPipeline.Remove(pipelineIdentity);
                swapchainWriterDynamicUiDrawCountByPipeline.Remove(pipelineIdentity);
            }

            void MarkSwapchainDynamicUiWriter(string writerLabel, int drawCount, int passIndex, int opIndex, int pipelineIdentity)
            {
                MarkSwapchainWriterCore(writerLabel, passIndex, opIndex, pipelineIdentity);
                swapchainWriterDynamicUiDrawCountByPipeline[pipelineIdentity] = drawCount;
                swapchainWriterOpByPipeline.Remove(pipelineIdentity);
                swapchainWriterDetailByPipeline.Remove(pipelineIdentity);
            }

            static bool IsOverlayContext(FrameOpContext context)
                => context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline;

            void CountLogicalSwapchainWriter(FrameOpContext context)
            {
                if (IsOverlayContext(context))
                    overlaySwapchainWriters++;
                else
                    sceneSwapchainWriters++;
            }

            void LogSwapchainWritersByPipeline(string phase)
            {
                if (!VulkanFrameDiagnosticsTraceEnabled)
                    return;

                if (swapchainWritesByPipeline.Count == 0)
                    return;

                TimeSpan logInterval = TimeSpan.FromSeconds(1);
                string summaryKey = $"Vulkan.FrameOpsByPipeline.{phase}.{GetHashCode()}";
                string detailKey = $"Vulkan.FrameOpsByPipeline.{phase}.Details.{GetHashCode()}";
                bool shouldLogSummary = Debug.ShouldLogEvery(summaryKey, logInterval);
                bool shouldLogDetails = Debug.ShouldLogEvery(detailKey, logInterval);
                if (!shouldLogSummary && !shouldLogDetails)
                    return;

                List<KeyValuePair<int, int>> sortedWriters = recordingScratch.SwapchainWriterCountSort;
                sortedWriters.Clear();
                sortedWriters.EnsureCapacity(swapchainWritesByPipeline.Count);
                foreach (KeyValuePair<int, int> pair in swapchainWritesByPipeline)
                    sortedWriters.Add(pair);
                sortedWriters.Sort(static (left, right) => right.Value.CompareTo(left.Value));

                if (shouldLogSummary)
                {
                    StringBuilder builder = recordingScratch.SwapchainWriterSummaryBuilder;
                    builder.Clear();
                    AppendSwapchainWriterSummary(
                        builder,
                        sortedWriters,
                        swapchainWriterLabelByPipeline,
                        pipelineNameByIdentity,
                        maxEntries: 6);
                    Debug.Vulkan(
                        "[Vulkan] Swapchain writers by pipeline ({0}): {1}",
                        phase,
                        builder.ToString());
                }

                if (shouldLogDetails)
                {
                    StringBuilder builder = recordingScratch.SwapchainWriterSummaryBuilder;
                    builder.Clear();
                    AppendSwapchainWriterDetails(
                        builder,
                        sortedWriters,
                        swapchainWriterLabelByPipeline,
                        swapchainWriterDetailByPipeline,
                        swapchainWriterOpByPipeline,
                        swapchainWriterDynamicUiDrawCountByPipeline,
                        swapchainWriterPassByPipeline,
                        swapchainWriterOpIndexByPipeline,
                        maxEntries: 4);
                    Debug.Vulkan(
                        "[Vulkan] Swapchain writer details ({0}): {1}",
                        phase,
                        builder.ToString());
                }
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.OpCensus"))
            {
                int opScanIndex = 0;
                foreach (var op in ops)
                {
                    switch (op)
                    {
                        case ClearOp clear:
                            RememberPipelineName(clear.Context);
                            clearCount++;
                            if (clear.Target is null && (clear.ClearColor || clear.ClearDepth || clear.ClearStencil))
                            {
                                swapchainWriteCount++;
                                swapchainClearWrites++;
                                CountLogicalSwapchainWriter(clear.Context);
                                MarkSwapchainFrameOpWriter(nameof(ClearOp), clear, clear.PassIndex, opScanIndex, clear.Context.PipelineIdentity);
                            }
                            break;
                        case MeshDrawOp meshDraw:
                            RememberPipelineName(meshDraw.Context);
                            drawCount++;
                            meshDrawCount++;
                            if (meshDraw.Target is null)
                            {
                                swapchainWriteCount++;
                                swapchainDrawWrites++;
                                CountLogicalSwapchainWriter(meshDraw.Context);
                                MarkSwapchainFrameOpWriter(nameof(MeshDrawOp), meshDraw, meshDraw.PassIndex, opScanIndex, meshDraw.Context.PipelineIdentity);
                            }
                            else
                            {
                                fboOnlyDrawOps++;
                            }
                            break;
                        case IndirectDrawOp indirectDraw:
                            RememberPipelineName(indirectDraw.Context);
                            drawCount++;
                            indirectDrawCount++;
                            if (indirectDraw.Target is null)
                            {
                                swapchainWriteCount++;
                                swapchainDrawWrites++;
                                CountLogicalSwapchainWriter(indirectDraw.Context);
                                MarkSwapchainFrameOpWriter(nameof(IndirectDrawOp), indirectDraw, indirectDraw.PassIndex, opScanIndex, indirectDraw.Context.PipelineIdentity);
                            }
                            else
                            {
                                fboOnlyDrawOps++;
                            }
                            break;
                        case MeshTaskDispatchIndirectCountOp meshTaskDispatch:
                            RememberPipelineName(meshTaskDispatch.Context);
                            drawCount++;
                            meshTaskDispatchCount++;
                            swapchainWriteCount++;
                            swapchainDrawWrites++;
                            CountLogicalSwapchainWriter(meshTaskDispatch.Context);
                            MarkSwapchainFrameOpWriter(nameof(MeshTaskDispatchIndirectCountOp), meshTaskDispatch, meshTaskDispatch.PassIndex, opScanIndex, meshTaskDispatch.Context.PipelineIdentity);
                            break;
                        case BlitOp blit:
                            RememberPipelineName(blit.Context);
                            blitCount++;
                            if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                            {
                                swapchainWriteCount++;
                                swapchainBlitWrites++;
                                CountLogicalSwapchainWriter(blit.Context);
                                MarkSwapchainFrameOpWriter(nameof(BlitOp), blit, blit.PassIndex, opScanIndex, blit.Context.PipelineIdentity);
                            }
                            else
                            {
                                fboOnlyBlitOps++;
                            }
                            break;
                        case ComputeDispatchOp or ComputeDispatchIndirectOp: computeCount++; break;
                        case DlssUpscaleOp: computeCount++; break;
                        case DlssFrameGenerationOp: computeCount++; break;
                    }

                    if (FrameOpTraceEnabled)
                    {
                        Debug.Vulkan(
                            "[VulkanFrameOp] index={0} op={1} pass={2} passName='{3}' target='{4}' targetId={5} pipe={6} vp={7} sched={8}{9}",
                            opScanIndex,
                            op.GetType().Name,
                            op.PassIndex,
                            TryGetPassName(op) ?? "<unknown>",
                            ResolveCommandChainTargetName(op),
                            ResolveCommandChainTargetIdentity(op),
                            op.Context.PipelineIdentity,
                            op.Context.ViewportIdentity,
                            op.Context.SchedulingIdentity,
                            DescribeFrameOpTraceDetails(op));
                    }

                    opScanIndex++;
                }

                RecordVulkanFrameOpCensus(
                    ops,
                    clearCount,
                    meshDrawCount,
                    indirectDrawCount,
                    meshTaskDispatchCount,
                    blitCount,
                    computeCount,
                    swapchainWriteCount,
                    fboOnlyDrawOps + fboOnlyBlitOps);
                if (FrameOpTraceEnabled)
                    CaptureLastFrameOpTrace(ops);

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.FrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] FrameOps: total={0} clears={1} draws={2} blits={3} computes={4} swapchainWrites={5} (C{6}/D{7}/B{8}) VkReq={9} VkCull={10} VkEmit={11} VkConsume={12} GpuVisible(O/M/A/E)={13}/{14}/{15}/{16}",
                        ops.Length,
                        clearCount,
                        drawCount,
                        blitCount,
                        computeCount,
                        swapchainWriteCount,
                        swapchainClearWrites,
                        swapchainDrawWrites,
                        swapchainBlitWrites,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanRequestedDraws,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanCulledDraws,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanEmittedIndirectDraws,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanConsumedDraws,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible);
                }

                LogSwapchainWritersByPipeline("PreOverlay");
            }

            bool renderPassActive = false;
            bool activeDynamicRendering = false;
            XRFrameBuffer? activeTarget = null;
            VkRenderQuery? activeInlineQuery = null;
            bool activeInlineQueryRecordedDraw = false;
            RenderPass activeRenderPass = default;
            Framebuffer activeFramebuffer = default;
            DynamicRenderingFormatSignature activeDynamicRenderingFormats = default;
            FrameBufferAttachmentSignature[]? activeFboAttachmentSignature = null;
            Rect2D activeRenderArea = default;
            bool activeDepthStencilReadOnly = false;
            int activePassIndex = int.MinValue;
            int activeSchedulingIdentity = int.MinValue;
            FrameOpContext activeContext = default;
            bool hasActiveContext = false;
            FrameOpContext plannerContext = default;
            bool hasPlannerContext = false;
            bool renderPassLabelActive = false;
            bool passIndexLabelActive = false;
            RuntimeEngine.Rendering.RenderingPipelineOverrideScope activePipelineOverrideScope = default;
            bool activePipelineOverrideScopeSet = false;

            // Track whether the swapchain has already had its first render pass
            // this frame. Subsequent re-entries (e.g. after a compute dispatch
            // forced EndActiveRenderPass) use LoadOp.Load to preserve contents
            // instead of clearing the composited scene.
            bool swapchainClearedThisFrame = false;

            bool skipUiPipelineOps = XREngine.Rendering.RenderDiagnosticsFlags.VkSkipUiPipeline;
            bool skipUiBatchTextOps = XREngine.Rendering.RenderDiagnosticsFlags.VkSkipUiBatchText;

            // Track swapchain writes that happen outside a swapchain render pass
            // (e.g. CmdBlitImage to swapchain). If true, the first swapchain render
            // pass this frame must Load existing color instead of clearing.
            bool swapchainWrittenOutsideRenderPass = false;
            int actualSwapchainWriteCount = 0;

            // Track per-FBO attachment layouts across render-pass restarts within
            // the current command buffer.  On first use the layouts are null
            // (â†’ initialLayout = Undefined);  after EndActiveRenderPass we store
            // the finalLayout of each attachment so the next BeginRenderPassForTarget
            // can set initialLayout correctly and preserve content across passes.
            Dictionary<XRFrameBuffer, ImageLayout[]> fboLayoutTracking = recordingScratch.FboLayoutTracking;
            fboLayoutTracking.Clear();
            fboLayoutTracking.EnsureCapacity(Math.Max(1, recordingScratch.RecordFboLayoutCapacityHint));

            ImageLayout ResolveCurrentSwapchainColorLayout()
                => swapchainFinalLayout;

            static PipelineStageFlags ResolveSwapchainLayoutStage(ImageLayout layout)
                => layout switch
                {
                    // The acquired image semaphore is waited at graphics stages. Put
                    // the first layout transition in that wait scope as well.
                    ImageLayout.Undefined => PipelineStageFlags.ColorAttachmentOutputBit,
                    ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                    ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                    ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                    _ => PipelineStageFlags.AllCommandsBit,
                };

            static AccessFlags ResolveSwapchainLayoutAccess(ImageLayout layout)
                => layout switch
                {
                    ImageLayout.Undefined => 0,
                    ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                    ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                    ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                    ImageLayout.PresentSrcKhr => 0,
                    _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                };

            void EmitPlannedSwapchainBarriers(
                CommandBuffer targetCommandBuffer,
                IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier>? plannedBarriers)
            {
                if (plannedBarriers is null || plannedBarriers.Count == 0 || !swapchainTarget.IsValid)
                    return;

                for (int i = 0; i < plannedBarriers.Count; i++)
                {
                    VulkanBarrierPlanner.PlannedSwapchainBarrier planned = plannedBarriers[i];
                    ImageLayout liveOldLayout = ResolveCurrentSwapchainColorLayout();
                    ImageLayout nextLayout = planned.Next.Layout;

                    if (nextLayout == ImageLayout.Undefined)
                        continue;

                    if (liveOldLayout != nextLayout)
                    {
                        PipelineStageFlags srcStages = ResolveSwapchainLayoutStage(liveOldLayout);
                        PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);
                        ImageMemoryBarrier barrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = FilterAccessFlagsForStages(ResolveSwapchainLayoutAccess(liveOldLayout), srcStages),
                            DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, dstStages),
                            OldLayout = liveOldLayout,
                            NewLayout = nextLayout,
                            SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                            DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                            Image = swapchainTarget.Image,
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        CmdPipelineBarrierTracked(
                            targetCommandBuffer,
                            srcStages,
                            dstStages,
                            DependencyFlags.None,
                            0,
                            null,
                            0,
                            null,
                            1,
                            &barrier);
                    }

                    swapchainInColorAttachmentLayout = nextLayout == ImageLayout.ColorAttachmentOptimal;
                    swapchainFinalLayout = nextLayout;
                }
            }

            void ApplyPipelineOverride(in FrameOpContext context)
            {
                if (activePipelineOverrideScopeSet)
                    activePipelineOverrideScope.Dispose();
                activePipelineOverrideScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
                activePipelineOverrideScopeSet = true;
            }

            void TransitionSwapchainToPresent()
            {
                if (!swapchainInColorAttachmentLayout || !swapchainTarget.IsValid)
                    return;

                if (swapchainFinalTargetLayout == ImageLayout.ColorAttachmentOptimal)
                {
                    swapchainInColorAttachmentLayout = false;
                    swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                    return;
                }

                ImageMemoryBarrier presentBarrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                    DstAccessMask = 0,
                    OldLayout = ImageLayout.ColorAttachmentOptimal,
                    NewLayout = swapchainFinalTargetLayout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = swapchainTarget.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    PipelineStageFlags.BottomOfPipeBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &presentBarrier);
                swapchainPresentTransitions++;
                swapchainInColorAttachmentLayout = false;
                swapchainFinalLayout = swapchainFinalTargetLayout;
            }

            void EnsureSwapchainColorAttachmentLayoutForBlit()
            {
                if (!swapchainTarget.IsValid)
                    return;

                ImageLayout oldLayout = ResolveCurrentSwapchainColorLayout();
                if (oldLayout == ImageLayout.ColorAttachmentOptimal)
                {
                    swapchainInColorAttachmentLayout = true;
                    swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                    return;
                }

                ImageMemoryBarrier colorBarrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = ResolveSwapchainLayoutAccess(oldLayout),
                    DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                    OldLayout = oldLayout,
                    NewLayout = ImageLayout.ColorAttachmentOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = swapchainTarget.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    ResolveSwapchainLayoutStage(oldLayout),
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &colorBarrier);

                swapchainInColorAttachmentLayout = true;
                swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
            }

            void TransitionUnwrittenSwapchainToPresent()
            {
                if (!transitionSwapchainToPresent || !swapchainTarget.IsValid)
                    return;

                if (swapchainInColorAttachmentLayout)
                {
                    TransitionSwapchainToPresent();
                    return;
                }

                ImageLayout oldLayout = ResolveCurrentSwapchainColorLayout();
                if (oldLayout == ImageLayout.PresentSrcKhr)
                {
                    swapchainFinalLayout = ImageLayout.PresentSrcKhr;
                    return;
                }

                ImageMemoryBarrier presentBarrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = ResolveSwapchainLayoutAccess(oldLayout),
                    DstAccessMask = 0,
                    OldLayout = oldLayout,
                    NewLayout = ImageLayout.PresentSrcKhr,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = swapchainTarget.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    ResolveSwapchainLayoutStage(oldLayout),
                    PipelineStageFlags.BottomOfPipeBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &presentBarrier);
                swapchainFinalLayout = ImageLayout.PresentSrcKhr;
            }

            bool TryRefreshUnwrittenSwapchainFromLastWindowPresentSource()
            {
                XRFrameBuffer? sourceFrameBuffer = _lastWindowPresentFrameBuffer;
                string? unavailableReason = sourceFrameBuffer is null
                    ? $"no tracked source framebuffer; colorTexture='{_lastWindowPresentColorTexture?.Name ?? "<null>"}'"
                    : !swapchainTarget.IsValid
                        ? "swapchain target is invalid"
                        : sourceFrameBuffer.Width == 0 || sourceFrameBuffer.Height == 0
                            ? $"tracked source framebuffer '{sourceFrameBuffer.Name ?? "<unnamed fbo>"}' has zero size {sourceFrameBuffer.Width}x{sourceFrameBuffer.Height}"
                            : swapchainRecordExtent.Width == 0 || swapchainRecordExtent.Height == 0
                                ? $"swapchain record extent is zero {swapchainRecordExtent.Width}x{swapchainRecordExtent.Height}"
                                : null;
                if (unavailableReason is not null)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.LastPresentRefresh.Unavailable.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Unable to refresh unwritten swapchain image from last present source: {0}.",
                        unavailableReason);
                    return false;
                }

                if (sourceFrameBuffer is null)
                    return false;

                EnsureSwapchainColorAttachmentLayoutForBlit();

                int passIndex = activePassIndex != int.MinValue
                    ? activePassIndex
                    : VulkanBarrierPlanner.SwapchainPassIndex;
                FrameOpContext blitContext = _lastWindowPresentFrameOpContext ?? (hasActiveContext ? activeContext : initialContext);
                BlitOp replayBlit = new(
                    passIndex,
                    sourceFrameBuffer,
                    null,
                    0,
                    0,
                    sourceFrameBuffer.Width,
                    sourceFrameBuffer.Height,
                    0,
                    0,
                    swapchainRecordExtent.Width,
                    swapchainRecordExtent.Height,
                    EReadBufferMode.ColorAttachment0,
                    ColorBit: true,
                    DepthBit: false,
                    StencilBit: false,
                    LinearFilter: true,
                    blitContext);

                bool blitRecorded;
                CmdBeginLabel(commandBuffer, "RefreshSwapchainFromLastPresentSource");
                using (EnterFrameOpResourcePlannerReadbackScope(blitContext))
                {
                    bool canResolveRefreshSource = TryResolveBlitImage(
                        sourceFrameBuffer,
                        imageIndex,
                        EReadBufferMode.ColorAttachment0,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out _,
                        isSource: true);
                    bool canResolveRefreshDestination = TryResolveBlitImage(
                        null,
                        imageIndex,
                        EReadBufferMode.ColorAttachment0,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out _,
                        isSource: false,
                        in swapchainTarget);
                    if (!canResolveRefreshSource || !canResolveRefreshDestination)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.LastPresentRefresh.ResolveFailure.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Unable to refresh unwritten swapchain image from last present source: resolve source={0} destination={1} sourceFbo='{2}' colorTexture='{3}' imageIndex={4}.",
                            canResolveRefreshSource,
                            canResolveRefreshDestination,
                            sourceFrameBuffer.Name ?? "<unnamed fbo>",
                            _lastWindowPresentColorTexture?.Name ?? "<null>",
                            imageIndex);
                    }

                    blitRecorded = RecordBlitOp(commandBuffer, imageIndex, replayBlit, in swapchainTarget);
                }
                CmdEndLabel(commandBuffer);
                if (!blitRecorded)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.LastPresentRefresh.BlitRejected.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Unable to refresh unwritten swapchain image from last present source: blit from '{0}' was not recorded.",
                        sourceFrameBuffer.Name ?? "<unnamed fbo>");
                    return false;
                }

                swapchainWrittenOutsideRenderPass = true;
                swapchainInColorAttachmentLayout = true;
                swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                swapchainWriteCount++;
                actualSwapchainWriteCount++;
                swapchainBlitWrites++;
                sceneSwapchainWriters++;
                MarkSwapchainStaticWriter(
                    "LastPresentSourceBlit",
                    $"refreshed acquired swapchain image from '{sourceFrameBuffer.Name ?? "<unnamed fbo>"}'",
                    passIndex,
                    ops.Length,
                    blitContext.PipelineIdentity);
                return true;
            }

            void EndActiveRenderPass(bool finalClose = false)
            {
                if (!renderPassActive)
                {
                    if (finalClose && !preserveSwapchainForOverlay)
                        TransitionSwapchainToPresent();
                    return;
                }

                bool transitionSwapchainToPresent = activeDynamicRendering && activeTarget is null;
                if (activeInlineQuery is not null)
                {
                    if (!activeInlineQueryRecordedDraw)
                        queryFrameOpsRequireRerecordLocal = true;
                    activeInlineQuery.EndQuery(commandBuffer);
                    activeInlineQuery.InvalidateRecordedResultEpoch(commandBuffer);
                    Debug.VulkanWarningEvery(
                        $"Vulkan.InterruptedInlineQuery.{activeInlineQuery.GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Inline occlusion query was interrupted by a render-scope transition; this epoch will resolve visible. Query='{0}'.",
                        activeInlineQuery.Data.Name ?? "<unnamed>");
                    activeInlineQuery = null;
                    activeInlineQueryRecordedDraw = false;
                }

                if (activeDynamicRendering)
                {
                    CmdEndDynamicRendering(commandBuffer);

                    if (transitionSwapchainToPresent)
                    {
                        swapchainInColorAttachmentLayout = true;
                        swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                        if (finalClose && !preserveSwapchainForOverlay)
                            TransitionSwapchainToPresent();
                    }
                    else if (activeTarget is not null && activeFboAttachmentSignature is not null)
                    {
                        VkFrameBuffer? vkFbo = GenericToAPI<VkFrameBuffer>(activeTarget);
                        if (vkFbo is not null)
                        {
                            // Dynamic rendering has just completed. Publish the attachment
                            // accesses (including store-op writes) before the final-layout
                            // barriers query the command-buffer-local synchronization state.
                            RecordFboAttachmentAccessState(
                                commandBuffer,
                                vkFbo,
                                activeFboAttachmentSignature,
                                useReferenceLayouts: true);
                        }

                        TransitionFboAttachmentsForDynamicRendering(
                            commandBuffer,
                            activeTarget,
                            activeFboAttachmentSignature,
                            beginRendering: false);

                        if (vkFbo is not null)
                        {
                            RecordFboAttachmentAccessState(
                                commandBuffer,
                                vkFbo,
                                activeFboAttachmentSignature,
                                useReferenceLayouts: false);
                        }

                        ImageLayout[] finalLayouts = GetFboAttachmentLayoutScratch(activeTarget, activeFboAttachmentSignature.Length);
                        VkFrameBuffer.WriteFinalLayouts(activeFboAttachmentSignature, finalLayouts);
                    }
                }
                else
                {
                    // Update physical group layout tracking for FBO attachment images.
                    // The render pass transitions each attachment from initialLayout to
                    // finalLayout, so after CmdEndRenderPass the images are in their
                    // finalLayout. We update the tracked layout so that subsequent blit
                    // barriers use the correct OldLayout.
                    if (activeTarget is not null)
                    {
                        // Record the finalLayout of each attachment so the NEXT render
                        // pass on this FBO can set initialLayout correctly and preserve
                        // content across pass boundaries.
                        var vkFbo = GenericToAPI<VkFrameBuffer>(activeTarget);
                        if (vkFbo is not null)
                        {
                            int attachmentCount = activeFboAttachmentSignature?.Length ?? (int)vkFbo.AttachmentCount;
                            ImageLayout[] finalLayouts = GetFboAttachmentLayoutScratch(activeTarget, attachmentCount);
                            if (activeFboAttachmentSignature is not null)
                                VkFrameBuffer.WriteFinalLayouts(activeFboAttachmentSignature, finalLayouts);
                            else
                                vkFbo.WriteFinalLayouts(finalLayouts);
                        }
                    }

                    Api!.CmdEndRenderPass(commandBuffer);
                    if (activeTarget is not null && activeFboAttachmentSignature is not null)
                    {
                        VkFrameBuffer? vkFbo = GenericToAPI<VkFrameBuffer>(activeTarget);
                        if (vkFbo is not null)
                        {
                            RecordFboAttachmentAccessState(
                                commandBuffer,
                                vkFbo,
                                activeFboAttachmentSignature,
                                useReferenceLayouts: false);
                        }
                    }
                }

                if (renderPassLabelActive)
                {
                    CmdEndLabel(commandBuffer);
                    renderPassLabelActive = false;
                }
                renderPassActive = false;
                activeDynamicRendering = false;
                activeTarget = null;
                activeRenderPass = default;
                activeFramebuffer = default;
                activeDynamicRenderingFormats = default;
                activeFboAttachmentSignature = null;
                activeRenderArea = default;
                activeDepthStencilReadOnly = false;
            }

            void BeginDynamicRenderingScope(in DynamicRenderingScopePlan plan, bool secondaryContents)
            {
                ReadOnlySpan<DynamicRenderingAttachmentPlan> colorPlans = plan.ColorAttachments;
                RenderingAttachmentInfo* colorAttachments = stackalloc RenderingAttachmentInfo[Math.Max(colorPlans.Length, 1)];
                for (int i = 0; i < colorPlans.Length; i++)
                    colorAttachments[i] = colorPlans[i].ToRenderingAttachmentInfo();

                RenderingAttachmentInfo depthAttachment = plan.HasDepthAttachment
                    ? plan.DepthAttachment.ToRenderingAttachmentInfo()
                    : default;
                RenderingAttachmentInfo stencilAttachment = plan.HasStencilAttachment
                    ? plan.StencilAttachment.ToRenderingAttachmentInfo()
                    : default;

                RenderingInfo renderingInfo = new()
                {
                    SType = StructureType.RenderingInfo,
                    Flags = secondaryContents ? RenderingFlags.ContentsSecondaryCommandBuffersBit : 0,
                    RenderArea = plan.RenderArea,
                    ViewMask = plan.ViewMask,
                    LayerCount = plan.LayerCount,
                    ColorAttachmentCount = (uint)colorPlans.Length,
                    PColorAttachments = colorPlans.Length > 0 ? colorAttachments : null,
                    PDepthAttachment = plan.HasDepthAttachment ? &depthAttachment : null,
                    PStencilAttachment = plan.HasStencilAttachment ? &stencilAttachment : null,
                };

                if (plan.LocalRead.Enabled && SupportsDynamicRenderingLocalRead)
                {
                    DynamicRenderingLocalReadPlan localRead = plan.LocalRead;
                    RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
                    RenderingInputAttachmentIndexInfo localReadInputIndices = default;
                    uint* colorAttachmentLocations = stackalloc uint[Math.Max(colorPlans.Length, 1)];
                    uint* colorInputAttachmentIndices = stackalloc uint[Math.Max(colorPlans.Length, 1)];
                    uint* depthInputAttachmentIndex = stackalloc uint[1];
                    uint* stencilInputAttachmentIndex = stackalloc uint[1];
                    void* localReadPNext = renderingInfo.PNext;

                    if (TryAppendDynamicRenderingLocalReadPNext(
                        in localRead,
                        (uint)colorPlans.Length,
                        ref localReadPNext,
                        &localReadAttachmentLocations,
                        &localReadInputIndices,
                        colorAttachmentLocations,
                        colorInputAttachmentIndices,
                        depthInputAttachmentIndex,
                        stencilInputAttachmentIndex))
                    {
                        renderingInfo.PNext = localReadPNext;
                    }
                }

                CmdBeginDynamicRendering(commandBuffer, &renderingInfo);
            }

            static SampleCountFlags ResolveDynamicRenderingSampleCount(FrameBufferAttachmentSignature[] signatures)
            {
                for (int i = 0; i < signatures.Length; i++)
                {
                    if (signatures[i].Role == AttachmentRole.Color && signatures[i].Samples != default)
                        return signatures[i].Samples;
                }

                for (int i = 0; i < signatures.Length; i++)
                {
                    if (signatures[i].Role != AttachmentRole.Resolve && signatures[i].Samples != default)
                        return signatures[i].Samples;
                }

                return SampleCountFlags.Count1Bit;
            }

            void BeginRenderPassForTarget(XRFrameBuffer? target, int passIndex, in FrameOpContext context, bool secondaryContents = false)
                => BeginRenderingForTarget(target, passIndex, in context, secondaryContents);

            void BeginRenderingForTarget(XRFrameBuffer? target, int passIndex, in FrameOpContext context, bool secondaryContents = false)
            {
                // Assumes no active render pass.
                if (target is null)
                {
                    bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                        swapchainTarget.IsValid;

                    CmdBeginLabel(commandBuffer, useDynamicRendering ? "Rendering:Swapchain" : "RenderPass:Swapchain");
                    renderPassLabelActive = true;

                    if (useDynamicRendering)
                    {
                        // On the first frame for a given swapchain image, it starts in UNDEFINED.
                        // Re-entries within the same command buffer keep the image in color-attachment
                        // layout until the final close transitions it to PresentSrcKhr.
                        ImageLayout colorOldLayout = ResolveCurrentSwapchainColorLayout();

                        // Preserve swapchain contents on re-entry so composited scene is not wiped.
                        bool overlaySwapchainPass = IsOverlayContext(context);
                        bool loadExistingSwapchainColor =
                            swapchainClearedThisFrame ||
                            swapchainWrittenOutsideRenderPass ||
                            (overlaySwapchainPass && imageWasEverPresentedAtRecordStart);
                        AttachmentLoadOp colorLoadOp = loadExistingSwapchainColor
                            ? AttachmentLoadOp.Load
                            : AttachmentLoadOp.Clear;

                        // Depth can always re-clear on re-entry; only the color contents
                        // (the composited scene) need to survive across render pass restarts.
                        AttachmentLoadOp depthLoadOp = AttachmentLoadOp.Clear;

                        ImageMemoryBarrier colorBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = 0,
                            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                            OldLayout = colorOldLayout,
                            NewLayout = ImageLayout.ColorAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapchainTarget.Image,
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        ImageSubresourceRange depthRange = new()
                        {
                            AspectMask = swapchainTarget.DepthAspect,
                            BaseMipLevel = 0,
                            LevelCount = 1,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        };
                        bool hasRecordedDepthState = TryGetRecordedImageAccessState(
                            commandBuffer,
                            swapchainTarget.DepthImage,
                            depthRange,
                            out VulkanImageAccessState recordedDepthState);
                        ImageLayout depthOldLayout = hasRecordedDepthState
                            ? recordedDepthState.Layout
                            : ImageLayout.Undefined;

                        ImageMemoryBarrier depthBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = hasRecordedDepthState
                                ? (AccessFlags)(ulong)recordedDepthState.AccessMask
                                : AccessFlags.None,
                            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = depthOldLayout,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapchainTarget.DepthImage,
                            SubresourceRange = depthRange
                        };

                        ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                        uint preRenderingBarrierCount = 0;
                        if (colorOldLayout != ImageLayout.ColorAttachmentOptimal)
                            preRenderingBarriers[preRenderingBarrierCount++] = colorBarrier;
                        preRenderingBarriers[preRenderingBarrierCount++] = depthBarrier;

                        CmdPipelineBarrierTracked(
                            commandBuffer,
                            PipelineStageFlags.ColorAttachmentOutputBit |
                                (hasRecordedDepthState
                                    ? (PipelineStageFlags)(ulong)recordedDepthState.StageMask
                                    : PipelineStageFlags.None),
                            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            preRenderingBarrierCount,
                            preRenderingBarriers);

                        ClearValue* dynamicClearValues = stackalloc ClearValue[2];
                        ActiveState.WriteClearValues(dynamicClearValues, 2);

                        Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[1];
                        colorAttachmentPlans[0] = new DynamicRenderingAttachmentPlan(
                            swapchainTarget.Image,
                            swapchainTarget.ImageView,
                            swapchainTarget.ImageFormat,
                            ImageAspectFlags.ColorBit,
                            colorOldLayout,
                            ImageLayout.ColorAttachmentOptimal,
                            ImageLayout.PresentSrcKhr,
                            colorLoadOp,
                            AttachmentStoreOp.Store,
                            dynamicClearValues[0]);

                        DynamicRenderingAttachmentPlan depthAttachmentPlan = new(
                            swapchainTarget.DepthImage,
                            swapchainTarget.DepthView,
                            swapchainTarget.DepthFormat,
                            swapchainTarget.DepthAspect,
                            depthOldLayout,
                            ImageLayout.DepthStencilAttachmentOptimal,
                            ImageLayout.DepthStencilAttachmentOptimal,
                            depthLoadOp,
                            AttachmentStoreOp.DontCare,
                            dynamicClearValues[1]);

                        DynamicRenderingFormatSignature swapchainDynamicRenderingFormats =
                            CreateSwapchainDynamicRenderingFormatSignature(swapchainTarget.ImageFormat, swapchainTarget.DepthFormat);
                        DynamicRenderingScopePlan scopePlan = new(
                            new Rect2D
                            {
                                Offset = new Offset2D(0, 0),
                                Extent = swapchainTarget.Extent
                            },
                            1u,
                            0u,
                            colorAttachmentPlans,
                            depthAttachmentPlan,
                            true,
                            default,
                            false,
                            false,
                            swapchainDynamicRenderingFormats,
                            SampleCountFlags.Count1Bit);

                        BeginDynamicRenderingScope(in scopePlan, secondaryContents);

                        renderPassActive = true;
                        activeDynamicRendering = true;
                        usedSwapchainDynamicRendering = true;
                        swapchainInColorAttachmentLayout = true;
                        swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                        activeTarget = null;
                        activeRenderPass = default;
                        activeFramebuffer = default;
                        activeDynamicRenderingFormats = scopePlan.FormatSignature;
                        activeFboAttachmentSignature = null;
                        activeRenderArea = scopePlan.RenderArea;
                        activeDepthStencilReadOnly = false;
                        swapchainClearedThisFrame = true;
                        if (TargetTraceEnabled)
                        {
                            Debug.Vulkan(
                                "[VulkanTarget] begin target='<swapchain>' pass={0} passName='{1}' dynamic=true imageIndex={2} colorView=0x{3:X} depthView=0x{4:X} extent={5}x{6} load={7} secondary={8}",
                                passIndex,
                                ResolvePassName(context.PassMetadata, passIndex),
                                imageIndex,
                                swapchainTarget.ImageView.Handle,
                                swapchainTarget.DepthView.Handle,
                                swapchainTarget.Extent.Width,
                                swapchainTarget.Extent.Height,
                                colorLoadOp,
                                secondaryContents);
                        }
                        return;
                    }

                    // Fallback: traditional render pass path.
                    // Use _renderPassLoad (LoadOp.Load) on re-entry to preserve contents.
                    bool legacyOverlaySwapchainPass = IsOverlayContext(context);
                    bool legacyLoadExistingSwapchainColor =
                        swapchainClearedThisFrame ||
                        swapchainWrittenOutsideRenderPass ||
                        (legacyOverlaySwapchainPass && imageWasEverPresentedAtRecordStart);
                    AttachmentLoadOp legacySwapchainLoadOp = legacyLoadExistingSwapchainColor
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear;
                    RenderPass selectedRenderPass = legacyLoadExistingSwapchainColor
                        ? _renderPassLoad
                        : _renderPass;

                    RenderPassBeginInfo renderPassInfo = new()
                    {
                        SType = StructureType.RenderPassBeginInfo,
                        RenderPass = selectedRenderPass,
                        Framebuffer = swapChainFramebuffers![imageIndex],
                        RenderArea = new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = swapchainRecordExtent
                        }
                    };

                    const uint attachmentCount = 2;
                    ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
                    ActiveState.WriteClearValues(clearValues, attachmentCount);
                    renderPassInfo.ClearValueCount = attachmentCount;
                    renderPassInfo.PClearValues = clearValues;

                    CmdBeginRenderPassTracked(
                        commandBuffer,
                        &renderPassInfo,
                        secondaryContents ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline);

                    renderPassActive = true;
                    activeDynamicRendering = false;
                    activeTarget = null;
                    activeRenderPass = selectedRenderPass;
                    activeFramebuffer = swapChainFramebuffers![imageIndex];
                    activeDynamicRenderingFormats = default;
                    activeFboAttachmentSignature = null;
                    activeRenderArea = renderPassInfo.RenderArea;
                    activeDepthStencilReadOnly = false;
                    swapchainClearedThisFrame = true;
                    if (TargetTraceEnabled)
                    {
                        Debug.Vulkan(
                            "[VulkanTarget] begin target='<swapchain>' pass={0} passName='{1}' dynamic=false imageIndex={2} renderPass=0x{3:X} framebuffer=0x{4:X} extent={5}x{6} load={7} secondary={8}",
                            passIndex,
                            ResolvePassName(context.PassMetadata, passIndex),
                            imageIndex,
                            selectedRenderPass.Handle,
                            swapChainFramebuffers![imageIndex].Handle,
                            swapchainRecordExtent.Width,
                            swapchainRecordExtent.Height,
                            legacySwapchainLoadOp,
                            secondaryContents);
                    }
                    return;
                }

                var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target) ?? throw new InvalidOperationException("Failed to resolve Vulkan framebuffer for target.");
                vkFrameBuffer.EnsureCurrent();

                string fboName = string.IsNullOrWhiteSpace(target.Name)
                    ? $"FBO[{target.GetHashCode()}]"
                    : target.Name!;
                if (CanRecordCommandBufferDebugLabels)
                    renderPassLabelActive = CmdBeginLabel(commandBuffer, $"{(UseDynamicRenderingRenderTargets ? "Rendering" : "RenderPass")}:{fboName}");

                // Look up the CURRENT tracked layout of each attachment so the render
                // pass can use those as initialLayout (preserving content) instead of
                // Undefined (which discards content).
                //
                // We always query â€” not just when this FBO was previously bound this
                // frame â€” because attachments can be SHARED across framebuffers (e.g.
                // the deferred GBuffer and the forward pass share the depth/stencil
                // texture).  The forward pass deliberately does not clear depth and
                // relies on the GBuffer-written depth; if we only preserved content
                // for FBOs already seen this frame, the first forward-pass bind would
                // discard the GBuffer depth and every depth-tested draw (skybox,
                // forward meshes, gizmo) would fail.  Querying the per-image tracked
                // layout also accounts for barrier-planner transitions or blits that
                // changed the actual image layout since the last render pass ended.
                bool targetReenteredThisCommandBuffer = fboLayoutTracking.ContainsKey(target);
                ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, vkFrameBuffer);
                // Update the tracking dict so that subsequent users see the
                // same layouts we resolved here.
                if (trackedLayouts is not null)
                    fboLayoutTracking[target] = trackedLayouts;
                FrameBufferAttachmentSignature[] fboSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                    passIndex,
                    context.PassMetadata,
                    trackedLayouts,
                    CompiledRenderGraph.Synchronization,
                    preserveTrackedClearLoads: targetReenteredThisCommandBuffer);
                bool passDepthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(fboSignature);
                if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(fboName))
                {
                    Debug.VulkanEvery(
                        $"DeferredLighting.BeginFBO.{fboName}",
                        TimeSpan.FromSeconds(1),
                        "[DeferredLightingDiag][BeginFBO] name='{0}' pass={1} dynamic={2} trackedLayouts={3} signature={4}",
                        fboName,
                        passIndex,
                        UseDynamicRenderingRenderTargets,
                        trackedLayouts is not null ? string.Join(",", trackedLayouts) : "null",
                        FormatFboAttachmentSignature(fboSignature));
                }

                Extent2D logicalFboExtent = ResolveFrameBufferDrawExtent(target);
                uint fboRenderWidth = logicalFboExtent.Width;
                uint fboRenderHeight = logicalFboExtent.Height;
                if (vkFrameBuffer.FramebufferWidth > 0)
                    fboRenderWidth = Math.Min(fboRenderWidth, vkFrameBuffer.FramebufferWidth);
                if (vkFrameBuffer.FramebufferHeight > 0)
                    fboRenderHeight = Math.Min(fboRenderHeight, vkFrameBuffer.FramebufferHeight);
                Extent2D attachmentCompatibleExtent = vkFrameBuffer.ResolveAttachmentCompatibleDrawExtent();
                if (attachmentCompatibleExtent.Width > 0)
                    fboRenderWidth = Math.Min(fboRenderWidth, attachmentCompatibleExtent.Width);
                if (attachmentCompatibleExtent.Height > 0)
                    fboRenderHeight = Math.Min(fboRenderHeight, attachmentCompatibleExtent.Height);

                Rect2D fboRenderArea = new()
                {
                    Offset = new Offset2D(0, 0),
                    // Use the attachment-compatible extent. Dynamic rendering
                    // validates against image-view dimensions, which can be
                    // smaller than the FBO's base texture dimensions for
                    // reduced-resolution passes or mip-level targets.
                    Extent = new Extent2D(Math.Max(fboRenderWidth, 1u), Math.Max(fboRenderHeight, 1u))
                };

                if (UseDynamicRenderingRenderTargets)
                {
                    if (CommandRecordingDiagnosticsEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.BeginRendering.FBO.{fboName}.{fboSignature.Length}",
                            TimeSpan.FromSeconds(2),
                            "[Vulkan] BeginRendering FBO='{0}' pass={1} attachments={2} fbDims={3}x{4} trackedLayouts={5}",
                            fboName,
                            passIndex,
                            fboSignature.Length,
                            vkFrameBuffer.FramebufferWidth,
                            vkFrameBuffer.FramebufferHeight,
                            trackedLayouts is not null ? string.Join(",", trackedLayouts) : "null");
                    }

                    TransitionFboAttachmentsForDynamicRendering(
                        commandBuffer,
                        target,
                        fboSignature,
                        beginRendering: true);
                    uint dynamicAttachmentCountFbo = Math.Max((uint)fboSignature.Length, 1u);
                    ClearValue* dynamicClearValuesFbo = stackalloc ClearValue[(int)dynamicAttachmentCountFbo];
                    vkFrameBuffer.WriteClearValues(dynamicClearValuesFbo, dynamicAttachmentCountFbo, fboSignature);

                    int colorAttachmentCount = 0;
                    for (int i = 0; i < fboSignature.Length; i++)
                    {
                        if (fboSignature[i].Role == AttachmentRole.Color)
                            colorAttachmentCount++;
                    }

                    Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[Math.Max(colorAttachmentCount, 1)];
                    Span<uint> colorAttachmentSourceIndices = stackalloc uint[Math.Max(colorAttachmentCount, 1)];
                    Span<DynamicRenderingAttachmentPlan> resolveAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[Math.Max(fboSignature.Length, 1)];
                    Span<uint> resolveAttachmentSourceIndices = stackalloc uint[Math.Max(fboSignature.Length, 1)];
                    int colorAttachmentIndex = 0;
                    int resolveAttachmentCount = 0;
                    DynamicRenderingAttachmentPlan depthAttachmentPlan = default;
                    DynamicRenderingAttachmentPlan stencilAttachmentPlan = default;
                    bool hasDepthAttachment = false;
                    bool hasStencilAttachment = false;

                    for (int i = 0; i < fboSignature.Length; i++)
                    {
                        if (!vkFrameBuffer.TryGetAttachmentView(i, out ImageView view))
                            throw new InvalidOperationException($"Framebuffer '{fboName}' attachment {i} has no valid Vulkan image view.");

                        FrameBufferAttachmentSignature signature = fboSignature[i];
                        Image attachmentImage = default;
                        if (TryGetDescriptorHeapImageViewCreateInfo(view, out ImageViewCreateInfo attachmentViewInfo) &&
                            attachmentViewInfo.Image.Handle != 0)
                        {
                            attachmentImage = attachmentViewInfo.Image;
                        }
                        else if (vkFrameBuffer.TryGetAttachmentTarget(
                                i,
                                out IFrameBufferAttachement? attachmentTarget,
                                out _,
                                out int attachmentMipLevel,
                                out int attachmentLayerIndex) &&
                            TryResolveAttachmentImage(
                                attachmentTarget,
                                attachmentMipLevel,
                                attachmentLayerIndex,
                                NormalizeBarrierAspectMask(signature.Format, signature.AspectMask),
                                out BlitImageInfo imageInfo) &&
                            imageInfo.Image.Handle != 0)
                        {
                            attachmentImage = imageInfo.Image;
                        }

                        DynamicRenderingAttachmentPlan attachmentPlan = new(
                            attachmentImage,
                            view,
                            signature.Format,
                            signature.AspectMask,
                            signature.InitialLayout,
                            signature.ReferenceLayout,
                            signature.FinalLayout,
                            signature.LoadOp,
                            signature.StoreOp,
                            dynamicClearValuesFbo[i]);

                        if (signature.Role == AttachmentRole.Color)
                        {
                            colorAttachmentPlans[colorAttachmentIndex] = attachmentPlan;
                            colorAttachmentSourceIndices[colorAttachmentIndex] = signature.ColorIndex;
                            colorAttachmentIndex++;
                            continue;
                        }

                        if (signature.Role == AttachmentRole.Resolve)
                        {
                            resolveAttachmentPlans[resolveAttachmentCount] = attachmentPlan;
                            resolveAttachmentSourceIndices[resolveAttachmentCount] = signature.ColorIndex;
                            resolveAttachmentCount++;
                            continue;
                        }

                        if (signature.Role is AttachmentRole.Depth or AttachmentRole.DepthStencil &&
                            (signature.AspectMask & ImageAspectFlags.DepthBit) != 0)
                        {
                            depthAttachmentPlan = attachmentPlan;
                            hasDepthAttachment = true;
                        }

                        if (signature.Role is AttachmentRole.Stencil or AttachmentRole.DepthStencil &&
                            (signature.AspectMask & ImageAspectFlags.StencilBit) != 0)
                        {
                            stencilAttachmentPlan = new DynamicRenderingAttachmentPlan(
                                attachmentImage,
                                view,
                                signature.Format,
                                signature.AspectMask,
                                signature.InitialLayout,
                                signature.ReferenceLayout,
                                signature.FinalLayout,
                                signature.StencilLoadOp,
                                signature.StencilStoreOp,
                                dynamicClearValuesFbo[i]);
                            hasStencilAttachment = true;
                        }
                    }

                    for (int resolveIndex = 0; resolveIndex < resolveAttachmentCount; resolveIndex++)
                    {
                        uint sourceColorIndex = resolveAttachmentSourceIndices[resolveIndex];
                        int sourcePlanIndex = -1;
                        for (int colorIndex = 0; colorIndex < colorAttachmentCount; colorIndex++)
                        {
                            if (colorAttachmentSourceIndices[colorIndex] == sourceColorIndex)
                            {
                                sourcePlanIndex = colorIndex;
                                break;
                            }
                        }

                        if (sourcePlanIndex < 0)
                        {
                            throw new InvalidOperationException(
                                $"Framebuffer '{fboName}' has a resolve attachment for color {sourceColorIndex}, but the dynamic rendering scope has no matching color source.");
                        }

                        colorAttachmentPlans[sourcePlanIndex] = colorAttachmentPlans[sourcePlanIndex].WithResolve(
                            in resolveAttachmentPlans[resolveIndex],
                            ResolveModeFlags.AverageBit);
                    }

                    uint fboViewMask = vkFrameBuffer.MultiviewViewMask;
                    uint fboLayerCount = ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask);
                    DynamicRenderingFormatSignature targetDynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                        fboSignature,
                        fboViewMask,
                        fboLayerCount);

                    DynamicRenderingScopePlan scopePlan = new(
                        fboRenderArea,
                        fboLayerCount,
                        fboViewMask,
                        colorAttachmentPlans[..colorAttachmentCount],
                        depthAttachmentPlan,
                        hasDepthAttachment,
                        stencilAttachmentPlan,
                        hasStencilAttachment,
                        passDepthStencilReadOnly,
                        targetDynamicRenderingFormats,
                        ResolveDynamicRenderingSampleCount(fboSignature));

                    BeginDynamicRenderingScope(in scopePlan, secondaryContents);

                    renderPassActive = true;
                    activeDynamicRendering = true;
                    activeTarget = target;
                    activeRenderPass = default;
                    activeFramebuffer = default;
                    activeDynamicRenderingFormats = scopePlan.FormatSignature;
                    activeFboAttachmentSignature = fboSignature;
                    activeRenderArea = scopePlan.RenderArea;
                    activeDepthStencilReadOnly = passDepthStencilReadOnly;
                    if (TargetTraceEnabled)
                    {
                        Debug.Vulkan(
                            "[VulkanTarget] begin target='{0}' targetId={1} pass={2} passName='{3}' dynamic=true framebuffer=0x{4:X} attachments={5} extent={6}x{7} layers={8} viewMask=0x{9:X} formats={10} secondary={11}",
                            fboName,
                            target.GetHashCode(),
                            passIndex,
                            ResolvePassName(context.PassMetadata, passIndex),
                            vkFrameBuffer.FrameBuffer.Handle,
                            fboSignature.Length,
                            scopePlan.RenderArea.Extent.Width,
                            scopePlan.RenderArea.Extent.Height,
                            scopePlan.LayerCount,
                            scopePlan.ViewMask,
                            activeDynamicRenderingFormats,
                            secondaryContents);
                    }
                    return;
                }

                // Keep the legacy fallback on the same explicit layout contract as
                // dynamic rendering. This removes the fragile dependency on cached
                // render-pass initial layouts when physical images are reused by a
                // newly compiled render graph.
                TransitionFboAttachmentsForDynamicRendering(
                    commandBuffer,
                    target,
                    fboSignature,
                    beginRendering: true);
                fboSignature = CreateLegacyRenderPassSignature(fboSignature);
                RenderPass passRenderPass = GetOrCreateFrameBufferRenderPass(fboSignature);

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                    $"Vulkan.BeginRP.FBO.{fboName}.{passRenderPass.Handle:X}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] BeginRenderPassForTarget FBO='{0}' pass={1} renderPass=0x{2:X} attachments={3} fbDims={4}x{5} trackedLayouts={6}",
                    fboName,
                    passIndex,
                    passRenderPass.Handle,
                    vkFrameBuffer.AttachmentCount,
                    vkFrameBuffer.FramebufferWidth,
                    vkFrameBuffer.FramebufferHeight,
                    trackedLayouts is not null ? string.Join(",", trackedLayouts) : "null");
                }
                RenderPassBeginInfo fboPassInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = passRenderPass,
                    Framebuffer = vkFrameBuffer.FrameBuffer,
                    RenderArea = fboRenderArea
                };

                uint attachmentCountFbo = Math.Max(vkFrameBuffer.AttachmentCount, 1u);
                ClearValue* clearValuesFbo = stackalloc ClearValue[(int)attachmentCountFbo];
                vkFrameBuffer.WriteClearValues(clearValuesFbo, attachmentCountFbo);
                fboPassInfo.ClearValueCount = attachmentCountFbo;
                fboPassInfo.PClearValues = clearValuesFbo;

                CmdBeginRenderPassTracked(
                    commandBuffer,
                    &fboPassInfo,
                    secondaryContents ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline);
                RecordFboAttachmentAccessState(
                    commandBuffer,
                    vkFrameBuffer,
                    fboSignature,
                    useReferenceLayouts: true);

                renderPassActive = true;
                activeDynamicRendering = false;
                activeTarget = target;
                activeRenderPass = passRenderPass;
                activeFramebuffer = vkFrameBuffer.FrameBuffer;
                activeDynamicRenderingFormats = default;
                activeFboAttachmentSignature = fboSignature;
                activeRenderArea = fboPassInfo.RenderArea;
                activeDepthStencilReadOnly = passDepthStencilReadOnly;
                if (TargetTraceEnabled)
                {
                    Debug.Vulkan(
                    "[VulkanTarget] begin target='{0}' targetId={1} pass={2} passName='{3}' dynamic=false renderPass=0x{4:X} framebuffer=0x{5:X} attachments={6} extent={7}x{8} secondary={9} signature={10}",
                        fboName,
                        target.GetHashCode(),
                        passIndex,
                        ResolvePassName(context.PassMetadata, passIndex),
                        passRenderPass.Handle,
                        vkFrameBuffer.FrameBuffer.Handle,
                        vkFrameBuffer.AttachmentCount,
                        fboPassInfo.RenderArea.Extent.Width,
                        fboPassInfo.RenderArea.Extent.Height,
                    secondaryContents,
                    FormatFboAttachmentSignature(fboSignature));
                }
            }

            bool RecordMeshDrawIntoCommandBuffer(
                CommandBuffer targetCommandBuffer,
                MeshDrawOp drawOp,
                int passIndex,
                int? drawUniformSlotOverride = null)
            {
                using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(drawOp.Context);
                Viewport viewport = drawOp.Draw.Viewport;
                Rect2D scissor = drawOp.Draw.Scissor;
                uint viewportScissorCount = drawOp.Draw.ViewportScissorCount;
                if (viewportScissorCount > 1 &&
                    drawOp.Draw.IndexedViewports is { } indexedViewports &&
                    drawOp.Draw.IndexedScissors is { } indexedScissors &&
                    indexedViewports.Length >= (int)viewportScissorCount &&
                    indexedScissors.Length >= (int)viewportScissorCount)
                {
                    SetViewportScissorTracked(targetCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
                }
                else
                {
                    SetViewportScissorTracked(targetCommandBuffer, viewport, scissor);
                }

                if (CommandRecordingDiagnosticsEnabled && drawOp.Target?.Name == "ForwardPassFBO")
                {
                    Debug.VulkanEvery(
                        "Vulkan.FwdDraw." + passIndex,
                        TimeSpan.FromSeconds(2),
                        "[Vulkan][FwdDraw] pipe='{0}' pass={1} rp=0x{2:X} vp=(x={3},y={4},w={5},h={6})",
                        drawOp.Context.PipelineInstance?.DebugName ?? "?",
                        passIndex, activeRenderPass.Handle,
                        viewport.X, viewport.Y, viewport.Width, viewport.Height);
                }

                string? drawTargetName = drawOp.Target?.Name;
                if (DeferredLightingDiagnostics.Enabled &&
                    (string.Equals(drawTargetName, DefaultRenderPipeline.DeferredGBufferFBOName, StringComparison.Ordinal) ||
                     string.Equals(drawTargetName, DefaultRenderPipeline.MsaaGBufferFBOName, StringComparison.Ordinal)))
                {
                    var draw = drawOp.Draw;
                    var material = draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
                    string gBufferTargetName = drawTargetName ?? "<unknown>";
                    Debug.VulkanEvery(
                        $"DeferredLighting.GBufferDraw.{gBufferTargetName}.{passIndex}.{draw.Renderer.GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[DeferredLightingDiag][GBufferDraw] target='{0}' pass={1} dyn={2} rp=0x{3:X} colors={4} depthFmt={5} layers={6} viewMask=0x{7:X} dsReadOnly={8} mesh='{9}' material='{10}' program='{11}' stereo={12} colorMask={13} blend={14} depth={15}/{16}/{17} cull={18} front={19} vp=({20},{21},{22},{23}) scissor=({24},{25},{26},{27}) pipe={28} pipeName='{29}' camera='{30}' camPos=({31},{32},{33}) camFwd=({34},{35},{36}) vpM=({37},{38},{39},{40})",
                        gBufferTargetName,
                        passIndex,
                        activeDynamicRendering,
                        activeRenderPass.Handle,
                        activeDynamicRendering ? activeDynamicRenderingFormats.DescribeColorFormats() : "<render-pass>",
                        activeDynamicRendering ? activeDynamicRenderingFormats.DepthAttachmentFormat : Format.Undefined,
                        activeDynamicRendering ? activeDynamicRenderingFormats.LayerCount : 1u,
                        activeDynamicRendering ? activeDynamicRenderingFormats.ViewMask : 0u,
                        activeDepthStencilReadOnly,
                        draw.Renderer.Mesh?.Name ?? draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                        material?.Name ?? "<unnamed material>",
                        draw.PreparedProgram?.Data?.Name ?? "<uncaptured program>",
                        draw.IsStereoPass,
                        draw.ColorWriteMask,
                        draw.BlendEnabled,
                        draw.DepthTestEnabled,
                        draw.DepthWriteEnabled,
                        draw.DepthCompareOp,
                        draw.CullMode,
                        draw.FrontFace,
                        viewport.X,
                        viewport.Y,
                        viewport.Width,
                        viewport.Height,
                        scissor.Offset.X,
                        scissor.Offset.Y,
                        scissor.Extent.Width,
                        scissor.Extent.Height,
                        drawOp.Context.PipelineIdentity,
                        drawOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                        draw.Camera?.GetType().Name ?? "<no camera>",
                        draw.CameraPosition.X,
                        draw.CameraPosition.Y,
                        draw.CameraPosition.Z,
                        draw.CameraForward.X,
                        draw.CameraForward.Y,
                        draw.CameraForward.Z,
                        draw.ViewProjectionMatrix.M11,
                        draw.ViewProjectionMatrix.M22,
                        draw.ViewProjectionMatrix.M33,
                        draw.ViewProjectionMatrix.M44);
                }

                int drawUniformSlot = drawUniformSlotOverride ??
                    GetMeshDrawUniformSlot(drawOp.Draw.Renderer, drawOp.Context, drawOp.Draw);
                bool recordedDraw = drawOp.Draw.Renderer.RecordDraw(
                    targetCommandBuffer,
                    drawOp.Draw,
                    activeRenderPass,
                    activeDynamicRendering,
                    activeDynamicRenderingFormats,
                    passIndex,
                    drawOp.Context.PassMetadata,
                    activeDepthStencilReadOnly,
                    drawOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                    drawOp.Target?.Name ?? "<swapchain>",
                    drawUniformSlot,
                    commandBufferImageSlot);

                if (DeferredLightingDiagnostics.Enabled &&
                    (string.Equals(drawTargetName, DefaultRenderPipeline.DeferredGBufferFBOName, StringComparison.Ordinal) ||
                     string.Equals(drawTargetName, DefaultRenderPipeline.MsaaGBufferFBOName, StringComparison.Ordinal)))
                {
                    Debug.VulkanEvery(
                        $"DeferredLighting.GBufferDraw.Result.{drawTargetName}.{passIndex}.{drawOp.Draw.Renderer.GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[DeferredLightingDiag][GBufferDraw.Result] target='{0}' pass={1} recorded={2} slot={3} blocker='{4}'",
                        drawTargetName ?? "<unknown>",
                        passIndex,
                        recordedDraw,
                        drawUniformSlot,
                        recordedDraw
                            ? "<none>"
                            : drawOp.Draw.Renderer.DescribeReusableCommandBufferFrameDataBlocker(drawOp.Draw, drawUniformSlot));
                }

                return recordedDraw;
            }

            void RecordIndirectDrawIntoCommandBuffer(CommandBuffer targetCommandBuffer, IndirectDrawOp indirectOp, int passIndex)
            {
                using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(indirectOp.Context);
                Viewport viewport = indirectOp.Draw.Viewport;
                Rect2D scissor = indirectOp.Draw.Scissor;
                uint viewportScissorCount = indirectOp.Draw.ViewportScissorCount;
                if (viewportScissorCount > 1 &&
                    indirectOp.Draw.IndexedViewports is { } indexedViewports &&
                    indirectOp.Draw.IndexedScissors is { } indexedScissors &&
                    indexedViewports.Length >= (int)viewportScissorCount &&
                    indexedScissors.Length >= (int)viewportScissorCount)
                {
                    SetViewportScissorTracked(targetCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
                }
                else
                {
                    SetViewportScissorTracked(targetCommandBuffer, viewport, scissor);
                }

                if (!indirectOp.MeshRenderer.RecordIndirectDrawState(
                        targetCommandBuffer,
                        indirectOp.Draw,
                        activeRenderPass,
                        activeDynamicRendering,
                        activeDynamicRenderingFormats,
                        passIndex,
                        indirectOp.Context.PassMetadata,
                        activeDepthStencilReadOnly,
                        indirectOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                        indirectOp.Target?.Name ?? "<swapchain>",
                        GetMeshDrawUniformSlot(indirectOp.MeshRenderer, indirectOp.Context, indirectOp.Draw),
                        out _))
                {
                    return;
                }

                RecordIndirectDrawOp(targetCommandBuffer, indirectOp, allowInlineBarrier: false);
            }

            void RecordIndirectDrawIntoSecondaryCommandBuffer(
                CommandBuffer targetCommandBuffer,
                IndirectDrawOp indirectOp,
                in VkMeshRenderer.IndirectDrawRecordingState recordingState,
                int passIndex,
                bool inheritedDynamicRendering,
                RenderPass inheritedRenderPass,
                DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                bool inheritedDepthStencilReadOnly,
                int uniformSlot)
            {
                Viewport viewport = indirectOp.Draw.Viewport;
                Rect2D scissor = indirectOp.Draw.Scissor;
                uint viewportScissorCount = indirectOp.Draw.ViewportScissorCount;
                if (viewportScissorCount > 1 &&
                    indirectOp.Draw.IndexedViewports is { } indexedViewports &&
                    indirectOp.Draw.IndexedScissors is { } indexedScissors &&
                    indexedViewports.Length >= (int)viewportScissorCount &&
                    indexedScissors.Length >= (int)viewportScissorCount)
                {
                    SetViewportScissorTracked(targetCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
                }
                else
                {
                    SetViewportScissorTracked(targetCommandBuffer, viewport, scissor);
                }

                if (!indirectOp.MeshRenderer.RecordPreparedIndirectDrawState(targetCommandBuffer, recordingState))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.IndirectSecondary.PreparedStateMissing.{GetHashCode()}.{indirectOp.MeshRenderer.GetHashCode()}.{uniformSlot}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Skipping indirect secondary draw because prepared immutable recording state is unavailable. mesh='{0}' target='{1}' slot={2}",
                        indirectOp.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                        indirectOp.Target?.Name ?? "<swapchain>",
                        uniformSlot);
                    return;
                }

                RecordIndirectDrawOp(targetCommandBuffer, indirectOp, allowInlineBarrier: false);
            }

            int ResolveRunCandidatePassIndex(MeshDrawOp drawOp)
            {
                if (drawOp.PassIndex == int.MinValue && activePassIndex != int.MinValue)
                    return activePassIndex;

                return drawOp.PassIndex;
            }

            int ResolveIndirectRunCandidatePassIndex(IndirectDrawOp drawOp)
            {
                if (drawOp.PassIndex == int.MinValue && activePassIndex != int.MinValue)
                    return activePassIndex;

                return drawOp.PassIndex;
            }

            int CountContiguousMeshCommandChainRun(int startIndex, MeshDrawOp firstDraw, int passIndex)
            {
                int count = 0;
                for (int i = startIndex; i < ops.Length; i++)
                {
                    if (ops[i] is not MeshDrawOp candidate)
                        break;

                    if (skipUiPipelineOps && candidate.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                        break;
                    if (skipUiBatchTextOps && IsUiBatchTextDrawOp(candidate))
                        break;
                    if (candidate.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                        break;
                    if (candidate.Target != firstDraw.Target)
                        break;
                    if (!AreFrameOpContextsRecordingCompatible(candidate.Context, activeContext))
                        break;
                    if (candidate.Context.SchedulingIdentity != activeSchedulingIdentity)
                        break;
                    if (ResolveRunCandidatePassIndex(candidate) != passIndex)
                        break;

                    count++;
                }

                return count;
            }

            int CountContiguousIndirectCommandChainRun(int startIndex, IndirectDrawOp firstDraw, int passIndex)
            {
                int count = 0;
                for (int i = startIndex; i < ops.Length; i++)
                {
                    if (ops[i] is not IndirectDrawOp candidate)
                        break;
                    if (!AreFrameOpContextsRecordingCompatible(candidate.Context, activeContext))
                        break;
                    if (candidate.Context.SchedulingIdentity != activeSchedulingIdentity)
                        break;
                    if (candidate.Target != firstDraw.Target)
                        break;
                    if (ResolveIndirectRunCandidatePassIndex(candidate) != passIndex)
                        break;

                    count++;
                }

                return count;
            }

            void EmitIndirectDrawRunReadBarrier()
            {
                MemoryBarrier memoryBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit,
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                    PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                    DependencyFlags.None,
                    1,
                    &memoryBarrier,
                    0,
                    null,
                    0,
                    null);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
            }

            bool TryResolveMeshSecondaryInheritance(
                XRFrameBuffer? target,
                int passIndex,
                in FrameOpContext context,
                out bool inheritedDynamicRendering,
                out RenderPass inheritedRenderPass,
                out Framebuffer inheritedFramebuffer,
                out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                out FrameBufferAttachmentSignature[]? inheritedFboAttachmentSignature,
                out bool inheritedDepthStencilReadOnly,
                out SampleCountFlags inheritedSamples)
            {
                inheritedDynamicRendering = false;
                inheritedRenderPass = default;
                inheritedFramebuffer = default;
                inheritedDynamicRenderingFormats = default;
                inheritedFboAttachmentSignature = null;
                inheritedDepthStencilReadOnly = false;
                inheritedSamples = SampleCountFlags.Count1Bit;

                if (target is null)
                {
                    bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                        swapchainTarget.IsValid;

                    if (useDynamicRendering)
                    {
                        inheritedDynamicRendering = true;
                        inheritedDynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(swapchainTarget.ImageFormat, swapchainTarget.DepthFormat);
                        inheritedDepthStencilReadOnly = false;
                        inheritedSamples = SampleCountFlags.Count1Bit;
                        return true;
                    }

                    if (swapChainFramebuffers is null || imageIndex >= swapChainFramebuffers.Length)
                    {
                        LogCommandChainSecondaryInheritanceMismatch(
                            "mesh",
                            null,
                            passIndex,
                            "legacy swapchain framebuffer is unavailable");
                        return false;
                    }

                    inheritedRenderPass = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
                        ? _renderPassLoad
                        : _renderPass;
                    inheritedFramebuffer = swapChainFramebuffers[imageIndex];
                    if (inheritedRenderPass.Handle == 0 || inheritedFramebuffer.Handle == 0)
                    {
                        LogCommandChainSecondaryInheritanceMismatch(
                            "mesh",
                            null,
                            passIndex,
                            $"legacy swapchain inheritance unavailable renderPass=0x{inheritedRenderPass.Handle:X} framebuffer=0x{inheritedFramebuffer.Handle:X}");
                        return false;
                    }

                    return true;
                }

                var vkFrameBuffer = GenericToAPI<VkFrameBuffer>(target);
                if (vkFrameBuffer is null)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        target,
                        passIndex,
                        "target does not have a Vulkan framebuffer");
                    return false;
                }

                vkFrameBuffer.EnsureCurrent();

                bool targetReenteredThisCommandBuffer = fboLayoutTracking.ContainsKey(target);
                ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, vkFrameBuffer);
                FrameBufferAttachmentSignature[] fboSignature = vkFrameBuffer.ResolveAttachmentSignatureForPass(
                    passIndex,
                    context.PassMetadata,
                    trackedLayouts,
                    CompiledRenderGraph.Synchronization,
                    preserveTrackedClearLoads: targetReenteredThisCommandBuffer);

                inheritedDepthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(fboSignature);

                if (UseDynamicRenderingRenderTargets)
                {
                    inheritedDynamicRendering = true;
                    uint fboViewMask = vkFrameBuffer.MultiviewViewMask;
                    inheritedDynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                        fboSignature,
                        fboViewMask,
                        ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask));
                    inheritedFboAttachmentSignature = fboSignature;
                    inheritedSamples = ResolveDynamicRenderingSamples(fboSignature);
                    return true;
                }

                inheritedRenderPass = vkFrameBuffer.ResolveRenderPassForPass(
                    passIndex,
                    context.PassMetadata,
                    trackedLayouts,
                    CompiledRenderGraph.Synchronization,
                    preserveTrackedClearLoads: targetReenteredThisCommandBuffer);
                inheritedFramebuffer = vkFrameBuffer.FrameBuffer;
                if (inheritedRenderPass.Handle == 0 || inheritedFramebuffer.Handle == 0)
                {
                    LogCommandChainSecondaryInheritanceMismatch(
                        "mesh",
                        target,
                        passIndex,
                        $"legacy FBO inheritance unavailable renderPass=0x{inheritedRenderPass.Handle:X} framebuffer=0x{inheritedFramebuffer.Handle:X}");
                    return false;
                }

                return true;
            }

            static SampleCountFlags ResolveDynamicRenderingSamples(FrameBufferAttachmentSignature[]? signatures)
            {
                if (signatures is { Length: > 0 })
                {
                    for (int i = 0; i < signatures.Length; i++)
                    {
                        if (signatures[i].Role == AttachmentRole.Color)
                            return signatures[i].Samples;
                    }

                    return signatures[0].Samples;
                }

                return SampleCountFlags.Count1Bit;
            }

            bool TryExecuteIndirectCommandChainSecondaryRun(int startIndex, int runCount, int passIndex, IndirectDrawOp firstDraw)
            {
                if (!IndirectCommandChainSecondaryRecordingSafe ||
                    !CommandChainsEnabledForCurrentRecording ||
                    !_enableSecondaryCommandBuffers ||
                    runCount <= 0)
                {
                    return false;
                }

                if (!TryResolveMeshSecondaryInheritance(
                        firstDraw.Target,
                        passIndex,
                        firstDraw.Context,
                        out bool inheritedDynamicRendering,
                        out RenderPass inheritedRenderPass,
                        out Framebuffer inheritedFramebuffer,
                        out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                        out _,
                        out bool inheritedDepthStencilReadOnly,
                        out SampleCountFlags inheritedSamples))
                {
                    return false;
                }

                // Command-chain mode already owns a persistent worker domain for
                // mesh packets. Running the older Task-based secondary recorder in
                // the same frame races shared program/resource state during startup.
                // Keep indirect chain secondaries serial until they use that worker
                // domain too.
                bool useParallelSecondary = false;

                CommandBuffer[] secondaryBuffers = ArrayPool<CommandBuffer>.Shared.Rent(runCount);
                CommandChain[] secondaryChains = ArrayPool<CommandChain>.Shared.Rent(runCount);
                int[] uniformSlots = ArrayPool<int>.Shared.Rent(runCount);
                VkMeshRenderer.IndirectDrawRecordingState[] recordingStates = ArrayPool<VkMeshRenderer.IndirectDrawRecordingState>.Shared.Rent(runCount);
                bool[] recordingStatePrepared = ArrayPool<bool>.Shared.Rent(runCount);
                Task[]? tasks = useParallelSecondary
                    ? ArrayPool<Task>.Shared.Rent(runCount)
                    : null;
                Exception? firstError = null;
                object errorLock = new();

                bool indirectLabelActive = false;
                if (CanRecordCommandBufferDebugLabels)
                {
                    indirectLabelActive = CmdBeginLabel(commandBuffer, useParallelSecondary
                        ? $"IndirectCommandChainSecondaryParallel[{runCount}]"
                        : $"IndirectCommandChainSecondary[{runCount}]");
                }

                try
                {
                    EndActiveRenderPass();
                    EmitIndirectDrawRunReadBarrier();

                    for (int i = 0; i < runCount; i++)
                    {
                        secondaryBuffers[i] = default;
                        secondaryChains[i] = null!;
                        recordingStates[i] = default;
                        recordingStatePrepared[i] = false;
                        IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + i];
                        uniformSlots[i] = GetMeshDrawUniformSlot(
                            indirectOp.MeshRenderer,
                            indirectOp.Context,
                            indirectOp.Draw);
                    }

                    for (int i = 0; i < runCount; i++)
                    {
                        IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + i];
                        indirectOp.MeshRenderer.EnsureUniformDrawSlotCapacity(uniformSlots[i] + 1);
                    }

                    for (int i = 0; i < runCount; i++)
                    {
                        IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + i];
                        using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(indirectOp.Context.PipelineInstance);
                        using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(indirectOp.Context);
                        if (!indirectOp.MeshRenderer.TryPrepareIndirectDrawRecordingState(
                                frameDataImageIndex,
                                indirectOp.Draw,
                                inheritedRenderPass,
                                inheritedDynamicRendering,
                                inheritedDynamicRenderingFormats,
                                passIndex,
                                indirectOp.Context.PassMetadata,
                                inheritedDepthStencilReadOnly,
                                indirectOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                                uniformSlots[i],
                                out recordingStates[i],
                                out string prepareReason))
                        {
                            Debug.VulkanWarningEvery(
                                $"Vulkan.IndirectSecondary.PrepareFailed.{GetHashCode()}.{indirectOp.MeshRenderer.GetHashCode()}.{prepareReason}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Indirect secondary pre-worker state capture failed. mesh='{0}' target='{1}' slot={2} reason={3}",
                                indirectOp.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                                indirectOp.Target?.Name ?? "<swapchain>",
                                uniformSlots[i],
                                prepareReason);
                            return false;
                        }

                        recordingStatePrepared[i] = true;
                    }

                    Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(frameDataImageIndex);
                    for (int i = 0; i < runCount; i++)
                    {
                        IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + i];
                        int primaryOwnedChainOrdinal = HashCode.Combine(startIndex, i, commandBuffer.Handle, 0x494E4452);
                        CommandChainKey chainKey = new(
                            commandBufferImageSlot,
                            BuildRenderViewKey(indirectOp, dynamicOverlay: false),
                            passIndex,
                            ResolveCommandChainTargetIdentity(indirectOp),
                            false,
                            primaryOwnedChainOrdinal);
                        CommandChain chain = GetOrCreateCommandChain(commandChainCache, chainKey);
                        if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, frameDataImageIndex, executedCommandChainSecondaryHandles, out CommandBuffer secondary))
                            return false;

                        secondaryChains[i] = chain;
                        secondaryBuffers[i] = secondary;
                    }

                    void RecordSecondaryAt(int relativeIndex)
                    {
                        CommandChain chain = secondaryChains[relativeIndex];
                        CommandBuffer secondary = secondaryBuffers[relativeIndex];

                        try
                        {
                            MarkCommandChainSecondaryCommandBufferInvalid(chain);
                            ResetVulkanCommandBufferTracked(secondary);

                            CommandBufferInheritanceInfo inheritanceInfo = new()
                            {
                                SType = StructureType.CommandBufferInheritanceInfo,
                                RenderPass = inheritedDynamicRendering ? default : inheritedRenderPass,
                                Subpass = 0,
                                Framebuffer = inheritedDynamicRendering ? default : inheritedFramebuffer,
                                OcclusionQueryEnable = Vk.False,
                                QueryFlags = QueryControlFlags.None,
                                PipelineStatistics = QueryPipelineStatisticFlags.None
                            };

                            Format* colorAttachmentFormats = stackalloc Format[(int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u)];
                            CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
                            if (inheritedDynamicRendering)
                            {
                                inheritedDynamicRenderingFormats.CopyColorAttachmentFormats(
                                    colorAttachmentFormats,
                                    inheritedDynamicRenderingFormats.ColorAttachmentCount);

                                renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                                {
                                    SType = StructureType.CommandBufferInheritanceRenderingInfo,
                                    Flags = 0,
                                    ViewMask = inheritedDynamicRenderingFormats.ViewMask,
                                    ColorAttachmentCount = inheritedDynamicRenderingFormats.ColorAttachmentCount,
                                    PColorAttachmentFormats = inheritedDynamicRenderingFormats.ColorAttachmentCount > 0 ? colorAttachmentFormats : null,
                                    DepthAttachmentFormat = inheritedDynamicRenderingFormats.DepthAttachmentFormat,
                                    StencilAttachmentFormat = inheritedDynamicRenderingFormats.StencilAttachmentFormat,
                                    RasterizationSamples = inheritedSamples
                                };
                                DynamicRenderingLocalReadPlan localReadInheritance = default;
                                void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                                TryAppendDynamicRenderingLocalReadPNext(
                                    in localReadInheritance,
                                    inheritedDynamicRenderingFormats.ColorAttachmentCount,
                                    ref localReadInheritancePNext,
                                    null,
                                    null,
                                    null,
                                    null,
                                    null,
                                    null);
                                renderingInheritanceInfo.PNext = localReadInheritancePNext;
                                inheritanceInfo.PNext = &renderingInheritanceInfo;
                            }

                            CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
                            BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
                            BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
                            TryAppendDescriptorHeapInheritancePNext(
                                ref inheritanceInfo,
                                &descriptorHeapInheritanceInfo,
                                &inheritedSamplerHeapInfo,
                                &inheritedResourceHeapInfo);

                            CommandBufferBeginInfo beginInfo = new()
                            {
                                SType = StructureType.CommandBufferBeginInfo,
                                Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.SimultaneousUseBit,
                                PInheritanceInfo = &inheritanceInfo
                            };

                            if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                                throw new Exception("Failed to begin Vulkan indirect secondary command buffer.");

                            ResetCommandBufferBindState(secondary);

                            IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + relativeIndex];
                            using (RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(indirectOp.Context.PipelineInstance))
                            {
                                RecordIndirectDrawIntoSecondaryCommandBuffer(
                                    secondary,
                                    indirectOp,
                                    recordingStates[relativeIndex],
                                    passIndex,
                                    inheritedDynamicRendering,
                                    inheritedRenderPass,
                                    inheritedDynamicRenderingFormats,
                                    inheritedDepthStencilReadOnly,
                                    uniformSlots[relativeIndex]);
                            }

                            if (EndCommandBufferTracked(secondary) != Result.Success)
                                throw new Exception("Failed to end Vulkan indirect secondary command buffer.");

                            MarkCommandChainSecondaryCommandBufferRecorded(chain);
                        }
                        catch (Exception ex)
                        {
                            lock (errorLock)
                                firstError ??= ex;

                            DestroyCommandChainSecondaryCommandBuffer(chain);
                            secondaryBuffers[relativeIndex] = default;
                        }
                    }

                    if (useParallelSecondary && tasks is not null)
                    {
                        for (int i = 0; i < runCount; i++)
                        {
                            int taskIndex = i;
                            tasks[i] = Task.Run(() => RecordSecondaryAt(taskIndex));
                        }

                        for (int i = 0; i < runCount; i++)
                            tasks[i]!.Wait();
                    }
                    else
                    {
                        for (int i = 0; i < runCount; i++)
                            RecordSecondaryAt(i);
                    }

                    if (firstError is not null)
                        throw firstError;

                    BeginRenderPassForTarget(firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                    fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                        CmdExecuteCommandsTracked(commandBuffer, (uint)runCount, secondaryPtr);
                    for (int i = 0; i < runCount; i++)
                    {
                        if (secondaryBuffers[i].Handle != 0)
                            executedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: runCount);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(
                        usedSecondary: true,
                        usedParallel: useParallelSecondary,
                        opCount: runCount);
                    return true;
                }
                finally
                {
                    EndActiveRenderPass();

                    if (tasks is not null)
                    {
                        Array.Clear(tasks, 0, runCount);
                        ArrayPool<Task>.Shared.Return(tasks);
                    }

                    Array.Clear(secondaryBuffers, 0, runCount);
                    Array.Clear(secondaryChains, 0, runCount);
                    Array.Clear(uniformSlots, 0, runCount);
                    for (int i = 0; i < runCount; i++)
                    {
                        if (recordingStatePrepared[i])
                            VkMeshRenderer.ReturnIndirectDrawRecordingStateBuffers(recordingStates[i]);
                    }

                    Array.Clear(recordingStates, 0, runCount);
                    Array.Clear(recordingStatePrepared, 0, runCount);
                    ArrayPool<CommandBuffer>.Shared.Return(secondaryBuffers);
                    ArrayPool<CommandChain>.Shared.Return(secondaryChains);
                    ArrayPool<int>.Shared.Return(uniformSlots);
                    ArrayPool<VkMeshRenderer.IndirectDrawRecordingState>.Shared.Return(recordingStates);
                    ArrayPool<bool>.Shared.Return(recordingStatePrepared);

                    if (indirectLabelActive)
                        CmdEndLabel(commandBuffer);
                }
            }

            bool TryGetScheduledCommandChainForOp(int opIndex, out CommandChain chain, out CommandChainKey key)
            {
                chain = null!;
                key = default;
                if (scheduledCommandChainKeysByOpIndex is null ||
                    scheduledCommandChainCache is null ||
                    (uint)opIndex >= (uint)scheduledCommandChainKeysByOpIndex.Length)
                {
                    return false;
                }

                key = scheduledCommandChainKeysByOpIndex[opIndex];
                if (key.ChainOrdinal == -1)
                    return false;

                if (!scheduledCommandChainCache.TryGetValue(key, out CommandChain? scheduledChain))
                    return false;

                if (scheduledChain.SourceStartIndex < 0 ||
                    scheduledChain.SourceCount <= 0 ||
                    opIndex < scheduledChain.SourceStartIndex ||
                    opIndex >= scheduledChain.SourceStartIndex + scheduledChain.SourceCount)
                    return false;

                chain = scheduledChain;
                return true;
            }

            bool ScheduledCommandChainSecondaryNeedsRecording(CommandChain chain)
            {
                if (chain.SecondaryCommandBuffer.Handle == 0)
                    return true;

                if (!chain.SecondaryCommandBufferExecutable)
                    return true;

                if (chain.State == CommandChainState.FrameDataRefreshed &&
                    chain.FrameDataRefreshTouchedDescriptors)
                    return true;

                return chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed);
            }

            bool TryExecuteScheduledMeshCommandChainSecondaryRun(int startIndex, int runCount, int passIndex, MeshDrawOp firstDraw)
            {
                // An explicit schedule may be supplied for an external OpenXR
                // target even though the ordinary desktop eligibility gate is
                // false inside that render scope. The builder has already
                // applied the appropriate policy for this recording.
                if (!_enableSecondaryCommandBuffers ||
                    scheduledCommandChainKeysByOpIndex is null ||
                    scheduledCommandChainCache is null ||
                    runCount <= 0 ||
                    activeInlineQuery is not null ||
                    firstDraw.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                {
                    return false;
                }

                // Query-bracket contents are intentionally absent from the command-chain
                // schedule. Preflight the complete run before closing the current render
                // scope so an unscheduled draw cannot terminate rendering as a side effect.
                for (int i = 0; i < runCount; i++)
                {
                    int opIndex = startIndex + i;
                    if (ops[opIndex] is not MeshDrawOp drawOp ||
                        drawOp.PassIndex != passIndex ||
                        drawOp.Target != firstDraw.Target ||
                        !TryGetScheduledCommandChainForOp(opIndex, out _, out _))
                    {
                        return false;
                    }
                }

                EndActiveRenderPass();

                if (!TryResolveMeshSecondaryInheritance(
                        firstDraw.Target,
                        passIndex,
                        firstDraw.Context,
                        out bool inheritedDynamicRendering,
                        out RenderPass inheritedRenderPass,
                        out Framebuffer inheritedFramebuffer,
                        out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                        out _,
                        out bool inheritedDepthStencilReadOnly,
                        out SampleCountFlags inheritedSamples))
                {
                    return false;
                }

                CommandBuffer[] secondaryBuffers = ArrayPool<CommandBuffer>.Shared.Rent(runCount);
                CommandChain[] secondaryChains = ArrayPool<CommandChain>.Shared.Rent(runCount);
                int[] recordJobChainIndices = ArrayPool<int>.Shared.Rent(runCount);
                int[] recordJobWorkerIndices = ArrayPool<int>.Shared.Rent(runCount);
                int[] uniformSlots = ArrayPool<int>.Shared.Rent(runCount);
                Array.Clear(secondaryBuffers, 0, runCount);
                Array.Clear(secondaryChains, 0, runCount);
                int secondaryCount = 0;
                int scheduledOpCount = 0;
                int recordJobCount = 0;
                bool meshLabelActive = false;
                CommandChainRecordingBatch batch = _commandChainRecordingBatch;

                if (CanRecordCommandBufferDebugLabels)
                    meshLabelActive = CmdBeginLabel(commandBuffer, "ScheduledMeshCommandChainSecondary");

                try
                {
                    for (int i = 0; i < runCount; i++)
                    {
                        int opIndex = startIndex + i;
                        if (ops[opIndex] is not MeshDrawOp drawOp ||
                            drawOp.PassIndex != passIndex ||
                            drawOp.Target != firstDraw.Target ||
                            !TryGetScheduledCommandChainForOp(opIndex, out _, out _))
                        {
                            return false;
                        }

                        uniformSlots[i] = GetMeshDrawUniformSlot(
                            drawOp.Draw.Renderer,
                            drawOp.Context,
                            drawOp.Draw);
                    }

                    for (int i = 0; i < runCount; i++)
                    {
                        int opIndex = startIndex + i;
                        _ = TryGetScheduledCommandChainForOp(opIndex, out CommandChain chain, out _);
                        if (opIndex != chain.SourceStartIndex)
                            continue;

                        if (chain.SourceCount > runCount - i)
                            return false;

                        secondaryChains[secondaryCount] = chain;

                        ulong currentUniformSlotSignature = ComputeCommandChainUniformSlotSignature(
                            uniformSlots,
                            chain.SourceStartIndex - startIndex,
                            chain.SourceCount);
                        bool uniformSlotMappingChanged =
                            chain.RecordedUniformSlotSignature != currentUniformSlotSignature;
                        bool needsRecording =
                            ScheduledCommandChainSecondaryNeedsRecording(chain) ||
                            uniformSlotMappingChanged;
                        if (uniformSlotMappingChanged && chain.SecondaryCommandBuffer.Handle != 0)
                        {
                            // Dynamic UBO offsets are baked into the secondary.
                            // Refreshing bytes at a newly assigned occurrence slot
                            // cannot make an old offset valid; re-record the chain.
                            chain.State = CommandChainState.Recorded;
                            chain.DirtyReason |= CommandChainDirtyReason.FrameDataRefreshFailed;
                        }

                        if (!needsRecording)
                        {
                            for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
                            {
                                MeshDrawOp refreshDraw = (MeshDrawOp)ops[chain.SourceStartIndex + drawIndex];
                                int refreshUniformSlot = uniformSlots[chain.SourceStartIndex + drawIndex - startIndex];
                                long descriptorSetContentUpdateGeneration =
                                    SnapshotDescriptorSetContentUpdateGeneration();
                                bool refreshedFrameData =
                                    refreshDraw.Draw.Renderer.TryRefreshReusableCommandBufferFrameData(
                                        frameDataImageIndex,
                                        refreshDraw.Draw,
                                        refreshUniformSlot,
                                        out string refreshReason,
                                        refreshMaterialUniforms: true);
                                bool descriptorsInvalidated =
                                    HaveDescriptorSetContentsUpdatedSince(descriptorSetContentUpdateGeneration);
                                if (!refreshedFrameData || descriptorsInvalidated)
                                {
									_lastReusableFrameDataRefreshFailureReason =
										$"scheduled chain op={chain.SourceStartIndex + drawIndex}/{ops.Length} mesh='{refreshDraw.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' slot={refreshUniformSlot}: " +
                                        (descriptorsInvalidated
                                            ? "descriptor contents changed without UPDATE_AFTER_BIND"
                                            : refreshReason);
									if (FrameDataReuseDiagnosticsEnabled)
									{
										Debug.VulkanEvery(
											$"Vulkan.FrameDataReuse.ScheduledChain.{GetHashCode()}",
											TimeSpan.FromSeconds(1),
											"[Vulkan] Scheduled command-chain frame-data refresh failed image={0} op={1}/{2} mesh='{3}' drawSlot={4}: {5}",
											frameDataImageIndex,
											chain.SourceStartIndex + drawIndex,
											ops.Length,
											refreshDraw.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
											refreshUniformSlot,
											descriptorsInvalidated
                                                ? "descriptor contents changed without UPDATE_AFTER_BIND"
                                                : refreshReason);
									}
                                    needsRecording = true;
                                    break;
                                }
                            }
                        }

                        if (needsRecording)
                        {
                            recordJobChainIndices[recordJobCount++] = secondaryCount;
                        }
                        else
                        {
                            CommandBuffer reusable = chain.SecondaryCommandBuffer;
                            if (reusable.Handle == 0 || !chain.SecondaryCommandBufferExecutable)
                                return false;
                            secondaryBuffers[secondaryCount] = reusable;
                        }

                        secondaryCount++;
                        scheduledOpCount += chain.SourceCount;
                    }

                    if (secondaryCount == 0 || scheduledOpCount != runCount)
                        return false;

                    batch.Ops = ops;
                    batch.Chains = secondaryChains;
                    batch.SecondaryBuffers = secondaryBuffers;
                    batch.RecordJobChainIndices = recordJobChainIndices;
                    batch.RecordJobWorkerIndices = recordJobWorkerIndices;
                    batch.UniformSlots = uniformSlots;
                    batch.StartIndex = startIndex;
                    batch.JobCount = recordJobCount;
                    batch.PassIndex = passIndex;
                    batch.FrameSlot = commandBufferImageSlot;
                    batch.ActiveWorkerMask = 0;
                    batch.DynamicRendering = inheritedDynamicRendering;
                    batch.RenderPass = inheritedRenderPass;
                    batch.Framebuffer = inheritedFramebuffer;
                    batch.DynamicRenderingFormats = inheritedDynamicRenderingFormats;
                    batch.DepthStencilReadOnly = inheritedDepthStencilReadOnly;
                    batch.Samples = inheritedSamples;
                    batch.TargetName = firstDraw.Target?.Name ?? "<swapchain>";
                    batch.Error = null;

                    int workerEligibleJobCount = 0;
                    for (int jobIndex = 0; jobIndex < recordJobCount; jobIndex++)
                    {
                        CommandChain chain = secondaryChains[recordJobChainIndices[jobIndex]];
                        if (TryResolveCommandChainRecordingRendererFamily(
                                ops,
                                chain,
                                commandBufferImageSlot,
                                EVulkanMeshFrameDataStreamKind.Primary,
                                out _))
                        {
                            workerEligibleJobCount++;
                        }
                    }

                    bool useWorkers = TryPrepareCommandChainRecordingWorkers(
                        workerEligibleJobCount,
                        frameDataImageIndex,
                        out CommandChainRecordingWorkerState[] workers,
                        out int workerCount,
                        out int workerFrameSlot);
                    for (int jobIndex = 0; jobIndex < recordJobCount; jobIndex++)
                    {
                        int chainIndex = recordJobChainIndices[jobIndex];
                        CommandChain chain = secondaryChains[chainIndex];
                        // Invalidate the whole dirty batch before any worker is
                        // released. If one worker fails, chains that had not yet
                        // begun recording cannot retain an executable old state.
                        MarkCommandChainSecondaryCommandBufferInvalid(chain);
                        bool hasHomogeneousRendererFamily =
                            TryResolveCommandChainRecordingRendererFamily(
                                ops,
                                chain,
                                commandBufferImageSlot,
                                EVulkanMeshFrameDataStreamKind.Primary,
                                out VulkanMeshFrameDataRendererFamilyKey rendererFamily);
                        int recordingWorkerIndex = useWorkers && hasHomogeneousRendererFamily
                            ? ResolveCommandChainRecordingWorkerIndex(rendererFamily, workerCount)
                            : -1;
                        recordJobWorkerIndices[jobIndex] = recordingWorkerIndex;
                        if (recordingWorkerIndex >= 0)
                            batch.ActiveWorkerMask |= 1u << recordingWorkerIndex;

                        bool allocated = recordingWorkerIndex >= 0
                            ? TryEnsureMutableCommandChainSecondaryCommandBufferFromWorkerPool(
                                chain,
                                frameDataImageIndex,
                                workers[recordingWorkerIndex].GraphicsCommandPoolsByFrameSlot[workerFrameSlot],
                                executedCommandChainSecondaryHandles,
                                out CommandBuffer secondary)
                            : TryEnsureMutableCommandChainSecondaryCommandBuffer(
                                chain,
                                frameDataImageIndex,
                                executedCommandChainSecondaryHandles,
                                out secondary);
                        if (!allocated)
                            throw new InvalidOperationException("Failed to allocate Vulkan scheduled mesh command-chain secondary command buffer.");

                        secondaryBuffers[chainIndex] = secondary;
                    }

                    if (useWorkers)
                    {
                        CommandChainWorkerTiming timing = DispatchCommandChainRecordingWorkers(batch, workers, workerCount);
                        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
                            chainWorkerRecordTime: timing.WorkerRecordTime,
                            renderThreadWaitForWorkersTime: timing.WaitForWorkersTime);
                    }

                    using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.SecondaryRecording))
                    {
                        for (int jobIndex = 0; jobIndex < recordJobCount; jobIndex++)
                        {
                            if (recordJobWorkerIndices[jobIndex] < 0)
                                RecordScheduledMeshCommandChainWorker(batch, recordJobChainIndices[jobIndex]);
                        }
                    }

                    BeginRenderPassForTarget(firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                    fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                        CmdExecuteCommandsTracked(commandBuffer, (uint)secondaryCount, secondaryPtr);
                    for (int i = 0; i < secondaryCount; i++)
                    {
                        if (secondaryBuffers[i].Handle != 0)
                            executedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: secondaryCount);
                    return true;
                }
                finally
                {
                    EndActiveRenderPass();

                    batch.ClearReferences();

                    Array.Clear(secondaryBuffers, 0, runCount);
                    Array.Clear(secondaryChains, 0, runCount);
                    ArrayPool<CommandBuffer>.Shared.Return(secondaryBuffers);
                    ArrayPool<CommandChain>.Shared.Return(secondaryChains);
                    ArrayPool<int>.Shared.Return(recordJobChainIndices);
                    ArrayPool<int>.Shared.Return(recordJobWorkerIndices);
                    ArrayPool<int>.Shared.Return(uniformSlots);

                    if (meshLabelActive)
                        CmdEndLabel(commandBuffer);
                }
            }

            bool TryExecuteMeshCommandChainSecondaryRun(int startIndex, int runCount, int passIndex, MeshDrawOp firstDraw)
            {
                const int minMeshDrawsPerSecondaryChain = MinMeshDrawsPerRenderPacket;

                if (!CommandChainsEnabledForCurrentRecording ||
                    !_enableSecondaryCommandBuffers ||
                    runCount < minMeshDrawsPerSecondaryChain ||
                    activeInlineQuery is not null ||
                    firstDraw.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                {
                    return false;
                }

                EndActiveRenderPass();

                if (!TryResolveMeshSecondaryInheritance(
                        firstDraw.Target,
                        passIndex,
                        firstDraw.Context,
                        out bool inheritedDynamicRendering,
                        out RenderPass inheritedRenderPass,
                        out Framebuffer inheritedFramebuffer,
                        out DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
                        out FrameBufferAttachmentSignature[]? inheritedFboAttachmentSignature,
                        out bool inheritedDepthStencilReadOnly,
                        out SampleCountFlags inheritedSamples))
                {
                    return false;
                }

                bool meshSecondaryNoOp = IsCommandChainFlagEnabled(XREngineEnvironmentVariables.VulkanCommandChainMeshSecondaryNoop);
                // A recorded primary command buffer bakes the secondary handle it executes.
                // Keep secondary ownership per primary variant so re-recording one variant
                // cannot invalidate another variant that still references its old secondary.
                int primaryOwnedChainOrdinal = HashCode.Combine(startIndex, commandBuffer.Handle);
                CommandChainKey chainKey = new(
                    commandBufferImageSlot,
                    BuildRenderViewKey(firstDraw, dynamicOverlay: false),
                    passIndex,
                    ResolveCommandChainTargetIdentity(firstDraw),
                    false,
                    primaryOwnedChainOrdinal);
                CommandChain chain = GetOrCreateCommandChain(GetCommandChainCache(frameDataImageIndex), chainKey);
                CommandBuffer secondary = chain.SecondaryCommandBuffer;
                bool executedInPrimary = false;
                bool meshLabelActive = false;
                bool secondaryRecordingFinished = false;
                int[]? drawUniformSlots = null;

                if (CanRecordCommandBufferDebugLabels)
                    meshLabelActive = CmdBeginLabel(commandBuffer, $"MeshCommandChainSecondary[{runCount}]");

                try
                {
                    if (secondary.Handle != 0 && chain.SecondaryCommandPool.Handle == 0)
                    {
                        LogCommandChainSecondaryInheritanceMismatch(
                            "mesh",
                            firstDraw.Target,
                            passIndex,
                            $"chain-owned secondary has no owner command pool key={chainKey}");
                        DestroyCommandChainSecondaryCommandBuffer(chain);
                        secondary = default;
                    }

                    if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, frameDataImageIndex, executedCommandChainSecondaryHandles, out secondary))
                        return false;

                    drawUniformSlots = ArrayPool<int>.Shared.Rent(runCount);
                    for (int i = 0; i < runCount; i++)
                    {
                        MeshDrawOp drawOp = (MeshDrawOp)ops[startIndex + i];
                        int drawUniformSlot = GetMeshDrawUniformSlot(
                            drawOp.Draw.Renderer,
                            drawOp.Context,
                            drawOp.Draw);
                        drawUniformSlots[i] = drawUniformSlot;
                        drawOp.Draw.Renderer.EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
                    }

                    MarkCommandChainSecondaryCommandBufferInvalid(chain);
                    ResetVulkanCommandBufferTracked(secondary);

                    CommandBufferInheritanceInfo inheritanceInfo = new()
                    {
                        SType = StructureType.CommandBufferInheritanceInfo,
                        RenderPass = inheritedDynamicRendering ? default : inheritedRenderPass,
                        Subpass = 0,
                        Framebuffer = inheritedDynamicRendering ? default : inheritedFramebuffer,
                        OcclusionQueryEnable = Vk.False,
                        QueryFlags = QueryControlFlags.None,
                        PipelineStatistics = QueryPipelineStatisticFlags.None
                    };

                    Format* colorAttachmentFormats = stackalloc Format[(int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u)];
                    CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
                    if (inheritedDynamicRendering)
                    {
                        inheritedDynamicRenderingFormats.CopyColorAttachmentFormats(
                            colorAttachmentFormats,
                            inheritedDynamicRenderingFormats.ColorAttachmentCount);

                        renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                        {
                            SType = StructureType.CommandBufferInheritanceRenderingInfo,
                            Flags = 0,
                            ViewMask = inheritedDynamicRenderingFormats.ViewMask,
                            ColorAttachmentCount = inheritedDynamicRenderingFormats.ColorAttachmentCount,
                            PColorAttachmentFormats = inheritedDynamicRenderingFormats.ColorAttachmentCount > 0 ? colorAttachmentFormats : null,
                            DepthAttachmentFormat = inheritedDynamicRenderingFormats.DepthAttachmentFormat,
                            StencilAttachmentFormat = inheritedDynamicRenderingFormats.StencilAttachmentFormat,
                            RasterizationSamples = inheritedSamples
                        };
                        DynamicRenderingLocalReadPlan localReadInheritance = default;
                        void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                        TryAppendDynamicRenderingLocalReadPNext(
                            in localReadInheritance,
                            inheritedDynamicRenderingFormats.ColorAttachmentCount,
                            ref localReadInheritancePNext,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null);
                        renderingInheritanceInfo.PNext = localReadInheritancePNext;
                        inheritanceInfo.PNext = &renderingInheritanceInfo;
                    }

                    CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
                    BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
                    BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
                    TryAppendDescriptorHeapInheritancePNext(
                        ref inheritanceInfo,
                        &descriptorHeapInheritanceInfo,
                        &inheritedSamplerHeapInfo,
                        &inheritedResourceHeapInfo);

                    CommandBufferBeginInfo beginInfo = new()
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit,
                        PInheritanceInfo = &inheritanceInfo
                    };

                    if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                        throw new Exception("Failed to begin Vulkan mesh command-chain secondary command buffer.");

                    ResetCommandBufferBindState(secondary);

                    bool savedActiveDynamicRendering = activeDynamicRendering;
                    RenderPass savedActiveRenderPass = activeRenderPass;
                    Framebuffer savedActiveFramebuffer = activeFramebuffer;
                    DynamicRenderingFormatSignature savedActiveDynamicRenderingFormats = activeDynamicRenderingFormats;
                    FrameBufferAttachmentSignature[]? savedActiveFboAttachmentSignature = activeFboAttachmentSignature;
                    bool savedActiveDepthStencilReadOnly = activeDepthStencilReadOnly;
                    XRFrameBuffer? savedActiveTarget = activeTarget;

                    activeDynamicRendering = inheritedDynamicRendering;
                    activeRenderPass = inheritedRenderPass;
                    activeFramebuffer = inheritedFramebuffer;
                    activeDynamicRenderingFormats = inheritedDynamicRenderingFormats;
                    activeFboAttachmentSignature = inheritedFboAttachmentSignature;
                    activeDepthStencilReadOnly = inheritedDepthStencilReadOnly;
                    activeTarget = firstDraw.Target;

                    try
                    {
                        for (int i = startIndex; !meshSecondaryNoOp && i < startIndex + runCount; i++)
                        {
                            MeshDrawOp drawOp = (MeshDrawOp)ops[i];
                            using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawOp.Context.PipelineInstance);
                            RecordMeshDrawIntoCommandBuffer(secondary, drawOp, passIndex, drawUniformSlots[i - startIndex]);
                        }
                    }
                    finally
                    {
                        activeDynamicRendering = savedActiveDynamicRendering;
                        activeRenderPass = savedActiveRenderPass;
                        activeFramebuffer = savedActiveFramebuffer;
                        activeDynamicRenderingFormats = savedActiveDynamicRenderingFormats;
                        activeFboAttachmentSignature = savedActiveFboAttachmentSignature;
                        activeDepthStencilReadOnly = savedActiveDepthStencilReadOnly;
                        activeTarget = savedActiveTarget;
                    }

                    if (EndCommandBufferTracked(secondary) != Result.Success)
                        throw new Exception("Failed to end Vulkan mesh command-chain secondary command buffer.");

                    MarkCommandChainSecondaryCommandBufferRecorded(chain);
                    secondaryRecordingFinished = true;
                    BeginRenderPassForTarget(firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                    CmdExecuteCommandsTracked(commandBuffer, 1, &secondary);
                    if (secondary.Handle != 0)
                        executedCommandChainSecondaryHandles.Add(secondary.Handle);
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: 1);
                    executedInPrimary = true;
                    return true;
                }
                finally
                {
                    EndActiveRenderPass();

                    if (!executedInPrimary && !secondaryRecordingFinished)
                        DestroyCommandChainSecondaryCommandBuffer(chain);

                    if (drawUniformSlots is not null)
                    {
                        Array.Clear(drawUniformSlots, 0, runCount);
                        ArrayPool<int>.Shared.Return(drawUniformSlots);
                    }

                    if (meshLabelActive)
                        CmdEndLabel(commandBuffer);
                }
            }

            void ExecuteDynamicUiBatchTextOverlay()
            {
                if (dynamicUiBatchTextOpCount <= 0)
                    return;

                CommandBuffer secondaryCommandBuffer = dynamicUiBatchTextSecondaryCommandBuffer;
                if (secondaryCommandBuffer.Handle == 0)
                    return;

                EndActiveRenderPass();
                CmdBeginLabel(commandBuffer, "DynamicUIBatchText");

                try
                {
                    bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                        swapchainTarget.IsValid;

                    if (useDynamicRendering)
                    {
                        ImageLayout colorOldLayout = ResolveCurrentSwapchainColorLayout();

                        bool loadExistingSwapchainColor =
                            swapchainClearedThisFrame ||
                            swapchainWrittenOutsideRenderPass ||
                            imageWasEverPresentedAtRecordStart;
                        AttachmentLoadOp colorLoadOp = loadExistingSwapchainColor
                            ? AttachmentLoadOp.Load
                            : AttachmentLoadOp.Clear;

                        ImageMemoryBarrier colorBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = 0,
                            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                            OldLayout = colorOldLayout,
                            NewLayout = ImageLayout.ColorAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapchainTarget.Image,
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        ImageSubresourceRange depthRange = new()
                        {
                            AspectMask = swapchainTarget.DepthAspect,
                            BaseMipLevel = 0,
                            LevelCount = 1,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        };
                        bool hasRecordedDepthState = TryGetRecordedImageAccessState(
                            commandBuffer,
                            swapchainTarget.DepthImage,
                            depthRange,
                            out VulkanImageAccessState recordedDepthState);
                        ImageLayout depthOldLayout = hasRecordedDepthState
                            ? recordedDepthState.Layout
                            : ImageLayout.Undefined;

                        ImageMemoryBarrier depthBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = hasRecordedDepthState
                                ? (AccessFlags)(ulong)recordedDepthState.AccessMask
                                : AccessFlags.None,
                            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = depthOldLayout,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapchainTarget.DepthImage,
                            SubresourceRange = depthRange
                        };

                        ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                        uint preRenderingBarrierCount = 0;
                        if (colorOldLayout != ImageLayout.ColorAttachmentOptimal)
                            preRenderingBarriers[preRenderingBarrierCount++] = colorBarrier;
                        preRenderingBarriers[preRenderingBarrierCount++] = depthBarrier;

                        CmdPipelineBarrierTracked(
                            commandBuffer,
                            PipelineStageFlags.ColorAttachmentOutputBit |
                                (hasRecordedDepthState
                                    ? (PipelineStageFlags)(ulong)recordedDepthState.StageMask
                                    : PipelineStageFlags.None),
                            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            preRenderingBarrierCount,
                            preRenderingBarriers);

                        ClearValue* dynamicClearValues = stackalloc ClearValue[2];
                        ActiveState.WriteClearValues(dynamicClearValues, 2);

                        Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[1];
                        colorAttachmentPlans[0] = new DynamicRenderingAttachmentPlan(
                            swapchainTarget.Image,
                            swapchainTarget.ImageView,
                            swapchainTarget.ImageFormat,
                            ImageAspectFlags.ColorBit,
                            colorOldLayout,
                            ImageLayout.ColorAttachmentOptimal,
                            ImageLayout.PresentSrcKhr,
                            colorLoadOp,
                            AttachmentStoreOp.Store,
                            dynamicClearValues[0]);

                        DynamicRenderingAttachmentPlan depthAttachmentPlan = new(
                            swapchainTarget.DepthImage,
                            swapchainTarget.DepthView,
                            swapchainTarget.DepthFormat,
                            swapchainTarget.DepthAspect,
                            depthOldLayout,
                            ImageLayout.DepthStencilAttachmentOptimal,
                            ImageLayout.DepthStencilAttachmentOptimal,
                            AttachmentLoadOp.Clear,
                            AttachmentStoreOp.DontCare,
                            dynamicClearValues[1]);

                        DynamicRenderingFormatSignature swapchainDynamicRenderingFormats =
                            CreateSwapchainDynamicRenderingFormatSignature(swapchainTarget.ImageFormat, swapchainTarget.DepthFormat);
                        DynamicRenderingScopePlan scopePlan = new(
                            new Rect2D
                            {
                                Offset = new Offset2D(0, 0),
                                Extent = swapchainTarget.Extent
                            },
                            1u,
                            0u,
                            colorAttachmentPlans,
                            depthAttachmentPlan,
                            true,
                            default,
                            false,
                            false,
                            swapchainDynamicRenderingFormats,
                            SampleCountFlags.Count1Bit);

                        BeginDynamicRenderingScope(in scopePlan, secondaryContents: true);
                        CmdExecuteCommandsTracked(commandBuffer, 1, &secondaryCommandBuffer);
                        CmdEndDynamicRendering(commandBuffer);

                        usedSwapchainDynamicRendering = true;
                        swapchainInColorAttachmentLayout = true;
                        swapchainClearedThisFrame = true;
                    }
                    else if (swapChainFramebuffers is not null && imageIndex < swapChainFramebuffers.Length)
                    {
                        RenderPassBeginInfo renderPassInfo = new()
                        {
                            SType = StructureType.RenderPassBeginInfo,
                            RenderPass = _renderPassLoad,
                            Framebuffer = swapChainFramebuffers[imageIndex],
                            RenderArea = new Rect2D
                            {
                                Offset = new Offset2D(0, 0),
                                Extent = swapchainRecordExtent
                            }
                        };

                        const uint attachmentCount = 2;
                        ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
                        ActiveState.WriteClearValues(clearValues, attachmentCount);
                        renderPassInfo.ClearValueCount = attachmentCount;
                        renderPassInfo.PClearValues = clearValues;

                        CmdBeginRenderPassTracked(commandBuffer, &renderPassInfo, SubpassContents.SecondaryCommandBuffers);
                        CmdExecuteCommandsTracked(commandBuffer, 1, &secondaryCommandBuffer);
                        Api!.CmdEndRenderPass(commandBuffer);

                        swapchainClearedThisFrame = true;
                    }

                    swapchainWriteCount++;
                    actualSwapchainWriteCount++;
                    swapchainDrawWrites++;
                    overlaySwapchainWriters++;
                    MarkSwapchainDynamicUiWriter(
                        "DynamicUIBatchText",
                        dynamicUiBatchTextOpCount,
                        activePassIndex != int.MinValue ? activePassIndex : VulkanBarrierPlanner.SwapchainPassIndex,
                        ops.Length,
                        hasActiveContext ? activeContext.PipelineIdentity : initialContext.PipelineIdentity);
                }
                finally
                {
                    CmdEndLabel(commandBuffer);
                }
            }

            void EmitPassBarriers(int passIndex)
            {
                // Emit any global pending memory barriers that accumulated before recording.
                // After the first pass consumes them they are cleared.
                EmitPendingMemoryBarriers(commandBuffer);

                // Ensure first-use physical-group images are transitioned out of UNDEFINED
                // before any planned pass consumes them.
                EmitInitialImageBarriersForUnknownPass(
                    commandBuffer,
                    skipDesktopSwapchainImages: excludeDesktopSwapchainBarriers);

                // Emit per-pass memory barriers registered during the frame.
                EMemoryBarrierMask perPassMask = ActiveState.DrainMemoryBarrierForPass(passIndex);
                if (perPassMask != EMemoryBarrierMask.None)
                    EmitMemoryBarrierMask(commandBuffer, perPassMask);

                var imageBarriers = BarrierPlanner.GetBarriersForPass(passIndex);
                var bufferBarriers = BarrierPlanner.GetBufferBarriersForPass(passIndex);
                var swapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(passIndex);

                // If the barrier planner doesn't recognise this pass at all, it has no planned
                // layout transitions. Emit a conservative full-pipeline memory barrier so that
                // all prior writes are visible to subsequent reads. We intentionally do NOT
                // substitute image barriers from another pass because those barriers carry
                // OldLayout values that may not match the images' actual layouts, causing
                // undefined behaviour (observed as CmdBlitImage segfaults on NVIDIA drivers).
                // Ops that need specific image layout transitions (e.g. blits) handle them
                // internally via TransitionForBlit.
                if (!BarrierPlanner.HasKnownPass(passIndex))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.UnknownPassBarrier.{passIndex}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Pass {0} is unknown to the barrier planner. Emitting conservative memory + image barriers.",
                        passIndex);

                    MemoryBarrier safetyBarrier = new()
                    {
                        SType = StructureType.MemoryBarrier,
                        SrcAccessMask = AccessFlags.MemoryWriteBit,
                        DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                    };

                    CmdPipelineBarrierTracked(
                        commandBuffer,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.AllCommandsBit,
                        DependencyFlags.None,
                        1,
                        &safetyBarrier,
                        0,
                        null,
                        0,
                        null);

                    return;
                }

                int queueOwnershipTransfers = 0;
                int stageFlushes = 0;

                for (int i = 0; i < imageBarriers.Count; i++)
                {
                    VulkanBarrierPlanner.PlannedImageBarrier planned = imageBarriers[i];
                    if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                    {
                        queueOwnershipTransfers++;
                    }

                    if (planned.Previous.StageMask != planned.Next.StageMask)
                        stageFlushes++;
                }

                for (int i = 0; i < swapchainBarriers.Count; i++)
                {
                    VulkanBarrierPlanner.PlannedSwapchainBarrier planned = swapchainBarriers[i];
                    if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                    {
                        queueOwnershipTransfers++;
                    }

                    if (planned.Previous.StageMask != planned.Next.StageMask)
                        stageFlushes++;
                }

                for (int i = 0; i < bufferBarriers.Count; i++)
                {
                    VulkanBarrierPlanner.PlannedBufferBarrier planned = bufferBarriers[i];
                    if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                        planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                    {
                        queueOwnershipTransfers++;
                    }

                    if (planned.Previous.StageMask != planned.Next.StageMask)
                        stageFlushes++;
                }

                if (swapchainBarriers.Count > 0 || imageBarriers.Count > 0 || bufferBarriers.Count > 0)
                {
                    CmdBeginLabel(commandBuffer, "PassBarriers");
                    EmitPlannedSwapchainBarriers(commandBuffer, swapchainBarriers);
                    EmitPlannedImageBarriers(
                        commandBuffer,
                        imageBarriers,
                        skipDesktopSwapchainImages: excludeDesktopSwapchainBarriers);
                    EmitPlannedBufferBarriers(commandBuffer, bufferBarriers);
                    CmdEndLabel(commandBuffer);

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBarrierPlannerPass(
                        imageBarrierCount: imageBarriers.Count + swapchainBarriers.Count,
                        bufferBarrierCount: bufferBarriers.Count,
                        queueOwnershipTransfers: queueOwnershipTransfers,
                        stageFlushes: stageFlushes);

                    if (CommandRecordingDiagnosticsEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.PassBarrierSummary.{passIndex}",
                            TimeSpan.FromSeconds(2),
                            "Pass barrier summary: pass={0} image={1} buffer={2} queueTransfers={3} stageFlushes={4}",
                            passIndex,
                            imageBarriers.Count + swapchainBarriers.Count,
                            bufferBarriers.Count,
                            queueOwnershipTransfers,
                            stageFlushes);
                    }
                }
            }

            // Reset every inline query pool before the first render operation. Query-pool
            // resets are illegal inside rendering, and deferring them until QueryOp would
            // force the forward pass through a store/reload cycle for proxy queries.
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.PrepareInlineQueries"))
            {
                for (int prepareIndex = 0; prepareIndex < ops.Length; prepareIndex++)
                {
                    if (ops[prepareIndex] is not QueryOp pendingQuery ||
                        pendingQuery.Operation is not (
                            ERenderQueryOperation.Reset or
                            ERenderQueryOperation.Begin or
                            ERenderQueryOperation.WriteTimestamp or
                            ERenderQueryOperation.WriteProperties) ||
                        !recordingScratch.PreparedInlineQueries.Add(pendingQuery.Query))
                    {
                        continue;
                    }

                    uint queryCount = 1u;
                    if (pendingQuery.Target is not null &&
                        GenericToAPI<VkFrameBuffer>(pendingQuery.Target) is { MultiviewViewMask: not 0u } queryFbo)
                    {
                        queryCount = (uint)System.Numerics.BitOperations.PopCount(queryFbo.MultiviewViewMask);
                    }

                    if (!pendingQuery.Query.PrepareForRecording(commandBuffer, queryCount))
                    {
                        recordingScratch.PreparedInlineQueries.Remove(pendingQuery.Query);
                    }
                }
            }

            try
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.MainOpLoop"))
                {
                for (int opIndex = 0; opIndex < ops.Length; opIndex++)
                {
                    if (optionalPipelineDeferredOpIndices.Contains(opIndex))
                        continue;

                    var op = ops[opIndex];
                    try
                    {
                        if (op is TextureUploadFrameOp textureUploadOp)
                        {
                            EndActiveRenderPass();
                            if (passIndexLabelActive)
                            {
                                CmdEndLabel(commandBuffer);
                                passIndexLabelActive = false;
                            }

                            CmdBeginLabel(commandBuffer, "TextureUpload");
                            RecordVulkanCommandDiagnosticMarker(commandBuffer, textureUploadOp, textureUploadOp.PassIndex, opIndex);
                            RecordTextureUploadOp(commandBuffer, textureUploadOp.Upload);
                            CmdEndLabel(commandBuffer);
                            continue;
                        }

                        if (!hasActiveContext || !AreFrameOpContextsRecordingCompatible(activeContext, op.Context))
                        {
                            IDisposable? contextChangeProfileScope = null;
                            if (CommandRecordingDetailProfilingEnabled)
                                contextChangeProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.ContextChange");
                            try
                            {
                            // When the context changes but both the active render pass and the
                            // incoming op target the swapchain (target == null), keep the render
                            // pass alive.  Ending and re-beginning the swapchain render pass
                            // causes a storeOp â†’ layout transition â†’ loadOp cycle that can lose
                            // composited content (e.g. the skybox turns black).
                            int incomingPassIndex = op.PassIndex == int.MinValue && activePassIndex != int.MinValue
                                ? activePassIndex
                                : EnsureValidPassIndex(op.PassIndex, op.GetType().Name, op.Context.PassMetadata);

                            bool preservedSwapchainPass = renderPassActive &&
                                activeTarget is null &&
                                VulkanRenderGraphCompiler.OpTargetsSwapchain(op) &&
                                incomingPassIndex == activePassIndex;

                            // Query begin/draw/end capture their contexts independently.
                            // Descriptor or resource generations may advance while the
                            // enclosed mesh is prepared, but that must not split an otherwise
                            // compatible Vulkan rendering scope and leave an empty query.
                            bool preservedInlineQueryPass = activeInlineQuery is not null &&
                                renderPassActive &&
                                ReferenceEquals(activeTarget, op.Target) &&
                                incomingPassIndex == activePassIndex &&
                                op.Context.SchedulingIdentity == activeSchedulingIdentity &&
                                AreFrameOpContextsQueryScopeCompatible(activeContext, op.Context);
                            bool preservedRenderPass = preservedSwapchainPass || preservedInlineQueryPass;

                            if (!preservedRenderPass)
                            {
                                EndActiveRenderPass();
                            }

                            if (!preservedRenderPass && passIndexLabelActive)
                            {
                                CmdEndLabel(commandBuffer);
                                passIndexLabelActive = false;
                            }

                            activeContext = op.Context;
                            hasActiveContext = true;
                            ApplyPipelineOverride(activeContext);

                            if (TryActivateFrameOpResourcePlannerState(activeContext))
                            {
                                plannerContext = activeContext;
                                hasPlannerContext = true;
                            }
                            else if (activeContext.PipelineInstance is not null && !hasPlannerContext)
                            {
                                plannerContext = activeContext;
                                hasPlannerContext = true;
                            }
                            else if (activeContext.PipelineInstance is not null &&
                                RequiresResourcePlannerRebuild(plannerContext, activeContext))
                            {
                                Debug.VulkanWarningEvery(
                                    $"Vulkan.ResourcePlanner.ContextChangeDuringRecord.{activeContext.PipelineIdentity}.{activeContext.ViewportIdentity}",
                                    TimeSpan.FromSeconds(2),
                                    "[VulkanResourcePlanner] Keeping pre-recorded physical plan during command-buffer recording despite context change. OldPipe={0} NewPipe={1} OldVp={2} NewVp={3}.",
                                    plannerContext.PipelineIdentity,
                                    activeContext.PipelineIdentity,
                                    plannerContext.ViewportIdentity,
                                    activeContext.ViewportIdentity);
                            }

                            if (preservedRenderPass)
                            {
                                activeSchedulingIdentity = op.Context.SchedulingIdentity;
                            }
                            else
                            {
                                activePassIndex = int.MinValue;
                                activeSchedulingIdentity = int.MinValue;
                            }
                            }
                            finally
                            {
                                contextChangeProfileScope?.Dispose();
                            }
                        }

                        int opPassIndex = op.PassIndex == int.MinValue && activePassIndex != int.MinValue
                            ? activePassIndex
                            : EnsureValidPassIndex(op.PassIndex, op.GetType().Name, op.Context.PassMetadata);

                        if (opPassIndex == int.MinValue)
                        {
                            droppedFrameOps++;
                            if (op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                                droppedDrawOps++;
                            if (op is ComputeDispatchOp)
                                droppedComputeOps++;
                            firstFailure ??= CaptureFrameOpFailure(op, new InvalidOperationException("No valid render-graph pass index could be resolved."));

                            Debug.VulkanWarningEvery(
                                $"Vulkan.OpDroppedNoPass.{op.GetType().Name}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Dropping op '{0}' because no valid render-graph pass index could be resolved.",
                                op.GetType().Name);
                            continue;
                        }

                        if (skipUiPipelineOps && op.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                        {
                            droppedFrameOps++;
                            if (op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                                droppedDrawOps++;
                            if (op is ComputeDispatchOp)
                                droppedComputeOps++;

                            Debug.VulkanEvery(
                                $"Vulkan.SkipUiPipeline.{GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Skipping UI pipeline op {0} pass={1} pipe={2} due to XRE_SKIP_UI_PIPELINE=1.",
                                op.GetType().Name,
                                opPassIndex,
                                op.Context.PipelineIdentity);
                            continue;
                        }

                        if (skipUiBatchTextOps && IsUiBatchTextDrawOp(op))
                        {
                            droppedFrameOps++;
                            droppedDrawOps++;

                            Debug.VulkanEvery(
                                $"Vulkan.SkipUiBatchText.{GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Skipping batched UI text op pass={0} pipe={1} due to XRE_SKIP_UI_BATCH_TEXT=1.",
                                opPassIndex,
                                op.Context.PipelineIdentity);
                            continue;
                        }

                        // Diagnostic: log the first few ops with invalid pass index per frame
                        if (op.PassIndex == int.MinValue)
                        {
                            Debug.VulkanWarningEvery(
                                $"Vulkan.OpInvalidPass.{op.GetType().Name}",
                                TimeSpan.FromSeconds(2),
                                "[Vulkan] Op[{0}] {1} had PassIndex=MinValue (resolved to {2}). " +
                                "CtxPipeline={3} CtxMetadataCount={4} CtxViewport={5}",
                                opIndex,
                                op.GetType().Name,
                                opPassIndex,
                                op.Context.PipelineIdentity,
                                op.Context.PassMetadata?.Count ?? -1,
                                op.Context.ViewportIdentity);
                        }

                        int opSchedulingIdentity = op.Context.SchedulingIdentity;
                        if (opPassIndex != activePassIndex || opSchedulingIdentity != activeSchedulingIdentity)
                        {
                            IDisposable? passTransitionProfileScope = null;
                            if (CommandRecordingDetailProfilingEnabled)
                                passTransitionProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.PassTransition");
                            try
                            {
                            // Barriers are safest outside render passes.
                            EndActiveRenderPass();

                            if (passIndexLabelActive)
                            {
                                CmdEndLabel(commandBuffer);
                                passIndexLabelActive = false;
                            }

                            if (CanRecordCommandBufferDebugLabels)
                            {
                                passIndexLabelActive = CmdBeginLabel(
                                    commandBuffer,
                                    $"Pass={opPassIndex} Pipe={op.Context.PipelineIdentity} Vp={op.Context.ViewportIdentity}");
                            }

                            EmitPassBarriers(opPassIndex);
                            TransitionFrameOpDescriptorSnapshotsForSampling(
                                commandBuffer,
                                ops,
                                opIndex,
                                opPassIndex,
                                opSchedulingIdentity);
                            activePassIndex = opPassIndex;
                            activeSchedulingIdentity = opSchedulingIdentity;
                            }
                            finally
                            {
                                passTransitionProfileScope?.Dispose();
                            }
                        }

                        RecordVulkanCommandDiagnosticMarker(commandBuffer, op, opPassIndex, opIndex);
                        using var vulkanGpuScope = TryBeginVulkanGpuProfilerScope(commandBuffer, op, opPassIndex);

                        IDisposable? frameOpProfileScope = null;
                        if (CommandRecordingDetailProfilingEnabled)
                            frameOpProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope(GetRecordPrimaryFrameOpProfileScopeName(op));
                        try
                        {
                        switch (op)
                        {
                    case BlitOp blit:
                        EndActiveRenderPass();
                        if (blit.ColorBit && (blit.InFbo is null || blit.OutFbo is null))
                            EnsureSwapchainColorAttachmentLayoutForBlit();
                        CmdBeginLabel(commandBuffer, "Blit");
                        bool blitRecorded = RecordBlitOp(commandBuffer, imageIndex, blit, in swapchainTarget);
                        CmdEndLabel(commandBuffer);
                        if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit) && blitRecorded)
                        {
                            swapchainWrittenOutsideRenderPass = true;
                            if (blit.ColorBit)
                            {
                                swapchainInColorAttachmentLayout = true;
                                swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                            }
                            actualSwapchainWriteCount++;
                        }
                        break;

                    case ClearOp clear:
                        if (CommandRecordingDiagnosticsEnabled && clear.Target?.Name == "ForwardPassFBO")
                        {
                            Debug.VulkanEvery(
                                "Vulkan.FwdClear",
                                TimeSpan.FromSeconds(2),
                                "[Vulkan][FwdClear] ForwardPassFBO clear pass={0} color={1} depth={2} stencil={3}",
                                opPassIndex, clear.ClearColor, clear.ClearDepth, clear.ClearStencil);
                        }
                        if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(clear.Target?.Name))
                        {
                            Debug.VulkanEvery(
                                $"DeferredLighting.ClearOp.{clear.Target?.Name}",
                                TimeSpan.FromSeconds(1),
                                "[DeferredLightingDiag][ClearOp] target='{0}' pass={1} color={2} depth={3} stencil={4} activeTarget='{5}'",
                                clear.Target?.Name ?? "<swapchain>",
                                opPassIndex,
                                clear.ClearColor,
                                clear.ClearDepth,
                                clear.ClearStencil,
                                activeTarget?.Name ?? "<none>");
                        }

                        if (!renderPassActive || activeTarget != clear.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(clear.Target, opPassIndex, activeContext);
                        }

                        // Skip explicit color clears on the swapchain after the first render pass.
                        // CmdClearAttachments would erase scene content composited by an earlier pipeline.
                        // Depth/stencil clears are still allowed since they don't affect composited color.
                        bool clearRecorded = false;
                        uint clearRenderLayerCount = activeDynamicRendering
                            ? Math.Max(activeDynamicRenderingFormats.LayerCount, 1u)
                            : 0u;
                        uint clearRenderViewMask = activeDynamicRendering
                            ? activeDynamicRenderingFormats.ViewMask
                            : 0u;
                        if (clear.Target is null && swapchainClearedThisFrame && clear.ClearColor)
                        {
                            if (clear.ClearDepth || clear.ClearStencil)
                            {
                                // Emit depth/stencil clear only â€” strip the color clear.
                                RecordClearOp(commandBuffer, imageIndex, clear with { ClearColor = false }, activeRenderArea, in swapchainTarget, clearRenderLayerCount, clearRenderViewMask);
                                clearRecorded = true;
                            }
                            // else: pure color clear on swapchain after first pass â†’ skip entirely
                        }
                        else
                        {
                            RecordClearOp(commandBuffer, imageIndex, clear, activeRenderArea, in swapchainTarget, clearRenderLayerCount, clearRenderViewMask);
                            clearRecorded = true;
                        }
                        if (clear.Target is null && clearRecorded)
                            actualSwapchainWriteCount++;
                        break;

                    case TransformFeedbackOp transformFeedbackOp:
                        if (!renderPassActive || activeTarget != transformFeedbackOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(transformFeedbackOp.Target, opPassIndex, activeContext);
                        }

                        bool transformFeedbackLabelActive = false;
                        if (CanRecordCommandBufferDebugLabels)
                            transformFeedbackLabelActive = CmdBeginLabel(commandBuffer, $"TransformFeedback.{transformFeedbackOp.Operation}");
                        RecordTransformFeedbackOp(commandBuffer, transformFeedbackOp);
                        if (transformFeedbackLabelActive)
                            CmdEndLabel(commandBuffer);
                        break;

                    case QueryOp queryOp:
                        if (queryOp.Operation == ERenderQueryOperation.Reset)
                            break;

                        if (queryOp.Operation == ERenderQueryOperation.WriteTimestamp)
                        {
                            if (recordingScratch.PreparedInlineQueries.Contains(queryOp.Query) &&
                                queryOp.Query.WriteTimestamp(
                                    commandBuffer,
                                    queryOp.TimestampStage,
                                    queryOp.PointIndex) != ERenderQueryReadStatus.Ready)
                            {
                                queryFrameOpsRequireRerecordLocal = true;
                            }
                            break;
                        }

                        if (queryOp.Operation == ERenderQueryOperation.WriteProperties)
                        {
                            EndActiveRenderPass();
                            if (!recordingScratch.PreparedInlineQueries.Contains(queryOp.Query) ||
                                queryOp.Query.WriteProperties(
                                    commandBuffer,
                                    queryOp.SourceHandles.Span) != ERenderQueryReadStatus.Ready)
                            {
                                queryFrameOpsRequireRerecordLocal = true;
                            }
                            break;
                        }

                        if (queryOp.Operation == ERenderQueryOperation.CopyResults)
                        {
                            EndActiveRenderPass();
                            if (queryOp.Query.CopyResults(
                                    commandBuffer,
                                    queryOp.ResultDestination,
                                    queryOp.ResultDestinationOffset,
                                    queryOp.ResultStride,
                                    queryOp.IncludeAvailability) != ERenderQueryReadStatus.Ready)
                            {
                                queryFrameOpsRequireRerecordLocal = true;
                            }
                            break;
                        }

                        bool firstBeginForQuery = queryOp.Operation == ERenderQueryOperation.Begin &&
                            !recordingScratch.BegunInlineQueries.Contains(queryOp.Query);
                        if (firstBeginForQuery &&
                            !recordingScratch.PreparedInlineQueries.Contains(queryOp.Query))
                        {
                            queryFrameOpsRequireRerecordLocal = true;
                            Debug.VulkanWarningEvery(
                                $"Vulkan.UnpreparedInlineOcclusionQuery.{queryOp.Query.GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Inline occlusion query begin suppressed because its pool was not prepared. Query='{0}' pass={1} op={2}.",
                                queryOp.Query.Data.Name ?? "<unnamed>",
                                opPassIndex,
                                opIndex);
                        }

                        if (!renderPassActive || activeTarget != queryOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(queryOp.Target, opPassIndex, activeContext);
                        }

                        bool queryLabelActive = false;
                        if (CanRecordCommandBufferDebugLabels)
                            queryLabelActive = CmdBeginLabel(commandBuffer, $"Query.{queryOp.Operation}");
                        if (queryOp.Operation == ERenderQueryOperation.Begin)
                        {
                            if (activeInlineQuery is not null)
                            {
                                queryFrameOpsRequireRerecordLocal = true;
                                queryOp.Query.InvalidateRecordedResultEpoch(commandBuffer);
                                Debug.VulkanWarningEvery(
                                    $"Vulkan.NestedInlineQuery.{queryOp.Query.GetHashCode()}",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan.Query] Nested query begin rejected. active='{0}' requested='{1}' pass={2} op={3}.",
                                    activeInlineQuery.Data.Name ?? activeInlineQuery.Data.Descriptor.Kind.ToString(),
                                    queryOp.Query.Data.Name ?? queryOp.Descriptor.Kind.ToString(),
                                    opPassIndex,
                                    opIndex);
                            }
                            else if (recordingScratch.PreparedInlineQueries.Contains(queryOp.Query) &&
                                recordingScratch.BegunInlineQueries.Add(queryOp.Query))
                            {
                                activeInlineQuery = queryOp.Query.BeginQuery(commandBuffer) == ERenderQueryReadStatus.Ready
                                    ? queryOp.Query
                                    : null;
                                if (activeInlineQuery is null)
                                    queryFrameOpsRequireRerecordLocal = true;
                                activeInlineQueryRecordedDraw = false;
                            }
                            else if (recordingScratch.PreparedInlineQueries.Contains(queryOp.Query))
                            {
                                activeInlineQuery = null;
                                queryFrameOpsRequireRerecordLocal = true;
                                Debug.VulkanWarningEvery(
                                    $"Vulkan.DuplicateInlineOcclusionQuery.{queryOp.Query.GetHashCode()}",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan] Duplicate inline occlusion query begin suppressed in one command buffer. Query='{0}' pass={1} op={2}.",
                                    queryOp.Query.Data.Name ?? "<unnamed>",
                                    opPassIndex,
                                    opIndex);
                            }
                        }
                        else if (ReferenceEquals(activeInlineQuery, queryOp.Query))
                        {
                            if (!activeInlineQueryRecordedDraw)
                            {
                                queryFrameOpsRequireRerecordLocal = true;
                                activeInlineQuery.InvalidateRecordedResultEpoch(commandBuffer);
                                Debug.VulkanWarningEvery(
                                    $"Vulkan.EmptyInlineQuery.{activeInlineQuery.GetHashCode()}",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan] Inline occlusion query contained no recorded draw; this epoch will resolve visible. Query='{0}'.",
                                    activeInlineQuery.Data.Name ?? "<unnamed>");
                            }
                            queryOp.Query.EndQuery(commandBuffer);
                            activeInlineQuery = null;
                            activeInlineQueryRecordedDraw = false;
                        }
                        else
                        {
                            queryFrameOpsRequireRerecordLocal = true;
                            queryOp.Query.InvalidateRecordedResultEpoch(commandBuffer);
                            Debug.VulkanWarningEvery(
                                $"Vulkan.MismatchedInlineQueryEnd.{queryOp.Query.GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan.Query] Query end rejected because it does not match the active query. active='{0}' requested='{1}' pass={2} op={3}.",
                                activeInlineQuery?.Data.Name ?? "<none>",
                                queryOp.Query.Data.Name ?? queryOp.Descriptor.Kind.ToString(),
                                opPassIndex,
                                opIndex);
                        }
                        if (queryLabelActive)
                            CmdEndLabel(commandBuffer);
                        break;

                    case MeshDrawOp drawOp:
                        if (CommandRecordingDiagnosticsEnabled &&
                            string.Equals(
                                drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name,
                                "CpuOcclusionProxy.UnitCube",
                                StringComparison.Ordinal))
                        {
                            Debug.VulkanEvery(
                                "Vulkan.CpuOcclusionProxy.RecordState",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan][CpuQueryDiag] activeQuery={0} viewport=({1},{2},{3},{4}) scissor=({5},{6},{7},{8}) modelT=({9:F3},{10:F3},{11:F3}) modelS=({12:F3},{13:F3},{14:F3}) cameraT=({15:F3},{16:F3},{17:F3}).",
                                activeInlineQuery is not null,
                                drawOp.Draw.Viewport.X,
                                drawOp.Draw.Viewport.Y,
                                drawOp.Draw.Viewport.Width,
                                drawOp.Draw.Viewport.Height,
                                drawOp.Draw.Scissor.Offset.X,
                                drawOp.Draw.Scissor.Offset.Y,
                                drawOp.Draw.Scissor.Extent.Width,
                                drawOp.Draw.Scissor.Extent.Height,
                                drawOp.Draw.ModelMatrix.M41,
                                drawOp.Draw.ModelMatrix.M42,
                                drawOp.Draw.ModelMatrix.M43,
                                drawOp.Draw.ModelMatrix.M11,
                                drawOp.Draw.ModelMatrix.M22,
                                drawOp.Draw.ModelMatrix.M33,
                                drawOp.Draw.CameraPosition.X,
                                drawOp.Draw.CameraPosition.Y,
                                drawOp.Draw.CameraPosition.Z);
                        }

                        int meshCommandChainRunCount = CountContiguousMeshCommandChainRun(opIndex, drawOp, opPassIndex);
                        if (TryExecuteScheduledMeshCommandChainSecondaryRun(opIndex, meshCommandChainRunCount, opPassIndex, drawOp) ||
                            TryExecuteMeshCommandChainSecondaryRun(opIndex, meshCommandChainRunCount, opPassIndex, drawOp))
                        {
                            if (drawOp.Target is null)
                                actualSwapchainWriteCount += meshCommandChainRunCount;
                            opIndex = opIndex + meshCommandChainRunCount - 1;
                            break;
                        }

                        if (!renderPassActive || activeTarget != drawOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(drawOp.Target, opPassIndex, activeContext);
                        }

                        bool recordedInlineDraw = RecordMeshDrawIntoCommandBuffer(commandBuffer, drawOp, opPassIndex);
                        if (activeInlineQuery is not null && recordedInlineDraw)
                            activeInlineQueryRecordedDraw = true;
                        if (drawOp.Target is null)
                            actualSwapchainWriteCount++;
                    break;

                case IndirectDrawOp indirectOp:
                    int indirectCommandChainRunCount = CountContiguousIndirectCommandChainRun(opIndex, indirectOp, opPassIndex);
                    if (TryExecuteIndirectCommandChainSecondaryRun(opIndex, indirectCommandChainRunCount, opPassIndex, indirectOp))
                    {
                        if (indirectOp.Target is null)
                            actualSwapchainWriteCount += indirectCommandChainRunCount;
                        opIndex = opIndex + indirectCommandChainRunCount - 1;
                        break;
                    }

                    EndActiveRenderPass();
                    EmitIndirectDrawRunReadBarrier();
                    BeginRenderPassForTarget(indirectOp.Target, opPassIndex, activeContext);

                    CmdBeginLabel(commandBuffer, "IndirectDraw");
                    RecordIndirectDrawIntoCommandBuffer(commandBuffer, indirectOp, opPassIndex);
                    CmdEndLabel(commandBuffer);

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(
                        usedSecondary: false,
                        usedParallel: false,
                        opCount: 1);
                    if (indirectOp.Target is null)
                        actualSwapchainWriteCount++;
                    break;

                    case MeshTaskDispatchIndirectCountOp meshTaskOp:
                        if (!renderPassActive || activeTarget is not null)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(null, opPassIndex, activeContext);
                        }

                        CmdBeginLabel(commandBuffer, "MeshTaskDispatchIndirectCount");
                        RecordMeshTaskDispatchIndirectCountOp(commandBuffer, meshTaskOp);
                        CmdEndLabel(commandBuffer);
                        actualSwapchainWriteCount++;
                        break;

                    case ComputeDispatchOp computeOp:
                        EndActiveRenderPass();
                        if (TryGetSecondaryBucketForStart(secondaryBuckets, secondaryBucketByStart, opIndex, out VulkanRenderGraphCompiler.SecondaryRecordingBucket computeBucket) &&
                            TryRecordSecondaryBucket(
                                primaryCommandBuffer: commandBuffer,
                                frameDataImageIndex,
                                executedCommandChainSecondaryHandles,
                                ops,
                                opIndex,
                                computeBucket,
                                "ComputeDispatch"))
                        {
                            opIndex = opIndex + computeBucket.Count - 1;
                        }
                        else
                        {
                            CmdBeginLabel(commandBuffer, "ComputeDispatch");
                            RecordComputeDispatchOp(commandBuffer, frameDataImageIndex, computeOp, opIndex);
                            CmdEndLabel(commandBuffer);
                        }
                        break;

                    case ComputeDispatchIndirectOp computeIndirectOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, computeIndirectOp.Label);
                        RecordComputeDispatchIndirectOp(commandBuffer, frameDataImageIndex, computeIndirectOp);
                        CmdEndLabel(commandBuffer);
                        break;

                    case BufferCopyOp bufferCopyOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, bufferCopyOp.Label);
                        RecordBufferCopyOp(commandBuffer, bufferCopyOp);
                        CmdEndLabel(commandBuffer);
                        break;

                    case SubmissionMarkerOp submissionMarkerOp:
                        EndActiveRenderPass();
                        RegisterSubmissionMarker(commandBuffer, submissionMarkerOp.Fence);
                        break;

                    case MemoryBarrierOp memoryBarrierOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "MemoryBarrier");
                        EmitMemoryBarrierMask(commandBuffer, memoryBarrierOp.Mask);
                        CmdEndLabel(commandBuffer);
                        break;

                    case PublishFramebufferForSamplingOp publishOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "PublishFramebufferForSampling");
                        RecordPublishFramebufferForSamplingOp(commandBuffer, publishOp);
                        CmdEndLabel(commandBuffer);
                        break;

                    case DlssUpscaleOp dlssOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "DLSS.SuperResolution");
                        RecordDlssUpscaleOp(commandBuffer, dlssOp);
                        CmdEndLabel(commandBuffer);
                        break;
                    case DlssFrameGenerationOp frameGenerationOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "DLSS.FrameGenerationInputs");
                        RecordDlssFrameGenerationOp(commandBuffer, frameDataImageIndex, frameGenerationOp);
                        CmdEndLabel(commandBuffer);
                        break;
                        }
                        }
                        finally
                        {
                            frameOpProfileScope?.Dispose();
                        }
                    }
                    catch (Exception opEx)
                    {
                        droppedFrameOps++;
                        if (op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                            droppedDrawOps++;
                        if (op is ComputeDispatchOp or ComputeDispatchIndirectOp)
                            droppedComputeOps++;
                        firstFailure ??= CaptureFrameOpFailure(op, opEx);

                        EndActiveRenderPass();
                        if (renderPassLabelActive)
                        {
                            CmdEndLabel(commandBuffer);
                            renderPassLabelActive = false;
                        }

                        string opContext = BuildFrameOpFailureContext(op);

                        Debug.VulkanEvery(
                            $"Vulkan.FrameOpError.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Frame op recording failed for {0}: {1}: {2}{3}{4}",
                            op.GetType().Name,
                            opEx.GetType().Name,
                            opEx.Message,
                            opContext,
                            opEx.StackTrace is { Length: > 0 } ? Environment.NewLine + opEx.StackTrace : string.Empty);

                        // Continue recording remaining ops instead of aborting the
                        // entire command buffer.  A single broken shader/pipeline
                        // should not prevent the rest of the frame from rendering.
                        continue;
                    }
                }

                }

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.FinalOverlayAndDiagnostics"))
                {
                if (passIndexLabelActive)
                {
                    CmdEndLabel(commandBuffer);
                    passIndexLabelActive = false;
                }

                bool forceMagentaSwapchain = XREngine.Rendering.RenderDiagnosticsFlags.VkForceSwapchainMagenta;
                int sceneActualSwapchainWritesBeforeOverlay = actualSwapchainWriteCount;

                ExecuteDynamicUiBatchTextOverlay();

                bool touchSwapchainForFinalOverlay =
                    sceneActualSwapchainWritesBeforeOverlay > 0 ||
                    forceMagentaSwapchain;

                if (TargetTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanTarget] finalOverlay sceneActualWrites={0} actualWritesAfterOverlay={1} logicalSceneWriters={2} overlayWriters={3} forceMagenta={4} touch={5} activeTarget='{6}' activePass={7} activePassName='{8}'",
                        sceneActualSwapchainWritesBeforeOverlay,
                        actualSwapchainWriteCount,
                        sceneSwapchainWriters,
                        overlaySwapchainWriters,
                        forceMagentaSwapchain,
                        touchSwapchainForFinalOverlay,
                        activeTarget?.Name ?? "<swapchain>",
                        activePassIndex,
                        activePassIndex != int.MinValue ? ResolvePassName((hasActiveContext ? activeContext : initialContext).PassMetadata, activePassIndex) : "<none>");
                }

                if (touchSwapchainForFinalOverlay)
                {
                    // Finish with a swapchain render pass only when this command buffer has
                    // actual swapchain work. Opening an otherwise-empty pass clears the
                    // editor window to the clear color and hides the last valid frame.
                    if (!renderPassActive || activeTarget is not null)
                    {
                        EndActiveRenderPass();
                        BeginRenderPassForTarget(
                            null,
                            activePassIndex != int.MinValue ? activePassIndex : VulkanBarrierPlanner.SwapchainPassIndex,
                            hasActiveContext ? activeContext : initialContext);
                    }

                    // For presentation we want deterministic full-surface state regardless of prior per-viewport scissor.
                    // This also makes resize issues obvious (the clear should cover the entire swapchain extent).
                    Viewport swapViewport = CreateVulkanViewport(swapchainRecordExtent);

                    Rect2D swapScissor = new()
                    {
                        Offset = new Offset2D(0, 0),
                        Extent = swapchainRecordExtent
                    };

                    Api!.CmdSetViewport(commandBuffer, 0, 1, &swapViewport);
                    Api!.CmdSetScissor(commandBuffer, 0, 1, &swapScissor);
                }
                else
                {
                    EndActiveRenderPass();
                    if (ShouldRefreshUnwrittenSwapchainForPresent(
                            touchSwapchainForFinalOverlay,
                            transitionSwapchainToPresent) &&
                        !TryRefreshUnwrittenSwapchainFromLastWindowPresentSource())
                    {
                        TransitionUnwrittenSwapchainToPresent();
                    }
                }

                bool hasSceneFrameWork = clearCount > 0 || drawCount > 0 || blitCount > 0 || computeCount > 0;
                bool expectsSceneSwapchainWriters = transitionSwapchainToPresent && !IsRenderingExternalSwapchainTarget;
                bool preservingOverlayOnlyFrame =
                    sceneActualSwapchainWritesBeforeOverlay == 0 &&
                    sceneSwapchainWriters == 0 &&
                    overlaySwapchainWriters > 0 &&
                    !forceMagentaSwapchain;
                bool preservingPresentedSwapchainImage =
                    sceneActualSwapchainWritesBeforeOverlay == 0 &&
                    actualSwapchainWriteCount == 0 &&
                    imageWasEverPresentedAtRecordStart &&
                    !forceMagentaSwapchain;
                bool missingSceneSwapchainWriters =
                    expectsSceneSwapchainWriters &&
                    hasSceneFrameWork &&
                    sceneSwapchainWriters == 0 &&
                    actualSwapchainWriteCount == 0 &&
                    !preservingOverlayOnlyFrame &&
                    !preservingPresentedSwapchainImage;
                if (missingSceneSwapchainWriters)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.MissingSceneSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(10),
                        "[Vulkan][FrameFailure] Scene frame recorded zero pre-overlay swapchain writers (clears={0}, draws={1}, blits={2}, computes={3}, fboOnlyDraws={4}, fboOnlyBlits={5}). Overlay or diagnostic clears may still present.",
                        clearCount,
                        drawCount,
                        blitCount,
                        computeCount,
                        fboOnlyDrawOps,
                        fboOnlyBlitOps);
                }
                else if (expectsSceneSwapchainWriters &&
                         swapchainWriteCount == 0 &&
                         !preservingPresentedSwapchainImage)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.NoSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(10),
                        "[Vulkan] No swapchain write commands were recorded this frame (clears={0}, draws={1}, blits={2}, computes={3}). Preserving acquired swapchain image contents when already initialised.",
                        clearCount,
                        drawCount,
                        blitCount,
                        computeCount);
                }

                if (forceMagentaSwapchain)
                {
                    ClearAttachment magentaAttachment = new()
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = 0,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue(1f, 0f, 1f, 1f)
                        }
                    };

                    ClearRect clearRect = new()
                    {
                        Rect = new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = swapchainRecordExtent
                        },
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    };

                    Api!.CmdClearAttachments(commandBuffer, 1, &magentaAttachment, 1, &clearRect);
                    swapchainWriteCount++;
                    actualSwapchainWriteCount++;
                    swapchainClearWrites++;
                    forcedDiagnosticSwapchainWriters++;
                    MarkSwapchainStaticWriter("ForceMagenta", "forced debug clear", activePassIndex, ops.Length, hasActiveContext ? activeContext.PipelineIdentity : 0);

                    Debug.VulkanEvery(
                        $"Vulkan.ForceSwapchainMagenta.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Forced magenta swapchain clear due to XRE_FORCE_SWAPCHAIN_MAGENTA=1.");
                }

                bool needsFrameDiagnosticSummary = droppedFrameOps > 0 || missingSceneSwapchainWriters;
                bool shouldUpdateOnScreenDiagnostic =
                    needsFrameDiagnosticSummary ||
                    droppedDrawOps > 0 ||
                    Debug.ShouldLogEvery($"Vulkan.OnScreenDiagnostic.{GetHashCode()}", TimeSpan.FromSeconds(1));
                string? swapchainWriterSummary = shouldUpdateOnScreenDiagnostic || needsFrameDiagnosticSummary
                    ? $"{swapchainLastWriter}@p{swapchainLastWriterPass}:w{swapchainWriteCount}(scene={sceneSwapchainWriters} overlay={overlaySwapchainWriters} diag={forcedDiagnosticSwapchainWriters} C{swapchainClearWrites}D{swapchainDrawWrites}B{swapchainBlitWrites}) presentTransitions={swapchainPresentTransitions} ops={ops.Length} fboD={fboOnlyDrawOps} fboB={fboOnlyBlitOps} comp={computeCount}"
                    : null;
                if (shouldUpdateOnScreenDiagnostic)
                {
                    string pipelineLabel = hasActiveContext
                        ? (!string.IsNullOrWhiteSpace(activeContext.PipelineInstance?.Pipeline?.GetType().Name)
                            ? $"{activeContext.PipelineInstance!.Pipeline!.GetType().Name}#{activeContext.PipelineIdentity}"
                            : $"Pipeline#{activeContext.PipelineIdentity}")
                        : "None";
                    UpdateVulkanOnScreenDiagnostic(
                        pipelineLabel,
                        GetClearColorValue(),
                        droppedDrawOps,
                        droppedFrameOps,
                        swapchainWriterSummary!);
                }

                string? frameDiagnosticSummary = null;
                if (needsFrameDiagnosticSummary)
                {
                    frameDiagnosticSummary = BuildVulkanFrameDiagnosticSummary(
                        ops,
                        clearCount,
                        drawCount,
                        blitCount,
                        computeCount,
                        sceneSwapchainWriters,
                        overlaySwapchainWriters,
                        forcedDiagnosticSwapchainWriters,
                        fboOnlyDrawOps,
                        fboOnlyBlitOps,
                        swapchainWriterSummary!,
                        hasActiveContext ? activeContext : initialContext,
                        firstFailure);
                }

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameDiagnostics(
                    droppedFrameOps,
                    droppedDrawOps,
                    droppedComputeOps,
                    sceneSwapchainWriters,
                    overlaySwapchainWriters,
                    forcedDiagnosticSwapchainWriters,
                    fboOnlyDrawOps,
                    fboOnlyBlitOps,
                    missingSceneSwapchainWriters,
                    firstFailure?.OpType,
                    firstFailure?.PassIndex ?? int.MinValue,
                    firstFailure?.PipelineIdentity ?? 0,
                    firstFailure?.ViewportIdentity ?? 0,
                    firstFailure?.TargetName,
                    firstFailure?.MaterialName,
                    firstFailure?.ShaderName,
                    firstFailure?.Message,
                    frameDiagnosticSummary);

                recordingScratch.RecordSwapchainWriterCapacityHint = Math.Max(1, swapchainWritesByPipeline.Count);
                recordingScratch.RecordPipelineNameCapacityHint = Math.Max(1, pipelineNameByIdentity.Count);
                recordingScratch.RecordMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRendererFamily.Count);
                recordingScratch.RecordFboLayoutCapacityHint = Math.Max(1, fboLayoutTracking.Count);

                EndActiveRenderPass(finalClose: true);

                int expectedPresentTransitions = preserveSwapchainForOverlay || !transitionSwapchainToPresent ? 0 : 1;
                if (usedSwapchainDynamicRendering && swapchainPresentTransitions != expectedPresentTransitions)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.DynamicRendering.PresentTransitions.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Dynamic-rendering swapchain transitioned to PresentSrcKhr {0} times this command buffer; expected {1}.",
                        swapchainPresentTransitions,
                        expectedPresentTransitions);
                }

                EndFrameTimingQueries(commandBuffer, commandBufferImageSlot);

                CmdEndLabel(commandBuffer);
                }

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.EndCommandBuffer"))
                {
                    Result endResult = EndCommandBufferTracked(
                        commandBuffer,
                        cacheVariant: true,
                        out string trackingFailure);
                    if (endResult != Result.Success)
                        throw new Exception("Failed to record command buffer.");
                    if (!string.IsNullOrEmpty(trackingFailure))
                    {
                        recordingDeferredReason = trackingFailure;
                        return false;
                    }
                }
            }
            finally
            {
                if (activePipelineOverrideScopeSet)
                    activePipelineOverrideScope.Dispose();
            }

            recordedSwapchainWriteCount = actualSwapchainWriteCount;
            recordedSwapchainFinalLayout = swapchainFinalLayout;
            queryFrameOpsRequireRerecord = queryFrameOpsRequireRerecordLocal;
            return true;
        }


        internal static bool ShouldRefreshUnwrittenSwapchainForPresent(
            bool touchedSwapchain,
            bool transitionSwapchainToPresent)
            => !touchedSwapchain && transitionSwapchainToPresent;

        private void RecordClearOp(
            CommandBuffer commandBuffer,
            uint imageIndex,
            ClearOp op,
            Rect2D activeRenderArea,
            in SwapchainRecordingTarget swapchainTarget,
            uint activeRenderLayerCount = 0u,
            uint activeRenderViewMask = 0u)
        {
            _ = imageIndex;

            Extent2D targetExtent = op.Target is null
                ? (swapchainTarget.IsValid ? swapchainTarget.Extent : swapChainExtent)
                : new Extent2D(Math.Max(op.Target.Width, 1u), Math.Max(op.Target.Height, 1u));

            Rect2D clearArea = ClampRectToExtent(
                op.Rect,
                targetExtent);
            clearArea = ClampRectToRenderArea(clearArea, activeRenderArea);

            // Vulkan validation requires non-zero extent for vkCmdClearAttachments.
            if (clearArea.Extent.Width == 0 || clearArea.Extent.Height == 0)
                return;

            VkFrameBuffer? clearTargetFrameBuffer = op.Target is not null
                ? GenericToAPI<VkFrameBuffer>(op.Target)
                : null;
            clearTargetFrameBuffer?.EnsureCurrent();
            uint clearLayerCount = ResolveClearRectLayerCount(op.Target, clearTargetFrameBuffer, activeRenderLayerCount, activeRenderViewMask);

            if (clearLayerCount > 1u)
            {
                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                    $"Vulkan.CmdClearAttachments.Layered.{op.Target?.Name ?? "<swapchain>"}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] CmdClearAttachments layered clear target='{0}' layers={1} activeLayers={2} activeViewMask=0x{3:X} fboLayers={4} fboViewMask=0x{5:X}",
                    op.Target?.Name ?? "<swapchain>",
                    clearLayerCount,
                    activeRenderLayerCount,
                    activeRenderViewMask,
                    clearTargetFrameBuffer?.FramebufferLayers ?? 0u,
                    clearTargetFrameBuffer?.MultiviewViewMask ?? 0u);
                }
            }

            ClearRect clearRect = new()
            {
                Rect = clearArea,
                BaseArrayLayer = 0,
                LayerCount = clearLayerCount
            };

            ClearRect* rectPtr = stackalloc ClearRect[1];
            rectPtr[0] = clearRect;

            if (op.Target is null)
            {
                // Swapchain: single color attachment + depth.
                ClearAttachment* attachments = stackalloc ClearAttachment[2];
                uint count = 0;

                if (op.ClearColor)
                {
                    attachments[count++] = new ClearAttachment
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = 0,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue
                            {
                                Float32_0 = op.Color.R,
                                Float32_1 = op.Color.G,
                                Float32_2 = op.Color.B,
                                Float32_3 = op.Color.A
                            }
                        }
                    };
                }

                if (op.ClearDepth || op.ClearStencil)
                {
                    ImageAspectFlags requestedAspects = ImageAspectFlags.None;
                    if (op.ClearDepth)
                        requestedAspects |= ImageAspectFlags.DepthBit;
                    if (op.ClearStencil)
                        requestedAspects |= ImageAspectFlags.StencilBit;

                    // Only emit aspects actually supported by the swapchain depth attachment view.
                    // Example: VK_FORMAT_D32_SFLOAT does not support stencil clears.
                    ImageAspectFlags depthAspect = swapchainTarget.IsValid ? swapchainTarget.DepthAspect : _swapchainDepthAspect;
                    ImageAspectFlags aspects = requestedAspects & depthAspect;

                    if (aspects == ImageAspectFlags.None)
                        goto SkipSwapchainDepthClear;

                    attachments[count++] = new ClearAttachment
                    {
                        AspectMask = aspects,
                        ClearValue = new ClearValue
                        {
                            DepthStencil = new ClearDepthStencilValue
                            {
                                Depth = op.Depth,
                                Stencil = op.Stencil
                            }
                        }
                    };
                }

            SkipSwapchainDepthClear:

                if (count > 0)
                    Api!.CmdClearAttachments(commandBuffer, count, attachments, 1, rectPtr);

                return;
            }

            var vkFrameBuffer = clearTargetFrameBuffer;
            if (vkFrameBuffer is null)
                return;

            uint maxAttachments = Math.Max(vkFrameBuffer.AttachmentCount + 1u, 2u);
            ClearAttachment* fboAttachments = stackalloc ClearAttachment[(int)maxAttachments];
            uint fboCount = vkFrameBuffer.WriteClearAttachments(fboAttachments, op.ClearColor, op.ClearDepth, op.ClearStencil);
            string targetName = op.Target.Name ?? "<unnamed>";
            if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(targetName))
            {
                Debug.VulkanEvery(
                    $"DeferredLighting.CmdClearAttachments.{targetName}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][CmdClearAttachments] target='{0}' count={1} color={2} depth={3} stencil={4} rect=({5},{6},{7},{8})",
                    targetName,
                    fboCount,
                    op.ClearColor,
                    op.ClearDepth,
                    op.ClearStencil,
                    clearArea.Offset.X,
                    clearArea.Offset.Y,
                    clearArea.Extent.Width,
                    clearArea.Extent.Height);
            }

            if (fboCount > 0)
                Api!.CmdClearAttachments(commandBuffer, fboCount, fboAttachments, 1, rectPtr);
        }

        private static uint ResolveClearRectLayerCount(
            XRFrameBuffer? target,
            VkFrameBuffer? clearTargetFrameBuffer,
            uint activeRenderLayerCount,
            uint activeRenderViewMask)
        {
            if (target is null)
                return 1u;

            if (activeRenderViewMask != 0u || clearTargetFrameBuffer?.MultiviewViewMask != 0u)
                return 1u;

            if (activeRenderLayerCount > 1u && IsStereoCompatibleClearTarget(target, clearTargetFrameBuffer))
                return 1u;

            if (activeRenderLayerCount > 1u && RuntimeEngine.Rendering.State.IsStereoPass)
                return 1u;

            if (activeRenderLayerCount > 0u)
                return Math.Max(activeRenderLayerCount, 1u);

            return Math.Max(clearTargetFrameBuffer?.FramebufferLayers ?? 1u, 1u);
        }

        private static bool IsStereoCompatibleClearTarget(XRFrameBuffer target, VkFrameBuffer? clearTargetFrameBuffer)
        {
            var targets = target.Targets;
            if (targets is null || targets.Length == 0)
                return false;

            uint framebufferLayers = clearTargetFrameBuffer?.FramebufferLayers ?? 0u;
            for (int i = 0; i < targets.Length; i++)
            {
                var (attachmentTarget, _, _, layerIndex) = targets[i];
                if (layerIndex >= 0)
                    continue;

                if (attachmentTarget is XRTexture texture &&
                    VkFrameBuffer.IsStereoCompatibleTextureArrayAttachment(texture, framebufferLayers))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatFboAttachmentSignature(FrameBufferAttachmentSignature[] signatures)
        {
            if (signatures.Length == 0)
                return "<none>";

            StringBuilder builder = new();
            for (int i = 0; i < signatures.Length; i++)
            {
                if (i > 0)
                    builder.Append("; ");

                FrameBufferAttachmentSignature signature = signatures[i];
                builder
                    .Append(i)
                    .Append(":role=").Append(signature.Role)
                    .Append("/color=").Append(signature.ColorIndex)
                    .Append("/format=").Append(signature.Format)
                    .Append("/samples=").Append(signature.Samples)
                    .Append("/aspect=").Append(signature.AspectMask)
                    .Append("/load=").Append(signature.LoadOp)
                    .Append("/store=").Append(signature.StoreOp)
                    .Append("/stencilLoad=").Append(signature.StencilLoadOp)
                    .Append("/stencilStore=").Append(signature.StencilStoreOp)
                    .Append("/initial=").Append(signature.InitialLayout)
                    .Append("/ref=").Append(signature.ReferenceLayout)
                    .Append("/final=").Append(signature.FinalLayout);
            }

            return builder.ToString();
        }

        private static Rect2D ClampRectToExtent(Rect2D rect, Extent2D extent)
        {
            int extentWidth = (int)Math.Max(extent.Width, 1u);
            int extentHeight = (int)Math.Max(extent.Height, 1u);

            int x = Math.Clamp(rect.Offset.X, 0, extentWidth);
            int y = Math.Clamp(rect.Offset.Y, 0, extentHeight);

            int maxWidth = Math.Max(extentWidth - x, 0);
            int maxHeight = Math.Max(extentHeight - y, 0);

            int width = Math.Clamp((int)rect.Extent.Width, 0, maxWidth);
            int height = Math.Clamp((int)rect.Extent.Height, 0, maxHeight);

            return new Rect2D
            {
                Offset = new Offset2D(x, y),
                Extent = new Extent2D((uint)width, (uint)height)
            };
        }

        private static Rect2D ClampRectToRenderArea(Rect2D rect, Rect2D renderArea)
        {
            int renderLeft = renderArea.Offset.X;
            int renderTop = renderArea.Offset.Y;
            int renderRight = AddExtentClamped(renderArea.Offset.X, renderArea.Extent.Width);
            int renderBottom = AddExtentClamped(renderArea.Offset.Y, renderArea.Extent.Height);

            int rectLeft = rect.Offset.X;
            int rectTop = rect.Offset.Y;
            int rectRight = AddExtentClamped(rect.Offset.X, rect.Extent.Width);
            int rectBottom = AddExtentClamped(rect.Offset.Y, rect.Extent.Height);

            int left = Math.Max(rectLeft, renderLeft);
            int top = Math.Max(rectTop, renderTop);
            int right = Math.Min(rectRight, renderRight);
            int bottom = Math.Min(rectBottom, renderBottom);

            if (right <= left || bottom <= top)
            {
                return new Rect2D
                {
                    Offset = new Offset2D(left, top),
                    Extent = new Extent2D(0, 0)
                };
            }

            return new Rect2D
            {
                Offset = new Offset2D(left, top),
                Extent = new Extent2D((uint)(right - left), (uint)(bottom - top))
            };
        }

        private static int AddExtentClamped(int offset, uint extent)
        {
            long value = (long)offset + extent;
            if (value > int.MaxValue)
                return int.MaxValue;
            if (value < int.MinValue)
                return int.MinValue;
            return (int)value;
        }

        private void RecordPublishFramebufferForSamplingOp(CommandBuffer commandBuffer, PublishFramebufferForSamplingOp op)
        {
            XRFrameBuffer fbo = op.FrameBuffer;
            if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is not VkFrameBuffer vkFbo)
                return;

            vkFbo.EnsureCurrent();
            if (vkFbo.AttachmentCount == 0)
                return;

            int maxLayerSpan = Math.Max((int)vkFbo.FramebufferLayers, 1);
            ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[checked((int)vkFbo.AttachmentCount * maxLayerSpan)];
            uint barrierCount = 0;
            PipelineStageFlags srcStages = 0;
            PipelineStageFlags dstStages = 0;

            for (int attachmentIndex = 0; attachmentIndex < (int)vkFbo.AttachmentCount; attachmentIndex++)
            {
                if (!vkFbo.TryGetAttachmentTarget(
                    attachmentIndex,
                    out IFrameBufferAttachement? target,
                    out EFrameBufferAttachment attachment,
                    out int mipLevel,
                    out int layerIndex) ||
                    !IsColorAttachment(attachment))
                {
                    continue;
                }

                const ImageAspectFlags requestedAspect = ImageAspectFlags.ColorBit;
                if (!TryResolveAttachmentImage(target, mipLevel, layerIndex, requestedAspect, out BlitImageInfo info) ||
                    info.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.PublishFboForSampling.Unresolved.{fbo.GetHashCode()}.{attachmentIndex}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping publish-for-sampling for '{0}' attachment {1}: image handle could not be resolved.",
                        fbo.Name ?? "<unnamed>",
                        attachmentIndex);
                    continue;
                }

                Image transitionImage = info.Image;
                uint transitionMipLevel = info.MipLevel;
                uint imageBaseLayer;
                uint transitionLayerCount;
                ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(info.Format, requestedAspect);

                if (vkFbo.TryGetAttachmentView(attachmentIndex, out ImageView attachmentView) &&
                    TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) &&
                    viewInfo.Image.Handle != 0)
                {
                    transitionImage = viewInfo.Image;
                    transitionMipLevel = viewInfo.SubresourceRange.BaseMipLevel;
                    imageBaseLayer = viewInfo.SubresourceRange.BaseArrayLayer;
                    transitionLayerCount = Math.Max(viewInfo.SubresourceRange.LayerCount, 1u);

                    ImageAspectFlags viewAspect = NormalizeBarrierAspectMask(info.Format, viewInfo.SubresourceRange.AspectMask);
                    if (viewAspect != ImageAspectFlags.None)
                        aspectMask = viewAspect;
                }
                else
                {
                    ResolveFboAttachmentImageLayerSpan(
                        vkFbo,
                        layerIndex,
                        in info,
                        out imageBaseLayer,
                        out transitionLayerCount);
                }

                ImageLayout targetLayout = ResolvePublishedSampledLayout(info.DescriptorSource, aspectMask);
                uint layerCount = Math.Max(transitionLayerCount, 1u);
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint imageLayer = imageBaseLayer + layerOffset;
                    ImageSubresourceRange transitionRange = new()
                    {
                        AspectMask = aspectMask,
                        BaseMipLevel = transitionMipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = imageLayer,
                        LayerCount = 1
                    };

                    ImageLayout oldLayout = TryGetRecordedImageAccessState(
                        commandBuffer,
                        transitionImage,
                        transitionRange,
                        out VulkanImageAccessState recordedState)
                            ? recordedState.Layout
                            : ImageLayout.Undefined;
                    if (oldLayout == ImageLayout.Undefined)
                        oldLayout = ImageLayout.ColorAttachmentOptimal;

                    if (oldLayout == targetLayout)
                        continue;

                    PipelineStageFlags srcStage = ResolvePublishedSampledSourceStage(oldLayout);
                    PipelineStageFlags dstStage = ResolvePublishedSampledDestinationStage(targetLayout);
                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = ResolvePublishedSampledSourceAccess(oldLayout),
                        DstAccessMask = ResolvePublishedSampledDestinationAccess(targetLayout),
                        OldLayout = oldLayout,
                        NewLayout = targetLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = transitionImage,
                        SubresourceRange = transitionRange
                    };

                    barriers[barrierCount++] = barrier;
                    srcStages |= srcStage;
                    dstStages |= dstStage;
                }
            }

            if (barrierCount == 0)
                return;

            CmdPipelineBarrierTracked(
                commandBuffer,
                NormalizePipelineStages(srcStages),
                NormalizePipelineStages(dstStages),
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                barrierCount,
                barriers);
        }

        private static ImageLayout ResolvePublishedSampledLayout(IVkImageDescriptorSource? source, ImageAspectFlags aspectMask)
        {
            if (source is not null &&
                (source.DescriptorUsage & ImageUsageFlags.StorageBit) != 0 &&
                (source.DescriptorUsage & ImageUsageFlags.SampledBit) != 0)
            {
                return ImageLayout.General;
            }

            return IsDepthOrStencilAspect(aspectMask)
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;
        }

        private static PipelineStageFlags ResolvePublishedSampledSourceStage(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal =>
                    PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.ShaderReadOnlyOptimal or ImageLayout.DepthStencilReadOnlyOptimal =>
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.General => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                _ => PipelineStageFlags.AllCommandsBit
            };

        private static PipelineStageFlags ResolvePublishedSampledDestinationStage(ImageLayout layout)
            => layout == ImageLayout.General
                ? PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit
                : PipelineStageFlags.FragmentShaderBit;

        private static AccessFlags ResolvePublishedSampledSourceAccess(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => 0,
                ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal =>
                    AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.ShaderReadBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
                ImageLayout.General => AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit
            };

        private static AccessFlags ResolvePublishedSampledDestinationAccess(ImageLayout layout)
            => layout == ImageLayout.General
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
                : AccessFlags.ShaderReadBit;

        private bool RecordBlitOp(CommandBuffer commandBuffer, uint imageIndex, BlitOp op)
        {
            SwapchainRecordingTarget swapchainTarget = default;
            return RecordBlitOp(commandBuffer, imageIndex, op, in swapchainTarget);
        }

        private bool RecordBlitOp(
            CommandBuffer commandBuffer,
            uint imageIndex,
            BlitOp op,
            in SwapchainRecordingTarget swapchainTarget)
        {
            bool ExecuteSingleBlit(in BlitImageInfo source, in BlitImageInfo destination, Filter filter)
            {
                if (!TryResolveLiveBlitImage(source, out BlitImageInfo resolvedSource) ||
                    !TryResolveLiveBlitImage(destination, out BlitImageInfo resolvedDestination))
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.UnresolvedLiveHandle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: source/destination image could not be resolved to a live handle.");
                    return false;
                }

                uint commonLayerCount = Math.Min(resolvedSource.LayerCount, resolvedDestination.LayerCount);
                if (commonLayerCount == 0)
                    return false;
                resolvedSource = resolvedSource.WithLayerCount(commonLayerCount);
                resolvedDestination = resolvedDestination.WithLayerCount(commonLayerCount);

                // Validate image handles before issuing Vulkan commands.
                // A stale/destroyed handle causes a native access violation (0xC0000005) in the driver.
                if (resolvedSource.Image.Handle == 0 || resolvedDestination.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.NullHandle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: null image handle. Src=0x{0:X} Dst=0x{1:X} SrcFmt={2} DstFmt={3}",
                        resolvedSource.Image.Handle,
                        resolvedDestination.Image.Handle,
                        resolvedSource.Format,
                        resolvedDestination.Format);
                    return false;
                }

                // Validate blit region dimensions â€” zero-sized regions can crash some drivers.
                if (op.InW == 0 || op.InH == 0 || op.OutW == 0 || op.OutH == 0)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.ZeroRegion",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: zero-sized region. In={0}x{1} Out={2}x{3}",
                        op.InW, op.InH, op.OutW, op.OutH);
                    return false;
                }

                if (!TryBuildImageBlit(
                    resolvedSource,
                    resolvedDestination,
                    op.InX,
                    op.InY,
                    op.InW,
                    op.InH,
                    op.OutX,
                    op.OutY,
                    op.OutW,
                    op.OutH,
                    out ImageBlit region))
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Blit.EmptyClampedRegion",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Blit skipped: requested region does not intersect live extents. SrcReq={0},{1}+{2}x{3} SrcExtent={4}x{5} DstReq={6},{7}+{8}x{9} DstExtent={10}x{11}",
                        op.InX,
                        op.InY,
                        op.InW,
                        op.InH,
                        resolvedSource.Extent.Width,
                        resolvedSource.Extent.Height,
                        op.OutX,
                        op.OutY,
                        op.OutW,
                        op.OutH,
                        resolvedDestination.Extent.Width,
                        resolvedDestination.Extent.Height);
                    return false;
                }

                // Derive post-blit target layouts.  PreferredLayout may be Undefined
                // for newly-created dedicated images whose tracked layout hasn't been
                // set yet.  In that case, fall back to the attachment-optimal layout
                // based on the image's aspect mask.
                static ImageLayout DerivePostBlitLayout(in BlitImageInfo info, bool isDestination)
                {
                    if (info.DescriptorSource is { } descriptorSource)
                    {
                        ImageUsageFlags usage = descriptorSource.DescriptorUsage;
                        if ((usage & ImageUsageFlags.StorageBit) != 0)
                            return ImageLayout.General;

                        if ((usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0)
                        {
                            return IsDepthOrStencilAspect(info.AspectMask)
                                ? ImageLayout.DepthStencilReadOnlyOptimal
                                : ImageLayout.ShaderReadOnlyOptimal;
                        }
                    }

                    if (info.PreferredLayout != ImageLayout.Undefined)
                        return info.PreferredLayout;
                    return IsDepthOrStencilAspect(info.AspectMask)
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.ColorAttachmentOptimal;
                }

                ImageLayout srcPostLayout = DerivePostBlitLayout(resolvedSource, false);
                ImageLayout dstPostLayout = DerivePostBlitLayout(resolvedDestination, true);

                // Pre-blit: transition from ACTUAL current layout (PreferredLayout)
                // to Transfer-optimal.  For newly-created images this is Undefined,
                // which is a valid OldLayout (content is discarded, which is fine for
                // the destination; for the source, reading from Undefined gives
                // undefined content but won't crash or cause validation errors).
                TransitionForBlit(
                    commandBuffer,
                    resolvedSource,
                    resolvedSource.PreferredLayout,
                    ImageLayout.TransferSrcOptimal,
                    resolvedSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    resolvedSource.StageMask,
                    PipelineStageFlags.TransferBit);

                TransitionForBlit(
                    commandBuffer,
                    resolvedDestination,
                    resolvedDestination.PreferredLayout,
                    ImageLayout.TransferDstOptimal,
                    resolvedDestination.AccessMask,
                    AccessFlags.TransferWriteBit,
                    resolvedDestination.StageMask,
                    PipelineStageFlags.TransferBit);

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        "Vulkan.Blit.Record",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] CmdBlitImage: src=0x{0:X}({1}) dst=0x{2:X}({3}) region={4},{5}+{6}x{7}â†’{8},{9}+{10}x{11} filter={12}",
                    resolvedSource.Image.Handle, resolvedSource.Format,
                    resolvedDestination.Image.Handle, resolvedDestination.Format,
                    op.InX, op.InY, op.InW, op.InH,
                        op.OutX, op.OutY, op.OutW, op.OutH,
                        filter);
                }

                CmdBlitImageTracked(
                    commandBuffer,
                    resolvedSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    resolvedDestination.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &region,
                    filter);

                // Post-blit: transition back to the attachment-optimal layout.
                TransitionForBlit(
                    commandBuffer,
                    resolvedSource,
                    ImageLayout.TransferSrcOptimal,
                    srcPostLayout,
                    AccessFlags.TransferReadBit,
                    resolvedSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    resolvedSource.StageMask);

                TransitionForBlit(
                    commandBuffer,
                    resolvedDestination,
                    ImageLayout.TransferDstOptimal,
                    dstPostLayout,
                    AccessFlags.TransferWriteBit,
                    resolvedDestination.AccessMask,
                    PipelineStageFlags.TransferBit,
                    resolvedDestination.StageMask);

                return true;
            }

            bool copiedAny = false;

            if (op.ColorBit &&
                TryResolveBlitImage(op.InFbo, imageIndex, op.ReadBufferMode, wantColor: true, wantDepth: false, wantStencil: false, out var colorSource, isSource: true, in swapchainTarget) &&
                TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.ColorAttachment0, wantColor: true, wantDepth: false, wantStencil: false, out var colorDestination, isSource: false, in swapchainTarget))
            {
                copiedAny |= ExecuteSingleBlit(colorSource, colorDestination, op.LinearFilter ? Filter.Linear : Filter.Nearest);
            }

            if ((op.DepthBit || op.StencilBit) &&
                TryResolveBlitImage(op.InFbo, imageIndex, op.ReadBufferMode, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthSource, isSource: true, in swapchainTarget) &&
                TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.None, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthDestination, isSource: false, in swapchainTarget))
            {
                // Vulkan only supports nearest filtering for depth/stencil blits.
                copiedAny |= ExecuteSingleBlit(depthSource, depthDestination, Filter.Nearest);
            }

            if (!copiedAny)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Blit.NoAttachment",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Blit skipped: unable to resolve source/destination attachments for requested masks (Color={0}, Depth={1}, Stencil={2}).",
                    op.ColorBit,
                    op.DepthBit,
                    op.StencilBit);
            }

            return copiedAny;
        }

        private bool PlannerCoversIndirectBufferTransition(int passIndex, Silk.NET.Vulkan.Buffer indirectBuffer)
        {
            IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> plannedBarriers = BarrierPlanner.GetBufferBarriersForPass(passIndex);
            if (plannedBarriers.Count == 0)
                return false;

            for (int i = 0; i < plannedBarriers.Count; i++)
            {
                VulkanBarrierPlanner.PlannedBufferBarrier planned = plannedBarriers[i];
                if (!TryResolveTrackedBuffer(planned.ResourceName, out Silk.NET.Vulkan.Buffer plannedBuffer, out _))
                    continue;

                if (plannedBuffer.Handle != indirectBuffer.Handle)
                    continue;

                bool transitionsToIndirectRead =
                    (planned.Next.AccessMask & AccessFlags.IndirectCommandReadBit) != 0 ||
                    (planned.Next.StageMask & PipelineStageFlags.DrawIndirectBit) != 0;

                if (transitionsToIndirectRead)
                    return true;
            }

            return false;
        }

        private void RecordIndirectDrawOp(CommandBuffer commandBuffer, IndirectDrawOp op, bool allowInlineBarrier = true)
        {
            var indirectBuffer = op.IndirectBuffer.BufferHandle;
            if (indirectBuffer is null || !indirectBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordIndirectDrawOp: Invalid indirect buffer.");
                return;
            }

            bool plannerCoversIndirectBarrier = PlannerCoversIndirectBufferTransition(op.PassIndex, indirectBuffer.Value);
            if (!plannerCoversIndirectBarrier && allowInlineBarrier)
            {
                MemoryBarrier memoryBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.IndirectCommandReadBit,
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                    PipelineStageFlags.DrawIndirectBit,
                    DependencyFlags.None,
                    1,
                    &memoryBarrier,
                    0,
                    null,
                    0,
                    null);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
            }
            else if (!plannerCoversIndirectBarrier)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
            }
            else
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
                Debug.VulkanWarningEvery(
                    "Vulkan.IndirectBarrier.Overlap",
                    TimeSpan.FromSeconds(2),
                    "Indirect barrier overlap detected and suppressed: pass={0} drawCount={1}",
                    op.PassIndex,
                    op.DrawCount);
            }

            // Calculate the byte offset into the indirect buffer
            ulong bufferOffset = op.ByteOffset;
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indirectBuffer.Value.Handle,
                "IndirectDraw.Commands");

            if (IndirectTraceEnabled)
            {
                Debug.Vulkan(
                    "[VulkanIndirect] pass={0} passName='{1}' target='{2}' targetId={3} indirect=0x{4:X} parameter=0x{5:X} offset={6} countOffset={7} stride={8} maxDraws={9} useCount={10} renderer={11} material='{12}' program='{13}'",
                    op.PassIndex,
                    ResolvePassName(op.Context.PassMetadata, op.PassIndex),
                    op.Target?.Name ?? "<swapchain>",
                    op.Target?.GetHashCode() ?? 0,
                    indirectBuffer.Value.Handle,
                    op.ParameterBuffer?.BufferHandle?.Handle ?? 0UL,
                    op.ByteOffset,
                    op.CountByteOffset,
                    op.Stride,
                    op.DrawCount,
                    op.UseCount,
                    op.MeshRenderer.MeshRenderer.Name ?? op.MeshRenderer.GetHashCode().ToString(),
                    (op.Draw.MaterialOverride ?? op.MeshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                    op.Draw.PreparedProgramIdentity ?? "<no program>");
            }

            if (op.DrawCount == 0)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Indirect.ZeroDrawCount",
                    TimeSpan.FromSeconds(1),
                    "RecordIndirectDrawOp skipped: drawCount was zero.");
                return;
            }

            if (op.BindlessMaterialTextures is { } bindlessMaterialTextures &&
                !TryBindGlobalMaterialTextureDescriptorSet(
                    commandBuffer,
                    bindlessMaterialTextures.Program,
                    bindlessMaterialTextures.Consumer))
            {
                return;
            }

            if (op.UseCount && _supportsDrawIndirectCount)
            {
                var parameterBuffer = op.ParameterBuffer?.BufferHandle;
                if (parameterBuffer is null || !parameterBuffer.HasValue)
                {
                    Debug.VulkanWarning("RecordIndirectDrawOp: Invalid parameter buffer for count draw.");
                    return;
                }

                // The parameter buffer contains the draw count at offset 0 (uint)
                TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.Buffer,
                    parameterBuffer.Value.Handle,
                    "IndirectDraw.Count");
                if (_usesCoreDrawIndirectCountCommands)
                {
                    Api!.CmdDrawIndexedIndirectCount(
                        commandBuffer,
                        indirectBuffer.Value,
                        bufferOffset,
                        parameterBuffer.Value,
                        (ulong)op.CountByteOffset,
                        op.DrawCount,
                        op.Stride);
                }
                else if (_khrDrawIndirectCount is not null)
                {
                    _khrDrawIndirectCount.CmdDrawIndexedIndirectCount(
                        commandBuffer,
                        indirectBuffer.Value,
                        bufferOffset,
                        parameterBuffer.Value,
                        (ulong)op.CountByteOffset,
                        op.DrawCount,
                        op.Stride);
                }
                else
                {
                    Debug.VulkanWarning("RecordIndirectDrawOp: Indirect-count support was published without a core or KHR command entry point.");
                    return;
                }

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                    usedCountPath: true,
                    usedLoopFallback: false,
                    apiCalls: 1,
                    submittedDraws: op.DrawCount);
            }
            else
            {
                // Prefer contiguous multi-draw in the non-count path.
                Api!.CmdDrawIndexedIndirect(
                    commandBuffer,
                    indirectBuffer.Value,
                    bufferOffset,
                    op.DrawCount,
                    op.Stride);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                    usedCountPath: false,
                    usedLoopFallback: false,
                    apiCalls: 1,
                    submittedDraws: op.DrawCount);
            }
        }

        private void RecordTransformFeedbackOp(CommandBuffer commandBuffer, TransformFeedbackOp op)
        {
            switch (op.Operation)
            {
                case EXRTransformFeedbackOperation.BindBuffer:
                    op.TransformFeedback.BindFeedbackBuffer(
                        commandBuffer,
                        op.FeedbackBufferOffset,
                        op.FeedbackBufferSize ?? Vk.WholeSize);
                    break;

                case EXRTransformFeedbackOperation.Begin:
                    if (op.CounterBuffer is null)
                    {
                        op.TransformFeedback.Begin(commandBuffer);
                    }
                    else if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? beginCounter))
                    {
                        op.TransformFeedback.Begin(commandBuffer, beginCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    }
                    break;

                case EXRTransformFeedbackOperation.End:
                    if (op.CounterBuffer is null)
                    {
                        op.TransformFeedback.End(commandBuffer);
                    }
                    else if (TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? endCounter))
                    {
                        op.TransformFeedback.End(commandBuffer, endCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    }
                    break;

                case EXRTransformFeedbackOperation.Pause:
                    if (!TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? pauseCounter))
                    {
                        Debug.VulkanWarning("Transform feedback pause skipped: Vulkan pause/resume requires a counter buffer.");
                        return;
                    }

                    op.TransformFeedback.End(commandBuffer, pauseCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    break;

                case EXRTransformFeedbackOperation.Resume:
                    if (!TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? resumeCounter))
                    {
                        Debug.VulkanWarning("Transform feedback resume skipped: Vulkan pause/resume requires a counter buffer.");
                        return;
                    }

                    op.TransformFeedback.Begin(commandBuffer, resumeCounter, op.TransformFeedback.Data.BindingLocation, op.CounterBufferOffset);
                    break;

                case EXRTransformFeedbackOperation.DrawIndirectByteCount:
                    if (!TryResolveTransformFeedbackBuffer(op.CounterBuffer, "counter", out VkDataBuffer? drawCounter))
                    {
                        Debug.VulkanWarning("Transform feedback byte-count draw skipped: missing counter buffer.");
                        return;
                    }

                    op.TransformFeedback.DrawIndirectByteCount(
                        commandBuffer,
                        op.InstanceCount,
                        op.FirstInstance,
                        drawCounter,
                        op.CounterBufferOffset,
                        op.CounterOffset,
                        op.VertexStride);
                    break;

                default:
                    Debug.VulkanWarning($"Unsupported Vulkan transform feedback operation '{op.Operation}'.");
                    break;
            }
        }

        private bool TryResolveTransformFeedbackBuffer(
            XRDataBuffer? dataBuffer,
            string role,
            [NotNullWhen(true)] out VkDataBuffer? buffer)
        {
            buffer = null;
			if (dataBuffer is null)
				return false;

			bool allowSynchronousBufferUpload = AllowSynchronousResourceUploads;
			if (GetOrCreateAPIRenderObject(dataBuffer, generateNow: allowSynchronousBufferUpload) is VkDataBuffer vkBuffer &&
				vkBuffer.TryEnsureReadyForRendering(allowSynchronousBufferUpload))
			{
				buffer = vkBuffer;
				return true;
			}

            Debug.VulkanWarning($"Failed to resolve Vulkan transform feedback {role} buffer.");
            return false;
        }

        private void RecordMeshTaskDispatchIndirectCountOp(CommandBuffer commandBuffer, MeshTaskDispatchIndirectCountOp op)
        {
            if (!SupportsVulkanMeshTaskIndirectCount || _extMeshShader is null)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: VK_EXT_mesh_shader indirect-count dispatch is unavailable.");
                return;
            }

            var indirectBuffer = op.IndirectBuffer.BufferHandle;
            if (indirectBuffer is null || !indirectBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: Invalid indirect buffer.");
                return;
            }

            var countBuffer = op.CountBuffer.BufferHandle;
            if (countBuffer is null || !countBuffer.HasValue)
            {
                Debug.VulkanWarning("RecordMeshTaskDispatchIndirectCountOp: Invalid count buffer.");
                return;
            }

            if (op.MaxDrawCount == 0u)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.MeshTaskIndirect.ZeroMaxDrawCount",
                    TimeSpan.FromSeconds(1),
                    "RecordMeshTaskDispatchIndirectCountOp skipped: maxDrawCount was zero.");
                return;
            }

            if (op.BindlessMaterialTextures is { } bindlessMaterialTextures &&
                !TryBindGlobalMaterialTextureDescriptorSet(
                    commandBuffer,
                    bindlessMaterialTextures.Program,
                    bindlessMaterialTextures.Consumer))
            {
                return;
            }

            bool plannerCoversIndirectBarrier =
                PlannerCoversIndirectBufferTransition(op.PassIndex, indirectBuffer.Value) &&
                PlannerCoversIndirectBufferTransition(op.PassIndex, countBuffer.Value);
            if (!plannerCoversIndirectBarrier)
            {
                MemoryBarrier memoryBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.IndirectCommandReadBit,
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                    PipelineStageFlags.DrawIndirectBit,
                    DependencyFlags.None,
                    1,
                    &memoryBarrier,
                    0,
                    null,
                    0,
                    null);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
            }
            else
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 0, redundantCount: 1);
            }

            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                indirectBuffer.Value.Handle,
                "MeshTaskIndirect.Commands");
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Buffer,
                countBuffer.Value.Handle,
                "MeshTaskIndirect.Count");
            _extMeshShader.CmdDrawMeshTasksIndirectCount(
                commandBuffer,
                indirectBuffer.Value,
                (ulong)op.ByteOffset,
                countBuffer.Value,
                (ulong)op.CountByteOffset,
                op.MaxDrawCount,
                op.Stride);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
                usedCountPath: true,
                usedLoopFallback: false,
                apiCalls: 1,
                submittedDraws: op.MaxDrawCount);
        }

        private void RecordComputeDispatchOp(CommandBuffer commandBuffer, uint imageIndex, ComputeDispatchOp op, int opIndex = -1)
        {
            if (!op.Program.Link())
                throw new InvalidOperationException($"Compute program '{op.Program.Data.Name ?? "UnnamedProgram"}' became unavailable after enqueue.");

            Pipeline pipeline;
            try
            {
                pipeline = op.Program.GetOrCreateComputePipeline(op.PassIndex, op.Context.PassMetadata);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"Failed to create Vulkan compute pipeline for '{op.Program.Data.Name ?? "UnnamedProgram"}': {ex.Message}");
                return;
            }

            if (pipeline.Handle == 0)
                throw new InvalidOperationException($"Compute pipeline '{op.Program.Data.Name ?? "UnnamedProgram"}' became unavailable after enqueue.");

            BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline);
            EnsureComputeStorageImageLayoutsForDispatch(commandBuffer, op.Snapshot);

            PushConstantsTracked(
                commandBuffer,
                op.Program.PipelineLayout,
                CommonPushConstantStageFlags,
                0,
                new ComputeDispatchPushConstants(op.GroupsX, op.GroupsY, op.GroupsZ, 0u));

            ulong reusableDescriptorKey = ComputeReusableComputeDescriptorBindingKey(op, opIndex);
            if (!op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, imageIndex, op.Snapshot, reusableDescriptorKey, out _, out var tempBuffers))
            {
                foreach ((Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) in tempBuffers)
                    DestroyBuffer(buffer, memory);

                // Descriptor binding failed (e.g. a required storage image lacks STORAGE_BIT).
                // Dispatching without valid descriptors causes GPU faults â†’ device lost.
                Debug.VulkanWarningEvery(
                    $"Vulkan.ComputeDispatch.NoDescriptors.{op.Program.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping compute dispatch for '{0}' â€” descriptor binding failed.",
                    op.Program.Data.Name ?? "UnnamedProgram");
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                    op.Program.Data.Name,
                    "descriptor-set",
                    "<compute-dispatch>",
                    0,
                    0,
                    skippedDraw: false,
                    skippedDispatch: true,
                    "compute dispatch skipped because descriptor binding failed");
                throw new InvalidOperationException($"Descriptor binding failed for compute program '{op.Program.Data.Name ?? "UnnamedProgram"}'.");
            }

            RegisterComputeTransientUniformBuffers(imageIndex, tempBuffers);
            Api!.CmdDispatch(commandBuffer, op.GroupsX, op.GroupsY, op.GroupsZ);
        }

        private void EnsureComputeStorageImageLayoutsForDispatch(CommandBuffer commandBuffer, ComputeDispatchSnapshot snapshot)
        {
            foreach (ProgramImageBinding binding in snapshot.Images.Values)
            {
                XRTexture texture = binding.Texture;
                if (texture is null)
                    continue;

                if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
                    continue;

                if (!source.UsesAllocatorImage)
                    continue;

                if ((source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
                    continue;

                uint mipLevels = Math.Max(source.DescriptorMipLevels, 1u);
                uint arrayLayers = Math.Max(source.DescriptorArrayLayers, 1u);
                uint baseMipLevel = binding.Level < 0 ? 0u : Math.Min((uint)binding.Level, mipLevels - 1u);
                uint baseArrayLayer = binding.Layered || binding.Layer < 0 ? 0u : Math.Min((uint)binding.Layer, arrayLayers - 1u);
                uint layerCount = binding.Layered || binding.Layer < 0 ? arrayLayers - baseArrayLayer : 1u;
                Image image = source.DescriptorImage;
                if (image.Handle == 0)
                    continue;

                ImageAspectFlags aspect = source.DescriptorAspect;
                if (aspect == 0)
                    aspect = ImageAspectFlags.ColorBit;

                ImageSubresourceRange range = new()
                {
                    AspectMask = aspect,
                    BaseMipLevel = baseMipLevel,
                    LevelCount = 1u,
                    BaseArrayLayer = baseArrayLayer,
                    LayerCount = Math.Max(layerCount, 1u)
                };

                ImageLayout oldLayout = TryGetRecordedImageAccessState(
                    commandBuffer,
                    image,
                    range,
                    out VulkanImageAccessState recordedState)
                        ? recordedState.Layout
                        : ImageLayout.Undefined;
                if (oldLayout == ImageLayout.General)
                    continue;

                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = ResolveComputeStorageImageSourceAccess(oldLayout),
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    OldLayout = oldLayout,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = range
                };

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    ResolveComputeStorageImageSourceStage(oldLayout),
                    PipelineStageFlags.ComputeShaderBit,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private static PipelineStageFlags ResolveComputeStorageImageSourceStage(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthStencilReadOnlyOptimal =>
                    PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                _ => PipelineStageFlags.AllCommandsBit
            };

        private static AccessFlags ResolveComputeStorageImageSourceAccess(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => AccessFlags.None,
                ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                ImageLayout.DepthStencilAttachmentOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags.DepthStencilAttachmentReadBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
                _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit
            };

        private void EmitPendingMemoryBarriers(CommandBuffer commandBuffer)
        {
            var pendingMask = ActiveState.PendingMemoryBarrierMask;
            if (pendingMask == EMemoryBarrierMask.None)
                return;

            EmitMemoryBarrierMask(commandBuffer, pendingMask);
            ActiveState.ClearPendingMemoryBarrierMask();
        }

        /// <summary>
        /// Emits a <c>vkCmdPipelineBarrier</c> for the given <see cref="EMemoryBarrierMask"/>.
        /// Used both for global pending barriers and per-pass barriers.
        /// </summary>
        private void EmitMemoryBarrierMask(CommandBuffer commandBuffer, EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            ResolveBarrierScopes(mask, out PipelineStageFlags srcStages, out PipelineStageFlags dstStages, out AccessFlags srcAccess, out AccessFlags dstAccess);

            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                srcStages,
                dstStages,
                DependencyFlags.None,
                1,
                &memoryBarrier,
                0,
                null,
                0,
                null);
        }

        private void ResolveBarrierScopes(
            EMemoryBarrierMask mask,
            out PipelineStageFlags srcStages,
            out PipelineStageFlags dstStages,
            out AccessFlags srcAccess,
            out AccessFlags dstAccess)
        {
            PipelineStageFlags srcStagesLocal = 0;
            PipelineStageFlags dstStagesLocal = 0;
            AccessFlags srcAccessLocal = 0;
            AccessFlags dstAccessLocal = 0;

            void Merge(bool condition, PipelineStageFlags srcStage, PipelineStageFlags dstStage, AccessFlags srcAcc, AccessFlags dstAcc)
            {
                if (!condition)
                    return;

                srcStagesLocal |= srcStage;
                dstStagesLocal |= dstStage;
                srcAccessLocal |= srcAcc;
                dstAccessLocal |= dstAcc;
            }

            Merge(mask.HasFlag(EMemoryBarrierMask.VertexAttribArray),
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit | AccessFlags.VertexAttributeReadBit,
                AccessFlags.VertexAttributeReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.ElementArray),
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit | AccessFlags.IndexReadBit,
                AccessFlags.IndexReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.Uniform),
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit,
                AccessFlags.UniformReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.TextureFetch) || mask.HasFlag(EMemoryBarrierMask.TextureUpdate),
                PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.TransferWriteBit | AccessFlags.ShaderReadBit,
                AccessFlags.ShaderReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.ShaderGlobalAccess) || mask.HasFlag(EMemoryBarrierMask.ShaderImageAccess) || mask.HasFlag(EMemoryBarrierMask.ShaderStorage),
                PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.Command),
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                PipelineStageFlags.DrawIndirectBit,
                AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
                AccessFlags.IndirectCommandReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.PixelBuffer) || mask.HasFlag(EMemoryBarrierMask.BufferUpdate),
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
                AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit | AccessFlags.VertexAttributeReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.Framebuffer),
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);

            if (mask.HasFlag(EMemoryBarrierMask.TransformFeedback))
            {
                if (SupportsTransformFeedback)
                {
                    Merge(
                        true,
                        PipelineStageFlags.TransformFeedbackBitExt,
                        PipelineStageFlags.TransformFeedbackBitExt |
                            PipelineStageFlags.VertexInputBit |
                            PipelineStageFlags.VertexShaderBit |
                            PipelineStageFlags.GeometryShaderBit |
                            PipelineStageFlags.ComputeShaderBit |
                            PipelineStageFlags.TransferBit |
                            PipelineStageFlags.DrawIndirectBit,
                        AccessFlags.TransformFeedbackWriteBitExt |
                            AccessFlags.TransformFeedbackCounterWriteBitExt,
                        AccessFlags.TransformFeedbackWriteBitExt |
                            AccessFlags.TransformFeedbackCounterReadBitExt |
                            AccessFlags.VertexAttributeReadBit |
                            AccessFlags.ShaderReadBit |
                            AccessFlags.TransferReadBit |
                            AccessFlags.IndirectCommandReadBit);
                }
                else
                {
                    Merge(
                        true,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.AllCommandsBit,
                        AccessFlags.MemoryWriteBit,
                        AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                }
            }

            Merge(mask.HasFlag(EMemoryBarrierMask.AtomicCounter),
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.ClientMappedBuffer),
                PipelineStageFlags.HostBit,
                PipelineStageFlags.TransferBit | PipelineStageFlags.VertexInputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.HostWriteBit,
                AccessFlags.TransferReadBit | AccessFlags.VertexAttributeReadBit | AccessFlags.UniformReadBit | AccessFlags.ShaderReadBit);

            Merge(mask.HasFlag(EMemoryBarrierMask.GpuReadback),
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.HostBit,
                AccessFlags.TransferWriteBit,
                AccessFlags.HostReadBit);

            // Query buffers: AllCommandsBit is justified per Vulkan spec because
            // queries can be written by any pipeline stage.
            Merge(mask.HasFlag(EMemoryBarrierMask.QueryBuffer),
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                AccessFlags.MemoryWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

            if (srcStagesLocal == 0)
                srcStagesLocal = PipelineStageFlags.AllCommandsBit;
            if (dstStagesLocal == 0)
                dstStagesLocal = PipelineStageFlags.AllCommandsBit;
            if (srcAccessLocal == 0)
                srcAccessLocal = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
            if (dstAccessLocal == 0)
                dstAccessLocal = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;

            srcStages = srcStagesLocal;
            dstStages = dstStagesLocal;
            srcAccess = srcAccessLocal;
            dstAccess = dstAccessLocal;
        }

        /// <summary>
        /// After ending a render pass for an FBO target, update the tracked layout
        /// on each physical image group backing the FBO's attachments. The render
        /// pass transitions each attachment from <c>initialLayout</c> through the
        /// subpass layout to <c>finalLayout</c> at <c>CmdEndRenderPass</c>.
        /// We must track the <b>finalLayout</b>, not the subpass layout.
        /// </summary>
        private void TransitionFboAttachmentsForDynamicRendering(
            CommandBuffer commandBuffer,
            XRFrameBuffer fbo,
            FrameBufferAttachmentSignature[] signatures,
            bool beginRendering)
        {
            if (signatures.Length == 0)
                return;

            VkFrameBuffer? vkFbo = GenericToAPI<VkFrameBuffer>(fbo);
            if (vkFbo is null || vkFbo.AttachmentCount == 0)
                return;

            int attachmentCapacity = Math.Min((int)vkFbo.AttachmentCount, signatures.Length);
            if (attachmentCapacity <= 0)
                return;

            int maxLayerSpan = Math.Max((int)vkFbo.FramebufferLayers, 1);
            ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[checked(attachmentCapacity * maxLayerSpan)];
            uint barrierCount = 0;
            PipelineStageFlags srcStages = 0;
            PipelineStageFlags dstStages = 0;

            for (int i = 0; i < attachmentCapacity; i++)
            {
                FrameBufferAttachmentSignature signature = signatures[i];
                if (signature.Role == AttachmentRole.Unused)
                    continue;
                ImageLayout requestedOldLayout = NormalizeFboAttachmentLayout(
                    signature,
                    beginRendering ? signature.InitialLayout : signature.ReferenceLayout);
                ImageLayout newLayout = NormalizeFboAttachmentLayout(
                    signature,
                    beginRendering ? signature.ReferenceLayout : signature.FinalLayout);
                if (newLayout == ImageLayout.Undefined)
                    continue;

                if (!vkFbo.TryGetAttachmentTarget(
                    i,
                    out IFrameBufferAttachement? target,
                    out _,
                    out int mipLevel,
                    out int layerIndex))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.DynamicRendering.FboTransition.NoTarget.{fbo.GetHashCode()}.{i}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping dynamic-rendering FBO transition for '{0}' attachment {1}: ordered attachment target metadata was unavailable.",
                        fbo.Name ?? "<unnamed>",
                        i);
                    continue;
                }

                ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(signature.Format, signature.AspectMask);
                if (!TryResolveAttachmentImage(target, mipLevel, layerIndex, aspectMask, out BlitImageInfo info) ||
                    info.Image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.DynamicRendering.FboTransition.Unresolved.{fbo.GetHashCode()}.{i}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Skipping dynamic-rendering FBO transition for '{0}' attachment {1}: image handle could not be resolved.",
                        fbo.Name ?? "<unnamed>",
                        i);
                    continue;
                }

                Image transitionImage = info.Image;
                uint transitionMipLevel = info.MipLevel;
                uint imageBaseLayer;
                uint transitionLayerCount;
                if (vkFbo.TryGetAttachmentView(i, out ImageView attachmentView) &&
                    TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) &&
                    viewInfo.Image.Handle != 0)
                {
                    transitionImage = viewInfo.Image;
                    transitionMipLevel = viewInfo.SubresourceRange.BaseMipLevel;
                    imageBaseLayer = viewInfo.SubresourceRange.BaseArrayLayer;
                    transitionLayerCount = Math.Max(viewInfo.SubresourceRange.LayerCount, 1u);

                    ImageAspectFlags viewAspect = NormalizeBarrierAspectMask(signature.Format, viewInfo.SubresourceRange.AspectMask);
                    if (viewAspect != ImageAspectFlags.None)
                        aspectMask = viewAspect;
                }
                else
                {
                    ResolveFboAttachmentImageLayerSpan(
                        vkFbo,
                        layerIndex,
                        in info,
                        out imageBaseLayer,
                        out transitionLayerCount);
                }

                ResolveFboAttachmentTrackedLayerSpan(
                    vkFbo,
                    layerIndex,
                    out uint trackedBaseLayer,
                    out uint trackedLayerCount);

                uint layerCount = Math.Max(transitionLayerCount, 1u);
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint imageLayer = imageBaseLayer + layerOffset;
                    int trackedLayer = checked((int)(trackedBaseLayer + Math.Min(layerOffset, Math.Max(trackedLayerCount, 1u) - 1u)));
                    ImageSubresourceRange transitionRange = new()
                    {
                        AspectMask = aspectMask,
                        BaseMipLevel = transitionMipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = imageLayer,
                        LayerCount = 1
                    };
                    ImageLayout oldLayout;
                    if (TryGetRecordedImageAccessState(
                        commandBuffer,
                        transitionImage,
                        transitionRange,
                        out VulkanImageAccessState recordedState))
                    {
                        oldLayout = NormalizeFboAttachmentLayout(signature, recordedState.Layout);
                    }
                    else
                    {
                        oldLayout = NormalizeFboAttachmentLayout(signature, requestedOldLayout);
                    }
                    bool sameLayout = oldLayout == newLayout;
                    // A render-pass attachment can remain in the same layout while
                    // its producer changes.  Dynamic rendering has no implicit
                    // external-subpass dependency, so retain this memory barrier on
                    // scope exit for a later sampled read of the attachment.

                    bool oldLayoutIsRenderAttachment = !beginRendering;
                    bool newLayoutIsRenderAttachment = beginRendering;
                    PipelineStageFlags srcStage = sameLayout
                        ? PipelineStageFlags.AllCommandsBit
                        : oldLayout == ImageLayout.Undefined
                        ? PipelineStageFlags.TopOfPipeBit
                        : ResolveFboAttachmentStage(oldLayout, signature, oldLayoutIsRenderAttachment);
                    PipelineStageFlags dstStage = ResolveFboAttachmentStage(newLayout, signature, newLayoutIsRenderAttachment);

                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = sameLayout
                            ? AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit
                            : oldLayout == ImageLayout.Undefined
                            ? 0
                            : ResolveFboAttachmentAccess(oldLayout, signature, oldLayoutIsRenderAttachment),
                        DstAccessMask = ResolveFboAttachmentAccess(newLayout, signature, newLayoutIsRenderAttachment),
                        OldLayout = oldLayout,
                        NewLayout = newLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = transitionImage,
                        SubresourceRange = transitionRange
                    };

                    bool traceDynamicFboTransition =
                        CommandRecordingDiagnosticsEnabled ||
                        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
                        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw ||
                        BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(fbo.Name);
                    if (traceDynamicFboTransition)
                    {
                        string targetName = target switch
                        {
                            XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                            XRRenderBuffer renderBuffer => renderBuffer.Name ?? renderBuffer.GetType().Name,
                            _ => target.GetType().Name
                        } ?? "<unnamed>";

                        Debug.VulkanEvery(
                            $"Vulkan.DynamicRendering.FboTransition.{fbo.Name}.{i}.{beginRendering}.{info.MipLevel}.{imageLayer}.{oldLayout}.{newLayout}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Dynamic FBO transition fbo='{0}' begin={1} attachment={2} target='{3}' viewMask=0x{4:X} imageLayer={5}/{6} trackedLayer={7}/{8} old={9} new={10} aspect={11} image=0x{12:X}",
                            fbo.Name ?? "<unnamed>",
                            beginRendering,
                            i,
                            targetName,
                            vkFbo.MultiviewViewMask,
                            imageLayer,
                            transitionLayerCount,
                            trackedLayer,
                            trackedLayerCount,
                            oldLayout,
                            newLayout,
                            aspectMask,
                            transitionImage.Handle);
                    }

                    barriers[barrierCount++] = barrier;
                    srcStages |= srcStage;
                    dstStages |= dstStage;
                }
            }

            if (barrierCount == 0)
                return;

            CmdPipelineBarrierTracked(
                commandBuffer,
                NormalizePipelineStages(srcStages),
                NormalizePipelineStages(dstStages),
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                barrierCount,
                barriers);
        }

        private static ImageLayout NormalizeFboAttachmentLayout(FrameBufferAttachmentSignature signature, ImageLayout layout)
        {
            if (layout == ImageLayout.Undefined || layout == ImageLayout.General ||
                layout == ImageLayout.TransferSrcOptimal || layout == ImageLayout.TransferDstOptimal ||
                layout == ImageLayout.PresentSrcKhr)
            {
                return layout;
            }

            bool isDepthStencil = signature.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil ||
                IsDepthOrStencilFormat(signature.Format) ||
                (signature.AspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;
            if (!isDepthStencil)
            {
                return layout switch
                {
                    ImageLayout.DepthStencilAttachmentOptimal => ImageLayout.ColorAttachmentOptimal,
                    ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.DepthReadOnlyOptimal or ImageLayout.StencilReadOnlyOptimal =>
                        ImageLayout.ShaderReadOnlyOptimal,
                    _ => layout
                };
            }

            return layout switch
            {
                ImageLayout.ColorAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal,
                ImageLayout.ShaderReadOnlyOptimal => ImageLayout.DepthStencilReadOnlyOptimal,
                _ => layout
            };
        }

        private static FrameBufferAttachmentSignature[] CreateLegacyRenderPassSignature(
            FrameBufferAttachmentSignature[] signatures)
        {
            FrameBufferAttachmentSignature[] result = (FrameBufferAttachmentSignature[])signatures.Clone();
            for (int i = 0; i < result.Length; i++)
            {
                FrameBufferAttachmentSignature signature = result[i];
                if (signature.Role == AttachmentRole.Unused || signature.ReferenceLayout == ImageLayout.Undefined)
                    continue;

                result[i] = new FrameBufferAttachmentSignature(
                    signature.Format,
                    signature.Samples,
                    signature.AspectMask,
                    signature.Role,
                    signature.ColorIndex,
                    signature.LoadOp,
                    signature.StoreOp,
                    signature.StencilLoadOp,
                    signature.StencilStoreOp,
                    signature.ReferenceLayout,
                    signature.FinalLayout,
                    signature.ReferenceLayout);
            }

            return result;
        }

        private static PipelineStageFlags ResolveFboAttachmentStage(
            ImageLayout layout,
            FrameBufferAttachmentSignature signature,
            bool asRenderAttachment)
        {
            if (layout == ImageLayout.ShaderReadOnlyOptimal)
                return PipelineStageFlags.FragmentShaderBit;

            if (layout is ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal)
                return PipelineStageFlags.TransferBit;

            if (layout == ImageLayout.ColorAttachmentOptimal ||
                (asRenderAttachment && IsColorLikeAttachmentRole(signature.Role)))
                return PipelineStageFlags.ColorAttachmentOutputBit;

            if (layout is ImageLayout.DepthStencilAttachmentOptimal ||
                (asRenderAttachment && signature.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil))
            {
                return PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            }

            if (layout is ImageLayout.DepthStencilReadOnlyOptimal
                    or ImageLayout.DepthReadOnlyOptimal
                    or ImageLayout.StencilReadOnlyOptimal)
            {
                PipelineStageFlags stages = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                if (!asRenderAttachment)
                    stages |= PipelineStageFlags.FragmentShaderBit;
                return stages;
            }

            return PipelineStageFlags.AllGraphicsBit;
        }

        private static AccessFlags ResolveFboAttachmentAccess(
            ImageLayout layout,
            FrameBufferAttachmentSignature signature,
            bool asRenderAttachment)
        {
            if (layout == ImageLayout.ShaderReadOnlyOptimal)
                return AccessFlags.ShaderReadBit;

            if (layout == ImageLayout.TransferSrcOptimal)
                return AccessFlags.TransferReadBit;

            if (layout == ImageLayout.TransferDstOptimal)
                return AccessFlags.TransferWriteBit;

            if (layout == ImageLayout.ColorAttachmentOptimal ||
                (asRenderAttachment && IsColorLikeAttachmentRole(signature.Role)))
                return AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

            if (layout is ImageLayout.DepthStencilReadOnlyOptimal
                    or ImageLayout.DepthReadOnlyOptimal
                    or ImageLayout.StencilReadOnlyOptimal)
            {
                AccessFlags access = AccessFlags.DepthStencilAttachmentReadBit;
                if (asRenderAttachment)
                    access |= AccessFlags.DepthStencilAttachmentWriteBit;
                else
                    access |= AccessFlags.ShaderReadBit;
                return access;
            }

            if (layout == ImageLayout.DepthStencilAttachmentOptimal ||
                (asRenderAttachment && signature.Role is AttachmentRole.Depth or AttachmentRole.Stencil or AttachmentRole.DepthStencil))
            {
                return AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
            }

            return AccessFlags.MemoryReadBit;
        }

        private static void ResolveFboAttachmentImageLayerSpan(
            VkFrameBuffer vkFbo,
            int layerIndex,
            in BlitImageInfo info,
            out uint baseLayer,
            out uint layerCount)
        {
            baseLayer = info.BaseArrayLayer;
            layerCount = Math.Max(info.LayerCount, 1u);

            if (TryResolveViewMaskLayerSpan(vkFbo.MultiviewViewMask, out uint viewBaseLayer, out uint viewLayerCount))
            {
                baseLayer += viewBaseLayer;
                layerCount = Math.Max(viewLayerCount, 1u);
                return;
            }

            if (layerIndex < 0)
            {
                baseLayer = info.BaseArrayLayer;
                layerCount = Math.Max(vkFbo.FramebufferLayers, layerCount);
            }
        }

        private static void ResolveFboAttachmentTrackedLayerSpan(
            VkFrameBuffer vkFbo,
            int layerIndex,
            out uint baseLayer,
            out uint layerCount)
        {
            if (TryResolveViewMaskLayerSpan(vkFbo.MultiviewViewMask, out baseLayer, out layerCount))
                return;

            if (layerIndex < 0)
            {
                baseLayer = 0u;
                layerCount = Math.Max(vkFbo.FramebufferLayers, 1u);
                return;
            }

            baseLayer = (uint)Math.Max(layerIndex, 0);
            layerCount = 1u;
        }

        private static bool TryResolveViewMaskLayerSpan(uint viewMask, out uint baseLayer, out uint layerCount)
        {
            baseLayer = 0u;
            layerCount = 0u;
            if (viewMask == 0u)
                return false;

            uint first = 32u;
            uint last = 0u;
            for (uint bit = 0u; bit < 32u; bit++)
            {
                if ((viewMask & (1u << (int)bit)) == 0u)
                    continue;

                first = Math.Min(first, bit);
                last = Math.Max(last, bit);
            }

            if (first >= 32u)
                return false;

            baseLayer = first;
            layerCount = last - first + 1u;
            return true;
        }

        /// <summary>
        /// Queries the current tracked layout of each attachment backing the given FBO.
        /// Returns an array suitable for <see cref="VkFrameBuffer.ResolveRenderPassForPass"/>
        /// that reflects any barrier-planner or blit transitions since the last render pass.
        /// </summary>
        private ImageLayout[]? QueryCurrentAttachmentLayouts(XRFrameBuffer fbo, VkFrameBuffer vkFbo)
        {
            if (vkFbo.AttachmentCount == 0)
                return null;

            int count = (int)vkFbo.AttachmentCount;
            ImageLayout[] layouts = GetFboAttachmentLayoutScratch(fbo, count);

            for (int i = 0; i < count; i++)
            {
                if (!vkFbo.TryGetAttachmentTarget(
                    i,
                    out IFrameBufferAttachement? target,
                    out EFrameBufferAttachment attachment,
                    out int mipLevel,
                    out int layerIndex))
                {
                    layouts[i] = ImageLayout.Undefined;
                    continue;
                }

                layouts[i] = TryGetExactTrackedFboAttachmentLayout(
                    vkFbo,
                    i,
                    target,
                    attachment,
                    mipLevel,
                    layerIndex,
                    out ImageLayout layout)
                    ? layout
                    : ImageLayout.Undefined;
            }

            return layouts;
        }

        private ImageLayout[] GetFboAttachmentLayoutScratch(XRFrameBuffer fbo, int attachmentCount)
        {
            CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
            if (!recordingScratch.FboAttachmentLayouts.TryGetValue(
                    fbo,
                    out CommandBufferRecordingScratch.FboAttachmentLayoutScratch? scratch))
            {
                scratch = new CommandBufferRecordingScratch.FboAttachmentLayoutScratch();
                recordingScratch.FboAttachmentLayouts.Add(fbo, scratch);
            }

            if (scratch.Layouts.Length != attachmentCount)
                scratch.Layouts = new ImageLayout[attachmentCount];

            recordingScratch.FboLayoutTracking[fbo] = scratch.Layouts;
            return scratch.Layouts;
        }
        private bool TryGetExactTrackedFboAttachmentLayout(
            VkFrameBuffer vkFbo,
            int attachmentIndex,
            IFrameBufferAttachement target,
            EFrameBufferAttachment attachment,
            int mipLevel,
            int layerIndex,
            out ImageLayout layout)
        {
            layout = ImageLayout.Undefined;

            ImageAspectFlags requestedAspect = ResolveFrameBufferAttachmentAspectMask(attachment);
            if (requestedAspect == ImageAspectFlags.None ||
                !TryResolveAttachmentImage(target, mipLevel, layerIndex, requestedAspect, out BlitImageInfo info) ||
                info.Image.Handle == 0)
            {
                return false;
            }

            Image image = info.Image;
            ImageSubresourceRange range = new()
            {
                AspectMask = NormalizeBarrierAspectMask(info.Format, requestedAspect),
                BaseMipLevel = info.MipLevel,
                LevelCount = 1,
                BaseArrayLayer = info.BaseArrayLayer,
                LayerCount = Math.Max(info.LayerCount, 1u)
            };

            if (vkFbo.TryGetAttachmentView(attachmentIndex, out ImageView attachmentView) &&
                TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) &&
                viewInfo.Image.Handle != 0)
            {
                image = viewInfo.Image;
                range = viewInfo.SubresourceRange;
                range.AspectMask = NormalizeBarrierAspectMask(info.Format, range.AspectMask);
                range.LevelCount = Math.Max(range.LevelCount, 1u);
                range.LayerCount = Math.Max(range.LayerCount, 1u);
            }

            return TryGetTrackedImageLayout(image, range, out layout);
        }

        private static ImageAspectFlags ResolveFrameBufferAttachmentAspectMask(EFrameBufferAttachment attachment)
        {
            if (IsColorAttachment(attachment))
                return ImageAspectFlags.ColorBit;

            return attachment switch
            {
                EFrameBufferAttachment.DepthStencilAttachment => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                EFrameBufferAttachment.DepthAttachment => ImageAspectFlags.DepthBit,
                EFrameBufferAttachment.StencilAttachment => ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.None
            };
        }

        /// <summary>
        /// When the barrier planner has no known passes, emit image memory barriers to
        /// transition any physical-group images still in <see cref="ImageLayout.Undefined"/>
        /// to a usable layout inside the current command buffer. Keeping this transition
        /// in-frame avoids out-of-band one-shot submissions while resource-planner states
        /// switch between desktop and OpenXR targets.
        /// </summary>
        private void EmitInitialImageBarriersForUnknownPass(
            CommandBuffer commandBuffer,
            bool skipDesktopSwapchainImages = false)
        {
            foreach (VulkanPhysicalImageGroup group in ResourceAllocator.EnumeratePhysicalGroups())
            {
                if (!group.IsAllocated || group.Image.Handle == 0)
                    continue;
                if (skipDesktopSwapchainImages && IsDesktopSwapchainImage(group.Image))
                    continue;

                bool isDepth = VulkanResourceAllocator.IsDepthStencilFormat(group.Format);
                ImageLayout targetLayout = ResolveInitialPhysicalGroupLayout(group.Usage, isDepth);

                PipelineStageFlags initDstStage = targetLayout switch
                {
                    ImageLayout.DepthStencilAttachmentOptimal =>
                        PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                    ImageLayout.ColorAttachmentOptimal =>
                        PipelineStageFlags.ColorAttachmentOutputBit,
                    ImageLayout.General =>
                        PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                    ImageLayout.ShaderReadOnlyOptimal =>
                        PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                    ImageLayout.TransferDstOptimal or ImageLayout.TransferSrcOptimal =>
                        PipelineStageFlags.TransferBit,
                    _ => PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                };
                VulkanImageAccessState targetState = ResolveVulkanImageAccessState(
                    targetLayout,
                    isDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit);
                AccessFlags initDstAccess = (AccessFlags)(ulong)targetState.AccessMask;

                if (isDepth)
                {
                    EmitInitialImageAspectBarriers(
                        commandBuffer,
                        group,
                        HasStencilComponent(group.Format)
                            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                            : ImageAspectFlags.DepthBit,
                        targetLayout,
                        initDstStage,
                        initDstAccess);
                }
                else
                {
                    EmitInitialImageAspectBarriers(
                        commandBuffer,
                        group,
                        ImageAspectFlags.ColorBit,
                        targetLayout,
                        initDstStage,
                        initDstAccess);
                }
            }
        }

        private void RecordFboAttachmentAccessState(
            CommandBuffer commandBuffer,
            VkFrameBuffer vkFbo,
            FrameBufferAttachmentSignature[] signatures,
            bool useReferenceLayouts)
        {
            int attachmentCount = Math.Min((int)vkFbo.AttachmentCount, signatures.Length);
            for (int attachmentIndex = 0; attachmentIndex < attachmentCount; attachmentIndex++)
            {
                FrameBufferAttachmentSignature signature = signatures[attachmentIndex];
                if (signature.Role == AttachmentRole.Unused)
                    continue;
                ImageLayout layout = useReferenceLayouts
                    ? signature.ReferenceLayout
                    : signature.FinalLayout;
                if (layout == ImageLayout.Undefined ||
                    !vkFbo.TryGetAttachmentView(attachmentIndex, out ImageView attachmentView) ||
                    !TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo viewInfo) ||
                    viewInfo.Image.Handle == 0)
                {
                    continue;
                }

                ImageSubresourceRange range = viewInfo.SubresourceRange;
                range.AspectMask = NormalizeBarrierAspectMask(signature.Format, range.AspectMask);
                range.LevelCount = Math.Max(range.LevelCount, 1u);
                range.LayerCount = Math.Max(range.LayerCount, 1u);
                ImageLayout accessLayout = signature.ReferenceLayout != ImageLayout.Undefined
                    ? signature.ReferenceLayout
                    : layout;
                PipelineStageFlags stageMask = ResolveFboAttachmentStage(
                    accessLayout,
                    signature,
                    asRenderAttachment: true);
                AccessFlags accessMask = ResolveFboAttachmentAccess(
                    accessLayout,
                    signature,
                    asRenderAttachment: true);
                RecordImageAccess(
                    commandBuffer,
                    viewInfo.Image,
                    range,
                    layout,
                    stageMask,
                    accessMask,
                    Vk.QueueFamilyIgnored);
            }
        }

        private void EmitInitialImageAspectBarriers(
            CommandBuffer commandBuffer,
            VulkanPhysicalImageGroup group,
            ImageAspectFlags aspect,
            ImageLayout targetLayout,
            PipelineStageFlags dstStage,
            AccessFlags dstAccess)
        {
            uint mipLevels = Math.Max(1u, group.MipLevels);
            uint layers = Math.Max(1u, group.Template.Layers);
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                uint layer = 0;
                while (layer < layers)
                {
                    ImageSubresourceRange single = new()
                    {
                        AspectMask = aspect,
                        BaseMipLevel = mip,
                        LevelCount = 1,
                        BaseArrayLayer = layer,
                        LayerCount = 1,
                    };
                    if (TryGetRecordedImageAccessState(commandBuffer, group.Image, single, out _))
                    {
                        layer++;
                        continue;
                    }

                    uint firstUnknownLayer = layer++;
                    while (layer < layers)
                    {
                        single.BaseArrayLayer = layer;
                        if (TryGetRecordedImageAccessState(commandBuffer, group.Image, single, out _))
                            break;
                        layer++;
                    }

                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        OldLayout = ImageLayout.Undefined,
                        NewLayout = targetLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = group.Image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = aspect,
                            BaseMipLevel = mip,
                            LevelCount = 1,
                            BaseArrayLayer = firstUnknownLayer,
                            LayerCount = layer - firstUnknownLayer,
                        },
                        SrcAccessMask = AccessFlags.None,
                        DstAccessMask = dstAccess,
                    };

                    CmdPipelineBarrierTracked(
                        commandBuffer,
                        PipelineStageFlags.TopOfPipeBit,
                        dstStage,
                        DependencyFlags.None,
                        0, null, 0, null,
                        1, &barrier);
                }
            }
        }

        private void EmitPlannedImageBarriers(
            CommandBuffer commandBuffer,
            IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier>? plannedBarriers,
            bool skipDesktopSwapchainImages = false)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            foreach (var planned in plannedBarriers)
            {
                planned.Group.EnsureAllocated(this);
                if (skipDesktopSwapchainImages && IsDesktopSwapchainImage(planned.Group.Image))
                    continue;

                // The barrier planner pre-computes OldLayout from the logical dependency
                // graph, but dynamic rendering, blits, and resource-plan replacement can
                // change the live VkImage layout before the planned edge is emitted. Vulkan
                // validation cares about the live subresource layout, so use the physical
                // group's tracker whenever it has a concrete value.
                ImageLayout effectiveOldLayout = planned.Previous.Layout;
                ImageSubresourceRange range = new()
                {
                    AspectMask = NormalizeBarrierAspectMask(planned.Group.Format, planned.Next.AspectMask),
                    BaseMipLevel = planned.Range.BaseMipLevel,
                    LevelCount = Math.Max(1u, planned.Range.LevelCount),
                    BaseArrayLayer = planned.Range.BaseArrayLayer,
                    LayerCount = Math.Max(1u, planned.Range.LayerCount)
                };
                if (TryGetRecordedImageLayout(
                        commandBuffer,
                        planned.Group.Image,
                        range,
                        out ImageLayout recordedLayout) &&
                    recordedLayout != effectiveOldLayout)
                {
                    if (CommandRecordingDiagnosticsEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Barrier.OldLayout.Reconciled.{planned.ResourceName}.{planned.PassIndex}",
                            TimeSpan.FromSeconds(2),
                            "[Vulkan] Reconciled planned oldLayout for '{0}' pass={1}: planned={2} tracked={3} next={4}.",
                            planned.ResourceName,
                            planned.PassIndex,
                            effectiveOldLayout,
                            recordedLayout,
                            planned.Next.Layout);
                    }
                    effectiveOldLayout = recordedLayout;
                }

                if (BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(planned.ResourceName))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.BloomDiag.PlannedBarrier.{planned.ResourceName}.{planned.PassIndex}.{range.BaseMipLevel}.{range.LevelCount}.{range.BaseArrayLayer}.{range.LayerCount}.{effectiveOldLayout}.{planned.Next.Layout}",
                        TimeSpan.FromSeconds(1),
                        "[BloomDiag][Vulkan] planned pass={0} resource='{1}' mip={2}+{3} layer={4}+{5} old={6} new={7} prevStage={8} nextStage={9} prevAccess={10} nextAccess={11} aspect={12} image=0x{13:X}",
                        planned.PassIndex,
                        planned.ResourceName,
                        range.BaseMipLevel,
                        range.LevelCount,
                        range.BaseArrayLayer,
                        range.LayerCount,
                        effectiveOldLayout,
                        planned.Next.Layout,
                        planned.Previous.StageMask,
                        planned.Next.StageMask,
                        planned.Previous.AccessMask,
                        planned.Next.AccessMask,
                        range.AspectMask,
                        planned.Group.Image.Handle);
                }

                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    OldLayout = effectiveOldLayout,
                    NewLayout = planned.Next.Layout,
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Image = planned.Group.Image,
                    SubresourceRange = range
                };

                PipelineStageFlags srcStages = NormalizePipelineStages(planned.Previous.StageMask);
                PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    srcStages,
                    dstStages,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);

            }
        }

        private bool IsDesktopSwapchainImage(Image image)
        {
            if (image.Handle == 0 || swapChainImages is null)
                return false;

            for (int i = 0; i < swapChainImages.Length; i++)
                if (swapChainImages[i].Handle == image.Handle)
                    return true;

            return false;
        }

        private void EmitPlannedBufferBarriers(CommandBuffer commandBuffer, IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            foreach (VulkanBarrierPlanner.PlannedBufferBarrier planned in plannedBarriers)
            {
                if (!TryResolveTrackedBuffer(planned.ResourceName, out Silk.NET.Vulkan.Buffer buffer, out ulong size) || buffer.Handle == 0)
                    continue;

                BufferMemoryBarrier barrier = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = FilterAccessFlagsForStages(planned.Previous.AccessMask, planned.Previous.StageMask),
                    DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, planned.Next.StageMask),
                    SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                    DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                    Buffer = buffer,
                    Offset = 0,
                    Size = size > 0 ? size : Vk.WholeSize
                };

                PipelineStageFlags srcStages = NormalizePipelineStages(planned.Previous.StageMask);
                PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    srcStages,
                    dstStages,
                    DependencyFlags.None,
                    0,
                    null,
                    1,
                    &barrier,
                    0,
                    null);
            }
        }

        private void TransitionFrameOpDescriptorSnapshotsForSampling(
            CommandBuffer commandBuffer,
            FrameOp[] ops,
            int startIndex,
            int passIndex,
            int schedulingIdentity)
        {
            for (int i = startIndex; i < ops.Length; i++)
            {
                FrameOp candidate = ops[i];
                int candidatePassIndex = candidate.PassIndex == int.MinValue
                    ? passIndex
                    : EnsureValidPassIndex(candidate.PassIndex, candidate.GetType().Name, candidate.Context.PassMetadata);
                if (candidatePassIndex != passIndex || candidate.Context.SchedulingIdentity != schedulingIdentity)
                    break;

                ComputeDispatchSnapshot? snapshot = candidate switch
                {
                    MeshDrawOp meshDraw => meshDraw.Draw.ProgramBindingSnapshot,
                    ComputeDispatchOp compute => compute.Snapshot,
                    ComputeDispatchIndirectOp computeIndirect => computeIndirect.Snapshot,
                    _ => null,
                };
                if (snapshot is null)
                    continue;

                foreach (XRTexture texture in snapshot.Samplers.Values)
                    TransitionDescriptorTextureForSampling(commandBuffer, texture, candidate.Target);
                foreach (XRTexture texture in snapshot.SamplersByName.Values)
                    TransitionDescriptorTextureForSampling(commandBuffer, texture, candidate.Target);
            }
        }

        private void TransitionDescriptorTextureForSampling(
            CommandBuffer commandBuffer,
            XRTexture texture,
            XRFrameBuffer? target)
        {
            if (GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source ||
                source.DescriptorView.Handle == 0 ||
                !TryGetDescriptorHeapImageViewCreateInfo(source.DescriptorView, out ImageViewCreateInfo viewInfo) ||
                viewInfo.Image.Handle == 0)
            {
                return;
            }

            ImageSubresourceRange range = viewInfo.SubresourceRange;
            range.AspectMask = NormalizeBarrierAspectMask(source.DescriptorFormat, range.AspectMask);
            range.LevelCount = Math.Max(range.LevelCount, 1u);
            range.LayerCount = Math.Max(range.LayerCount, 1u);
            if (IsImageRangeAttachedToFrameBuffer(target, viewInfo.Image, range))
                return;
            if (!TryGetRecordedImageAccessState(
                    commandBuffer,
                    viewInfo.Image,
                    range,
                    out VulkanImageAccessState priorState))
            {
                return;
            }

            ImageLayout targetLayout = ResolveDescriptorImageLayout(source, DescriptorType.CombinedImageSampler);
            if (priorState.Layout == targetLayout)
                return;

            VulkanImageAccessState nextState = ResolveVulkanImageAccessState(targetLayout, range.AspectMask);
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = (AccessFlags)(ulong)priorState.AccessMask,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = priorState.Layout,
                NewLayout = targetLayout,
                SrcQueueFamilyIndex = priorState.QueueFamilyIndex,
                DstQueueFamilyIndex = priorState.QueueFamilyIndex,
                Image = viewInfo.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                (PipelineStageFlags)(ulong)priorState.StageMask,
                (PipelineStageFlags)(ulong)nextState.StageMask,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                &barrier,
                nameof(TransitionFrameOpDescriptorSnapshotsForSampling));
        }

        private bool IsImageRangeAttachedToFrameBuffer(
            XRFrameBuffer? target,
            Image image,
            ImageSubresourceRange range)
        {
            if (target is null || GenericToAPI<VkFrameBuffer>(target) is not { } vkFbo)
                return false;

            for (int i = 0; i < vkFbo.AttachmentCount; i++)
            {
                if (!vkFbo.TryGetAttachmentView(i, out ImageView attachmentView) ||
                    !TryGetDescriptorHeapImageViewCreateInfo(attachmentView, out ImageViewCreateInfo attachmentInfo) ||
                    attachmentInfo.Image.Handle != image.Handle)
                {
                    continue;
                }

                ImageSubresourceRange attachmentRange = attachmentInfo.SubresourceRange;
                bool aspectOverlap = (attachmentRange.AspectMask & range.AspectMask) != 0;
                bool mipOverlap = attachmentRange.BaseMipLevel < range.BaseMipLevel + Math.Max(range.LevelCount, 1u) &&
                    range.BaseMipLevel < attachmentRange.BaseMipLevel + Math.Max(attachmentRange.LevelCount, 1u);
                bool layerOverlap = attachmentRange.BaseArrayLayer < range.BaseArrayLayer + Math.Max(range.LayerCount, 1u) &&
                    range.BaseArrayLayer < attachmentRange.BaseArrayLayer + Math.Max(attachmentRange.LayerCount, 1u);
                if (aspectOverlap && mipOverlap && layerOverlap)
                    return true;
            }

            return false;
        }

        private static bool PrepareQueryFrameOpsForCommandBufferReuse(
            CommandBuffer commandBuffer,
            FrameOp[] ops)
        {
            for (int index = 0; index < ops.Length; index++)
            {
                if (ops[index] is QueryOp queryOp &&
                    queryOp.Operation is (
                        ERenderQueryOperation.Reset or
                        ERenderQueryOperation.Begin or
                        ERenderQueryOperation.WriteTimestamp or
                        ERenderQueryOperation.WriteProperties) &&
                    !queryOp.Query.PrepareForCommandBufferReuse(commandBuffer))
                {
                    return false;
                }
            }

            return true;
        }

        /// Appends dynamic rendering local-read pNext structs when a pass explicitly
        /// opts into framebuffer-local attachment reads.
        private bool TryAppendDynamicRenderingLocalReadPNext(
            in DynamicRenderingLocalReadPlan localRead,
            uint colorAttachmentCount,
            ref void* pNext,
            RenderingAttachmentLocationInfo* attachmentLocationInfo,
            RenderingInputAttachmentIndexInfo* inputAttachmentIndexInfo,
            uint* colorAttachmentLocations,
            uint* colorInputAttachmentIndices,
            uint* depthInputAttachmentIndex,
            uint* stencilInputAttachmentIndex)
        {
            if (!SupportsDynamicRenderingLocalRead || !localRead.Enabled)
                return false;

            bool hasAttachmentLocations = localRead.ColorAttachmentLocations.Length > 0;
            bool hasColorInputIndices = localRead.ColorInputAttachmentIndices.Length > 0;
            bool hasInputIndices =
                hasColorInputIndices ||
                localRead.DepthInputAttachmentIndex.HasValue ||
                localRead.StencilInputAttachmentIndex.HasValue;

            if (!hasAttachmentLocations && !hasInputIndices)
                return false;

            if ((hasAttachmentLocations && (uint)localRead.ColorAttachmentLocations.Length != colorAttachmentCount) ||
                (hasColorInputIndices && (uint)localRead.ColorInputAttachmentIndices.Length != colorAttachmentCount))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.DynamicRendering.LocalRead.InvalidPlan",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan] Dynamic rendering local-read plan ignored because color counts do not match the active rendering scope (attachments={0}, locations={1}, inputIndices={2}).",
                    colorAttachmentCount,
                    localRead.ColorAttachmentLocations.Length,
                    localRead.ColorInputAttachmentIndices.Length);
                return false;
            }

            if ((hasAttachmentLocations && (attachmentLocationInfo is null || colorAttachmentLocations is null)) ||
                (hasInputIndices && (inputAttachmentIndexInfo is null || (hasColorInputIndices && colorInputAttachmentIndices is null))) ||
                (localRead.DepthInputAttachmentIndex.HasValue && depthInputAttachmentIndex is null) ||
                (localRead.StencilInputAttachmentIndex.HasValue && stencilInputAttachmentIndex is null))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.DynamicRendering.LocalRead.MissingScratch",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan] Dynamic rendering local-read plan ignored because scratch storage was not provided for the pNext chain.");
                return false;
            }

            void* next = pNext;

            if (hasAttachmentLocations)
            {
                for (int i = 0; i < localRead.ColorAttachmentLocations.Length; i++)
                    colorAttachmentLocations[i] = localRead.ColorAttachmentLocations[i];

                *attachmentLocationInfo = new RenderingAttachmentLocationInfo
                {
                    SType = StructureType.RenderingAttachmentLocationInfo,
                    PNext = next,
                    ColorAttachmentCount = colorAttachmentCount,
                    PColorAttachmentLocations = colorAttachmentLocations,
                };
                next = attachmentLocationInfo;
            }

            if (hasInputIndices)
            {
                uint* colorInputPtr = null;
                uint colorInputCount = 0;
                if (hasColorInputIndices)
                {
                    for (int i = 0; i < localRead.ColorInputAttachmentIndices.Length; i++)
                        colorInputAttachmentIndices[i] = localRead.ColorInputAttachmentIndices[i];

                    colorInputPtr = colorInputAttachmentIndices;
                    colorInputCount = colorAttachmentCount;
                }

                uint* depthInputPtr = null;
                if (localRead.DepthInputAttachmentIndex.HasValue)
                {
                    *depthInputAttachmentIndex = localRead.DepthInputAttachmentIndex.Value;
                    depthInputPtr = depthInputAttachmentIndex;
                }

                uint* stencilInputPtr = null;
                if (localRead.StencilInputAttachmentIndex.HasValue)
                {
                    *stencilInputAttachmentIndex = localRead.StencilInputAttachmentIndex.Value;
                    stencilInputPtr = stencilInputAttachmentIndex;
                }

                *inputAttachmentIndexInfo = new RenderingInputAttachmentIndexInfo
                {
                    SType = StructureType.RenderingInputAttachmentIndexInfo,
                    PNext = next,
                    ColorAttachmentCount = colorInputCount,
                    PColorAttachmentInputIndices = colorInputPtr,
                    PDepthInputAttachmentIndex = depthInputPtr,
                    PStencilInputAttachmentIndex = stencilInputPtr,
                };
                next = inputAttachmentIndexInfo;
            }

            pNext = next;
            return true;
        }

        /// Pipeline stages must not be zero; fall back to AllCommandsBit as safety net.
        /// The planner should produce non-zero masks; this guards against edge cases.
        private static PipelineStageFlags NormalizePipelineStages(PipelineStageFlags stageMask)
            => stageMask == 0 ? PipelineStageFlags.AllCommandsBit : stageMask;

        private static AccessFlags FilterAccessFlagsForStages(AccessFlags accessMask, PipelineStageFlags stageMask)
        {
            if (accessMask == 0)
                return 0;

            if ((stageMask & (PipelineStageFlags.AllCommandsBit | PipelineStageFlags.AllGraphicsBit)) != 0)
                return accessMask;

            AccessFlags allowed = 0;

            if ((stageMask & PipelineStageFlags.TransferBit) != 0)
                allowed |= AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit;

            if ((stageMask & PipelineStageFlags.DrawIndirectBit) != 0)
                allowed |= AccessFlags.IndirectCommandReadBit;

            if ((stageMask & PipelineStageFlags.VertexInputBit) != 0)
                allowed |= AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit;

            if ((stageMask & (PipelineStageFlags.VertexShaderBit |
                              PipelineStageFlags.TessellationControlShaderBit |
                              PipelineStageFlags.TessellationEvaluationShaderBit |
                              PipelineStageFlags.GeometryShaderBit |
                              PipelineStageFlags.FragmentShaderBit |
                              PipelineStageFlags.ComputeShaderBit |
                              PipelineStageFlags.TaskShaderBitNV |
                              PipelineStageFlags.MeshShaderBitNV)) != 0)
            {
                allowed |= AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.UniformReadBit;
            }

            if ((stageMask & PipelineStageFlags.ColorAttachmentOutputBit) != 0)
                allowed |= AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

            if ((stageMask & (PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit)) != 0)
                allowed |= AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;

            if ((stageMask & PipelineStageFlags.HostBit) != 0)
                allowed |= AccessFlags.HostReadBit | AccessFlags.HostWriteBit;

            if (allowed == 0)
                return accessMask;

            return accessMask & allowed;
        }

    }
}
