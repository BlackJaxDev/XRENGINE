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
    internal sealed unsafe partial class VulkanCommandRuntime
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
            ReadOnlySpan<FrameOp> ops,
            FrameOp[] dynamicUiBatchTextOps,
            FrameOperationSequence sealedDynamicUiBatchTextOps,
            bool delayDynamicUiSecondaryRecording,
            bool preserveSwapchainForOverlay,
            bool requiresTrackedPresentSourceRefresh,
            bool swapchainImageEverPresented,
            CommandChainSchedule? preparedSchedule,
            SwapchainRecordingTarget recordingTarget,
            VulkanCommandRecordingPolicySnapshot recordingPolicy,
            out CommandBuffer commandBuffer,
            out CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer,
            out int dynamicUiBatchTextOverlayOpCount,
            out PrimaryCommandArtifactOwner? dynamicUiBatchTextOverlayVariant,
            out ImageLayout swapchainLayoutAfterCommandBuffer)
        {
            commandBuffer = default;
            dynamicUiBatchTextSecondaryCommandBuffer = default;
            dynamicUiBatchTextOverlayOpCount = 0;
            dynamicUiBatchTextOverlayVariant = null;
            swapchainLayoutAfterCommandBuffer = ImageLayout.PresentSrcKhr;
            CommandBufferRecordingScratch frameDataScratch =
                _commandBufferRecordingScratch.Value!;
            using VulkanCpuStageScope commandBufferReuseStage = new(_frameTelemetry, EVulkanCpuStage.CommandBufferReuse);

            if (!CommandChainsEnabledForCurrentRecording ||
                _primaryCommandArtifactOwners is null ||
                imageIndex >= _primaryCommandArtifactOwners.Length)
            {
                return false;
            }

            if (preparedSchedule is not { } cachedSchedule)
            {
                TraceCommandChainPrimaryReuseRejection(
                    imageIndex,
                    "MissingPreparedSchedule");
                return false;
            }

            PrimaryCommandArtifactOwner variant = _primaryCommandArtifactOwners[imageIndex];
            if (variant.Dirty)
            {
                TraceCommandChainPrimaryReuseRejection(
                    imageIndex,
                    "VariantDirty",
                    variant.DirtyReason);
                return false;
            }

            // The scheduler's cache identity covers the sealed operation streams,
            // resource/descriptor versions, planner revision, and native output
            // target. The artifact authority clock advances inside every secondary
            // allocation, record, executable publication, invalidation, and
            // retirement. Together these two immutable publications replace the
            // former O(chains + operations + reflected bindings) validation walk.
            if (variant.RecordedCommandChainScheduleCacheIdentity !=
                cachedSchedule.CacheIdentity)
            {
                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    CommandChainScheduleCacheIdentity recordedIdentity =
                        variant.RecordedCommandChainScheduleCacheIdentity;
                    CommandChainScheduleCacheIdentity currentIdentity =
                        cachedSchedule.CacheIdentity;
                    TraceCommandChainPrimaryScheduleIdentityRejection(
                        imageIndex,
                        in recordedIdentity,
                        in currentIdentity);
                }
                if (variant.FrameOpsSignature != frameOpsSignature)
                {
                    LogFrameOpSignatureDiff(
                        imageIndex,
                        variant,
                        frameOpsSignature,
                        ops);
                }
                return false;
            }

            long artifactMutationGeneration =
                CommandChains.SnapshotArtifactMutationGeneration();
            if (variant.RecordedCommandChainArtifactMutationGeneration !=
                    artifactMutationGeneration ||
                cachedSchedule.ArtifactMutationGeneration !=
                    artifactMutationGeneration)
            {
                TraceCommandChainPrimaryReuseRejection(
                    imageIndex,
                    "SecondaryArtifactGeneration");
                return false;
            }

            if (variant.FrameOpsSignature != frameOpsSignature ||
                variant.PlannerRevision != plannerRevision ||
                variant.RecordedFrameOpContextFingerprint !=
                    frameOpContextFingerprint)
            {
                TraceCommandChainPrimaryReuseRejection(
                    imageIndex,
                    "PrimaryPublicationIdentity");
                return false;
            }

            VulkanCommandIdentityComponents currentPrimaryIdentityComponents =
                variant.CommandChainPrimaryIdentityComponents;
            ulong currentPrimaryGroupSignature =
                variant.CommandChainPrimaryGroupSignature;
            int currentPrimaryGroupCount =
                variant.CommandChainPrimaryGroupCount;
            ulong currentPrimarySkeletonSignature =
                variant.CommandChainPrimarySkeletonSignature;
            // The equality check above proves this exact sealed resource and
            // descriptor publication is the one baked into the cached primary.
            // That proof applies to inline post-process draws as well as mesh
            // secondaries; a physical frame-source replacement changes the
            // resource/descriptor versions and rejects reuse before this point.
            bool descriptorResourcesCapturedByFrameSignature =
                cachedSchedule.CacheIdentity.IsReusable;
            CommandRecordingDependencySignature currentPrimaryDependencySignature =
                variant.RecordedDependencySignature;
            Dictionary<CommandChainKey, CommandChain> commandChainCache =
                GetCommandChainCache(imageIndex);
            VulkanReusableFrameDataRefreshBatchInfo primaryBatchInfo =
                frameDataScratch.PrimaryReusableFrameDataRefreshBatchInfo;
            bool primaryOwnerOnlyRefresh =
                (primaryBatchInfo.MeshRequestCount > 0 &&
                 primaryBatchInfo.SupportsDirectOwnerOnlyRefresh) ||
                variant.PrimaryFrameDataRefreshState.CanUseOwnerOnlyRefresh(
                    primaryBatchInfo);
            ReadOnlySpan<CommandChainKey> scheduledCommandChainKeys =
                primaryOwnerOnlyRefresh
                    ? ReadOnlySpan<CommandChainKey>.Empty
                    : PrepareReusableCommandChainKeysByOpIndex(
                        cachedSchedule,
                        commandChainCache,
                        ops.Length,
                        frameDataScratch);

            bool hasDynamicUiBatchTextOverlay = dynamicUiBatchTextOpCount > 0;
                if (variant.CommandChainScheduleSignature !=
                    cachedSchedule.StructuralSignature)
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "ScheduleSignature");
                    return false;
                }
                if (IsCommandBufferVariantImageLayoutStateDirty(
                        variant,
                        imageLayoutStartSignature))
                {
                    string? imageStateDetail = null;
                    if (VulkanFrameDiagnosticsTraceEnabled &&
                        TryGetRecordedImageEntryStateMismatch(
                            variant.PrimaryCommandBuffer,
                            out VulkanImageEntryStateMismatch imageStateMismatch))
                    {
                        imageStateDetail = DescribePrimaryImageEntryStateMismatch(
                            imageStateMismatch,
                            variant.RecordedImageLayoutStartSignature,
                            imageLayoutStartSignature);
                    }
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "ImageLayoutEntryState",
                        imageStateDetail);
                    return false;
                }
                if (variant.PreserveSwapchainForOverlay != preserveSwapchainForOverlay)
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "PreserveSwapchainForOverlay");
                    return false;
                }
                if (requiresTrackedPresentSourceRefresh &&
                    !variant.RecordedSwapchainRefreshFromLastPresentSource)
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "PresentSourceRefresh");
                    return false;
                }
                if (variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented)
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "SwapchainPresentationHistory");
                    return false;
                }
                if ((variant.DynamicUiOpCount > 0) != hasDynamicUiBatchTextOverlay)
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "DynamicUiPresence");
                    return false;
                }
                if (!delayDynamicUiSecondaryRecording &&
                    IsDynamicUiBatchTextSecondaryDirty(
                        variant,
                        dynamicUiBatchTextSignature))
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "DynamicUiSecondary");
                    return false;
                }
                if (IsCommandBufferVariantGpuProfilerStateDirty(
                        variant,
                        gpuPipelineProfilingActive,
                        commandBufferImageSlot))
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "GpuProfilerState");
                    return false;
                }

                bool refreshedReusableFrameData;
                bool dynamicUiFrameDataNeedsRerecord = false;
                using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.FrameDataRefresh))
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
                                descriptorResourcesCapturedByFrameSignature,
                                commandChainCache: commandChainCache,
                                scheduledCommandChainKeys:
                                    scheduledCommandChainKeys);
                    if (refreshedReusableFrameData && dynamicUiBatchTextOps.Length > 0)
                    {
                        dynamicUiFrameDataNeedsRerecord =
                            !TryRefreshReusableCommandBufferFrameData(
                                imageIndex,
                                frameDataScratch
                                    .DynamicUiReusableFrameDataRefreshRequests,
                                frameDataScratch
                                    .DynamicUiReusableFrameDataOwnerWorkRequests,
                                frameDataScratch
                                    .DynamicUiReusableFrameDataRefreshBatchInfo,
                                    variant.DynamicUiFrameDataRefreshState,
                                    dynamicUi: true,
                                    commandChainCache: null,
                                    scheduledCommandChainKeys: ReadOnlySpan<CommandChainKey>.Empty);

                        // Dynamic batched text is recorded into a dedicated
                        // secondary. A descriptor-set pool miss prevents an
                        // in-place refresh of that secondary, but it does not
                        // invalidate the scene primary that only executes its
                        // stable command-buffer handle. Re-record the isolated
                        // text secondary below instead of rebuilding every scene
                        // and shadow render scope.
                        if (dynamicUiFrameDataNeedsRerecord)
                            _lastReusableFrameDataRefreshFailureReason = null;
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
                    using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.SecondaryRecording))
                        dynamicUiSecondaryReady = RecordDynamicUiBatchTextSecondaryCommandBuffer(
                            imageIndex,
                            variant,
                            sealedDynamicUiBatchTextOps,
                            dynamicUiBatchTextSignature,
                            forceRecord: dynamicUiFrameDataNeedsRerecord,
                            recordingTarget: recordingTarget,
                            policy: recordingPolicy);
                }
                else if (dynamicUiBatchTextOpCount > 0)
                {
                    ReleaseDeferredSecondaryCommandBuffers(imageIndex);
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                               "Vulkan.RecordCommandBuffer.FastReuse.RecordDeferredDynamicUiSecondary"))
                    using (VulkanCpuStageScope cpuStage =
                           new(_frameTelemetry, EVulkanCpuStage.SecondaryRecording))
                    {
                        dynamicUiSecondaryReady = RecordDynamicUiBatchTextSecondaryCommandBuffer(
                            imageIndex,
                            variant,
                            sealedDynamicUiBatchTextOps,
                            dynamicUiBatchTextSignature,
                            forceRecord: dynamicUiFrameDataNeedsRerecord,
                            includeDepthAttachment: false,
                            recordingTarget: recordingTarget,
                            policy: recordingPolicy);
                    }
                }

                if (dynamicUiBatchTextOpCount > 0 && !dynamicUiSecondaryReady)
                {
                    return false;
                }

                if (CommandChains.SnapshotArtifactMutationGeneration() !=
                    artifactMutationGeneration)
                {
                    TraceCommandChainPrimaryReuseRejection(
                        imageIndex,
                        "ConcurrentSecondaryArtifactMutation");
                    return false;
                }

                variant.DynamicUiSignature = dynamicUiSecondaryReady
                    ? dynamicUiBatchTextSignature
                    : 0;
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
                // Preserve the same inline-only binding scope used by the primary
                // comparison. Publishing the aggregate draw signature here poisoned
                // the clean variant after one reuse and made the next camera frame
                // look structurally dirty again.
                variant.RecordedDependencySignature = currentPrimaryDependencySignature;
                variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
                variant.RecordedFrameOpContextId = frameOpContextId;
                variant.LastUsedFrameId = VulkanFrameCounter;
                variant.DirtyReason = null;
                StoreFrameOpSignatureDebugParts(variant, ops);
                SetActivePrimaryCommandArtifactOwner(imageIndex, variant);
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

        private void TraceCommandChainPrimaryReuseRejection(
            uint imageIndex,
            string reason,
            string? detail = null)
        {
            if (!VulkanFrameDiagnosticsTraceEnabled)
                return;

            Debug.VulkanEvery(
                $"Vulkan.PrimaryReuse.Rejection.{GetHashCode()}.{imageIndex}.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Cached primary rejected. Image={0} Reason={1} Detail={2}",
                imageIndex,
                reason,
                detail ?? "<none>");
        }

        private void TraceCommandChainPrimaryScheduleIdentityRejection(
            uint imageIndex,
            in CommandChainScheduleCacheIdentity recorded,
            in CommandChainScheduleCacheIdentity current)
        {
            if (!VulkanFrameDiagnosticsTraceEnabled)
                return;

            TraceCommandChainPrimaryReuseRejection(
                imageIndex,
                "ScheduleCacheIdentity",
                recorded.DescribeFirstMismatch(in current));
        }

        internal bool TryRefreshReusableCommandBufferFrameData(
            uint imageIndex,
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                ownerWorkRequests,
            in VulkanReusableFrameDataRefreshBatchInfo batchInfo,
            VulkanReusableFrameDataRefreshState refreshState,
            bool dynamicUi,
            bool descriptorResourcesCapturedByFrameSignature = false,
            bool refreshMaterialUniforms = true,
            IReadOnlyDictionary<CommandChainKey, CommandChain>? commandChainCache = null,
            ReadOnlySpan<CommandChainKey> scheduledCommandChainKeys = default)
        {
            if (requests.IsEmpty)
                return true;

            bool directOwnerOnlyRefresh =
                batchInfo.MeshRequestCount > 0 &&
                batchInfo.SupportsDirectOwnerOnlyRefresh;
            bool cachedOwnerOnlyRefresh =
                refreshState.CanUseOwnerOnlyRefresh(batchInfo);
            bool ownerOnlyRefresh =
                directOwnerOnlyRefresh || cachedOwnerOnlyRefresh;
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
                                CommandBuffer recordedSecondaryCommandBuffer =
                                    ResolveRecordedSecondaryCommandBuffer(
                                        request,
                                        commandChainCache,
                                        scheduledCommandChainKeys);
                                if (!meshRenderer.TryRefreshReusableCommandBufferFrameData(
                                        imageIndex,
                                        request.Draw,
                                        request.DrawUniformSlot,
                                        out string reason,
                                        refreshMaterialUniforms,
                                        descriptorResourcesCapturedByFrameSignature,
                                        recordedSecondaryCommandBuffer))
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
                                CommandBuffer recordedSecondaryCommandBuffer =
                                    ResolveRecordedSecondaryCommandBuffer(
                                        request,
                                        commandChainCache,
                                        scheduledCommandChainKeys);
                                bool refreshed =
                                    meshRenderer.TryRefreshReusableCommandBufferFrameData(
                                        imageIndex,
                                        request.Draw,
                                        request.DrawUniformSlot,
                                        out string reason,
                                        refreshMaterialUniforms,
                                        descriptorResourcesCapturedByFrameSignature,
                                        recordedSecondaryCommandBuffer);
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
                                if (refreshState.IsOwnerGenerationPublished(
                                        imageIndex,
                                        request.OwnerKey))
                                {
                                    break;
                                }

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
                                refreshState.PublishOwnerGeneration(
                                    imageIndex,
                                    request.OwnerKey);
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

            if (cachedOwnerOnlyRefresh && !directOwnerOnlyRefresh)
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
                    if (descriptorResourcesCapturedByFrameSignature &&
                        request.Kind is
                            (EVulkanReusableFrameDataRefreshKind.Mesh or
                             EVulkanReusableFrameDataRefreshKind.IndirectMesh) &&
                        request.MeshRenderer is { } planOwnedMeshRenderer &&
                        planOwnedMeshRenderer
                            .SupportsOwnerOnlyReusableFrameDataRefresh(
                                request.Draw,
                                allowPlanOwnedFrameSourceSamplers: true))
                    {
                        // Cached secondary authority includes the exact frame
                        // resource plan and descriptor publication identities.
                        // A resize/replan invalidates that authority, so stable
                        // post-process sources do not need a second per-draw
                        // descriptor fingerprint walk on every reuse frame.
                        continue;
                    }

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
            else if (directOwnerOnlyRefresh)
                refreshState.CommitDirectOwnerOnlyRefresh(batchInfo);

            return true;
        }

        private static CommandBuffer ResolveRecordedSecondaryCommandBuffer(
            in VulkanReusableFrameDataRefreshRequest request,
            IReadOnlyDictionary<CommandChainKey, CommandChain>? commandChainCache,
            ReadOnlySpan<CommandChainKey> scheduledCommandChainKeys)
        {
            if (commandChainCache is null ||
                (uint)request.SourceOpIndex >= (uint)scheduledCommandChainKeys.Length)
            {
                return default;
            }

            CommandChainKey key = scheduledCommandChainKeys[request.SourceOpIndex];
            return key.ChainOrdinal != -1 &&
                   commandChainCache.TryGetValue(key, out CommandChain? chain) &&
                   chain.SecondaryCommandBufferExecutable
                ? chain.SecondaryCommandBuffer
                : default;
        }

        private static ReadOnlySpan<CommandChainKey>
            PrepareReusableCommandChainKeysByOpIndex(
                CommandChainSchedule schedule,
                IReadOnlyDictionary<CommandChainKey, CommandChain> commandChainCache,
                int operationCount,
                CommandBufferRecordingScratch scratch)
        {
            if (operationCount <= 0)
                return ReadOnlySpan<CommandChainKey>.Empty;

            if (scratch.ScheduledCommandChainKeysByOpIndex.Length < operationCount)
            {
                int capacity = Math.Max(
                    operationCount,
                    Math.Max(
                        scratch.ScheduledCommandChainKeysByOpIndex.Length * 2,
                        16));
                scratch.ScheduledCommandChainKeysByOpIndex =
                    new CommandChainKey[capacity];
            }

            Span<CommandChainKey> keys =
                scratch.ScheduledCommandChainKeysByOpIndex.AsSpan(0, operationCount);
            PopulateCommandChainKeysByFrameOpIndex(
                schedule,
                commandChainCache,
                keys,
                operationCount);
            return keys;
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
            PrimaryCommandArtifactOwner variant,
            ulong dynamicUiBatchTextSignature)
            => variant.DynamicUiSignature != dynamicUiBatchTextSignature ||
               (dynamicUiBatchTextSignature != 0 && !variant.DynamicUiSecondaryRecorded);

        private static bool IsDynamicUiBatchTextPrimaryStructureDirty(
            PrimaryCommandArtifactOwner variant,
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
