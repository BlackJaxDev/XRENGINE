using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

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
            out CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            out int dynamicUiBatchTextOverlayOpCount,
            out FrameOp[] dynamicUiBatchTextOverlayOps,
            out ulong dynamicUiBatchTextOverlaySignature,
            out CommandBufferCacheVariant? dynamicUiBatchTextOverlayVariant,
            out ImageLayout swapchainLayoutAfterCommandBuffer)
        {
            _lastEnsureCommandBufferRecordedPrimary = false;
            dynamicUiBatchTextSecondaryCommandBuffer = default;
            dynamicUiBatchTextOverlayOpCount = 0;
            dynamicUiBatchTextOverlayOps = Array.Empty<FrameOp>();
            dynamicUiBatchTextOverlaySignature = 0;
            dynamicUiBatchTextOverlayVariant = null;
            swapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;

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
            bool frameOpSignatureDirty = false;
            bool plannerDirty = false;
            bool profilerDirty = false;
            bool frameDataDirty = false;
            bool dynamicUiDirty = false;
            bool swapchainLifecycleDirty = false;
            bool commandChainPrimaryDirty = false;
            bool primaryFrameStateDirty = false;
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
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.DrainFrameOps"))
            {
                ops = DrainFrameOps(
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
                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.NormalizeFrameOps"))
                {
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

            bool hasStaticFrameOps = ops.Length > 0;
            bool hasQueryFrameOps = HasQueryFrameOps(ops) || HasQueryFrameOps(dynamicUiBatchTextOps);
            bool delayDynamicUiBatchTextOverlayRecording =
                preserveSwapchainForOverlay &&
                dynamicUiBatchTextOps.Length > 0;
            bool requiresTrackedPresentSourceRefresh =
                !hasStaticFrameOps &&
                HasLastWindowPresentSourceForSwapchainRefresh();

            ulong plannerRevision;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.ResourcePlan"))
            {
                if (hasStaticFrameOps)
                {
                    FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(ops);
                    RefreshFrameOpResourceWrappers(
                        ops,
                        plannerContext,
                        "Vulkan command-chain resource planner refresh",
                        AllowSynchronousResourceUploads);
                }
                else if (dynamicUiBatchTextOps.Length > 0)
                {
                    FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(dynamicUiBatchTextOps);
                    RefreshFrameOpResourceWrappers(
                        dynamicUiBatchTextOps,
                        plannerContext,
                        "Vulkan command-chain dynamic UI resource planner refresh",
                        AllowSynchronousResourceUploads);
                }

                plannerRevision = hasStaticFrameOps
                    ? PrepareFrameOpResourcePlannerStatesForFrameOps(ops)
                    : dynamicUiBatchTextOps.Length > 0
                        ? PrepareFrameOpResourcePlannerStatesForFrameOps(dynamicUiBatchTextOps)
                        : ResourcePlannerRevision;
            }

            if (!requiresTrackedPresentSourceRefresh &&
                !hasStaticFrameOps &&
                dynamicUiBatchTextOps.Length == 0 &&
                !preserveSwapchainForOverlay &&
                TryReuseLastSwapchainWriterVariant(
                    imageIndex,
                    plannerRevision,
                    swapchainImageEverPresentedAtRecord,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot,
                    out CommandBuffer lastSwapchainWriterCommandBuffer,
                    out swapchainLayoutAfterCommandBuffer))
            {
                return lastSwapchainWriterCommandBuffer;
            }

            if (VulkanPrimaryCommandBufferReuseEnabled &&
                !imageForcedDirty &&
                !gpuPipelineProfilingActive &&
                !hasQueryFrameOps &&
                TryReuseCleanCommandChainPrimaryVariant(
                    imageIndex,
                    frameOpsSignature,
                    dynamicUiBatchTextSignature,
                    dynamicUiBatchTextOps.Length,
                    plannerRevision,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot,
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

                return reusableCommandBuffer;
            }

            CommandChainSchedule? commandChainSchedule = null;
            CommandChainLoweringStats commandChainStats = default;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.CommandChainLowering"))
            {
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
            bool frameOpsRequireFreshPrimary = hasStaticFrameOps && (!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps);

            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.DirtyEvaluation"))
            {
                if (!dirty && frameOpsRequireFreshPrimary)
                {
                    dirty = true;
                    primaryFrameStateDirty = true;
                }

                if (gpuProfilerCommandBufferStateDirty)
                    profilerDirty = true;

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
                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.RefreshFrameData"))
                {
                    refreshedReusableFrameData = !hasStaticFrameOps ||
                        TryRefreshReusableCommandBufferFrameData(imageIndex, ops);
                    if (refreshedReusableFrameData && dynamicUiBatchTextOps.Length > 0)
                        refreshedReusableFrameData = TryRefreshReusableCommandBufferFrameData(imageIndex, dynamicUiBatchTextOps);
                }

                if (!refreshedReusableFrameData)
                {
                    dirty = true;
                    frameDataDirty = true;
                }
                else
                {
                    bool dynamicUiSecondaryReady = true;
                    if (!delayDynamicUiBatchTextOverlayRecording)
                    {
                        using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.RecordDynamicUiSecondary"))
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
                        variant.LastUsedFrameId = VulkanFrameCounter;
                        variant.DirtyReason = null;
                        SetActiveCommandBufferVariant(imageIndex, variant);
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
                        return variant.PrimaryCommandBuffer;
                    }
                }
            }

            string dirtyReason = forcedDirty
                ? FormatForcedCommandBufferDirtyReason(imageForcedDirty, variant.Dirty, forcedVariantDirtyReason)
                : frameOpSignatureDirty
                    ? "frame-ops"
                    : plannerDirty
                        ? "planner"
                        : profilerDirty
                            ? "profiler"
                            : frameDataDirty
                                ? string.IsNullOrEmpty(_lastReusableFrameDataRefreshFailureReason)
                                    ? "frame-data"
                                    : $"frame-data:{_lastReusableFrameDataRefreshFailureReason}"
                    : dynamicUiDirty
                        ? "dynamic-ui"
                        : swapchainLifecycleDirty
                            ? "swapchain-lifecycle"
                        : commandChainPrimaryDirty
                            ? $"command-chain-primary:{commandChainPrimaryDirtyReason}"
                        : primaryFrameStateDirty
                            ? "primary-frame-state"
                            : "unknown";

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                reusedClean: false,
                recorded: true,
                forcedDirty,
                frameOpSignatureDirty,
                plannerDirty,
                profilerDirty,
                dirtyReason);
            if (commandChainSchedule is not null)
            {
                CommandChainWorkerTiming workerTiming = DispatchCommandChainRecordingWorkers(commandChainSchedule);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
                    primaryCommandBuffersRecorded: 1,
                    chainWorkerRecordTime: workerTiming.WorkerRecordTime,
                    renderThreadWaitForWorkersTime: workerTiming.WaitForWorkersTime);
            }

            _lastEnsureCommandBufferRecordedPrimary = true;
            _isRecordingCommandBuffer = true;
            bool recordedDynamicUiSecondaryReady = delayDynamicUiBatchTextOverlayRecording;
            int recordedSwapchainWriteCount = 0;
            try
            {
                BeginRecordedTextureUploadSubmitBatch();

                if (!delayDynamicUiBatchTextOverlayRecording)
                {
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.RecordDynamicUiSecondary"))
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

                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.RecordPrimary"))
                    swapchainLayoutAfterCommandBuffer = RecordCommandBuffer(
                        imageIndex,
                        variant.PrimaryCommandBuffer,
                        variant.DynamicUiSecondaryCommandBuffer,
                        ops,
                        recordedDynamicUiSecondaryReady && !preserveSwapchainForOverlay ? dynamicUiBatchTextOps.Length : 0,
                        commandChainSchedule,
                        preserveSwapchainForOverlay,
                        out recordedSwapchainWriteCount);
            }
            catch
            {
                CancelRecordedTextureUploadSubmitBatch("command buffer recording failed before upload submit");
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
            variant.RecordedSwapchainImageEverPresented = swapchainImageEverPresentedAtRecord;
            variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
            variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
            variant.RecordedSwapchainRefreshFromLastPresentSource =
                requiresTrackedPresentSourceRefresh &&
                recordedSwapchainWriteCount > 0;
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
                MarkCommandBufferVariantTransientAfterTextureUpload(variant);
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
            return variant.PrimaryCommandBuffer;
        }

        private bool TryReuseLastSwapchainWriterVariant(
            uint imageIndex,
            ulong plannerRevision,
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
                if (variant.RecordedSwapchainWriteCount <= 0 ||
                    variant.RecordedSwapchainFinalLayout != ImageLayout.PresentSrcKhr ||
                    variant.PlannerRevision != plannerRevision ||
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

        private static void MarkCommandBufferVariantTransientAfterTextureUpload(CommandBufferCacheVariant variant)
        {
            variant.Dirty = true;
            variant.DirtyReason = "transient texture upload";
            variant.FrameOpsSignature = ulong.MaxValue;
            variant.DynamicUiSignature = ulong.MaxValue;
            variant.DynamicUiOpCount = -1;
            variant.DynamicUiSecondaryRecorded = false;
            variant.PreserveSwapchainForOverlay = false;
            variant.RecordedSwapchainImageEverPresented = false;
            variant.RecordedSwapchainFinalLayout = ImageLayout.PresentSrcKhr;
            variant.RecordedSwapchainWriteCount = 0;
            variant.RecordedSwapchainRefreshFromLastPresentSource = false;
            variant.CommandChainScheduleSignature = ulong.MaxValue;
            variant.CommandChainPrimaryGroupSignature = ulong.MaxValue;
            variant.CommandChainPrimaryGroupCount = -1;
            variant.PlannerRevision = ulong.MaxValue;
            variant.GpuProfilerActive = false;
            variant.GpuProfilerFrameSlot = -1;
            variant.GpuProfilerScopes = null;
            variant.GpuProfilerQueryCount = 0;
            variant.SignatureDebugParts = null;
        }

        private bool TryReuseCleanCommandChainPrimaryVariant(
            uint imageIndex,
            ulong frameOpsSignature,
            ulong dynamicUiBatchTextSignature,
            int dynamicUiBatchTextOpCount,
            ulong plannerRevision,
            bool gpuPipelineProfilingActive,
            int commandBufferImageSlot,
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
            if (ActiveFrameOpResourcePlannerSwitchingState.SwitchingActive)
                return false;

            if (!CommandChainsEnabled ||
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
                if (variant.Dirty ||
                    variant.CommandChainScheduleSignature != cachedSchedule.StructuralSignature ||
                    variant.CommandChainPrimaryGroupSignature != currentPrimaryGroupSignature ||
                    variant.CommandChainPrimaryGroupCount != currentPrimaryGroupCount ||
                    variant.FrameOpsSignature != frameOpsSignature ||
                    variant.PlannerRevision != plannerRevision ||
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
                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.FastReuse.RefreshFrameData"))
                {
                    refreshedReusableFrameData = ops.Length == 0 ||
                        TryRefreshReusableCommandBufferFrameData(imageIndex, ops);
                    if (refreshedReusableFrameData && dynamicUiBatchTextOps.Length > 0)
                        refreshedReusableFrameData = TryRefreshReusableCommandBufferFrameData(imageIndex, dynamicUiBatchTextOps);
                }
                if (!refreshedReusableFrameData)
                    return false;

                bool dynamicUiSecondaryReady = true;
                if (!delayDynamicUiSecondaryRecording)
                {
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordCommandBuffer.FastReuse.RecordDynamicUiSecondary"))
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
                variant.LastUsedFrameId = VulkanFrameCounter;
                variant.DirtyReason = null;
                StoreFrameOpSignatureDebugParts(variant, ops);
                SetActiveCommandBufferVariant(imageIndex, variant);
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

        private bool TryRefreshReusableCommandBufferFrameData(uint imageIndex, FrameOp[] ops, bool refreshMaterialUniforms = true)
        {
            if (ops.Length == 0)
                return true;

            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = _refreshMeshDrawSlotsByRendererScratch;
            meshDrawSlotsByRenderer.Clear();
            meshDrawSlotsByRenderer.EnsureCapacity(_refreshMeshDrawSlotCapacityHint);
            int GetMeshDrawUniformSlot(VkMeshRenderer renderer)
            {
                ref int slotRef = ref CollectionsMarshal.GetValueRefOrAddDefault(meshDrawSlotsByRenderer, renderer, out _);
                int slot = slotRef;
                slotRef = slot + 1;
                return slot;
            }

            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                switch (op)
                {
                    case MeshDrawOp drawOp:
                    {
                        int drawUniformSlot = GetMeshDrawUniformSlot(drawOp.Draw.Renderer);
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

            _refreshMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);
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
                MemoryBarrierOp => "Vulkan.RecordPrimary.Op.MemoryBarrier",
                DlssUpscaleOp => "Vulkan.RecordPrimary.Op.DlssUpscale",
                DlssFrameGenerationOp => "Vulkan.RecordPrimary.Op.DlssFrameGeneration",
                TextureUploadFrameOp => "Vulkan.RecordPrimary.Op.TextureUpload",
                _ => "Vulkan.RecordPrimary.Op.Unknown"
            };

        private static void EnsureMeshDrawUniformSlotCapacityForRecording(
            FrameOp[] ops,
            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer)
        {
            meshDrawSlotsByRenderer.Clear();

            for (int i = 0; i < ops.Length; i++)
            {
                if (ops[i] is not MeshDrawOp drawOp)
                    continue;

                VkMeshRenderer renderer = drawOp.Draw.Renderer;
                meshDrawSlotsByRenderer.TryGetValue(renderer, out int count);
                meshDrawSlotsByRenderer[renderer] = count + 1;
            }

            foreach (KeyValuePair<VkMeshRenderer, int> pair in meshDrawSlotsByRenderer)
                pair.Key.EnsureUniformDrawSlotCapacity(pair.Value);
        }

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
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
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
                Api!.CmdCopyBufferToImage(
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
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                request.TextureName,
                request.SourcePath,
                request.StreamingGeneration,
                upload.PublicationToken,
                "uploadRecordToDescriptorPublication",
                upload.RecordTimestamp == 0L ? 0.0 : TextureRuntimeDiagnostics.ElapsedMilliseconds(upload.RecordTimestamp));
            TextureRuntimeDiagnostics.LogVulkanImportedTextureUploadLatency(
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
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

        private SwapchainRecordingTarget ResolveSwapchainRecordingTarget(
            uint imageIndex,
            OpenXrEyeRenderTargetContext? openXrTargetContext)
        {
            if (openXrTargetContext is { } openXrTarget && openXrTarget.IsValid)
            {
                return new SwapchainRecordingTarget(
                    openXrTarget.Image,
                    openXrTarget.ImageView,
                    openXrTarget.ImageFormat,
                    openXrTarget.Extent,
                    openXrTarget.DepthImage,
                    openXrTarget.DepthView,
                    openXrTarget.DepthFormat,
                    openXrTarget.DepthAspect,
                    ImageEverPresentedAtRecordStart: false);
            }

            if (swapChainImages is null ||
                swapChainImageViews is null ||
                imageIndex >= swapChainImages.Length ||
                imageIndex >= swapChainImageViews.Length)
            {
                return default;
            }

            return new SwapchainRecordingTarget(
                swapChainImages[imageIndex],
                swapChainImageViews[imageIndex],
                swapChainImageFormat,
                swapChainExtent,
                _swapchainDepthImage,
                _swapchainDepthView,
                _swapchainDepthFormat,
                _swapchainDepthAspect,
                IsSwapchainImageEverPresented(imageIndex));
        }

        private ImageLayout RecordCommandBuffer(
            uint imageIndex,
            CommandBuffer commandBuffer,
            CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            FrameOp[] ops,
            int dynamicUiBatchTextOpCount,
            CommandChainSchedule? commandChainSchedule,
            bool preserveSwapchainForOverlay,
            out int recordedSwapchainWriteCount,
            bool transitionSwapchainToPresent = true,
            uint? frameDataImageIndexOverride = null,
            OpenXrEyeRenderTargetContext? openXrTargetContext = null)
        {
            recordedSwapchainWriteCount = 0;
            int droppedDrawOps = 0;
            int droppedComputeOps = 0;
            int droppedFrameOps = 0;
            FrameOpFailureSnapshot? firstFailure = null;
            uint frameDataImageIndex = frameDataImageIndexOverride ?? imageIndex;
            int commandBufferImageSlot = unchecked((int)Math.Min(frameDataImageIndex, int.MaxValue));
            SwapchainRecordingTarget swapchainTarget = ResolveSwapchainRecordingTarget(imageIndex, openXrTargetContext);
            Extent2D swapchainRecordExtent = swapchainTarget.IsValid ? swapchainTarget.Extent : swapChainExtent;
            bool imageWasEverPresentedAtRecordStart = swapchainTarget.ImageEverPresentedAtRecordStart;
            CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
            HashSet<nint> executedCommandChainSecondaryHandles = recordingScratch.ExecutedCommandChainSecondaryHandles;
            executedCommandChainSecondaryHandles.Clear();

            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.ResetAndBegin"))
            {
                ReleaseDeferredSecondaryCommandBuffers(frameDataImageIndex);
                Api!.ResetCommandBuffer(commandBuffer, 0);
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

                CmdBeginLabel(commandBuffer, frameDataImageIndex == imageIndex
                    ? $"FrameCmd[{imageIndex}]"
                    : $"FrameCmd[target={imageIndex} frame={frameDataImageIndex}]");
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
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.SortAndSecondaryBuckets"))
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

                if (commandChainSchedule is not null && CommandChainValidationEnabled)
                    ValidatePrimaryCommandChainSchedule(commandChainSchedule, ops, dynamicUiBatchTextOpCount);

                if (commandChainSchedule is not null &&
                    TryGetCommandChainScheduleFrameSlot(commandChainSchedule, out int commandChainScheduleFrameSlot))
                {
                    scheduledCommandChainKeysByOpIndex = BuildCommandChainKeysByFrameOpIndex(commandChainSchedule, ops.Length);
                    scheduledCommandChainCache = GetCommandChainCache(unchecked((uint)commandChainScheduleFrameSlot));
                }
            }

            initialContext = ops.Length > 0
                ? ops[0].Context
                : initialContext;
            using FrameOpResourcePlannerRecordingScope frameOpResourcePlannerRecordingScope = EnterFrameOpResourcePlannerRecordingScope();
            _ = TryActivateFrameOpResourcePlannerState(initialContext);

            if (commandChainSchedule is not null)
            {
                _lastReusableFrameDataRefreshFailureReason = null;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.RefreshScheduledSecondaryFrameData"))
                    _ = TryRefreshReusableCommandBufferFrameData(frameDataImageIndex, ops);
            }

            int swapchainPresentTransitions = 0;
            bool usedSwapchainDynamicRendering = false;
            bool swapchainInColorAttachmentLayout = false;
            ImageLayout swapchainFinalTargetLayout = transitionSwapchainToPresent
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.ColorAttachmentOptimal;
            ImageLayout swapchainFinalLayout = imageWasEverPresentedAtRecordStart
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.FrameStartBarriers"))
            {
                CmdBeginLabel(commandBuffer, "SwapchainBarriers");
                var plannedSwapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                var swapchainImageBarriers = BarrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                var swapchainBufferBarriers = BarrierPlanner.GetBufferBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                EmitPlannedSwapchainBarriers(commandBuffer, plannedSwapchainBarriers);
                EmitPlannedImageBarriers(commandBuffer, swapchainImageBarriers);
                EmitPlannedBufferBarriers(commandBuffer, swapchainBufferBarriers);
                CmdEndLabel(commandBuffer);

                // Transition any freshly-allocated physical images from UNDEFINED to
                // a safe initial layout so that render passes never see UNDEFINED.
                EmitInitialImageBarriersForUnknownPass(commandBuffer);
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
            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = recordingScratch.MeshDrawSlotsByRenderer;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.ScratchAndUniformSlots"))
            {
                swapchainWritesByPipeline.Clear();
                swapchainWriterLabelByPipeline.Clear();
                swapchainWriterDetailByPipeline.Clear();
                swapchainWriterOpByPipeline.Clear();
                swapchainWriterDynamicUiDrawCountByPipeline.Clear();
                swapchainWriterPassByPipeline.Clear();
                swapchainWriterOpIndexByPipeline.Clear();
                pipelineNameByIdentity.Clear();
                meshDrawSlotsByRenderer.Clear();
                int writerCapacityHint = Math.Max(1, recordingScratch.RecordSwapchainWriterCapacityHint);
                swapchainWritesByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterLabelByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterDetailByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterOpByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterDynamicUiDrawCountByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterPassByPipeline.EnsureCapacity(writerCapacityHint);
                swapchainWriterOpIndexByPipeline.EnsureCapacity(writerCapacityHint);
                pipelineNameByIdentity.EnsureCapacity(Math.Max(1, recordingScratch.RecordPipelineNameCapacityHint));
                meshDrawSlotsByRenderer.EnsureCapacity(Math.Max(1, recordingScratch.RecordMeshDrawSlotCapacityHint));
                EnsureMeshDrawUniformSlotCapacityForRecording(ops, meshDrawSlotsByRenderer);
                meshDrawSlotsByRenderer.Clear();
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
                    IndirectDrawOp indirect =>
                        $" renderer='{indirect.MeshRenderer.MeshRenderer?.Name ?? "<unnamed renderer>"}' draws={indirect.DrawCount} stride={indirect.Stride} offset={indirect.ByteOffset} countOffset={indirect.CountByteOffset} useCount={indirect.UseCount}",
                    QueryOp query =>
                        $" query={query.Operation} target={query.QueryTarget}",
                    BlitOp blit =>
                        $" in='{blit.InFbo?.Name ?? "<swapchain>"}' out='{blit.OutFbo?.Name ?? "<swapchain>"}' color={blit.ColorBit} depth={blit.DepthBit} stencil={blit.StencilBit}",
                    _ => string.Empty
                };

            int GetMeshDrawUniformSlot(VkMeshRenderer renderer)
            {
                ref int slotRef = ref CollectionsMarshal.GetValueRefOrAddDefault(meshDrawSlotsByRenderer, renderer, out _);
                int slot = slotRef;
                slotRef = slot + 1;
                return slot;
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

            void LogSwapchainWritersByPipeline(string phase)
            {
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

            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.OpCensus"))
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
                            MarkSwapchainFrameOpWriter(nameof(MeshTaskDispatchIndirectCountOp), meshTaskDispatch, meshTaskDispatch.PassIndex, opScanIndex, meshTaskDispatch.Context.PipelineIdentity);
                            break;
                        case BlitOp blit:
                            RememberPipelineName(blit.Context);
                            blitCount++;
                            if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                            {
                                swapchainWriteCount++;
                                swapchainBlitWrites++;
                                MarkSwapchainFrameOpWriter(nameof(BlitOp), blit, blit.PassIndex, opScanIndex, blit.Context.PipelineIdentity);
                            }
                            else
                            {
                                fboOnlyBlitOps++;
                            }
                            break;
                        case ComputeDispatchOp: computeCount++; break;
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

                sceneSwapchainWriters = swapchainWriteCount;
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

                LogSwapchainWritersByPipeline("PreOverlay");
            }

            bool renderPassActive = false;
            bool activeDynamicRendering = false;
            XRFrameBuffer? activeTarget = null;
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
            IDisposable? activePipelineOverrideScope = null;

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
                    ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
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
                activePipelineOverrideScope?.Dispose();
                activePipelineOverrideScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
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

                if (imageWasEverPresentedAtRecordStart)
                {
                    swapchainFinalLayout = ImageLayout.PresentSrcKhr;
                    return;
                }

                ImageMemoryBarrier presentBarrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = 0,
                    OldLayout = ImageLayout.Undefined,
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
                    PipelineStageFlags.TopOfPipeBit,
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
                        isSource: false);
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

                    blitRecorded = RecordBlitOp(commandBuffer, imageIndex, replayBlit);
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
                if (activeDynamicRendering)
                {
                    Api!.CmdEndRendering(commandBuffer);

                    if (transitionSwapchainToPresent)
                    {
                        swapchainInColorAttachmentLayout = true;
                        swapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                        if (finalClose && !preserveSwapchainForOverlay)
                            TransitionSwapchainToPresent();
                    }
                    else if (activeTarget is not null && activeFboAttachmentSignature is not null)
                    {
                        TransitionFboAttachmentsForDynamicRendering(
                            commandBuffer,
                            activeTarget,
                            activeFboAttachmentSignature,
                            beginRendering: false);

                        UpdatePhysicalGroupLayoutsForFbo(
                            activeTarget,
                            activeFboAttachmentSignature,
                            useReferenceLayouts: false);

                        ImageLayout[] finalLayouts = VkFrameBuffer.GetFinalLayouts(activeFboAttachmentSignature);
                        fboLayoutTracking[activeTarget] = finalLayouts;
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
                        UpdatePhysicalGroupLayoutsForFbo(activeTarget);

                        // Record the finalLayout of each attachment so the NEXT render
                        // pass on this FBO can set initialLayout correctly and preserve
                        // content across pass boundaries.
                        var vkFbo = GenericToAPI<VkFrameBuffer>(activeTarget);
                        if (vkFbo is not null)
                            fboLayoutTracking[activeTarget] = vkFbo.GetFinalLayouts();
                    }

                    Api!.CmdEndRenderPass(commandBuffer);
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

                Api!.CmdBeginRendering(commandBuffer, &renderingInfo);
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
                        AttachmentLoadOp colorLoadOp = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
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

                        ImageMemoryBarrier depthBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = 0,
                            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = ImageLayout.Undefined,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapchainTarget.DepthImage,
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = swapchainTarget.DepthAspect,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                        uint preRenderingBarrierCount = 0;
                        if (colorOldLayout != ImageLayout.ColorAttachmentOptimal)
                            preRenderingBarriers[preRenderingBarrierCount++] = colorBarrier;
                        preRenderingBarriers[preRenderingBarrierCount++] = depthBarrier;

                        CmdPipelineBarrierTracked(
                            commandBuffer,
                            PipelineStageFlags.TopOfPipeBit,
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
                            ImageLayout.Undefined,
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
                    AttachmentLoadOp legacySwapchainLoadOp = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear;
                    RenderPass selectedRenderPass = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
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

                    Api!.CmdBeginRenderPass(
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
                CmdBeginLabel(commandBuffer, $"{(UseDynamicRenderingRenderTargets ? "Rendering" : "RenderPass")}:{fboName}");
                renderPassLabelActive = true;

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
                bool passDepthStencilReadOnly = vkFrameBuffer.UsesReadOnlyDepthStencilForPass(
                    passIndex,
                    context.PassMetadata,
                    trackedLayouts,
                    preserveTrackedClearLoads: targetReenteredThisCommandBuffer);
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
                    UpdatePhysicalGroupLayoutsForFbo(
                        target,
                        fboSignature,
                        useReferenceLayouts: true);

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
                        if (vkFrameBuffer.TryGetAttachmentTarget(
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

                        if ((signature.AspectMask & ImageAspectFlags.DepthBit) != 0)
                        {
                            depthAttachmentPlan = attachmentPlan;
                            hasDepthAttachment = true;
                        }

                        if ((signature.AspectMask & ImageAspectFlags.StencilBit) != 0)
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

                RenderPass passRenderPass = vkFrameBuffer.ResolveRenderPassForPass(
                    passIndex,
                    context.PassMetadata,
                    trackedLayouts,
                    CompiledRenderGraph.Synchronization,
                    preserveTrackedClearLoads: targetReenteredThisCommandBuffer);

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

                Api!.CmdBeginRenderPass(
                    commandBuffer,
                    &fboPassInfo,
                    secondaryContents ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline);

                renderPassActive = true;
                activeDynamicRendering = false;
                activeTarget = target;
                activeRenderPass = passRenderPass;
                activeFramebuffer = vkFrameBuffer.FrameBuffer;
                activeDynamicRenderingFormats = default;
                activeFboAttachmentSignature = null;
                activeRenderArea = fboPassInfo.RenderArea;
                activeDepthStencilReadOnly = passDepthStencilReadOnly;
                if (TargetTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanTarget] begin target='{0}' targetId={1} pass={2} passName='{3}' dynamic=false renderPass=0x{4:X} framebuffer=0x{5:X} attachments={6} extent={7}x{8} secondary={9}",
                        fboName,
                        target.GetHashCode(),
                        passIndex,
                        ResolvePassName(context.PassMetadata, passIndex),
                        passRenderPass.Handle,
                        vkFrameBuffer.FrameBuffer.Handle,
                        vkFrameBuffer.AttachmentCount,
                        fboPassInfo.RenderArea.Extent.Width,
                        fboPassInfo.RenderArea.Extent.Height,
                        secondaryContents);
                }
            }

            void RecordMeshDrawIntoCommandBuffer(
                CommandBuffer targetCommandBuffer,
                MeshDrawOp drawOp,
                int passIndex,
                int? drawUniformSlotOverride = null)
            {
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
                        "[DeferredLightingDiag][GBufferDraw] target='{0}' pass={1} dyn={2} rp=0x{3:X} colors={4} depthFmt={5} layers={6} viewMask=0x{7:X} dsReadOnly={8} mesh='{9}' material='{10}' program='{11}' stereo={12} colorMask={13} blend={14} depth={15}/{16}/{17} cull={18} front={19} vp=({20},{21},{22},{23}) scissor=({24},{25},{26},{27})",
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
                        scissor.Extent.Height);
                }

                int drawUniformSlot = drawUniformSlotOverride ?? GetMeshDrawUniformSlot(drawOp.Draw.Renderer);
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
                    drawUniformSlot);

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
            }

            void RecordIndirectDrawIntoCommandBuffer(CommandBuffer targetCommandBuffer, IndirectDrawOp indirectOp, int passIndex)
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
                        GetMeshDrawUniformSlot(indirectOp.MeshRenderer),
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
                    if (!Equals(candidate.Context, activeContext))
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
                    if (!Equals(candidate.Context, activeContext))
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

                inheritedDepthStencilReadOnly = vkFrameBuffer.UsesReadOnlyDepthStencilForPass(
                    passIndex,
                    context.PassMetadata,
                    trackedLayouts,
                    preserveTrackedClearLoads: targetReenteredThisCommandBuffer);

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
                if (!CommandChainsEnabledForCurrentRecording ||
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

                bool useParallelSecondary =
                    _enableParallelSecondaryCommandBufferRecording &&
                    runCount >= Math.Max(_parallelSecondaryIndirectRunThreshold, 2);

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

                CmdBeginLabel(commandBuffer, useParallelSecondary
                    ? $"IndirectCommandChainSecondaryParallel[{runCount}]"
                    : $"IndirectCommandChainSecondary[{runCount}]");

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
                        uniformSlots[i] = GetMeshDrawUniformSlot(indirectOp.MeshRenderer);
                    }

                    for (int i = 0; i < runCount; i++)
                    {
                        IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + i];
                        indirectOp.MeshRenderer.EnsureUniformDrawSlotCapacity(uniformSlots[i] + 1);
                    }

                    for (int i = 0; i < runCount; i++)
                    {
                        IndirectDrawOp indirectOp = (IndirectDrawOp)ops[startIndex + i];
                        using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(indirectOp.Context.PipelineInstance);
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
                            Api!.ResetCommandBuffer(secondary, 0);

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

                            if (Api!.EndCommandBuffer(secondary) != Result.Success)
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
                        Api!.CmdExecuteCommands(commandBuffer, (uint)runCount, secondaryPtr);
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
                if (!scheduledCommandChainCache.TryGetValue(key, out CommandChain? scheduledChain))
                    return false;

                if (scheduledChain.SourceStartIndex != opIndex || scheduledChain.SourceCount != 1)
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
                {
                    return true;
                }

                return chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed);
            }

            bool TryExecuteScheduledMeshCommandChainSecondaryRun(int startIndex, int runCount, int passIndex, MeshDrawOp firstDraw)
            {
                if (!CommandChainsEnabledForCurrentRecording ||
                    !_enableSecondaryCommandBuffers ||
                    scheduledCommandChainKeysByOpIndex is null ||
                    scheduledCommandChainCache is null ||
                    runCount <= 0 ||
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

                Format* scheduledColorAttachmentFormats = stackalloc Format[(int)Math.Max(inheritedDynamicRenderingFormats.ColorAttachmentCount, 1u)];
                CommandBufferInheritanceRenderingInfo scheduledRenderingInheritanceInfo = default;
                    if (inheritedDynamicRendering)
                    {
                        inheritedDynamicRenderingFormats.CopyColorAttachmentFormats(
                            scheduledColorAttachmentFormats,
                        inheritedDynamicRenderingFormats.ColorAttachmentCount);

                    scheduledRenderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                    {
                        SType = StructureType.CommandBufferInheritanceRenderingInfo,
                        Flags = 0,
                        ViewMask = inheritedDynamicRenderingFormats.ViewMask,
                        ColorAttachmentCount = inheritedDynamicRenderingFormats.ColorAttachmentCount,
                        PColorAttachmentFormats = inheritedDynamicRenderingFormats.ColorAttachmentCount > 0 ? scheduledColorAttachmentFormats : null,
                        DepthAttachmentFormat = inheritedDynamicRenderingFormats.DepthAttachmentFormat,
                        StencilAttachmentFormat = inheritedDynamicRenderingFormats.StencilAttachmentFormat,
                        RasterizationSamples = inheritedSamples
                    };
                    DynamicRenderingLocalReadPlan localReadInheritance = default;
                    void* localReadInheritancePNext = scheduledRenderingInheritanceInfo.PNext;
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
                        scheduledRenderingInheritanceInfo.PNext = localReadInheritancePNext;
                    }

                    CommandBuffer[] secondaryBuffers = ArrayPool<CommandBuffer>.Shared.Rent(runCount);
                CommandChain[] secondaryChains = ArrayPool<CommandChain>.Shared.Rent(runCount);
                Array.Clear(secondaryBuffers, 0, runCount);
                Array.Clear(secondaryChains, 0, runCount);
                bool meshLabelActive = false;

                CmdBeginLabel(commandBuffer, $"ScheduledMeshCommandChainSecondary[{runCount}]");
                meshLabelActive = true;

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
                    }

                    for (int i = 0; i < runCount; i++)
                    {
                        int opIndex = startIndex + i;
                        MeshDrawOp drawOp = (MeshDrawOp)ops[opIndex];
                        _ = TryGetScheduledCommandChainForOp(opIndex, out CommandChain chain, out _);

                        if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, frameDataImageIndex, executedCommandChainSecondaryHandles, out CommandBuffer secondary))
                            throw new InvalidOperationException("Failed to allocate Vulkan scheduled mesh command-chain secondary command buffer.");

                        secondaryChains[i] = chain;
                        secondaryBuffers[i] = secondary;

                        int drawUniformSlot = GetMeshDrawUniformSlot(drawOp.Draw.Renderer);

                        if (!ScheduledCommandChainSecondaryNeedsRecording(chain) &&
                            drawOp.Draw.Renderer.TryRefreshReusableCommandBufferFrameData(
                                frameDataImageIndex,
                                drawOp.Draw,
                                drawUniformSlot,
                                refreshMaterialUniforms: true))
                        {
                            continue;
                        }

                        MarkCommandChainSecondaryCommandBufferInvalid(chain);
                        Api!.ResetCommandBuffer(secondary, 0);

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
                        if (inheritedDynamicRendering)
                            inheritanceInfo.PNext = &scheduledRenderingInheritanceInfo;

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
                            throw new Exception("Failed to begin Vulkan scheduled mesh command-chain secondary command buffer.");

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
                            using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawOp.Context.PipelineInstance);
                            RecordMeshDrawIntoCommandBuffer(secondary, drawOp, passIndex, drawUniformSlot);
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

                        if (Api!.EndCommandBuffer(secondary) != Result.Success)
                            throw new Exception("Failed to end Vulkan scheduled mesh command-chain secondary command buffer.");

                        chain.State = CommandChainState.Recorded;
                        chain.FrameDataRefreshTouchedDescriptors = false;
                        MarkCommandChainSecondaryCommandBufferRecorded(chain);
                    }

                    BeginRenderPassForTarget(firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                    fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                        Api!.CmdExecuteCommands(commandBuffer, (uint)runCount, secondaryPtr);
                    for (int i = 0; i < runCount; i++)
                    {
                        if (secondaryBuffers[i].Handle != 0)
                            executedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                    }

                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: runCount);
                    return true;
                }
                finally
                {
                    EndActiveRenderPass();

                    Array.Clear(secondaryBuffers, 0, runCount);
                    Array.Clear(secondaryChains, 0, runCount);
                    ArrayPool<CommandBuffer>.Shared.Return(secondaryBuffers);
                    ArrayPool<CommandChain>.Shared.Return(secondaryChains);

                    if (meshLabelActive)
                        CmdEndLabel(commandBuffer);
                }
            }

            bool TryExecuteMeshCommandChainSecondaryRun(int startIndex, int runCount, int passIndex, MeshDrawOp firstDraw)
            {
                const int minMeshDrawsPerSecondaryChain = 4;

                if (!CommandChainsEnabledForCurrentRecording ||
                    !_enableSecondaryCommandBuffers ||
                    runCount < minMeshDrawsPerSecondaryChain ||
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

                CmdBeginLabel(commandBuffer, $"MeshCommandChainSecondary[{runCount}]");
                meshLabelActive = true;

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
                        int drawUniformSlot = GetMeshDrawUniformSlot(drawOp.Draw.Renderer);
                        drawUniformSlots[i] = drawUniformSlot;
                        drawOp.Draw.Renderer.EnsureUniformDrawSlotCapacity(drawUniformSlot + 1);
                    }

                    MarkCommandChainSecondaryCommandBufferInvalid(chain);
                    Api!.ResetCommandBuffer(secondary, 0);

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
                            using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawOp.Context.PipelineInstance);
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

                    if (Api!.EndCommandBuffer(secondary) != Result.Success)
                        throw new Exception("Failed to end Vulkan mesh command-chain secondary command buffer.");

                    MarkCommandChainSecondaryCommandBufferRecorded(chain);
                    secondaryRecordingFinished = true;
                    BeginRenderPassForTarget(firstDraw.Target, passIndex, firstDraw.Context, secondaryContents: true);
                    Api!.CmdExecuteCommands(commandBuffer, 1, &secondary);
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

                        AttachmentLoadOp colorLoadOp = (swapchainClearedThisFrame || swapchainWrittenOutsideRenderPass)
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

                        ImageMemoryBarrier depthBarrier = new()
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = 0,
                            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = ImageLayout.Undefined,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = swapchainTarget.DepthImage,
                            SubresourceRange = new ImageSubresourceRange
                            {
                                AspectMask = swapchainTarget.DepthAspect,
                                BaseMipLevel = 0,
                                LevelCount = 1,
                                BaseArrayLayer = 0,
                                LayerCount = 1
                            }
                        };

                        ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                        uint preRenderingBarrierCount = 0;
                        if (colorOldLayout != ImageLayout.ColorAttachmentOptimal)
                            preRenderingBarriers[preRenderingBarrierCount++] = colorBarrier;
                        preRenderingBarriers[preRenderingBarrierCount++] = depthBarrier;

                        CmdPipelineBarrierTracked(
                            commandBuffer,
                            PipelineStageFlags.TopOfPipeBit,
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
                            ImageLayout.Undefined,
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
                        Api!.CmdExecuteCommands(commandBuffer, 1, &secondaryCommandBuffer);
                        Api!.CmdEndRendering(commandBuffer);

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

                        Api!.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.SecondaryCommandBuffers);
                        Api!.CmdExecuteCommands(commandBuffer, 1, &secondaryCommandBuffer);
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
                EmitInitialImageBarriersForUnknownPass(commandBuffer);

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

                    // Emit image layout transitions for any physical-group images that
                    // are still in UNDEFINED.  Without this, the first draw that
                    // references these images triggers a validation error because the
                    // barrier planner never planned a transition for them.
                    EmitInitialImageBarriersForUnknownPass(commandBuffer);

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
                    EmitPlannedImageBarriers(commandBuffer, imageBarriers);
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

            try
            {
                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.MainOpLoop"))
                {
                for (int opIndex = 0; opIndex < ops.Length; opIndex++)
                {
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
                            RecordTextureUploadOp(commandBuffer, textureUploadOp.Upload);
                            CmdEndLabel(commandBuffer);
                            continue;
                        }

                        if (!hasActiveContext || !Equals(activeContext, op.Context))
                        {
                            IDisposable? contextChangeProfileScope = null;
                            if (CommandRecordingDetailProfilingEnabled)
                                contextChangeProfileScope = RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.ContextChange");
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

                            if (!preservedSwapchainPass)
                            {
                                EndActiveRenderPass();
                            }

                            if (!preservedSwapchainPass && passIndexLabelActive)
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

                            if (preservedSwapchainPass)
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
                                passTransitionProfileScope = RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.PassTransition");
                            try
                            {
                            // Barriers are safest outside render passes.
                            EndActiveRenderPass();

                            if (passIndexLabelActive)
                            {
                                CmdEndLabel(commandBuffer);
                                passIndexLabelActive = false;
                            }

                            CmdBeginLabel(
                                commandBuffer,
                                $"Pass={opPassIndex} Pipe={op.Context.PipelineIdentity} Vp={op.Context.ViewportIdentity}");
                            passIndexLabelActive = true;

                            EmitPassBarriers(opPassIndex);
                            activePassIndex = opPassIndex;
                            activeSchedulingIdentity = opSchedulingIdentity;
                            }
                            finally
                            {
                                passTransitionProfileScope?.Dispose();
                            }
                        }

                        using var vulkanGpuScope = TryBeginVulkanGpuProfilerScope(commandBuffer, op, opPassIndex);

                        IDisposable? frameOpProfileScope = null;
                        if (CommandRecordingDetailProfilingEnabled)
                            frameOpProfileScope = RuntimeRenderingHostServices.Current.StartProfileScope(GetRecordPrimaryFrameOpProfileScopeName(op));
                        try
                        {
                        switch (op)
                        {
                    case BlitOp blit:
                        EndActiveRenderPass();
                        if (blit.ColorBit && (blit.InFbo is null || blit.OutFbo is null))
                            EnsureSwapchainColorAttachmentLayoutForBlit();
                        CmdBeginLabel(commandBuffer, "Blit");
                        bool blitRecorded = RecordBlitOp(commandBuffer, imageIndex, blit);
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

                        CmdBeginLabel(commandBuffer, $"TransformFeedback.{transformFeedbackOp.Operation}");
                        RecordTransformFeedbackOp(commandBuffer, transformFeedbackOp);
                        CmdEndLabel(commandBuffer);
                        break;

                    case QueryOp queryOp:
                        if (!renderPassActive || activeTarget != queryOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(queryOp.Target, opPassIndex, activeContext);
                        }

                        CmdBeginLabel(commandBuffer, $"Query.{queryOp.Operation}");
                        if (queryOp.Operation == EVulkanQueryFrameOpKind.Begin)
                            queryOp.Query.BeginQuery(commandBuffer, queryOp.QueryTarget);
                        else
                            queryOp.Query.EndQuery(commandBuffer);
                        CmdEndLabel(commandBuffer);
                        break;

                    case MeshDrawOp drawOp:
                        int meshCommandChainRunCount = CountContiguousMeshCommandChainRun(opIndex, drawOp, opPassIndex);
                        if (TryExecuteScheduledMeshCommandChainSecondaryRun(opIndex, meshCommandChainRunCount, opPassIndex, drawOp) ||
                            TryExecuteMeshCommandChainSecondaryRun(opIndex, meshCommandChainRunCount, opPassIndex, drawOp))
                        {
                            opIndex = opIndex + meshCommandChainRunCount - 1;
                            break;
                        }

                        if (!renderPassActive || activeTarget != drawOp.Target)
                        {
                            EndActiveRenderPass();
                            BeginRenderPassForTarget(drawOp.Target, opPassIndex, activeContext);
                        }

                        RecordMeshDrawIntoCommandBuffer(commandBuffer, drawOp, opPassIndex);
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

                    case MemoryBarrierOp memoryBarrierOp:
                        EndActiveRenderPass();
                        CmdBeginLabel(commandBuffer, "MemoryBarrier");
                        EmitMemoryBarrierMask(commandBuffer, memoryBarrierOp.Mask);
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
                        RecordDlssFrameGenerationOp(commandBuffer, frameGenerationOp);
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
                        if (op is ComputeDispatchOp)
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

                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.FinalOverlayAndDiagnostics"))
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
                    if (!TryRefreshUnwrittenSwapchainFromLastWindowPresentSource())
                        TransitionUnwrittenSwapchainToPresent();
                }

                bool hasSceneFrameWork = clearCount > 0 || drawCount > 0 || blitCount > 0 || computeCount > 0;
                bool expectsSceneSwapchainWriters = transitionSwapchainToPresent && !IsRenderingExternalSwapchainTarget;
                bool missingSceneSwapchainWriters = expectsSceneSwapchainWriters && hasSceneFrameWork && sceneSwapchainWriters == 0;
                if (missingSceneSwapchainWriters)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.MissingSceneSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan][FrameFailure] Scene frame recorded zero pre-overlay swapchain writers (clears={0}, draws={1}, blits={2}, computes={3}, fboOnlyDraws={4}, fboOnlyBlits={5}). Overlay or diagnostic clears may still present.",
                        clearCount,
                        drawCount,
                        blitCount,
                        computeCount,
                        fboOnlyDrawOps,
                        fboOnlyBlitOps);
                }
                else if (expectsSceneSwapchainWriters && swapchainWriteCount == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.NoSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
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
                recordingScratch.RecordMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);
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

                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.RecordPrimary.EndCommandBuffer"))
                {
                    if (Api!.EndCommandBuffer(commandBuffer) != Result.Success)
                        throw new Exception("Failed to record command buffer.");
                }
            }
            finally
            {
                activePipelineOverrideScope?.Dispose();
                activePipelineOverrideScope = null;
            }

            recordedSwapchainWriteCount = actualSwapchainWriteCount;
            return swapchainFinalLayout;
        }

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

        private bool RecordBlitOp(CommandBuffer commandBuffer, uint imageIndex, BlitOp op)
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

                Debug.VulkanEvery(
                    "Vulkan.Blit.Record",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] CmdBlitImage: src=0x{0:X}({1}) dst=0x{2:X}({3}) region={4},{5}+{6}x{7}â†’{8},{9}+{10}x{11} filter={12}",
                    resolvedSource.Image.Handle, resolvedSource.Format,
                    resolvedDestination.Image.Handle, resolvedDestination.Format,
                    op.InX, op.InY, op.InW, op.InH,
                    op.OutX, op.OutY, op.OutW, op.OutH,
                    filter);

                Api!.CmdBlitImage(
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
                TryResolveBlitImage(op.InFbo, imageIndex, op.ReadBufferMode, wantColor: true, wantDepth: false, wantStencil: false, out var colorSource, isSource: true) &&
                TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.ColorAttachment0, wantColor: true, wantDepth: false, wantStencil: false, out var colorDestination, isSource: false))
            {
                copiedAny |= ExecuteSingleBlit(colorSource, colorDestination, op.LinearFilter ? Filter.Linear : Filter.Nearest);
            }

            if ((op.DepthBit || op.StencilBit) &&
                TryResolveBlitImage(op.InFbo, imageIndex, op.ReadBufferMode, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthSource, isSource: true) &&
                TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.None, wantColor: false, wantDepth: op.DepthBit, wantStencil: op.StencilBit, out var depthDestination, isSource: false))
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

            if (op.UseCount && _supportsDrawIndirectCount && _khrDrawIndirectCount is not null)
            {
                // Use VK_KHR_draw_indirect_count path
                var parameterBuffer = op.ParameterBuffer?.BufferHandle;
                if (parameterBuffer is null || !parameterBuffer.HasValue)
                {
                    Debug.VulkanWarning("RecordIndirectDrawOp: Invalid parameter buffer for count draw.");
                    return;
                }

                // The parameter buffer contains the draw count at offset 0 (uint)
                _khrDrawIndirectCount.CmdDrawIndexedIndirectCount(
                    commandBuffer,
                    indirectBuffer.Value,
                    bufferOffset,
                    parameterBuffer.Value,
                    (ulong)op.CountByteOffset,
                    op.DrawCount,
                    op.Stride);

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
                return;

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
                return;

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
                return;
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
                int trackedLayer = binding.Layered ? -1 : (int)baseArrayLayer;

                IVkFrameBufferAttachmentSource? attachmentSource = source as IVkFrameBufferAttachmentSource;
                ImageLayout oldLayout = attachmentSource?.GetAttachmentTrackedLayout((int)baseMipLevel, trackedLayer)
                    ?? source.TrackedImageLayout;
                if (oldLayout == ImageLayout.Undefined && (mipLevels > 1u || arrayLayers > 1u))
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.ComputeStorageImage.MixedLayout.{texture.Name ?? texture.GetHashCode().ToString()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Skipping fallback compute storage layout transition for '{0}' because its live layout is mixed/unknown across subresources.",
                        texture.Name ?? texture.GetDescribingName());
                    continue;
                }

                if (oldLayout == ImageLayout.General)
                    continue;

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

                if (attachmentSource is not null)
                {
                    if (baseMipLevel == 0u && mipLevels == 1u && baseArrayLayer == 0u && layerCount >= arrayLayers)
                        attachmentSource.UpdateTrackedLayout(ImageLayout.General);
                    else
                        attachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.General, (int)baseMipLevel, trackedLayer);
                }
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

                ResolveFboAttachmentImageLayerSpan(
                    vkFbo,
                    layerIndex,
                    in info,
                    out uint imageBaseLayer,
                    out uint transitionLayerCount);
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
                    ImageLayout oldLayout = NormalizeFboAttachmentLayout(
                        signature,
                        ResolveFboAttachmentOldLayout(target, mipLevel, trackedLayer, requestedOldLayout));
                    if (oldLayout == newLayout)
                        continue;

                    bool oldLayoutIsRenderAttachment = !beginRendering;
                    bool newLayoutIsRenderAttachment = beginRendering;
                    PipelineStageFlags srcStage = oldLayout == ImageLayout.Undefined
                        ? PipelineStageFlags.TopOfPipeBit
                        : ResolveFboAttachmentStage(oldLayout, signature, oldLayoutIsRenderAttachment);
                    PipelineStageFlags dstStage = ResolveFboAttachmentStage(newLayout, signature, newLayoutIsRenderAttachment);

                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = oldLayout == ImageLayout.Undefined
                            ? 0
                            : ResolveFboAttachmentAccess(oldLayout, signature, oldLayoutIsRenderAttachment),
                        DstAccessMask = ResolveFboAttachmentAccess(newLayout, signature, newLayoutIsRenderAttachment),
                        OldLayout = oldLayout,
                        NewLayout = newLayout,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = info.Image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = aspectMask,
                            BaseMipLevel = info.MipLevel,
                            LevelCount = 1,
                            BaseArrayLayer = imageLayer,
                            LayerCount = 1
                        }
                    };

                    if (vkFbo.MultiviewViewMask != 0u || BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(fbo.Name))
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
                            info.Image.Handle);
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
                if (!asRenderAttachment)
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

        private void UpdatePhysicalGroupLayoutsForFbo(XRFrameBuffer fbo)
        {
            var vkFbo = GenericToAPI<VkFrameBuffer>(fbo);
            if (vkFbo is not null)
                UpdatePhysicalGroupLayoutsForFbo(vkFbo, vkFbo.GetFinalLayouts());
        }

        private void UpdatePhysicalGroupLayoutsForFbo(XRFrameBuffer fbo, ImageLayout[]? finalLayouts)
        {
            var vkFbo = GenericToAPI<VkFrameBuffer>(fbo);
            if (vkFbo is not null)
                UpdatePhysicalGroupLayoutsForFbo(vkFbo, finalLayouts);
        }

        private void UpdatePhysicalGroupLayoutsForFbo(VkFrameBuffer vkFbo, ImageLayout[]? finalLayouts)
        {
            int attachmentCount = vkFbo.AttachmentCount > 0
                ? (int)vkFbo.AttachmentCount
                : finalLayouts?.Length ?? 0;
            for (int attachmentIndex = 0; attachmentIndex < attachmentCount; attachmentIndex++)
            {
                ImageLayout finalLayout = (finalLayouts is not null && attachmentIndex < finalLayouts.Length)
                    ? finalLayouts[attachmentIndex]
                    : ImageLayout.Undefined;

                if (finalLayout == ImageLayout.Undefined)
                    continue;

                if (vkFbo.TryGetAttachmentTarget(
                    attachmentIndex,
                    out IFrameBufferAttachement? target,
                    out _,
                    out int mipLevel,
                    out int layerIndex))
                    UpdateAttachmentTrackedLayoutForFbo(vkFbo, target, mipLevel, layerIndex, finalLayout);
            }
        }

        private void UpdatePhysicalGroupLayoutsForFbo(
            XRFrameBuffer fbo,
            FrameBufferAttachmentSignature[] signatures,
            bool useReferenceLayouts)
        {
            if (signatures.Length == 0)
                return;

            var vkFbo = GenericToAPI<VkFrameBuffer>(fbo);
            if (vkFbo is null || vkFbo.AttachmentCount == 0)
                return;

            int attachmentCount = Math.Min((int)vkFbo.AttachmentCount, signatures.Length);
            for (int attachmentIndex = 0; attachmentIndex < attachmentCount; attachmentIndex++)
            {
                ImageLayout layout = useReferenceLayouts
                    ? signatures[attachmentIndex].ReferenceLayout
                    : signatures[attachmentIndex].FinalLayout;

                if (layout == ImageLayout.Undefined)
                    continue;

                if (vkFbo.TryGetAttachmentTarget(
                    attachmentIndex,
                    out IFrameBufferAttachement? target,
                    out _,
                    out int mipLevel,
                    out int layerIndex))
                    UpdateAttachmentTrackedLayoutForFbo(vkFbo, target, mipLevel, layerIndex, layout);
            }
        }

        private void UpdateAttachmentTrackedLayoutForFbo(
            VkFrameBuffer vkFbo,
            IFrameBufferAttachement target,
            int mipLevel,
            int layerIndex,
            ImageLayout layout)
        {
            ResolveFboAttachmentTrackedLayerSpan(
                vkFbo,
                layerIndex,
                out uint baseLayer,
                out uint layerCount);

            if (layerCount <= 1u)
            {
                UpdateAttachmentTrackedLayout(target, mipLevel, layerIndex, layout);
                return;
            }

            for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                UpdateAttachmentTrackedLayout(target, mipLevel, checked((int)(baseLayer + layerOffset)), layout);
        }

        private void UpdateAttachmentTrackedLayout(
            IFrameBufferAttachement target,
            int mipLevel,
            int layerIndex,
            ImageLayout layout)
        {
            switch (target)
            {
                case XRRenderBuffer rb:
                {
                    if (GetOrCreateAPIRenderObject(rb, true) is VkRenderBuffer vkRb && vkRb.PhysicalGroup is { } group)
                        group.LastKnownLayout = layout;
                    break;
                }
                case XRTexture tex:
                {
                    if (GetOrCreateAPIRenderObject(tex, true) is IVkFrameBufferAttachmentSource attSrc)
                        attSrc.UpdateAttachmentTrackedLayout(layout, mipLevel, layerIndex);
                    break;
                }
            }
        }

        private ImageLayout ResolveFboAttachmentOldLayout(
            IFrameBufferAttachement target,
            int mipLevel,
            int layerIndex,
            ImageLayout fallback)
        {
            ImageLayout layout = fallback;
            switch (target)
            {
                case XRRenderBuffer rb:
                {
                    if (GetOrCreateAPIRenderObject(rb, true) is VkRenderBuffer vkRb && vkRb.PhysicalGroup is { } group)
                        layout = group.LastKnownLayout;
                    break;
                }
                case XRTexture tex:
                {
                    if (GetOrCreateAPIRenderObject(tex, true) is IVkFrameBufferAttachmentSource attSrc)
                    {
                        ImageLayout tracked = attSrc.GetAttachmentTrackedLayout(mipLevel, layerIndex);
                        if (tracked != ImageLayout.Undefined)
                            layout = tracked;
                    }
                    else if (GetOrCreateAPIRenderObject(tex, true) is IVkImageDescriptorSource imgSrc)
                    {
                        ImageLayout tracked = imgSrc.TrackedImageLayout;
                        if (tracked != ImageLayout.Undefined)
                            layout = tracked;
                    }
                    break;
                }
            }

            return layout;
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
            ImageLayout[] layouts = new ImageLayout[count];

            for (int i = 0; i < count; i++)
            {
                if (!vkFbo.TryGetAttachmentTarget(
                    i,
                    out IFrameBufferAttachement? target,
                    out _,
                    out int mipLevel,
                    out int layerIndex))
                {
                    layouts[i] = ImageLayout.Undefined;
                    continue;
                }

                ImageLayout layout = ImageLayout.Undefined;
                switch (target)
                {
                    case XRRenderBuffer rb:
                    {
                        if (GetOrCreateAPIRenderObject(rb, true) is VkRenderBuffer vkRb && vkRb.PhysicalGroup is { } group)
                            layout = group.LastKnownLayout;
                        break;
                    }
                    case XRTexture tex:
                    {
                        if (GetOrCreateAPIRenderObject(tex, true) is IVkFrameBufferAttachmentSource attSrc)
                            layout = attSrc.GetAttachmentTrackedLayout(mipLevel, layerIndex);
                        else if (GetOrCreateAPIRenderObject(tex, true) is IVkImageDescriptorSource imgSrc)
                            layout = imgSrc.TrackedImageLayout;
                        break;
                    }
                }
                layouts[i] = layout;
            }

            return layouts;
        }

        /// <summary>
        /// When the barrier planner has no known passes, emit image memory barriers to
        /// transition any physical-group images still in <see cref="ImageLayout.Undefined"/>
        /// to a usable layout inside the current command buffer.  This is the in-CB
        /// counterpart of <see cref="TransitionNewPhysicalImagesToInitialLayout"/> (which
        /// runs one-shot commands outside the frame).  Both paths are necessary:
        /// the one-shot path handles newly-allocated images before recording starts,
        /// and this path covers images that became UNDEFINED due to mid-frame recreation
        /// or races with resource planner rebuilds.
        /// </summary>
        private void EmitInitialImageBarriersForUnknownPass(CommandBuffer commandBuffer)
        {
            foreach (VulkanPhysicalImageGroup group in ResourceAllocator.EnumeratePhysicalGroups())
            {
                if (!group.IsAllocated || group.LastKnownLayout != ImageLayout.Undefined)
                    continue;

                bool isDepth = VulkanResourceAllocator.IsDepthStencilFormat(group.Format);
                ImageLayout targetLayout = ResolveInitialPhysicalGroupLayout(group.Usage, isDepth);
                ImageAspectFlags aspect = isDepth
                    ? ImageAspectFlags.DepthBit | (HasStencilComponent(group.Format) ? ImageAspectFlags.StencilBit : 0)
                    : ImageAspectFlags.ColorBit;

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
                        BaseMipLevel = 0,
                        LevelCount = Math.Max(1u, group.MipLevels),
                        BaseArrayLayer = 0,
                        LayerCount = Math.Max(group.Template.Layers, 1u),
                    },
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                };

                // Narrow dst stage based on target layout instead of AllCommandsBit.
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

                CmdPipelineBarrierTracked(
                    commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    initDstStage,
                    DependencyFlags.None,
                    0, null, 0, null,
                    1, &barrier);

                group.LastKnownLayout = targetLayout;
            }
        }

        private void EmitPlannedImageBarriers(CommandBuffer commandBuffer, IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0)
                return;

            foreach (var planned in plannedBarriers)
            {
                planned.Group.EnsureAllocated(this);

                // The barrier planner pre-computes OldLayout from the logical dependency
                // graph, but dynamic rendering, blits, and resource-plan replacement can
                // change the live VkImage layout before the planned edge is emitted. Vulkan
                // validation cares about the live subresource layout, so use the physical
                // group's tracker whenever it has a concrete value.
                ImageLayout effectiveOldLayout = planned.Previous.Layout;
                ImageLayout groupLayout = planned.Group.GetKnownLayout(
                    planned.Range.BaseMipLevel,
                    planned.Range.LevelCount,
                    planned.Range.BaseArrayLayer,
                    planned.Range.LayerCount);
                if (groupLayout != ImageLayout.Undefined &&
                    groupLayout != effectiveOldLayout)
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
                            groupLayout,
                            planned.Next.Layout);
                    }
                    effectiveOldLayout = groupLayout;
                }

                ImageSubresourceRange range = new()
                {
                    AspectMask = planned.Next.AspectMask,
                    BaseMipLevel = planned.Range.BaseMipLevel,
                    LevelCount = Math.Max(1u, planned.Range.LevelCount),
                    BaseArrayLayer = planned.Range.BaseArrayLayer,
                    LayerCount = Math.Max(1u, planned.Range.LayerCount)
                };

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

                // Update the group's tracked layout so subsequent barriers and blit
                // operations use the correct OldLayout.
                planned.Group.UpdateKnownLayout(
                    planned.Next.Layout,
                    planned.Range.BaseMipLevel,
                    planned.Range.LevelCount,
                    planned.Range.BaseArrayLayer,
                    planned.Range.LayerCount);
            }
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
