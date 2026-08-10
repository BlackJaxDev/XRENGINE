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
        private static void CapturePrimaryCommandBufferRecordingContext(
            scoped in VulkanCommandRecordingContext context,
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            recordingState.ImageIndex = context.ImageIndex;
            recordingState.CommandBuffer = context.CommandBuffer;
            recordingState.DynamicUiBatchTextSecondaryCommandBuffer = context.DynamicUiSecondaryCommandBuffer;
            recordingState.Ops = context.Operations;
            recordingState.DynamicUiBatchTextOpCount = context.DynamicUiOperationCount;
            recordingState.CommandChainSchedule = context.CommandChainSchedule;
            recordingState.PreserveSwapchainForOverlay = context.PreserveSwapchainForOverlay;
            recordingState.TransitionSwapchainToPresent = context.TransitionSwapchainToPresent;
            recordingState.FrameDataImageIndexOverride = context.FrameDataImageIndexOverride;
            recordingState.OpenXrTargetContext = context.OpenXrTargetContext;
            recordingState.ExcludeDesktopSwapchainBarriers = context.ExcludeDesktopSwapchainBarriers;
            recordingState.PrimaryCommandPlan = context.PrimaryCommandPlan;
            recordingState.FramePlan = context.FramePlan;
            recordingState.SwapchainTarget = context.RecordingTarget;
            recordingState.PresentationSource = context.PresentationSource;
            recordingState.Policy = context.Policy;
            recordingState.ResourcePlanStamp = context.ResourcePlanStamp;
            recordingState.RenderGraphPlan = context.RenderGraphPlan;
            recordingState.ClearState = context.ClearState;
        }

        private void InitializePrimaryCommandBufferRecordingState(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            recordingState.RecordedSwapchainWriteCount = 0;
            recordingState.RecordedSwapchainFinalLayout = ImageLayout.Undefined;
            recordingState.RecordingDeferredReason = string.Empty;
            recordingState.FrameOpsRequireRerecord = false;
            recordingState.FrameOpsRequireRerecordLocal = false;
            recordingState.Metrics.DroppedDrawOps = 0;
            recordingState.Metrics.DroppedComputeOps = 0;
            recordingState.Metrics.DroppedFrameOps = 0;
            recordingState.Metrics.FirstFailure = null;
            recordingState.FrameDataImageIndex = recordingState.FrameDataImageIndexOverride ?? recordingState.ImageIndex;
            recordingState.CommandBufferImageSlot = unchecked((int)Math.Min(recordingState.FrameDataImageIndex, int.MaxValue));
            // The strict-SPS mirror recorder targets an engine-owned layered FBO
            // and intentionally has no OpenXR image target context. Do not let its
            // frame-data index alias desktop swapchain image 0. Direct per-eye XR
            // recording supplies an explicit target context and remains valid.
            recordingState.SwapchainRecordExtent = recordingState.SwapchainTarget.Extent;
            recordingState.ImageWasEverPresentedAtRecordStart = recordingState.SwapchainTarget.ImageEverPresentedAtRecordStart;
            recordingState.InitialSwapchainColorLayout = recordingState.SwapchainTarget.IsValid
                ? recordingState.SwapchainTarget.InitialColorLayout
                : ImageLayout.Undefined;
            recordingState.RecordingScratch = _commandBufferRecordingScratch.Value!;
            recordingState.SecondaryBuckets =
                recordingState.RecordingScratch.SecondaryRecordingBuckets;
            recordingState.SecondaryBucketByStart = null;
            recordingState.ScheduledCommandChainKeysByOpIndex = null;
            recordingState.ScheduledCommandChainCache = null;
            recordingState.MeshSecondaryFallbackEndIndex = 0;
            // Schedule before resource prewarm so clean secondary chains can
            // reuse their already-compiled graphics pipelines. It also ensures
            // the primary plan is built from the final sorted operation order.
            PreparePrimaryOperationSchedule(ref recordingState);
            ValidatePrimaryPlanPassIndicesForRecording(recordingState.Ops);
            if (recordingState.PrimaryCommandPlan.OperationCount != recordingState.Ops.Length)
            {
                throw new VulkanPlanPreconditionException(
                    $"frame-plan precondition failed: frozen primary plan operation count {recordingState.PrimaryCommandPlan.OperationCount} does not match sealed frame plan operation count {recordingState.Ops.Length}");
            }
            recordingState.MeshDrawUniformSlotsByOpIndex = recordingState.RecordingScratch.PreparePrimaryMeshDrawUniformSlots(recordingState.Ops.Length);
            recordingState.ScheduledCommandChainFrameDataRefreshedByOpIndex =
                recordingState.RecordingScratch
                    .PreparePrimaryScheduledCommandChainFrameDataRefreshFlags(
                        recordingState.Ops.Length);
            recordingState.ExecutedCommandChainSecondaryHandles = recordingState.RecordingScratch.ExecutedCommandChainSecondaryHandles;
            recordingState.ExecutedCommandChainSecondaryHandles.Clear();
            recordingState.ExecutedCommandChainSecondaryArtifactSequence =
                recordingState.RecordingScratch.ExecutedCommandChainSecondaryArtifactSequence;
            recordingState.ExecutedCommandChainSecondaryArtifactSequence.Clear();
        }

        private bool TryPreparePrimaryFrameData(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            out VulkanMeshFrameDataReservationManifest frameDataManifest)
        {
            // Publish the complete command-stream reservation before vkBeginCommandBuffer.
            // Arena offsets are stable, but descriptor slabs and CPU view tables must also be
            // materialized at this legal boundary so a draw cannot grow shared state midway
            // through recording.
            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = recordingState.RecordingScratch.MeshDrawSlotsByRenderer;
            meshDrawSlotsByRenderer.Clear();
            meshDrawSlotsByRenderer.EnsureCapacity(Math.Max(1, recordingState.RecordingScratch.RecordMeshDrawSlotCapacityHint));
            frameDataManifest = recordingState.RecordingScratch.MeshFrameDataManifest;
            ulong frameDataGeneration = MappedFrameArena?.Generation ?? 0UL;
            using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.PrimaryFrameDataManifest))
            {
                frameDataManifest.Begin(frameDataGeneration, recordingState.RecordingScratch.RecordMeshDrawSlotCapacityHint);
                if (!TryRegisterFrameWideMeshFrameDataRequirements(
                        recordingState.Ops,
                        Array.Empty<FrameOp>(),
                        recordingState.CommandBufferImageSlot,
                        sealAfterRegister: true,
                        meshDrawSlotsByRenderer,
                        recordingState.RecordingScratch,
                        recordingState.RecordingScratch.PrimaryMeshFrameDataFamilyBases,
                        out _,
                        out string frameWideReason))
                {
                    frameDataManifest.End();
                    recordingState.RecordingDeferredReason =
                        $"Frame-wide mesh frame-data manifest deferred command recording: {frameWideReason}";
                    return false;
                }
                foreach (KeyValuePair<VkMeshRenderer, int> reservation in meshDrawSlotsByRenderer)
                {
                    if (frameDataManifest.TryReserve(reservation.Key, reservation.Value))
                        continue;
                    frameDataManifest.End();
                    recordingState.RecordingDeferredReason =
                        $"Unable to reserve {reservation.Value} mesh frame-data slots before command recording.";
                    return false;
                }
            }
            recordingState.MeshDrawSlotsByRendererFamily = recordingState.RecordingScratch.PrimaryMeshDrawSlotsByRendererFamily;
            recordingState.MeshFrameDataFamilyBases = recordingState.RecordingScratch.PrimaryMeshFrameDataFamilyBases;
            recordingState.MeshDrawSlotsByRendererFamily.Clear();
            recordingState.PipelineDeferredOps = recordingState.RecordingScratch.PipelineDeferredOps;
            recordingState.PipelineDeferredOps.Clear();
            PrepareScheduledCommandChainFrameDataRefresh(
                ref recordingState);
            EMeshSubmissionStrategy submissionStrategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
            ulong frameStructuralSignature = recordingState.FramePlan?.StaticOperationSignature
                ?? throw new VulkanPlanPreconditionException(
                    "Primary pipeline preparation requires a sealed frame plan.");
            VulkanPipelineVariantManifest pipelineVariantManifest = ResourceRuntime.PipelineManager.GetOrBuildVariantManifest(
                recordingState.RenderGraphPlan.CompiledGraph.Plan,
                recordingState.Ops,
                submissionStrategy,
                recordingState.Policy.UseDynamicRendering,
                frameStructuralSignature);
            HashSet<int> deferredRequirementIndices =
                recordingState.RecordingScratch.PipelineDeferredRequirementIndices;
            ulong pipelineCompileActivityGeneration =
                VulkanPipelineCompileActivityGeneration;
            ulong sharedPipelineGeneration = SharedGraphicsPipelineGeneration;
            bool reuseDeferredPipelineReadiness =
                IsVulkanPipelineAsyncCompilationEnabled &&
                deferredRequirementIndices.Count > 0 &&
                recordingState.RecordingScratch.PipelineDeferredManifestIdentity ==
                    pipelineVariantManifest.CompatibilityIdentity &&
                recordingState.RecordingScratch.PipelineDeferredActivityGeneration ==
                    pipelineCompileActivityGeneration &&
                recordingState.RecordingScratch.PipelineDeferredSharedPipelineGeneration ==
                    sharedPipelineGeneration;
            if (!reuseDeferredPipelineReadiness)
                deferredRequirementIndices.Clear();
            bool warmupPreviouslyCompleted = pipelineVariantManifest.WarmupCompleted;
            bool graphicsPipelinesReady = true;
            int deferredPipelineDrawCount = 0;
            using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.PrimaryPrewarm))
            {
                string firstGraphicsPipelinePendingReason = string.Empty;
                string firstDeferredPipelineReason = string.Empty;
                for (int requirementIndex = 0;
                     requirementIndex < pipelineVariantManifest.Requirements.Count;
                     requirementIndex++)
                {
                    VulkanPipelineVariantRequirement requirement =
                        pipelineVariantManifest.Requirements[requirementIndex];
                    int opIndex = requirement.OpIndex;
                    PendingMeshDraw pendingDraw = recordingState.Ops[opIndex] switch
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
                            recordingState.PipelineDeferredOps.Add(recordingState.Ops[opIndex]);
                        }
                        continue;
                    }
                    XRFrameBuffer? target = recordingState.Ops[opIndex].Target;

                    int drawSlot =
                        recordingState.MeshDrawUniformSlotsByOpIndex[opIndex];
                    if (drawSlot < 0)
                    {
                        drawSlot = GetFrameWideMeshDrawUniformSlot(
                            recordingState.MeshDrawSlotsByRendererFamily,
                            recordingState.MeshFrameDataFamilyBases,
                            meshRenderer,
                            recordingState.CommandBufferImageSlot,
                            EVulkanMeshFrameDataStreamKind.Primary,
                            recordingState.Ops[opIndex].Context,
                            pendingDraw);
                    }
                    recordingState.MeshDrawUniformSlotsByOpIndex[opIndex] = drawSlot;
                    bool frameDataAlreadyRefreshed =
                        recordingState
                            .ScheduledCommandChainFrameDataRefreshedByOpIndex[
                                opIndex];
                    if (frameDataAlreadyRefreshed)
                    {
                        // The reusable-chain refresh already published the exact
                        // draw slot and validated its descriptor/pipeline identity.
                        // Entering a planner readback scope for every draw here is
                        // otherwise especially costly for four CSM cohorts, even
                        // though the body performs no additional work.
                        continue;
                    }

                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        recordingState.Ops[opIndex].Context.PipelineInstance);
                    if (!meshRenderer.TryPrewarmFrameDataForRecording(
                            pendingDraw,
                            drawSlot,
                            recordingState.CommandBufferImageSlot,
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
                        recordingState.RecordingDeferredReason =
                            $"Mesh frame-data reservation deferred before command recording for " +
                            $"mesh '{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}', slot {drawSlot}: {prewarmReason}";
                        return false;
                    }

                    int pipelinePassIndex = recordingState.Ops[opIndex].PassIndex;
                    if (pipelinePassIndex == int.MinValue)
                        continue;

                    if (CanSkipScheduledCommandChainPipelinePrewarm(
                            ref recordingState,
                            opIndex))
                    {
                        continue;
                    }

                    if (reuseDeferredPipelineReadiness &&
                        deferredRequirementIndices.Contains(requirementIndex))
                    {
                        recordingState.PipelineDeferredOps.Add(recordingState.Ops[opIndex]);
                        deferredPipelineDrawCount++;
                        if (firstDeferredPipelineReason.Length == 0)
                        {
                            firstDeferredPipelineReason =
                                $"Pass={requirement.PassName} Mesh='{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' " +
                                "Reason=pipeline compile still pending";
                        }
                        continue;
                    }

                    if (!TryResolveGraphicsPipelinePrewarmTarget(
                            target,
                            pipelinePassIndex,
                            recordingState.Ops[opIndex].Context,
                            recordingState.SwapchainTarget,
                            recordingState.Policy.UseDynamicRendering,
                            recordingState.RenderGraphPlan.CompiledGraph,
                            out bool useDynamicRendering,
                            out RenderPass prewarmRenderPass,
                            out DynamicRenderingFormatSignature prewarmDynamicRenderingFormats,
                            out bool depthStencilReadOnly,
                            out string targetReason))
                    {
                        if (!requirement.Required)
                        {
                            recordingState.PipelineDeferredOps.Add(recordingState.Ops[opIndex]);
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
                            recordingState.Ops[opIndex].Context.PassMetadata,
                            depthStencilReadOnly,
                            recordingState.Ops[opIndex].Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                            out string pipelineReason))
                    {
                        continue;
                    }

                    recordingState.PipelineDeferredOps.Add(recordingState.Ops[opIndex]);
                    if (IsVulkanPipelineAsyncCompilationEnabled)
                        deferredRequirementIndices.Add(requirementIndex);
                    deferredPipelineDrawCount++;
                    if (firstDeferredPipelineReason.Length == 0)
                    {
                        firstDeferredPipelineReason =
                            $"Pass={requirement.PassName} Required={requirement.Required} " +
                            $"Variant={requirement.SubmissionStrategy} Dynamic={requirement.DynamicRendering} " +
                            $"Stereo={requirement.Stereo} Multiview={requirement.Multiview} " +
                            $"Mesh='{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' Reason={pipelineReason}";
                    }
                }
                recordingState.RecordingScratch.RecordMeshDrawSlotCapacityHint = Math.Max(
                    recordingState.RecordingScratch.RecordMeshDrawSlotCapacityHint,
                    recordingState.MeshDrawSlotsByRendererFamily.Count);
                recordingState.MeshDrawSlotsByRendererFamily.Clear();

                if (!graphicsPipelinesReady)
                {
                    frameDataManifest.End();
                    recordingState.RecordingDeferredReason = warmupPreviouslyCompleted
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

                if (deferredPipelineDrawCount == 0)
                {
                    deferredRequirementIndices.Clear();
                    pipelineVariantManifest.MarkWarmupCompleted();
                }
                else
                {
                    // A primary recorded while required draw pipelines are pending is
                    // intentionally incomplete. It may submit for startup progress,
                    // but publishing it as reusable would freeze those omitted draws
                    // after an async compile completes (or a saturated queue frees).
                    recordingState.FrameOpsRequireRerecordLocal = true;
                    if (IsVulkanPipelineAsyncCompilationEnabled)
                    {
                        recordingState.RecordingScratch.PipelineDeferredManifestIdentity =
                            pipelineVariantManifest.CompatibilityIdentity;
                        recordingState.RecordingScratch.PipelineDeferredActivityGeneration =
                            pipelineCompileActivityGeneration;
                        recordingState.RecordingScratch.PipelineDeferredSharedPipelineGeneration =
                            sharedPipelineGeneration;
                    }
                    Debug.VulkanEvery(
                        $"Vulkan.PipelineDrawDeferralSummary.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Recording a partial frame with {0} draw operation(s) deferred for pipeline compilation; the rest of the frame will still submit. First={1}",
                        deferredPipelineDrawCount,
                        firstDeferredPipelineReason);
                }
            }

            if (!frameDataManifest.TrySeal(
                    MappedFrameArena?.Generation ?? 0UL,
                    MappedFrameArena?.ReservedBytes ?? 0UL))
            {
                frameDataManifest.End();
                recordingState.RecordingDeferredReason =
                    "Mesh frame-data generation changed while the command-stream reservation manifest was being materialized.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Refreshes the frame-buffered data consumed by executable scheduled
        /// secondaries before primary recording begins. The reusable refresh
        /// state collapses stable cohorts to frequency-owner work, avoiding a
        /// full draw preparation pass and a second refresh while encoding.
        /// </summary>
        private void PrepareScheduledCommandChainFrameDataRefresh(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            CommandBufferRecordingScratch scratch =
                recordingState.RecordingScratch;
            scratch.BeginScheduledCommandChainFrameDataRefreshRequests();

            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests =
                scratch.PrimaryReusableFrameDataRefreshRequests;
            FrameOpSignatureHasher stableMeshHash = new();
            stableMeshHash.Add(0x53454346);
            stableMeshHash.Add(MappedFrameArena?.Generation ?? 0UL);
            int meshRequestCount = 0;
            bool supportsDirectOwnerOnlyRefresh = true;

            for (int requestIndex = 0;
                 requestIndex < requests.Length;
                 requestIndex++)
            {
                ref readonly VulkanReusableFrameDataRefreshRequest request =
                    ref requests[requestIndex];
                int opIndex = request.SourceOpIndex;
                if ((uint)opIndex >= (uint)recordingState.Ops.Length)
                    continue;

                if (request.Kind is
                    EVulkanReusableFrameDataRefreshKind.Mesh or
                    EVulkanReusableFrameDataRefreshKind.IndirectMesh)
                {
                    recordingState.MeshDrawUniformSlotsByOpIndex[opIndex] =
                        request.DrawUniformSlot;
                }

                if (request.Kind != EVulkanReusableFrameDataRefreshKind.Mesh ||
                    !CanSkipScheduledCommandChainPipelinePrewarm(
                        ref recordingState,
                        opIndex))
                {
                    continue;
                }

                scratch.AddScheduledCommandChainFrameDataRefreshRequest(
                    request);
                supportsDirectOwnerOnlyRefresh &=
                    request.MeshRenderer is not null &&
                    request.MeshRenderer
                        .SupportsOwnerOnlyReusableFrameDataRefresh(
                            request.Draw);
                stableMeshHash.Add(
                    ComputeReusableMeshStableDataSignature(request));
                AddReusableFrequencyOwnerWorkRequests(
                    request,
                    dynamicUi: false,
                    scratch,
                    scheduledCommandChain: true);
                meshRequestCount++;
            }

            stableMeshHash.Add(meshRequestCount);
            scratch.SetScheduledCommandChainFrameDataRefreshBatchInfo(
                new VulkanReusableFrameDataRefreshBatchInfo(
                    stableMeshHash.ToHash(),
                    meshRequestCount,
                    supportsDirectOwnerOnlyRefresh));
            if (meshRequestCount == 0)
                return;

            _lastReusableFrameDataRefreshFailureReason = null;
            bool refreshed;
            using (VulkanCpuStageScope cpuStage =
                   new(_frameTelemetry, EVulkanCpuStage.FrameDataRefresh))
            {
                refreshed = TryRefreshReusableCommandBufferFrameData(
                    recordingState.FrameDataImageIndex,
                    scratch.ScheduledCommandChainFrameDataRefreshRequests,
                    scratch.ScheduledCommandChainFrameDataOwnerWorkRequests,
                    scratch.ScheduledCommandChainFrameDataRefreshBatchInfo,
                    scratch.ScheduledCommandChainFrameDataRefreshState,
                    dynamicUi: false,
                    descriptorResourcesCapturedByFrameSignature: true,
                    refreshMaterialUniforms: true);
            }

            if (!refreshed)
            {
                InvalidateScheduledCommandChainFrameDataRefresh(
                    ref recordingState,
                    scratch.ScheduledCommandChainFrameDataRefreshRequests);
                return;
            }

            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                refreshedRequests =
                    scratch.ScheduledCommandChainFrameDataRefreshRequests;
            for (int requestIndex = 0;
                 requestIndex < refreshedRequests.Length;
                 requestIndex++)
            {
                int opIndex = refreshedRequests[requestIndex].SourceOpIndex;
                if ((uint)opIndex < (uint)recordingState.Ops.Length)
                {
                    recordingState
                        .ScheduledCommandChainFrameDataRefreshedByOpIndex[
                            opIndex] = true;
                }
            }
        }

        private void InvalidateScheduledCommandChainFrameDataRefresh(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests)
        {
            Array.Fill(
                recordingState
                    .ScheduledCommandChainFrameDataRefreshedByOpIndex,
                false,
                0,
                recordingState.Ops.Length);
            for (int requestIndex = 0;
                 requestIndex < requests.Length;
                 requestIndex++)
            {
                int opIndex = requests[requestIndex].SourceOpIndex;
                if (!TryGetScheduledCommandChainForOp(
                        ref recordingState,
                        opIndex,
                        out CommandChain chain,
                        out _))
                {
                    continue;
                }

                chain.State = CommandChainState.Recorded;
                chain.DirtyReason |=
                    CommandChainDirtyReason.FrameDataRefreshFailed;
                chain.FrameDataRefreshTouchedDescriptors = false;
            }
        }

        private bool CanSkipScheduledCommandChainPipelinePrewarm(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            int opIndex)
        {
            if (CommandChainBenchmarkForceRerecord ||
                !TryGetScheduledCommandChainForOp(
                    ref recordingState,
                    opIndex,
                    out CommandChain chain,
                    out _))
            {
                return false;
            }

            return chain.SecondaryCommandBuffer.Handle != 0 &&
                   chain.SecondaryCommandBufferExecutable &&
                   (chain.State is
                       CommandChainState.Reused or
                       CommandChainState.FrameDataRefreshed) &&
                   !chain.FrameDataRefreshTouchedDescriptors;
        }

        private void PreparePrimaryCommandEncoding(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {

            ResetAndBeginPrimaryCommandBuffer(ref recordingState);

            // Global pending barriers are deferred until the first pass boundary to
            // maintain pass-scoped ordering.  Any remaining global mask is emitted
            // before the first pass barrier group via EmitPassBarriers.

            if (recordingState.Ops.Length > 0)
                recordingState.InitialContext = recordingState.Ops[0].Context;
        }

        private void InitializePrimaryCommandEncodingState(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {

            if (recordingState.CommandChainSchedule is not null)
                _lastReusableFrameDataRefreshFailureReason = null;

            recordingState.SwapchainPresentTransitions = 0;
            recordingState.UsedSwapchainDynamicRendering = false;
            recordingState.SwapchainInColorAttachmentLayout = false;
            recordingState.SwapchainFinalTargetLayout = recordingState.TransitionSwapchainToPresent
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.ColorAttachmentOptimal;
            recordingState.SwapchainFinalLayout = recordingState.InitialSwapchainColorLayout;

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            EmitPrimaryFrameStartBarriers(ref recordingState);

            recordingState.Metrics.ClearCount = 0;
            recordingState.Metrics.DrawCount = 0;
            recordingState.Metrics.MeshDrawCount = 0;
            recordingState.Metrics.IndirectDrawCount = 0;
            recordingState.Metrics.MeshTaskDispatchCount = 0;
            recordingState.Metrics.BlitCount = 0;
            recordingState.Metrics.ComputeCount = 0;
            recordingState.SwapchainWriteCount = 0;
            recordingState.Metrics.SwapchainClearWrites = 0;
            recordingState.SwapchainDrawWrites = 0;
            recordingState.SwapchainBlitWrites = 0;
            recordingState.SceneSwapchainWriters = 0;
            recordingState.OverlaySwapchainWriters = 0;
            recordingState.Metrics.ForcedDiagnosticSwapchainWriters = 0;
            recordingState.Metrics.FboOnlyDrawOps = 0;
            recordingState.Metrics.FboOnlyBlitOps = 0;
            recordingState.SwapchainLastWriter = "None";
            recordingState.SwapchainLastWriterPass = int.MinValue;
            recordingState.SwapchainLastWriterOpIndex = -1;

            // Per-pipeline context identity tracking for swapchain writes
            recordingState.SwapchainWritesByPipeline = recordingState.RecordingScratch.SwapchainWritesByPipeline;
            recordingState.SwapchainWriterLabelByPipeline = recordingState.RecordingScratch.SwapchainWriterLabelByPipeline;
            recordingState.SwapchainWriterDetailByPipeline = recordingState.RecordingScratch.SwapchainWriterDetailByPipeline;
            recordingState.SwapchainWriterOpByPipeline = recordingState.RecordingScratch.SwapchainWriterOpByPipeline;
            recordingState.SwapchainWriterDynamicUiDrawCountByPipeline = recordingState.RecordingScratch.SwapchainWriterDynamicUiDrawCountByPipeline;
            recordingState.SwapchainWriterPassByPipeline = recordingState.RecordingScratch.SwapchainWriterPassByPipeline;
            recordingState.SwapchainWriterOpIndexByPipeline = recordingState.RecordingScratch.SwapchainWriterOpIndexByPipeline;
            recordingState.PipelineNameByIdentity = recordingState.RecordingScratch.PipelineNameByIdentity;
            ResetPrimaryRecordingScratch(ref recordingState);

            CollectPrimaryOperationCensus(ref recordingState);

            recordingState.RenderScope = recordingState.RecordingScratch.RenderScope;
            recordingState.RenderScope.Deactivate();
            recordingState.ActiveInlineQuery = null;
            recordingState.ActiveInlineQueryRecordedDraw = false;
            recordingState.ActivePassIndex = int.MinValue;
            recordingState.ActiveSchedulingIdentity = int.MinValue;
            recordingState.ActiveContext = default;
            recordingState.HasActiveContext = false;
            recordingState.PlannerContext = default;
            recordingState.HasPlannerContext = false;
            recordingState.RenderPassLabelActive = false;
            recordingState.PassIndexLabelActive = false;
            recordingState.ActivePipelineOverrideScope = default;
            recordingState.ActivePipelineOverrideScopeSet = false;

            // Track whether the swapchain has already had its first render pass
            // this frame. Subsequent re-entries (e.g. after a compute dispatch
            // forced EndActiveRenderPass) use LoadOp.Load to preserve contents
            // instead of clearing the composited scene.
            recordingState.SwapchainClearedThisFrame = false;

            recordingState.SkipUiPipelineOps = XREngine.Rendering.RenderDiagnosticsFlags.VkSkipUiPipeline;
            recordingState.SkipUiBatchTextOps = XREngine.Rendering.RenderDiagnosticsFlags.VkSkipUiBatchText;

            // Track swapchain writes that happen outside a swapchain render pass
            // (e.g. CmdBlitImage to swapchain). If true, the first swapchain render
            // pass this frame must Load existing color instead of clearing.
            recordingState.SwapchainWrittenOutsideRenderPass = false;
            recordingState.ActualSwapchainWriteCount = 0;

            // Track per-FBO attachment layouts across render-pass restarts within
            // the current command buffer.  On first use the layouts are null
            // (â†’ initialLayout = Undefined);  after EndActiveRenderPass we store
            // the finalLayout of each attachment so the next BeginRenderPassForTarget
            // can set initialLayout correctly and preserve content across passes.
            recordingState.FboLayoutTracking = recordingState.RecordingScratch.FboLayoutTracking;
            recordingState.FboLayoutTracking.Clear();
            recordingState.FboLayoutTracking.EnsureCapacity(Math.Max(1, recordingState.RecordingScratch.RecordFboLayoutCapacityHint));

            // Reset every inline query pool before the first render operation. Query-pool
            // resets are illegal inside rendering, and deferring them until QueryOp would
            // force the forward pass through a store/reload cycle for proxy queries.
            PreparePrimaryInlineQueries(ref recordingState);
        }

    }
}
