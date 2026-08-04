using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
            out ImageLayout swapchainLayoutAfterCommandBuffer,
            out ulong preparedFastScheduleSignature,
            out bool hasPreparedFastScheduleSignature)
        {
            commandBuffer = default;
            dynamicUiBatchTextSecondaryCommandBuffer = default;
            dynamicUiBatchTextOverlayOpCount = 0;
            dynamicUiBatchTextOverlayVariant = null;
            swapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;
            preparedFastScheduleSignature = 0;
            hasPreparedFastScheduleSignature = false;
            CommandBufferRecordingScratch frameDataScratch =
                _commandBufferRecordingScratch.Value!;
            using VulkanCpuStageScope commandBufferReuseStage = new(EVulkanCpuStage.CommandBufferReuse);

            if (!CommandChainsEnabledForCurrentRecording ||
                _commandBufferVariants is null ||
                imageIndex >= _commandBufferVariants.Length)
            {
                return false;
            }

            FrameOp[] scheduledDynamicUiBatchTextOps = preserveSwapchainForOverlay
                ? Array.Empty<FrameOp>()
                : dynamicUiBatchTextOps;
            ulong fastScheduleSignature;
            using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.CommandChainFastSignature))
            {
                fastScheduleSignature = ComputeCommandChainFastScheduleSignature(
                    imageIndex,
                    ops,
                    scheduledDynamicUiBatchTextOps,
                    plannerRevision);
            }
            hasPreparedFastScheduleSignature = true;
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
            if (!TryValidatePrimaryCommandBufferGroupSharedDependencies(
                    cachedSchedule,
                    commandChainCache,
                    out _))
            {
                return false;
            }

            VulkanCommandIdentityComponents currentPrimaryIdentityComponents =
                ComputePrimaryCommandBufferGroupIdentity(
                    cachedSchedule,
                    commandChainCache);
            ulong currentPrimaryGroupSignature =
                currentPrimaryIdentityComponents.Combined;
            int currentPrimaryGroupCount = cachedSchedule.Groups.Length;
            ulong currentPrimarySkeletonSignature = ComputeCommandChainPrimarySkeletonSignature(ops);
            bool allCommandChainGroupsUseSecondaryBuffers =
                UsesOnlySecondaryCommandBufferGroups(cachedSchedule);

            List<CommandBufferCacheVariant> variants = _commandBufferVariants[imageIndex];
            bool hasDynamicUiBatchTextOverlay = dynamicUiBatchTextOpCount > 0;
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                CommandRecordingDependencyMismatch dependencyMismatch =
                    variant.RecordedDependencySignature.CompareCommandChainPrimary(
                        currentDependencySignature,
                        allCommandChainGroupsUseSecondaryBuffers);
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
                    variant.CommandChainPrimaryGroupSignature != currentPrimaryGroupSignature ||
                    variant.CommandChainPrimarySkeletonSignature != currentPrimarySkeletonSignature ||
                    variant.CommandChainPrimaryGroupCount != currentPrimaryGroupCount ||
                    // Query brackets stay inline and are deliberately omitted from the
                    // command-chain schedule. They therefore need their own primary-cache
                    // identity; otherwise a primary recorded for query A can be replayed
                    // while the current frame refreshes proxy data for query B.
                    variant.RecordedGenerations.Query != currentGenerations.Query ||
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
                        TryRefreshReusableCommandBufferFrameData(
                            imageIndex,
                            frameDataScratch
                                .PrimaryReusableFrameDataRefreshRequests,
                            frameDataScratch
                                .PrimaryReusableFrameDataOwnerWorkRequests,
                            frameDataScratch
                                .PrimaryReusableFrameDataRefreshBatchInfo,
                            variant.PrimaryFrameDataRefreshState,
                            dynamicUi: false,
                            descriptorResourcesCapturedByFrameSignature:
                                allCommandChainGroupsUseSecondaryBuffers);
                    if (refreshedReusableFrameData && dynamicUiBatchTextOps.Length > 0)
                    {
                        refreshedReusableFrameData =
                            TryRefreshReusableCommandBufferFrameData(
                                imageIndex,
                                frameDataScratch
                                    .DynamicUiReusableFrameDataRefreshRequests,
                                frameDataScratch
                                    .DynamicUiReusableFrameDataOwnerWorkRequests,
                                frameDataScratch
                                    .DynamicUiReusableFrameDataRefreshBatchInfo,
                                variant.DynamicUiFrameDataRefreshState,
                                dynamicUi: true);
                    }
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
                variant.FrameOpsSignature = frameOpsSignature;
                variant.CommandChainScheduleSignature = cachedSchedule.StructuralSignature;
                variant.CommandChainPrimaryGroupSignature = currentPrimaryGroupSignature;
                variant.CommandChainPrimaryIdentityComponents =
                    currentPrimaryIdentityComponents;
                variant.CommandChainPrimarySkeletonSignature = currentPrimarySkeletonSignature;
                variant.CommandChainPrimaryGroupCount = currentPrimaryGroupCount;
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
                PrepareSubmissionMarkersForCommandBufferReuse(
                    commandBuffer,
                    ops,
                    dynamicUiBatchTextOps);
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
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                ownerWorkRequests,
            in VulkanReusableFrameDataRefreshBatchInfo batchInfo,
            VulkanReusableFrameDataRefreshState refreshState,
            bool dynamicUi,
            bool descriptorResourcesCapturedByFrameSignature = false,
            bool refreshMaterialUniforms = true)
        {
            if (requests.IsEmpty)
                return true;

            bool ownerOnlyRefresh =
                refreshState.CanUseOwnerOnlyRefresh(batchInfo);
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                activeRequests =
                    ownerOnlyRefresh
                        ? ownerWorkRequests
                        : requests;
            if (!ownerOnlyRefresh)
                refreshState.BeginFullRefresh(batchInfo);
            long descriptorSetContentUpdateGeneration =
                SnapshotDescriptorSetContentUpdateGeneration();

            int packetStart = 0;
            while (packetStart < activeRequests.Length)
            {
                ref readonly VulkanReusableFrameDataRefreshRequest packetRequest =
                    ref activeRequests[packetStart];
                FrameOpContext packetContext = packetRequest.Context;
                VulkanFrameOpPlannerStateKey packetPlannerKey =
                    packetRequest.PlannerKey;
                int packetEnd = packetStart + 1;
                while (packetEnd < activeRequests.Length &&
                       activeRequests[packetEnd].PlannerKey == packetPlannerKey)
                {
                    packetEnd++;
                }

                // Resource-planner state is packet state, not draw state. Keep one
                // readback scope for a contiguous compatible range so warmed primary
                // reuse does not serialize a full planner save/restore around every op.
                using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(packetContext);
                for (int i = packetStart; i < packetEnd; i++)
                {
                    ref readonly VulkanReusableFrameDataRefreshRequest request =
                        ref activeRequests[i];
                    switch (request.Kind)
                    {
                        case EVulkanReusableFrameDataRefreshKind.Mesh:
                            {
                                RuntimeEngine.Rendering.Stats.Vulkan
                                    .RecordVulkanPreparedFrameDataDrawVisited(
                                        dynamicUi);
                                VkMeshRenderer meshRenderer =
                                    request.MeshRenderer!;
                                if (!meshRenderer.TryRefreshReusableCommandBufferFrameData(
                                        imageIndex,
                                        request.Draw,
                                        request.DrawUniformSlot,
                                        out string reason,
                                        refreshMaterialUniforms,
                                        descriptorResourcesCapturedByFrameSignature))
                                {
                                    _lastReusableFrameDataRefreshFailureReason =
                                        $"mesh op={request.SourceOpIndex}/{request.SourceOpCount} mesh='{meshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(request.Draw.MaterialOverride ?? meshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' slot={request.DrawUniformSlot}: {reason}";
                                    if (FrameDataReuseDiagnosticsEnabled)
                                    {
                                        Debug.VulkanEvery(
                                            $"Vulkan.FrameDataReuse.Mesh.{GetHashCode()}",
                                            TimeSpan.FromSeconds(1),
                                            "[Vulkan] Reusable command-buffer frame-data refresh failed image={0} op={1}/{2} mesh='{3}' material='{4}' drawSlot={5}: {6}",
                                            imageIndex,
                                            request.SourceOpIndex,
                                            request.SourceOpCount,
                                            meshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                                            (request.Draw.MaterialOverride ?? meshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                                            request.DrawUniformSlot,
                                            reason);
                                    }
                                    refreshState.Invalidate();
                                    return false;
                                }
                                if (!meshRenderer
                                        .SupportsOwnerOnlyReusableFrameDataRefresh(
                                            request.Draw))
                                {
                                    refreshState.AddFallbackRequestIndex(i);
                                }
                                LogOwnerOnlyRefreshBlocker(
                                    meshRenderer,
                                    request);
                                break;
                            }
                        case EVulkanReusableFrameDataRefreshKind.IndirectMesh:
                            {
                                RuntimeEngine.Rendering.Stats.Vulkan
                                    .RecordVulkanPreparedFrameDataDrawVisited(
                                        dynamicUi);
                                VkMeshRenderer meshRenderer =
                                    request.MeshRenderer!;
                                bool refreshed =
                                    meshRenderer.TryRefreshReusableCommandBufferFrameData(
                                        imageIndex,
                                        request.Draw,
                                        request.DrawUniformSlot,
                                        out string reason,
                                        refreshMaterialUniforms,
                                        descriptorResourcesCapturedByFrameSignature);
                                if (!refreshed)
                                {
                                    _lastReusableFrameDataRefreshFailureReason =
                                        $"indirect op={request.SourceOpIndex}/{request.SourceOpCount} mesh='{meshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(request.Draw.MaterialOverride ?? meshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' slot={request.DrawUniformSlot}: {reason}";
                                    refreshState.Invalidate();
                                    return false;
                                }
                                if (!meshRenderer
                                        .SupportsOwnerOnlyReusableFrameDataRefresh(
                                            request.Draw))
                                {
                                    refreshState.AddFallbackRequestIndex(i);
                                }
                                LogOwnerOnlyRefreshBlocker(
                                    meshRenderer,
                                    request);
                                break;
                            }
                        case EVulkanReusableFrameDataRefreshKind
                            .FrequencyOwnerMesh:
                            {
                                VkMeshRenderer meshRenderer =
                                    request.MeshRenderer!;
                                if (!meshRenderer
                                        .TryRefreshReusableFrequencyData(
                                            imageIndex,
                                            request.Draw,
                                            request.DrawUniformSlot,
                                            request.FrequencyMask,
                                            out string reason))
                                {
                                    _lastReusableFrameDataRefreshFailureReason =
                                        $"frame owner op={request.SourceOpIndex}/{request.SourceOpCount} mesh='{meshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' slot={request.DrawUniformSlot}: {reason}";
                                    refreshState.Invalidate();
                                    return false;
                                }
                                break;
                            }
                        case EVulkanReusableFrameDataRefreshKind.Compute:
                            {
                                VkRenderProgram computeProgram =
                                    request.ComputeProgram!;
                                if (!computeProgram.TryRefreshReusableComputeDispatchFrameData(
                                        imageIndex,
                                        request.ComputeSnapshot!,
                                        request.ComputeDescriptorKey))
                                {
                                    _lastReusableFrameDataRefreshFailureReason =
                                        $"compute op={request.SourceOpIndex}/{request.SourceOpCount} program='{computeProgram.Data?.Name ?? "<unnamed program>"}'";
                                    if (FrameDataReuseDiagnosticsEnabled)
                                    {
                                        Debug.VulkanEvery(
                                            $"Vulkan.FrameDataReuse.Compute.{GetHashCode()}",
                                            TimeSpan.FromSeconds(1),
                                            "[Vulkan] Reusable command-buffer compute frame-data refresh failed image={0} op={1}/{2} program='{3}' groups={4}x{5}x{6}.",
                                            imageIndex,
                                            request.SourceOpIndex,
                                            request.SourceOpCount,
                                            computeProgram.Data?.Name ?? "<unnamed program>",
                                            request.ComputeGroupsX,
                                            request.ComputeGroupsY,
                                            request.ComputeGroupsZ);
                                    }
                                    refreshState.Invalidate();
                                    return false;
                                }
                                break;
                            }
                    }
                }

                packetStart = packetEnd;
            }

            if (ownerOnlyRefresh)
            {
                ReadOnlySpan<int> fallbackRequestIndices =
                    refreshState.FallbackRequestIndices;
                for (int fallbackIndex = 0;
                     fallbackIndex < fallbackRequestIndices.Length;
                     fallbackIndex++)
                {
                    int requestIndex = fallbackRequestIndices[fallbackIndex];
                    if ((uint)requestIndex >= (uint)requests.Length)
                    {
                        _lastReusableFrameDataRefreshFailureReason =
                            $"owner fallback request index {requestIndex} is outside the prepared request range {requests.Length}";
                        refreshState.Invalidate();
                        return false;
                    }

                    ref readonly VulkanReusableFrameDataRefreshRequest request =
                        ref requests[requestIndex];
                    using var plannerScope =
                        EnterFrameOpResourcePlannerReadbackScope(
                            request.Context);
                    if (!TryRefreshReusableFallbackMeshRequest(
                            imageIndex,
                            request,
                            dynamicUi,
                            refreshMaterialUniforms,
                            descriptorResourcesCapturedByFrameSignature))
                    {
                        refreshState.Invalidate();
                        return false;
                    }
                }
            }

            if (HaveDescriptorSetContentsUpdatedSince(descriptorSetContentUpdateGeneration))
            {
                _lastReusableFrameDataRefreshFailureReason =
                    "descriptor contents changed without UPDATE_AFTER_BIND; command recording is required";
                refreshState.Invalidate();
                return false;
            }

            if (!ownerOnlyRefresh)
                refreshState.CommitFullRefresh();

            return true;
        }

        private bool TryRefreshReusableFallbackMeshRequest(
            uint imageIndex,
            in VulkanReusableFrameDataRefreshRequest request,
            bool dynamicUi,
            bool refreshMaterialUniforms,
            bool descriptorResourcesCapturedByFrameSignature)
        {
            if (request.Kind is not
                (EVulkanReusableFrameDataRefreshKind.Mesh or
                 EVulkanReusableFrameDataRefreshKind.IndirectMesh) ||
                request.MeshRenderer is not { } meshRenderer)
            {
                _lastReusableFrameDataRefreshFailureReason =
                    $"owner fallback request {request.SourceOpIndex}/{request.SourceOpCount} is not a mesh draw";
                return false;
            }

            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanPreparedFrameDataDrawVisited(dynamicUi);
            if (meshRenderer.TryRefreshReusableCommandBufferFrameData(
                    imageIndex,
                    request.Draw,
                    request.DrawUniformSlot,
                    out string reason,
                    refreshMaterialUniforms,
                    descriptorResourcesCapturedByFrameSignature))
            {
                return true;
            }

            _lastReusableFrameDataRefreshFailureReason =
                $"owner fallback op={request.SourceOpIndex}/{request.SourceOpCount} mesh='{meshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(request.Draw.MaterialOverride ?? meshRenderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' slot={request.DrawUniformSlot}: {reason}";
            return false;
        }

        private void LogOwnerOnlyRefreshBlocker(
            VkMeshRenderer meshRenderer,
            in VulkanReusableFrameDataRefreshRequest request)
        {
            if (!FrameDataReuseDiagnosticsEnabled ||
                meshRenderer.SupportsOwnerOnlyReusableFrameDataRefresh(
                    request.Draw))
            {
                return;
            }

            Debug.VulkanEvery(
                $"Vulkan.FrameDataReuse.OwnerOnly.{GetHashCode()}.{meshRenderer.BindingId}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Owner-only reusable frame-data refresh rejected mesh='{0}' material='{1}' op={2}/{3}: {4}.",
                meshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                (request.Draw.MaterialOverride ??
                    meshRenderer.MeshRenderer.Material)?.Name ??
                    "<unnamed material>",
                request.SourceOpIndex,
                request.SourceOpCount,
                meshRenderer
                    .DescribeOwnerOnlyReusableFrameDataRefreshBlocker(
                        request.Draw) ??
                    "unknown");
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

        /// <summary>
        /// Resolves every operation's render-graph pass before the typed plan is
        /// hashed. Recording then consumes the same published pass index, so an
        /// inherited sentinel cannot make barrier or queue-ownership actions
        /// differ between planning and native emission.
        /// </summary>
    }
}
