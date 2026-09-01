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
using XREngine.Rendering.Diagnostics;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        // Cold visibility changes can expose hundreds of pipeline variants at once.
        // Queue/check only a small slice before yielding the frame so the last
        // completed presentation and its overlays remain responsive while workers
        // compile the newly visible variants.
        private static readonly long PrimaryPipelinePrewarmSliceTicks =
            Math.Max(1L, Stopwatch.Frequency / 500L);
        private static readonly long PrimaryFrameDataColdPreparationSliceTicks =
            Math.Max(1L, Stopwatch.Frequency / 250L);

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
            recordingState.ReadOnlyStorageAuthority = context.ReadOnlyStorageAuthority;
            recordingState.OpenXrTargetContext = context.OpenXrTargetContext;
            recordingState.ExcludeDesktopSwapchainBarriers = context.ExcludeDesktopSwapchainBarriers;
            recordingState.PrimaryCommandPlan = context.PrimaryCommandPlan;
            recordingState.FramePlan = context.FramePlan;
            recordingState.RecordingStaticOperationSignature =
                context.RecordingStaticOperationSignature;
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
            recordingState.ScheduledCommandChainsByOpIndex = null;
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
            recordingState.CommandChainRecordingAdmittedByOpIndex =
                recordingState.RecordingScratch
                    .PreparePrimaryCommandChainRecordingAdmissionFlags(
                        recordingState.Ops.Length);
            PrepareProgressiveCommandChainRecordingAdmission(
                ref recordingState);
            recordingState.ExecutedCommandChainSecondaryHandles = recordingState.RecordingScratch.ExecutedCommandChainSecondaryHandles;
            recordingState.ExecutedCommandChainSecondaryHandles.Clear();
            recordingState.ExecutedCommandChainSecondaryArtifactSequence =
                recordingState.RecordingScratch.ExecutedCommandChainSecondaryArtifactSequence;
            recordingState.ExecutedCommandChainSecondaryArtifactSequence.Clear();
        }

        /// <summary>
        /// Selects a bounded cold-secondary slice before frame-data prewarm. A
        /// completed desktop presentation source lets the frame loop replay the
        /// last coherent scene while the selected artifacts become reusable.
        /// OpenXR/external targets and diagnostic force-record modes retain the
        /// immediate all-required-work contract.
        /// </summary>
        private void PrepareProgressiveCommandChainRecordingAdmission(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            recordingState.ProgressiveCommandChainPublicationPending = false;
            recordingState.CanProgressivelyDeferCommandChainPublication = false;
            recordingState.CommandChainPublicationDeferred = false;
            recordingState.ProgressiveCommandChainAdmittedJobs = 0;
            recordingState.ProgressiveCommandChainAdmittedOperations = 0;
            recordingState.ProgressiveCommandChainDeferredJobs = 0;

            if (!recordingState.Policy.AllowsSecondaryDeferral ||
                recordingState.Policy.IsExternalSwapchainTarget ||
                !recordingState.PresentationSource.HasLogicalSource ||
                recordingState.ScheduledCommandChainsByOpIndex is null ||
                CommandChainBenchmarkForceRerecord ||
                CommandChainValidationEnabled ||
                CommandChainTraceEnabled)
            {
                return;
            }

            if (!ResourceRuntime.TryValidatePresentationSourceForReplay(
                    recordingState.PresentationSource,
                    out _))
            {
                return;
            }

            recordingState.CanProgressivelyDeferCommandChainPublication = true;
            int dirtyChainCount = 0;
            int dirtyOperationCount = 0;
            for (int opIndex = 0;
                 opIndex < recordingState.Ops.Length;
                 opIndex++)
            {
                CommandChain? chain =
                    recordingState.ScheduledCommandChainsByOpIndex[opIndex];
                if (chain is null ||
                    chain.Key.DynamicOverlay ||
                    chain.SourceStartIndex != opIndex ||
                    !CommandChainNeedsColdPublication(chain))
                {
                    continue;
                }

                dirtyChainCount++;
                dirtyOperationCount += chain.SourceCount;
            }

            int maxAdmittedChains = MaxSecondaryCommandBuffersPerScope;
            if (dirtyChainCount <= maxAdmittedChains &&
                dirtyOperationCount <=
                    MaxProgressiveDesktopCommandChainRecordOperations)
                return;

            int admitted = 0;
            int admittedOperations = 0;
            int deferred = 0;
            for (int opIndex = 0;
                 opIndex < recordingState.Ops.Length;
                 opIndex++)
            {
                CommandChain? chain =
                    recordingState.ScheduledCommandChainsByOpIndex[opIndex];
                if (chain is null ||
                    chain.Key.DynamicOverlay ||
                    chain.SourceStartIndex != opIndex ||
                    !CommandChainNeedsColdPublication(chain))
                {
                    continue;
                }

                bool admit = admitted < maxAdmittedChains &&
                    (admitted == 0 ||
                     admittedOperations + chain.SourceCount <=
                        MaxProgressiveDesktopCommandChainRecordOperations);
                if (admit)
                {
                    admitted++;
                    admittedOperations += chain.SourceCount;
                }
                else
                    deferred++;

                int endIndex = Math.Min(
                    recordingState.Ops.Length,
                    chain.SourceStartIndex + chain.SourceCount);
                for (int chainOpIndex = chain.SourceStartIndex;
                     chainOpIndex < endIndex;
                     chainOpIndex++)
                {
                    recordingState.CommandChainRecordingAdmittedByOpIndex[
                        chainOpIndex] = admit;
                }
            }

            recordingState.ProgressiveCommandChainPublicationPending =
                deferred > 0;
            recordingState.ProgressiveCommandChainAdmittedJobs = admitted;
            recordingState.ProgressiveCommandChainAdmittedOperations =
                admittedOperations;
            recordingState.ProgressiveCommandChainDeferredJobs = deferred;
        }

        private static bool CommandChainNeedsColdPublication(CommandChain chain)
            => chain.SecondaryCommandBuffer.Handle == 0 ||
               !chain.SecondaryCommandBufferExecutable ||
               chain.State is CommandChainState.Recorded or
                   CommandChainState.NotReady ||
               chain.FrameDataRefreshTouchedDescriptors;

        private bool TryPreparePrimaryFrameData(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            out VulkanMeshFrameDataReservationManifest frameDataManifest)
        {
            CommandBufferRecordingScratch recordingScratch =
                recordingState.RecordingScratch;
            recordingState.PipelineDeferredOperationIndices =
                recordingScratch.PipelineDeferredOperationIndices;
            recordingState.PipelineDeferredOperationIndices.Clear();

            EMeshSubmissionStrategy submissionStrategy =
                RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
            FramePlan framePlan = recordingState.FramePlan
                ?? throw new VulkanPlanPreconditionException(
                    "Primary pipeline preparation requires a sealed frame plan.");
            ulong targetCompatibilitySignature =
                VulkanPipelineVariantManifest.ComputeTargetCompatibilitySignature(
                    recordingState.ResourcePlanStamp.ResourceAllocationSignature,
                    recordingState.SwapchainTarget,
                    recordingState.Policy.UseDynamicRendering,
                    ResourceRuntime.SwapchainRenderPass);
            VulkanPipelineVariantManifest pipelineVariantManifest =
                ResourceRuntime.PipelineManager.GetOrBuildVariantManifest(
                    recordingState.RenderGraphPlan.CompiledGraph.Plan,
                    recordingState.Ops,
                    submissionStrategy,
                    recordingState.Policy.UseDynamicRendering,
                    targetCompatibilitySignature,
                    framePlan);
            if (!TryPrepareAdvancedVisibilityOperations(ref recordingState))
            {
                frameDataManifest = default!;
                return false;
            }
            frameDataManifest = recordingScratch.MeshFrameDataManifest;
            if (!TryAdmitPrimaryGraphicsPipelines(
                    ref recordingState,
                    pipelineVariantManifest))
            {
                return false;
            }

            // Publish the complete command-stream reservation before vkBeginCommandBuffer.
            // Arena offsets are stable, but descriptor slabs and CPU view tables must also be
            // materialized at this legal boundary so a draw cannot grow shared state midway
            // through recording.
            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer =
                recordingScratch.MeshDrawSlotsByRenderer;
            bool reusePublishedRefreshCohort =
                recordingScratch.IsReusableFrameDataRefreshCohortCurrent(
                    framePlan.Generation,
                    framePlan.RenderFrameId,
                    recordingState.FrameDataImageIndex);
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int>
                meshFrameDataFamilyBases = reusePublishedRefreshCohort
                    ? recordingScratch.ReusableMeshFrameDataFamilyBases
                    : recordingScratch.PrimaryMeshFrameDataFamilyBases;
            if (!reusePublishedRefreshCohort)
            {
                meshDrawSlotsByRenderer.Clear();
                meshDrawSlotsByRenderer.EnsureCapacity(
                    Math.Max(
                        1,
                        recordingScratch.RecordMeshDrawSlotCapacityHint));
            }
            ulong frameDataGeneration = MappedFrameArena?.Generation ?? 0UL;
            using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.PrimaryFrameDataManifest))
            {
                frameDataManifest.Begin(
                    frameDataGeneration,
                    recordingScratch.RecordMeshDrawSlotCapacityHint);
                if (!reusePublishedRefreshCohort &&
                    !TryRegisterFrameWideMeshFrameDataRequirements(
                        recordingState.Ops,
                        FrameOperationSequence.Empty,
                        recordingState.CommandBufferImageSlot,
                        sealAfterRegister: true,
                        meshDrawSlotsByRenderer,
                        recordingScratch,
                        meshFrameDataFamilyBases,
                        0UL,
                        0UL,
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
            recordingState.MeshDrawSlotsByRendererFamily =
                recordingScratch.PrimaryMeshDrawSlotsByRendererFamily;
            recordingState.MeshFrameDataFamilyBases =
                meshFrameDataFamilyBases;
            recordingState.MeshDrawSlotsByRendererFamily.Clear();
            PrepareScheduledCommandChainFrameDataRefresh(
                ref recordingState);

            if (!TryAdmitPrimaryFrameDataStructures(
                    ref recordingState,
                    pipelineVariantManifest,
                    frameDataManifest))
            {
                return false;
            }

            int frameDataPrewarmProcessed = 0;
            int frameDataPrewarmUnmapped = 0;
            int frameDataPrewarmPipelineDeferred = 0;
            int frameDataPrewarmPublicationDeferred = 0;
            int frameDataPrewarmReusableRefreshes = 0;
            long frameDataPrewarmStart = Stopwatch.GetTimestamp();
            using (VulkanCpuStageScope cpuStage = new(
                       _frameTelemetry,
                       EVulkanCpuStage.PrimaryPrewarm))
            {
                for (int requirementIndex = 0;
                     requirementIndex < pipelineVariantManifest.Requirements.Count;
                     requirementIndex++)
                {
                    VulkanPipelineVariantRequirement requirement =
                        pipelineVariantManifest.Requirements[requirementIndex];
                    int opIndex = requirement.OpIndex;
                    ref readonly FrameOperationHeader operationHeader =
                        ref recordingState.Ops.GetHeader(opIndex);
                    PendingMeshDraw pendingDraw = operationHeader.OpCode switch
                    {
                        EVulkanPrimaryPlanNodeKind.MeshDraw =>
                            recordingState.Ops.GetMeshDraw(opIndex).Draw,
                        EVulkanPrimaryPlanNodeKind.IndirectDraw =>
                            recordingState.Ops.GetIndirectDraw(opIndex).Draw,
                        _ => default,
                    };
                    VkMeshRenderer? meshRenderer = pendingDraw.Renderer;
                    if (meshRenderer is null)
                    {
                        frameDataPrewarmUnmapped++;
                        continue;
                    }
                    if (recordingState.PipelineDeferredOperationIndices.Contains(
                            opIndex))
                    {
                        frameDataPrewarmPipelineDeferred++;
                        continue;
                    }
                    if (!recordingState
                            .CommandChainRecordingAdmittedByOpIndex[opIndex])
                    {
                        frameDataPrewarmPublicationDeferred++;
                        continue;
                    }
                    bool frameDataAlreadyRefreshed =
                        recordingState
                            .ScheduledCommandChainFrameDataRefreshedByOpIndex[
                                opIndex];
                    if (frameDataAlreadyRefreshed)
                    {
                        frameDataPrewarmReusableRefreshes++;
                        // PrepareScheduledCommandChainFrameDataRefresh already
                        // published the exact draw slot. Avoid resolving that slot
                        // again through the renderer-family dictionaries; this was
                        // the dominant cost when a newly visible avatar produced a
                        // large reusable draw cohort.
                        continue;
                    }

                    FrameOpContext operationContext = recordingState.Ops.GetContext(opIndex);

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
                            operationContext,
                            pendingDraw);
                    }
                    recordingState.MeshDrawUniformSlotsByOpIndex[opIndex] = drawSlot;
                    frameDataPrewarmProcessed++;
                    using VulkanPreparedResourcePlannerThreadScope resourceScope =
                        EnterRecordingResourceScope(recordingState.FramePlan, in operationContext);
                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        operationContext.PipelineInstance);
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

                }
                recordingScratch.RecordMeshDrawSlotCapacityHint = Math.Max(
                    recordingScratch.RecordMeshDrawSlotCapacityHint,
                    recordingState.MeshDrawSlotsByRendererFamily.Count);
                recordingState.MeshDrawSlotsByRendererFamily.Clear();
            }
            TimeSpan frameDataPrewarmElapsed =
                Stopwatch.GetElapsedTime(frameDataPrewarmStart);
            if (frameDataPrewarmElapsed >= TimeSpan.FromMilliseconds(20))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.PrimaryFrameDataPrewarm.Slow.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Slow primary frame-data preparation: elapsedMs={0:F3} requirements={1} processed={2} reusableRefreshes={3} publicationDeferred={4} pipelineDeferred={5} unmapped={6} admittedColdJobs={7} admittedColdOps={8} deferredColdJobs={9}.",
                    frameDataPrewarmElapsed.TotalMilliseconds,
                    pipelineVariantManifest.Requirements.Count,
                    frameDataPrewarmProcessed,
                    frameDataPrewarmReusableRefreshes,
                    frameDataPrewarmPublicationDeferred,
                    frameDataPrewarmPipelineDeferred,
                    frameDataPrewarmUnmapped,
                    recordingState.ProgressiveCommandChainAdmittedJobs,
                    recordingState.ProgressiveCommandChainAdmittedOperations,
                    recordingState.ProgressiveCommandChainDeferredJobs);
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
        /// Performs the sole legal late binding for the visibility lane before
        /// <c>vkBeginCommandBuffer</c>: exact set-1 frame-slot ranges are
        /// allocated and initialized after the sealed publication is checked.
        /// Compute and raster program closures are resolved here. Raster also
        /// seals its exact stable-bin strategy/ranges and retains every resident
        /// template through the accepted frame-slot lifetime. Any incomplete
        /// family is rejected before command recording rather than falling back.
        /// </summary>
        private bool TryPrepareAdvancedVisibilityOperations(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            FramePlan framePlan = recordingState.FramePlan
                ?? throw new VulkanPlanPreconditionException(
                    "Advanced visibility preparation requires a sealed frame plan.");
            VulkanAdvancedVisibilityStageRequest familyRequest = default;
            VulkanAdvancedVisibilityInputStorage? familyInput = null;
            VulkanAdvancedScenePublicationState familySceneState = default;
            VulkanAdvancedVisibilityResourceState familyState = default;
            VulkanAdvancedVisibilityTargetClosure rasterTargetClosure = default;
            VulkanAdvancedVisibilityTargetClosure lateRasterTargetClosure = default;
            int preparationStageCount = 0;
            int rasterStageCount = 0;
            int lateComputeStageCount = 0;
            int lateRasterStageCount = 0;

            for (int operationIndex = 0;
                 operationIndex < recordingState.Ops.Length;
                 operationIndex++)
            {
                if (recordingState.Ops.GetHeader(operationIndex).OpCode !=
                    EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
                    continue;

                ref readonly VulkanAdvancedVisibilityOperationPayload payload = ref
                    recordingState.Ops.GetAdvancedVisibility(operationIndex);
                VulkanAdvancedVisibilityStageRequest request = payload.Request;
                VulkanAdvancedVisibilityInputStorage input = payload.Input;
                if (!request.IsValid || input is null ||
                    !input.MatchesRequest(in request))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: stage '{request.Stage}' phase '{request.Phase}' is outside the admitted physical family.";
                    return false;
                }
                AdvancedPreparationPublication publication = input.Publication;
                AdvancedIndirectPreparationResult indirect = input.Indirect;
                AdvancedVisibilityFamilyReservation reservation = request.Reservation;
                if (request.RenderFrameId != framePlan.RenderFrameId ||
                    !_commandRuntime.IsAdvancedVisibilityReservationCurrent(in reservation))
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: its sealed preparation publication is stale or incomplete.";
                    recordingState.FailureKind =
                        EVulkanCommandRecordingFailureKind.RetryFrame;
                    return false;
                }
                if (preparationStageCount + rasterStageCount + lateComputeStageCount + lateRasterStageCount == 0)
                {
                    familyRequest = request;
                    familyInput = input;
                }
                else if (!familyRequest.MatchesFamily(in request) ||
                    !ReferenceEquals(familyInput, input))
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: the sealed stages do not form one exact publication/view/target family.";
                    return false;
                }
                switch (request.Stage, request.Phase)
                {
                    case (EAdvancedRenderStage.VisibilityPreparation, EAdvancedVisibilityStageBackendPhase.Complete):
                        preparationStageCount++;
                        break;
                    case (EAdvancedRenderStage.VisibilityRaster, EAdvancedVisibilityStageBackendPhase.Complete):
                        rasterStageCount++;
                        break;
                    case (EAdvancedRenderStage.DepthPyramidAndLateVisibility, EAdvancedVisibilityStageBackendPhase.LateCompute):
                        lateComputeStageCount++;
                        break;
                    case (EAdvancedRenderStage.DepthPyramidAndLateVisibility, EAdvancedVisibilityStageBackendPhase.LateRaster):
                        lateRasterStageCount++;
                        break;
                }
                if (preparationStageCount > 1 || rasterStageCount > 1 ||
                    lateComputeStageCount > 1 || lateRasterStageCount > 1)
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: one frame plan contains duplicate stages for a single set-1 family.";
                    return false;
                }

                FrameOpContext operationContext =
                    recordingState.Ops.GetContext(operationIndex);
                using VulkanPreparedResourcePlannerThreadScope resourceScope =
                    EnterRecordingResourceScope(framePlan, in operationContext);
                if (operationContext.OutputSchedulingInstanceIdentity != reservation.OutputId)
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: its pipeline scope does not belong to the reserved output.";
                    return false;
                }
                VulkanCompiledRenderGraph operationGraph =
                    recordingState.RenderGraphPlan.CompiledGraph;
                if (framePlan.TryResolveRenderGraphPlan(
                        in operationContext,
                        out VulkanRenderGraphPlan operationPlan))
                {
                    operationGraph = operationPlan.CompiledGraph;
                }
                VulkanAdvancedVisibilityTargetClosure targetClosure = default;
                int operationPassIndex =
                    recordingState.Ops.GetHeader(operationIndex).PassIndex;
                bool requiresGraphicsTargetClosure =
                    request.Stage == EAdvancedRenderStage.VisibilityRaster ||
                    request.Phase == EAdvancedVisibilityStageBackendPhase.LateRaster;
                VkFrameBuffer? targetWrapper = null;
                if (requiresGraphicsTargetClosure &&
                    (targetWrapper = ResourceRuntime.BackendObjects.Get(request.Target)
                        as VkFrameBuffer) is null)
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: the authoritative target has no Vulkan framebuffer wrapper.";
                    recordingState.FailureKind = EVulkanCommandRecordingFailureKind
                        .RecoverAfterStateChange;
                    return false;
                }
                if (requiresGraphicsTargetClosure && !TryValidateAdvancedVisibilityTarget(
                        in request,
                        targetWrapper!,
                        out string targetShapeReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: target closure is incomplete: {targetShapeReason}";
                    recordingState.FailureKind = EVulkanCommandRecordingFailureKind
                        .RecoverAfterStateChange;
                    return false;
                }
                if (requiresGraphicsTargetClosure)
                {
                    // A deferrable output consumes only its preflighted native
                    // target; EnsureCurrent may publish a resize-time backing.
                    if (recordingState.Policy.AllowSynchronousResourceUploads)
                        targetWrapper!.EnsureCurrent();
                if (!targetWrapper!.TryCaptureRecordedRenderTargetSnapshot(
                        out VulkanRecordedRenderTargetSnapshot targetSnapshot))
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: the native target snapshot is incomplete.";
                    recordingState.FailureKind = EVulkanCommandRecordingFailureKind
                        .RecoverAfterStateChange;
                    return false;
                }
                if (!TryResolveGraphicsPipelinePrewarmTarget(
                        request.Target,
                        operationPassIndex,
                        operationContext,
                        recordingState.SwapchainTarget,
                        recordingState.Policy.UseDynamicRendering,
                        operationGraph,
                        out bool usesDynamicRendering,
                        out RenderPass targetRenderPass,
                        out DynamicRenderingFormatSignature targetFormats,
                        out SampleCountFlags rasterizationSamples,
                        out bool depthStencilReadOnly,
                        out string targetCompatibilityReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: target compatibility failed: {targetCompatibilityReason}";
                    recordingState.FailureKind = EVulkanCommandRecordingFailureKind
                        .RecoverAfterStateChange;
                    return false;
                }
                targetClosure = new(
                        request.Target,
                    targetSnapshot,
                    usesDynamicRendering,
                    targetRenderPass,
                    targetFormats,
                    rasterizationSamples,
                    depthStencilReadOnly);
                if (request.Stage == EAdvancedRenderStage.VisibilityRaster)
                    rasterTargetClosure = targetClosure;
                else if (request.Phase == EAdvancedVisibilityStageBackendPhase.LateRaster)
                    lateRasterTargetClosure = targetClosure;
                }
                if (!TryPrepareAdvancedVisibilityScenePublication(
                        framePlan,
                        in request,
                        out VulkanAdvancedScenePublicationState sceneState,
                        out EVulkanAdvancedSceneResourceFailure sceneFailure,
                        out string sceneReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: set-2/set-3 scene publication failed: {sceneReason}";
                    recordingState.FailureKind =
                        VulkanAdvancedSceneResourceFailurePolicy
                            .RequiresFrameRetry(sceneFailure)
                            ? EVulkanCommandRecordingFailureKind.RetryFrame
                            : VulkanAdvancedSceneResourceFailurePolicy
                                .AllowsRecoveryAfterStateChange(sceneFailure)
                                ? EVulkanCommandRecordingFailureKind
                                    .RecoverAfterStateChange
                                : EVulkanCommandRecordingFailureKind
                                    .RendererTerminal;
                    return false;
                }
                if (!familySceneState.IsValid)
                {
                    familySceneState = sceneState;
                }
                else if (familySceneState != sceneState)
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: the family stages resolved different canonical scene publications.";
                    recordingState.FailureKind =
                        EVulkanCommandRecordingFailureKind.RendererTerminal;
                    return false;
                }
                if (request.Stage == EAdvancedRenderStage.VisibilityRaster &&
                    !framePlan.StableBins.HasSealedSubmissionPlans &&
                    !TrySealAdvancedVisibilityBins(
                        framePlan.StableBins,
                        in request,
                        input,
                        in sceneState,
                        in operationContext,
                        operationPassIndex,
                        out string binReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: stable-bin sealing failed: {binReason}";
                    return false;
                }
                if (request.Stage == EAdvancedRenderStage.VisibilityRaster &&
                    !framePlan.StableBins.TryRetainTemplatesForFramePlan(
                        ResourceRuntime.ResidentDrawTemplates,
                        out string templateRetentionReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: template retention failed: {templateRetentionReason}";
                    return false;
                }
                if (request.Stage == EAdvancedRenderStage.VisibilityRaster &&
                    !framePlan.StableBins.TryPrepareVisibilityRasterPipelines(
                        ResourceRuntime.AdvancedVisibilityPipelines,
                        input.Payloads,
                        in targetClosure,
                        out string rasterPipelineReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: raster closure failed: {rasterPipelineReason}";
                    recordingState.FailureKind = EVulkanCommandRecordingFailureKind
                        .RecoverAfterStateChange;
                    return false;
                }
                if (requiresGraphicsTargetClosure &&
                    !recordingState.Ops.Stream.TryAssociateAdvancedVisibilityTarget(
                        operationIndex,
                        in request,
                        in targetClosure))
                {
                    recordingState.RecordingDeferredReason =
                        "Advanced visibility operation is Unsupported: its exact native target closure could not be retained.";
                    return false;
                }

                if (!recordingState.Ops.Stream
                        .TryAssociateAdvancedVisibilityPublication(
                            operationIndex,
                            in sceneState))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: set-2/set-3 scene publication failed: {sceneReason}";
                    return false;
                }

                ReadOnlySpan<AdvancedVisibilityPayload> visibilityPayloads =
                    input.Payloads;
                if (request.Stage != EAdvancedRenderStage.VisibilityRaster)
                    continue;
                if (!framePlan.StableBins.TryBuildVisibilityRasterPayloads(
                    visibilityPayloads,
                    out visibilityPayloads,
                    out string rasterPayloadReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: native-local raster arguments failed: {rasterPayloadReason}";
                    return false;
                }
                VulkanAdvancedSceneLookupSegments lookupSegments =
                    sceneState.LookupSegments;
                VulkanAdvancedVisibilityGeometrySlices geometrySlices = new(
                    sceneState.StaticVertices,
                    sceneState.PreSkinnedCurrent,
                    sceneState.PreSkinnedPrevious,
                    sceneState.MeshletDescriptors,
                    sceneState.MeshletVertexIndices,
                    sceneState.MeshletTriangleWords);
                VulkanAdvancedVisibilityFamilySeal familySeal = new(
                    framePlan,
                    request.Reservation,
                    input,
                    publication,
                    publication.VisibilityContentGeneration,
                    indirect,
                    lookupSegments,
                    geometrySlices,
                    sceneState.NativeGeneration,
                    checked((uint)request.Views.ViewCount));
                if (!ResourceRuntime.AdvancedVisibilityResources.TryPrepare(
                        framePlan.FrameSlot,
                        framePlan.Generation,
                        in publication,
                        in indirect,
                        in lookupSegments,
                        input,
                        visibilityPayloads,
                        in geometrySlices,
                        checked((uint)request.Views.ViewCount),
                        in familySeal,
                        out familyState,
                        out EVulkanAdvancedVisibilityResourceFailure failure,
                        out string resourceReason))
                {
                    recordingState.RecordingDeferredReason =
                        $"Advanced visibility operation is Unsupported: set-1 frame-slot realization failed ({failure}): {resourceReason}";
                    recordingState.FailureKind = failure ==
                        EVulkanAdvancedVisibilityResourceFailure.RuntimeUnavailable
                            ? EVulkanCommandRecordingFailureKind
                                .RecoverAfterStateChange
                            : EVulkanCommandRecordingFailureKind.RendererTerminal;
                    return false;
                }
            }

            if (preparationStageCount + rasterStageCount + lateComputeStageCount + lateRasterStageCount == 0)
                return true;
            if (preparationStageCount != 1 || rasterStageCount != 1 ||
                lateComputeStageCount != 1 || lateRasterStageCount != 1 ||
                !familyState.IsValid)
            {
                recordingState.RecordingDeferredReason =
                    "Advanced visibility operation is Unsupported: the frame plan must seal exactly one preparation, early raster, late compute, and late raster stage before one immutable set-1 family is published.";
                recordingState.FailureKind =
                    EVulkanCommandRecordingFailureKind.RendererTerminal;
                return false;
            }
            // The late stage reuses the raster family's sealed graphics
            // pipelines after its compute work. Legacy clear/load render-pass
            // handles can differ between these passes even when attachments
            // look alike, so admit the baseline only when both stages resolve
            // the same dynamic-rendering compatibility closure. This proof is
            // completed before vkBeginCommandBuffer and before any barriers.
            if (!rasterTargetClosure.IsValid ||
                !lateRasterTargetClosure.IsValid ||
                !rasterTargetClosure.UsesDynamicRendering ||
                !lateRasterTargetClosure.UsesDynamicRendering ||
                rasterTargetClosure != lateRasterTargetClosure)
            {
                recordingState.RecordingDeferredReason =
                    "Advanced visibility operation is Unsupported: raster and late raster must share one exact dynamic-rendering target closure.";
                recordingState.FailureKind =
                    EVulkanCommandRecordingFailureKind.RendererTerminal;
                return false;
            }
            VulkanAdvancedVisibilityPipelineReadiness computePipelineReadiness =
                ResourceRuntime.AdvancedVisibilityPipelines.TryGetComputePipelines(
                    out VkRenderProgram earlyVisibilityProgram,
                    out VkRenderProgram buildIndirectProgram,
                    out string pipelineReason);
            if (computePipelineReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            {
                recordingState.RecordingDeferredReason =
                    computePipelineReadiness is VulkanAdvancedVisibilityPipelineReadiness.Pending or
                        VulkanAdvancedVisibilityPipelineReadiness.Missing
                        ? $"Advanced visibility operation is waiting for compute pipeline admission: {pipelineReason}"
                        : $"Advanced visibility operation is Unsupported: compute pipeline realization failed: {pipelineReason}";
                recordingState.FailureKind = computePipelineReadiness is
                    VulkanAdvancedVisibilityPipelineReadiness.Pending or
                    VulkanAdvancedVisibilityPipelineReadiness.Missing
                        ? EVulkanCommandRecordingFailureKind.RetryFrame
                        : EVulkanCommandRecordingFailureKind
                            .RecoverAfterStateChange;
                return false;
            }

            for (int operationIndex = 0;
                 operationIndex < recordingState.Ops.Length;
                 operationIndex++)
            {
                if (recordingState.Ops.GetHeader(operationIndex).OpCode !=
                    EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
                    continue;

                ref readonly VulkanAdvancedVisibilityOperationPayload payload = ref
                    recordingState.Ops.GetAdvancedVisibility(operationIndex);
                VulkanAdvancedVisibilityStageRequest request = payload.Request;
                VulkanAdvancedVisibilityPipelineReadiness stateAssociationReadiness =
                    recordingState.Ops.Stream.TryAssociateAdvancedVisibilityState(
                        operationIndex,
                        in request,
                        in familyState,
                        earlyVisibilityProgram,
                        buildIndirectProgram,
                        out string stateAssociationReason);
                if (stateAssociationReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                {
                    recordingState.RecordingDeferredReason =
                        stateAssociationReadiness is VulkanAdvancedVisibilityPipelineReadiness.Pending or
                            VulkanAdvancedVisibilityPipelineReadiness.Missing
                            ? $"Advanced visibility operation is waiting for immutable set-1 pipeline admission: {stateAssociationReason}"
                            : $"Advanced visibility operation is Unsupported: the immutable set-1 family could not be associated with every stage: {stateAssociationReason}";
                    recordingState.FailureKind = stateAssociationReadiness is
                        VulkanAdvancedVisibilityPipelineReadiness.Pending or
                        VulkanAdvancedVisibilityPipelineReadiness.Missing
                            ? EVulkanCommandRecordingFailureKind.RetryFrame
                            : EVulkanCommandRecordingFailureKind
                                .RecoverAfterStateChange;
                    return false;
                }

                if (request.Phase == EAdvancedVisibilityStageBackendPhase.LateCompute)
                {
                    VulkanCompiledRenderGraph operationGraph =
                        recordingState.RenderGraphPlan.CompiledGraph;
                    FrameOpContext operationContext =
                        recordingState.Ops.GetContext(operationIndex);
                    if (framePlan.TryResolveRenderGraphPlan(
                            in operationContext,
                            out VulkanRenderGraphPlan operationPlan))
                    {
                        operationGraph = operationPlan.CompiledGraph;
                    }
                    VulkanAdvancedVisibilityLateClosureStorage lateStorage =
                        recordingState.Ops.Stream
                            .GetAdvancedVisibilityLateClosureStorage(operationIndex);
                    try
                    {
                        if (!ResourceRuntime.AdvancedVisibilityResources
                                .TryCaptureLateTargetClosure(
                                    operationGraph,
                                    request.DepthTargetName,
                                    request.CurrentDepthPyramidTargetName,
                                    familyState.ViewCount,
                                    lateStorage,
                                    out VulkanAdvancedVisibilityLateTargetClosure lateClosure,
                                    out string lateTargetReason))
                        {
                            recordingState.RecordingDeferredReason =
                                $"Advanced visibility operation is Unsupported: sealed late depth-pyramid closure failed: {lateTargetReason}.";
                            recordingState.FailureKind =
                                EVulkanCommandRecordingFailureKind
                                    .RecoverAfterStateChange;
                            return false;
                        }
                        VulkanAdvancedVisibilityPipelineReadiness latePipelineReadiness =
                            ResourceRuntime.AdvancedVisibilityPipelines
                                .TryGetLateVisibilityComputePipelines(
                                    out VkRenderProgram buildDepthPyramidProgram,
                                    out VkRenderProgram lateVisibilityProgram,
                                    out string latePipelineReason);
                        if (latePipelineReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                        {
                            recordingState.RecordingDeferredReason =
                                latePipelineReadiness is VulkanAdvancedVisibilityPipelineReadiness.Pending or
                                    VulkanAdvancedVisibilityPipelineReadiness.Missing
                                    ? $"Advanced visibility operation is waiting for sealed late compute pipeline admission: {latePipelineReason}."
                                    : $"Advanced visibility operation is Unsupported: sealed late compute pipeline failed: {latePipelineReason}.";
                            recordingState.FailureKind = latePipelineReadiness is
                                VulkanAdvancedVisibilityPipelineReadiness.Pending or
                                VulkanAdvancedVisibilityPipelineReadiness.Missing
                                    ? EVulkanCommandRecordingFailureKind.RetryFrame
                                    : EVulkanCommandRecordingFailureKind
                                        .RecoverAfterStateChange;
                            return false;
                        }

                        VulkanAdvancedVisibilityPipelineReadiness lateAssociationReadiness =
                            recordingState.Ops.Stream
                                .TryAssociateAdvancedVisibilityLateClosure(
                                    operationIndex,
                                    in request,
                                    in lateClosure,
                                    buildDepthPyramidProgram,
                                    lateVisibilityProgram,
                                    out string lateAssociationReason);
                        if (lateAssociationReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                        {
                            recordingState.RecordingDeferredReason =
                                lateAssociationReadiness is VulkanAdvancedVisibilityPipelineReadiness.Pending or
                                    VulkanAdvancedVisibilityPipelineReadiness.Missing
                                    ? $"Advanced visibility operation is waiting for sealed late compute association: {lateAssociationReason}."
                                    : $"Advanced visibility operation is Unsupported: sealed late compute closure failed: {lateAssociationReason}.";
                            recordingState.FailureKind = lateAssociationReadiness is
                                VulkanAdvancedVisibilityPipelineReadiness.Pending or
                                VulkanAdvancedVisibilityPipelineReadiness.Missing
                                    ? EVulkanCommandRecordingFailureKind.RetryFrame
                                    : EVulkanCommandRecordingFailureKind
                                        .RecoverAfterStateChange;
                            return false;
                        }

                        int lateSetCount = checked(2 * (int)familyState.ViewCount);
                        DescriptorSet[] lateSets = lateStorage.DescriptorSets;
                        lateSets.AsSpan(0, lateSetCount).Clear();
                        for (uint viewIndex = 0u; viewIndex < familyState.ViewCount; ++viewIndex)
                        {
                            DescriptorImageInfo sampledDescriptor =
                                lateClosure.PyramidSampledDescriptors[checked((int)viewIndex)];
                            DescriptorImageInfo storageDescriptor =
                                lateClosure.PyramidStorageDescriptors[checked((int)viewIndex)];
                            if (!ResourceRuntime.AdvancedVisibilityResources
                                    .TryAcquireLateDepthPyramidDescriptorSet(
                                        in familyState,
                                        operationIndex,
                                        viewIndex,
                                        0u,
                                        in sampledDescriptor,
                                        in storageDescriptor,
                                        out lateSets[lateClosure.DescriptorIndex(viewIndex, 0)],
                                        out string descriptorReason))
                            {
                                recordingState.RecordingDeferredReason =
                                    $"Advanced visibility operation is Unsupported: immutable depth-pyramid descriptor sealing failed: {descriptorReason}";
                                return false;
                            }
                        }
                        for (uint viewIndex = 0u; viewIndex < familyState.ViewCount; ++viewIndex)
                        {
                            DescriptorImageInfo lateSampledDescriptor =
                                lateClosure.LateSampledDescriptors[viewIndex];
                            DescriptorImageInfo lateStorageDescriptor =
                                lateClosure.LateStorageDescriptors[viewIndex];
                            if (!ResourceRuntime.AdvancedVisibilityResources
                                    .TryAcquireLateDepthPyramidDescriptorSet(
                                        in familyState,
                                        operationIndex,
                                        viewIndex,
                                        1u,
                                        in lateSampledDescriptor,
                                        in lateStorageDescriptor,
                                        out lateSets[lateClosure.DescriptorIndex(
                                            viewIndex, 1)],
                                        out string lateDescriptorReason))
                            {
                                recordingState.RecordingDeferredReason =
                                    $"Advanced visibility operation is Unsupported: immutable late-visibility descriptor sealing failed: {lateDescriptorReason}.";
                                return false;
                            }
                        }
                        if (!recordingState.Ops.Stream.TrySealAdvancedVisibilityLateDescriptors(
                                operationIndex,
                                in request,
                                lateSets,
                                lateSetCount))
                        {
                            recordingState.RecordingDeferredReason =
                                "Advanced visibility operation is Unsupported: immutable late descriptor publication was rejected.";
                            return false;
                        }
                    }
                    finally
                    {
                        lateStorage.ReleaseAcquiredViews(ResourceRuntime.Images);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Retains and realizes the canonical scene publication before set-1
        /// visibility storage is initialized. The resulting native use is
        /// transferred immediately to frame-slot retirement, so primary
        /// recording never depends on a later scheduled-secondary batch.
        /// </summary>
        private bool TryPrepareAdvancedVisibilityScenePublication(
            FramePlan framePlan,
            in VulkanAdvancedVisibilityStageRequest request,
            out VulkanAdvancedScenePublicationState state,
            out EVulkanAdvancedSceneResourceFailure failure,
            out string reason)
        {
            state = default;
            failure =
                EVulkanAdvancedSceneResourceFailure.PublicationSnapshotUnavailable;
            reason = "the canonical scene publication is unavailable";
            if (!request.BackendPackage.TryGetCurrent(
                    out BackendReadyFramePackage package))
            {
                reason =
                    "the captured canonical backend package changed after visibility authoring";
                return false;
            }

            VulkanPreparedFrameRecording preparation =
                _advancedVisibilityPublicationPreparation;
            preparation.Begin(framePlan.FrameSlot, framePlan.Generation);
            try
            {
                preparation.AttachFramePlan(framePlan);
                preparation.CaptureGlobalResources(
                    package,
                    framePlan.Generation);
                VulkanPreparedFrameGlobalResourceSnapshot globals =
                    preparation.GlobalResources;
                AdvancedGpuScenePublicationReference publication =
                    globals.Publication;
                failure =
                    EVulkanAdvancedSceneResourceFailure.InvalidPublication;
                if (globals.Database is null || !publication.IsValid ||
                    !preparation.TryPrepareAdvancedScenePublication(
                        ResourceRuntime.AdvancedSceneResources,
                        globals.Database,
                        in publication,
                        out state,
                        out failure,
                        out reason,
                        out _))
                {
                    if (string.IsNullOrWhiteSpace(reason))
                        reason = failure.ToString();
                    return false;
                }
                if (!preparation.TryTransferRetainedLifetimesTo(
                        ResourceRuntime.ResidentTemplateFrameSlotLifetimes,
                        out reason))
                {
                    state = default;
                    failure =
                        EVulkanAdvancedSceneResourceFailure.TransactionIntegrityFailure;
                    return false;
                }

                failure = EVulkanAdvancedSceneResourceFailure.None;
                reason = "Ready";
                return true;
            }
            finally
            {
                preparation.Reset();
            }
        }

        private bool TrySealAdvancedVisibilityBins(
            VulkanPreparedStableBinStream bins,
            in VulkanAdvancedVisibilityStageRequest request,
            VulkanAdvancedVisibilityInputStorage input,
            in VulkanAdvancedScenePublicationState sceneState,
            in FrameOpContext context,
            int passIndex,
            out string reason)
        {
            if (!request.BackendPackage.TryGetCurrent(
                    out BackendReadyFramePackage package))
            {
                reason =
                    "the captured canonical backend package changed after visibility authoring";
                return false;
            }

            BackendReadySubmissionResolution resolution =
                package.SubmissionResolution;
            RenderOutputRequest outputRequest = context.OutputSchedulingRequest;
            bool isDesktopCanonicalOutput = outputRequest.OutputKind is
                EFrameOutputKind.DesktopScene or EFrameOutputKind.EditorScenePanel;
            bool isExternalOutput = outputRequest.Target.TargetClass ==
                ERenderOutputTargetClass.RuntimeExternalImage;
            bool isExplicitOutput = outputRequest.IsDefined &&
                (!isDesktopCanonicalOutput ||
                 outputRequest.Target.TargetClass != ERenderOutputTargetClass.DesktopSwapchain);
            uint outputViewMask = outputRequest.IsDefined
                ? outputRequest.Target.ViewMask
                : 0u;
            if (outputViewMask == 0u)
            {
                outputViewMask = request.Views.ViewCount > 1
                    ? 0b11u
                    : 0b1u;
            }
            // Visibility raster is canonical-geometry-only. Ordinary resident
            // ingress may coexist for other passes, but it must never decide
            // which geometry address space this ABI consumes.
            bins.ThawForReuse();
            if (!bins.TryBuildVisibilityGeometryStream(
                    ResourceRuntime,
                    input.Payloads,
                    package,
                    in sceneState,
                    passIndex,
                    outputViewMask,
                    in context,
                    out reason) ||
                !bins.TryResolveManifests(
                    ResourceRuntime.ResidentDrawTemplates
                        .StableBinManifestCache,
                    package.CanonicalScenePublication.TopologyGeneration))
            {
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "canonical visibility atlas manifests could not be sealed";
                return false;
            }
            GpuDiagnosticReadbackPlanNode? diagnosticNode = null;
            ReadOnlySpan<GpuDiagnosticReadbackPlan> diagnostics =
                package.DiagnosticReadbackPlans;
            for (int index = 0; index < diagnostics.Length; ++index)
            {
                if (!diagnostics[index].IsEnabled ||
                    diagnostics[index].Strategy != resolution.Resolved)
                    continue;
                diagnosticNode = diagnostics[index].Node;
                break;
            }

            VulkanSubmissionLaneCapabilities capabilities = new(
                SupportsIndirectCount: DeviceContext.Capabilities.Supports(
                    EVulkanDeviceCapability.DrawIndirectCount),
                SupportsMeshletIndirectCount:
                    DeviceContext.SupportsMeshTaskIndirectCount,
                AllowsInstrumentedDiagnostics: diagnosticNode.HasValue,
                AllowsInstrumentedCpuSafetyNet: diagnosticNode.HasValue,
                IndirectCrossoverSourceCount: 1u,
                MeshletCrossoverSourceCount: 1u);
            ulong passIdentity = diagnosticNode is { PassIdentity: not 0u } node
                ? node.PassIdentity
                : checked((ulong)(uint)(passIndex + 1));
            VulkanSubmissionOutputPolicy outputPolicy = new(
                PassIdentity: passIdentity,
                AllowsGpuDrivenSubmission: resolution.Resolved !=
                    EMeshSubmissionStrategy.CpuDirect,
                IsShadowPass: outputRequest.OutputKind == EFrameOutputKind.Shadow,
                IsExplicitOutput: isExplicitOutput,
                IsOpenXrOutput: outputRequest.OutputKind ==
                    EFrameOutputKind.OpenXREyeSubmit,
                IsMirrorOutput: outputRequest.OutputKind is
                    EFrameOutputKind.DesktopMirror or
                    EFrameOutputKind.VrPickupMirror or
                    EFrameOutputKind.InWorldMirror,
                IsCaptureOutput: outputRequest.OutputKind is
                    EFrameOutputKind.SceneCapture or
                    EFrameOutputKind.LightProbeCapture or
                    EFrameOutputKind.ReflectionProbeCapture or
                    EFrameOutputKind.ImageBasedLighting or
                    EFrameOutputKind.Thumbnail,
                IsExternalOutput: isExternalOutput);
            if (!outputPolicy.AllowsCanonicalVisibilityFamily)
            {
                reason = outputPolicy.DescribeCanonicalVisibilityRejection();
                return false;
            }
            if (!bins.TrySealSubmissionPlans(
                    ResourceRuntime.ResidentDrawTemplates,
                    input.Payloads,
                    input.IndirectRanges,
                    input.IndirectPayloadIndices,
                    resolution.Resolved,
                    in capabilities,
                    in outputPolicy,
                    diagnosticNode,
                    requestCpuSafetyNet: false,
                    out VulkanSubmissionPlanRejectionReason rejection))
            {
                reason = rejection.ToString();
                return false;
            }

            reason = "Ready";
            return true;
        }

        private static bool TryValidateAdvancedVisibilityTarget(
            in VulkanAdvancedVisibilityStageRequest request,
            VkFrameBuffer target,
            out string reason)
        {
            if (request.Target.Targets is not { Length: 4 } attachments)
            {
                reason = "the authoritative framebuffer does not have exactly three color attachments and one depth-stencil attachment";
                return false;
            }

            EFrameBufferAttachment[] expected =
            [
                EFrameBufferAttachment.ColorAttachment0,
                EFrameBufferAttachment.ColorAttachment1,
                EFrameBufferAttachment.ColorAttachment2,
                EFrameBufferAttachment.DepthStencilAttachment,
            ];
            string[] names =
            [
                request.IdentityTargetName,
                request.MetadataTargetName,
                request.SelectionTargetName,
                request.DepthTargetName,
            ];
            for (int index = 0; index < attachments.Length; ++index)
            {
                if (attachments[index].Attachment != expected[index] ||
                    attachments[index].Target is not GenericRenderObject targetObject ||
                    !string.Equals(
                        targetObject.Name,
                        names[index],
                        StringComparison.Ordinal))
                {
                    reason = $"attachment {index} is not the expected '{names[index]}'/{expected[index]} resource";
                    return false;
                }
            }
            if (target.FramebufferWidth == 0u || target.FramebufferHeight == 0u)
            {
                reason = "the native framebuffer extent is empty";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Converges cold renderer program/buffer/vertex-input state under a
        /// bounded desktop slice before the current frame performs its complete
        /// dynamic-data publication. Successful preparation signatures persist
        /// across rejected frames; no partially refreshed frame is recorded or
        /// submitted.
        /// </summary>
        private bool TryAdmitPrimaryFrameDataStructures(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest pipelineVariantManifest,
            VulkanMeshFrameDataReservationManifest frameDataManifest)
        {
            if (!recordingState.CanProgressivelyDeferCommandChainPublication)
                return true;

            HashSet<ulong> admittedSignatures = recordingState.RecordingScratch
                .AdmittedFrameDataPreparationSignatures;
            long sliceStart = Stopwatch.GetTimestamp();
            int newlyPrepared = 0;
            for (int requirementIndex = 0;
                 requirementIndex < pipelineVariantManifest.Requirements.Count;
                 requirementIndex++)
            {
                VulkanPipelineVariantRequirement requirement =
                    pipelineVariantManifest.Requirements[requirementIndex];
                int opIndex = requirement.OpIndex;
                ref readonly FrameOperationHeader operationHeader =
                    ref recordingState.Ops.GetHeader(opIndex);
                PendingMeshDraw pendingDraw = operationHeader.OpCode switch
                {
                    EVulkanPrimaryPlanNodeKind.MeshDraw =>
                        recordingState.Ops.GetMeshDraw(opIndex).Draw,
                    EVulkanPrimaryPlanNodeKind.IndirectDraw =>
                        recordingState.Ops.GetIndirectDraw(opIndex).Draw,
                    _ => default,
                };
                VkMeshRenderer? meshRenderer = pendingDraw.Renderer;
                if (meshRenderer is null ||
                    recordingState.PipelineDeferredOperationIndices.Contains(
                        opIndex) ||
                    !recordingState
                        .CommandChainRecordingAdmittedByOpIndex[opIndex] ||
                    recordingState
                        .ScheduledCommandChainFrameDataRefreshedByOpIndex[
                            opIndex])
                {
                    continue;
                }

                FrameOpContext operationContext =
                    recordingState.Ops.GetContext(opIndex);
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
                        operationContext,
                        pendingDraw);
                    recordingState.MeshDrawUniformSlotsByOpIndex[opIndex] =
                        drawSlot;
                }

                ulong preparationSignature =
                    ComputePrimaryFrameDataPreparationLedgerSignature(
                        requirement.PreparationCompatibilitySignature,
                        recordingState.ResourcePlanStamp
                            .ResourceAllocationSignature,
                        MappedFrameArena?.Generation ?? 0UL,
                        recordingState.CommandBufferImageSlot,
                        drawSlot);
                if (admittedSignatures.Contains(preparationSignature))
                    continue;

                using VulkanPreparedResourcePlannerThreadScope resourceScope =
                    EnterRecordingResourceScope(recordingState.FramePlan, in operationContext);
                using var pipelineScope =
                    RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                        operationContext.PipelineInstance);
                if (!meshRenderer.TryPrepareFrameDataStructuresForRecording(
                        pendingDraw,
                        out string prewarmReason))
                {
                    recordingState.MeshDrawSlotsByRendererFamily.Clear();
                    frameDataManifest.End();
                    recordingState.RecordingDeferredReason =
                        $"Mesh frame-data structural preparation deferred for " +
                        $"mesh '{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}', " +
                        $"slot {drawSlot}: {prewarmReason}";
                    return false;
                }

                if (admittedSignatures.Count >= CommandBufferRecordingScratch
                        .MaxAdmittedFrameDataPreparationSignatures)
                {
                    admittedSignatures.Clear();
                }
                admittedSignatures.Add(preparationSignature);
                newlyPrepared++;

                if (Stopwatch.GetTimestamp() - sliceStart <
                    PrimaryFrameDataColdPreparationSliceTicks)
                {
                    continue;
                }

                recordingState.MeshDrawSlotsByRendererFamily.Clear();
                frameDataManifest.End();
                recordingState.RecordingDeferredReason =
                    "Cold mesh frame-data structures are converging within " +
                    "the pre-recording CPU budget.";
                Debug.VulkanEvery(
                    $"Vulkan.PrimaryFrameDataStructuralAdmission.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Primary frame-data structural admission yielded after preparing {0} new signatures; cached={1} requirement={2}/{3}.",
                    newlyPrepared,
                    admittedSignatures.Count,
                    requirementIndex + 1,
                    pipelineVariantManifest.Requirements.Count);
                return false;
            }

            return true;
        }

        private static ulong ComputePrimaryFrameDataPreparationLedgerSignature(
            ulong requirementSignature,
            ulong resourceAllocationSignature,
            ulong arenaGeneration,
            int frameDataSlot,
            int drawSlot)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(0x46525052U);
            hash.Add(requirementSignature);
            hash.Add(resourceAllocationSignature);
            hash.Add(arenaGeneration);
            hash.Add(frameDataSlot);
            hash.Add(drawSlot);
            return hash.ToHash();
        }

        private bool TryAdmitPrimaryGraphicsPipelines(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest pipelineVariantManifest)
        {
            if (pipelineVariantManifest.WarmupCompleted)
                return TryAssociatePrimaryMeshTaskPipelines(
                    ref recordingState,
                    pipelineVariantManifest);

            CommandBufferRecordingScratch scratch =
                recordingState.RecordingScratch;
            HashSet<int> pendingRequirements =
                scratch.PipelineDeferredRequirementIndices;
            HashSet<int> optionalRequirements =
                scratch.PipelineOptionalDeferredRequirementIndices;
            ulong manifestIdentity =
                pipelineVariantManifest.CompatibilityIdentity;
            if (scratch.PipelineDeferredManifestIdentity != manifestIdentity)
            {
                scratch.PipelineDeferredManifestIdentity = manifestIdentity;
                scratch.PipelinePrewarmRequirementCursor = 0;
                scratch.PipelinePrewarmInitialScanComplete = false;
                pendingRequirements.Clear();
                optionalRequirements.Clear();
            }

            AddOptionalPipelineDeferrals(
                recordingState.PipelineDeferredOperationIndices,
                optionalRequirements,
                pipelineVariantManifest);

            int requirementCount =
                pipelineVariantManifest.Requirements.Count;
            int requirementCursor =
                scratch.PipelinePrewarmRequirementCursor;
            bool initialScanComplete =
                scratch.PipelinePrewarmInitialScanComplete;
            int firstPendingRequirementIndex = -1;
            string firstPendingReason = string.Empty;
            long sliceStart = Stopwatch.GetTimestamp();
            bool foregroundRequired =
                recordingState.Policy.FreshSerialRecording &&
                recordingState.Policy.IsPresentNow &&
                recordingState.Policy.ReadinessPolicy ==
                    ERenderOutputReadinessPolicy.BlockForExact;
            using (VulkanCpuStageScope cpuStage =
                   new(_frameTelemetry, EVulkanCpuStage.PrimaryPrewarm))
            {
                while (requirementCursor < requirementCount)
                {
                    int requirementIndex = requirementCursor++;
                    bool shouldCheck = !initialScanComplete ||
                        pendingRequirements.Contains(requirementIndex) ||
                        optionalRequirements.Contains(requirementIndex);
                    if (shouldCheck)
                    {
                        bool ready = TryPreparePrimaryPipelineRequirement(
                            ref recordingState,
                            pipelineVariantManifest.Requirements[
                                requirementIndex],
                            out bool optionalDeferred,
                            out _,
                            out string pendingReason);
                        int opIndex = pipelineVariantManifest.Requirements[
                            requirementIndex].OpIndex;
                        if (ready)
                        {
                            pendingRequirements.Remove(requirementIndex);
                            optionalRequirements.Remove(requirementIndex);
                            recordingState.PipelineDeferredOperationIndices.Remove(
                                opIndex);
                        }
                        else if (optionalDeferred)
                        {
                            pendingRequirements.Remove(requirementIndex);
                            optionalRequirements.Add(requirementIndex);
                            recordingState.PipelineDeferredOperationIndices.Add(
                                opIndex);
                        }
                        else
                        {
                            optionalRequirements.Remove(requirementIndex);
                            pendingRequirements.Add(requirementIndex);
                            if (firstPendingRequirementIndex < 0)
                            {
                                firstPendingRequirementIndex = requirementIndex;
                                firstPendingReason = pendingReason;
                            }
                        }
                    }

                    if (!foregroundRequired && Stopwatch.GetTimestamp() - sliceStart >=
                        PrimaryPipelinePrewarmSliceTicks)
                    {
                        break;
                    }
                }
            }

            bool reachedEnd = requirementCursor >= requirementCount;
            if (reachedEnd)
            {
                requirementCursor = 0;
                initialScanComplete = true;
            }
            scratch.PipelinePrewarmRequirementCursor = requirementCursor;
            scratch.PipelinePrewarmInitialScanComplete = initialScanComplete;

            AddOptionalPipelineDeferrals(
                recordingState.PipelineDeferredOperationIndices,
                optionalRequirements,
                pipelineVariantManifest);

            if (!initialScanComplete)
            {
                recordingState.RecordingDeferredReason =
                    "Graphics pipeline admission is continuing within its pre-recording CPU budget.";
                RecordPrimaryPipelineAdmissionDeferred(
                    requirementCursor,
                    requirementCount,
                    pendingRequirements.Count,
                    firstPendingRequirementIndex,
                    firstPendingReason);
                return false;
            }

            if (pendingRequirements.Count > 0)
            {
                recordingState.RecordingDeferredReason =
                    "Graphics pipeline compilation is still pending before vkBeginCommandBuffer.";
                RecordPrimaryPipelineAdmissionDeferred(
                    requirementCursor,
                    requirementCount,
                    pendingRequirements.Count,
                    firstPendingRequirementIndex,
                    firstPendingReason);
                return false;
            }

            if (!TryAssociatePrimaryMeshTaskPipelines(
                    ref recordingState,
                    pipelineVariantManifest))
            {
                return false;
            }

            if (optionalRequirements.Count == 0)
                pipelineVariantManifest.MarkWarmupCompleted();
            return true;
        }

        /// <summary>
        /// Re-associates the exact target-compatible pipeline with each newly
        /// lowered mesh-task payload even when the shared variant manifest is
        /// already warm. Unlike ordinary mesh draws, mesh-task operations are
        /// authored with an empty pipeline and receive their immutable native
        /// pipeline only at this pre-recording admission boundary.
        /// </summary>
        private bool TryAssociatePrimaryMeshTaskPipelines(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest pipelineVariantManifest)
        {
            int requirementIndex = 0;
            int requirementCount = pipelineVariantManifest.Requirements.Count;
            for (int opIndex = 0; opIndex < recordingState.Ops.Length; opIndex++)
            {
                if (recordingState.Ops.GetHeader(opIndex).OpCode !=
                    EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
                    continue;

                while (requirementIndex < requirementCount &&
                       pipelineVariantManifest.Requirements[requirementIndex]
                           .OpIndex < opIndex)
                    requirementIndex++;

                if (requirementIndex >= requirementCount ||
                    pipelineVariantManifest.Requirements[requirementIndex]
                        .OpIndex != opIndex)
                {
                    recordingState.RecordingDeferredReason =
                        "The warm pipeline manifest does not contain a directly recorded mesh-task operation.";
                    return false;
                }

                VulkanPipelineVariantRequirement requirement =
                    pipelineVariantManifest.Requirements[requirementIndex++];

                if (TryPreparePrimaryMeshTaskPipelineRequirement(
                        ref recordingState,
                        in requirement,
                        out _,
                        out _,
                        out string pendingReason))
                {
                    recordingState.PipelineDeferredOperationIndices.Remove(opIndex);
                    continue;
                }

                recordingState.RecordingDeferredReason =
                    $"Mesh-task pipeline association deferred command recording: {pendingReason}";
                return false;
            }

            return true;
        }

        private bool TryPreparePrimaryPipelineRequirement(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            in VulkanPipelineVariantRequirement requirement,
            out bool optionalDeferred,
            out bool retryable,
            out string pendingReason)
        {
            optionalDeferred = false;
            retryable = false;
            pendingReason = string.Empty;
            int opIndex = requirement.OpIndex;
            if ((uint)opIndex >= (uint)recordingState.Ops.Length)
            {
                pendingReason = "operation is outside the sealed frame plan";
                return false;
            }

            ref readonly FrameOperationHeader operationHeader =
                ref recordingState.Ops.GetHeader(opIndex);
            using VulkanPreparedResourcePlannerThreadScope resourceScope =
                EnterRecordingResourceScope(recordingState.FramePlan, recordingState.Ops.GetContext(opIndex));
            if (operationHeader.OpCode ==
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
            {
                return TryPreparePrimaryMeshTaskPipelineRequirement(
                    ref recordingState,
                    in requirement,
                    out optionalDeferred,
                    out retryable,
                    out pendingReason);
            }

            PendingMeshDraw pendingDraw = operationHeader.OpCode switch
            {
                EVulkanPrimaryPlanNodeKind.MeshDraw =>
                    recordingState.Ops.GetMeshDraw(opIndex).Draw,
                EVulkanPrimaryPlanNodeKind.IndirectDraw =>
                    recordingState.Ops.GetIndirectDraw(opIndex).Draw,
                _ => default,
            };
            VkMeshRenderer? meshRenderer = pendingDraw.Renderer;
            if (meshRenderer is null)
            {
                optionalDeferred = !requirement.Required;
                retryable = true;
                pendingReason = "mesh renderer is not prepared";
                return false;
            }

            int pipelinePassIndex = operationHeader.PassIndex;
            if (pipelinePassIndex == int.MinValue ||
                CanSkipScheduledCommandChainPipelinePrewarm(
                    ref recordingState,
                    opIndex))
            {
                return true;
            }

            ulong preparationSignature =
                ComputePipelinePreparationLedgerSignature(
                    requirement.PreparationCompatibilitySignature,
                    recordingState.ResourcePlanStamp.ResourceAllocationSignature);
            HashSet<ulong> admittedSignatures = recordingState.RecordingScratch
                .AdmittedPipelinePreparationSignatures;
            if (preparationSignature != 0 &&
                admittedSignatures.Contains(preparationSignature))
            {
                return true;
            }

            XRFrameBuffer? target = recordingState.Ops.GetTarget(opIndex);
            FrameOpContext operationContext =
                recordingState.Ops.GetContext(opIndex);
            using var pipelineScope =
                RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                    operationContext.PipelineInstance);
            VulkanCompiledRenderGraph operationGraph =
                recordingState.RenderGraphPlan.CompiledGraph;
            if (recordingState.FramePlan is not null &&
                recordingState.FramePlan.TryResolveRenderGraphPlan(
                    in operationContext,
                    out VulkanRenderGraphPlan operationPlan))
            {
                operationGraph = operationPlan.CompiledGraph;
            }

            if (!TryResolveGraphicsPipelinePrewarmTarget(
                    target,
                    pipelinePassIndex,
                    operationContext,
                    recordingState.SwapchainTarget,
                    recordingState.Policy.UseDynamicRendering,
                    operationGraph,
                    out bool useDynamicRendering,
                    out RenderPass prewarmRenderPass,
                    out DynamicRenderingFormatSignature
                        prewarmDynamicRenderingFormats,
                    out _,
                    out bool depthStencilReadOnly,
                    out string targetReason))
            {
                optionalDeferred = !requirement.Required;
                pendingReason = targetReason;
                return false;
            }

            if (meshRenderer.TryPrewarmGraphicsPipelinesForRecording(
                    pendingDraw,
                    prewarmRenderPass,
                    useDynamicRendering,
                    prewarmDynamicRenderingFormats,
                    pipelinePassIndex,
                    operationContext.PassMetadata,
                    depthStencilReadOnly,
                    operationContext.PipelineInstance?.DebugName ??
                        "<no pipeline>",
                    foregroundRequired:
                        recordingState.Policy.FreshSerialRecording &&
                        recordingState.Policy.IsPresentNow &&
                        recordingState.Policy.ReadinessPolicy ==
                            ERenderOutputReadinessPolicy.BlockForExact,
                    out retryable,
                    out string pipelineReason))
            {
                if (preparationSignature != 0)
                {
                    if (admittedSignatures.Count >=
                            CommandBufferRecordingScratch
                                .MaxAdmittedPipelinePreparationSignatures &&
                        !admittedSignatures.Contains(preparationSignature))
                    {
                        admittedSignatures.Clear();
                    }

                    admittedSignatures.Add(preparationSignature);
                }
                return true;
            }

            // Even an optional producer cannot be omitted after its pass/resource
            // transitions have been admitted. Defer the whole sealed graph until
            // the variant is executable rather than publishing speculative layouts.
            pendingReason = pipelineReason;
            return false;
        }

        /// <summary>
        /// Admits task/mesh pipelines from their sealed payload rather than a
        /// renderer draw. The producer snapshot carries the intended target;
        /// using the numeric operation target here would incorrectly select the
        /// default swapchain target because mesh-task operations are targetless
        /// authoring operations by design.
        /// </summary>
        private bool TryPreparePrimaryMeshTaskPipelineRequirement(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            in VulkanPipelineVariantRequirement requirement,
            out bool optionalDeferred,
            out bool retryable,
            out string pendingReason)
        {
            optionalDeferred = false;
            retryable = false;
            pendingReason = string.Empty;
            int opIndex = requirement.OpIndex;
            ref readonly MeshTaskDispatchIndirectCountPayload meshTask =
                ref recordingState.Ops.GetMeshTask(opIndex);
            if (meshTask.Program is null ||
                meshTask.ProgramLinkGeneration != meshTask.Program.LinkGeneration)
            {
                optionalDeferred = !requirement.Required;
                retryable = true;
                pendingReason = "mesh-task program generation changed after the frame plan was sealed";
                return false;
            }

            int pipelinePassIndex = requirement.PassIndex;
            if (pipelinePassIndex == int.MinValue)
            {
                pendingReason = "mesh-task pipeline admission has no valid render pass";
                return false;
            }

            HashSet<ulong> admittedSignatures = recordingState.RecordingScratch
                .AdmittedPipelinePreparationSignatures;

            FrameOpContext operationContext = recordingState.Ops.GetContext(opIndex);
            using var pipelineScope = RuntimeEngine.Rendering.State
                .PushRenderingPipelineOverride(operationContext.PipelineInstance);
            VulkanCompiledRenderGraph operationGraph =
                recordingState.RenderGraphPlan.CompiledGraph;
            if (recordingState.FramePlan is not null &&
                recordingState.FramePlan.TryResolveRenderGraphPlan(
                    in operationContext,
                    out VulkanRenderGraphPlan operationPlan))
            {
                operationGraph = operationPlan.CompiledGraph;
            }

            if (!TryResolveGraphicsPipelinePrewarmTarget(
                    meshTask.ProducerSnapshot.Target,
                    pipelinePassIndex,
                    operationContext,
                    recordingState.SwapchainTarget,
                    recordingState.Policy.UseDynamicRendering,
                    operationGraph,
                    out bool useDynamicRendering,
                    out RenderPass prewarmRenderPass,
                    out DynamicRenderingFormatSignature
                        prewarmDynamicRenderingFormats,
                    out SampleCountFlags rasterizationSamples,
                    out bool depthStencilReadOnly,
                    out string targetReason))
            {
                optionalDeferred = !requirement.Required;
                pendingReason = targetReason;
                return false;
            }

            ulong preparationSignature =
                ComputeMeshTaskPipelinePreparationLedgerSignature(
                    requirement.PreparationCompatibilitySignature,
                    recordingState.ResourcePlanStamp.ResourceAllocationSignature,
                    rasterizationSamples);

            if (!VulkanMeshTaskDrawProducer.TryAdmitPrimaryPipeline(
                    this,
                    meshTask.Program,
                    meshTask.ProducerSnapshot,
                    pipelinePassIndex,
                    operationContext.PassMetadata,
                    prewarmRenderPass,
                    useDynamicRendering,
                    prewarmDynamicRenderingFormats,
                    rasterizationSamples,
                    depthStencilReadOnly,
                    out Pipeline pipeline,
                    out string pipelineReason))
            {
                // A required producer cannot be omitted after the graph's
                // attachment transitions were admitted. Keep the sealed graph
                // intact and defer before vkBeginCommandBuffer.
                pendingReason = pipelineReason;
                return false;
            }

            if (!recordingState.Ops.TryAssociateAdmittedMeshTaskPipeline(
                    opIndex,
                    meshTask.Program,
                    meshTask.ProgramLinkGeneration,
                    meshTask.ProgramBindingSnapshot,
                    meshTask.ProducerSnapshot,
                    pipeline))
            {
                pendingReason = "mesh-task pipeline association rejected because the sealed payload changed";
                return false;
            }

            if (preparationSignature != 0)
            {
                if (admittedSignatures.Count >=
                        CommandBufferRecordingScratch
                            .MaxAdmittedPipelinePreparationSignatures &&
                    !admittedSignatures.Contains(preparationSignature))
                {
                    admittedSignatures.Clear();
                }

                admittedSignatures.Add(preparationSignature);
            }
            return true;
        }

        private static ulong ComputePipelinePreparationLedgerSignature(
            ulong requirementSignature,
            ulong resourceAllocationSignature)
        {
            if (requirementSignature == 0)
                return 0;

            var hash = new VulkanStableHash64(schemaVersion: 1);
            hash.Add(requirementSignature);
            hash.Add(resourceAllocationSignature);
            return hash.Value;
        }

        private static ulong ComputeMeshTaskPipelinePreparationLedgerSignature(
            ulong requirementSignature,
            ulong resourceAllocationSignature,
            SampleCountFlags rasterizationSamples)
        {
            ulong baseSignature = ComputePipelinePreparationLedgerSignature(
                requirementSignature,
                resourceAllocationSignature);
            if (baseSignature == 0)
                return 0;

            var hash = new VulkanStableHash64(schemaVersion: 1);
            hash.Add(baseSignature);
            hash.Add((uint)rasterizationSamples);
            return hash.Value;
        }

        private static void AddOptionalPipelineDeferrals(
            HashSet<int> deferredOperationIndices,
            HashSet<int> optionalRequirementIndices,
            VulkanPipelineVariantManifest pipelineVariantManifest)
        {
            foreach (int requirementIndex in optionalRequirementIndices)
            {
                if ((uint)requirementIndex >=
                    (uint)pipelineVariantManifest.Requirements.Count)
                {
                    continue;
                }

                deferredOperationIndices.Add(
                    pipelineVariantManifest.Requirements[
                        requirementIndex].OpIndex);
            }
        }

        private void RecordPrimaryPipelineAdmissionDeferred(
            int requirementCursor,
            int requirementCount,
            int pendingRequirementCount,
            int firstPendingRequirementIndex,
            string firstPendingReason)
        {
            Debug.VulkanWarningEvery(
                "Vulkan.Primary.PipelineAdmissionDeferred",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Primary command recording yielded before vkBeginCommandBuffer. progress={0}/{1} pending={2} firstPending={3} reason={4}",
                requirementCursor,
                requirementCount,
                pendingRequirementCount,
                firstPendingRequirementIndex,
                firstPendingReason.Length == 0
                    ? "not sampled in this slice"
                    : firstPendingReason);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
                EVulkanPipelineTelemetryEvent.RequiredPipelineRecordDeferred);
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
                recordingState.InitialContext = recordingState.Ops.GetContext(0);
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
                ? recordingState.Policy.FinalTargetLayout
                : ImageLayout.ColorAttachmentOptimal;
            recordingState.SwapchainFinalLayout = recordingState.InitialSwapchainColorLayout;

            if (recordingState.Ops.Length > 0)
            {
                FrameOpContext firstContext = recordingState.Ops.GetContext(0);
                recordingState.RenderGraphPlan = ResolvePrimaryRenderGraphPlan(
                    ref recordingState,
                    in firstContext);
            }

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
