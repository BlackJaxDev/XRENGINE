using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen primary-recording preparation owned by the desktop frame loop.</summary>
internal sealed partial class VulkanFrameLoop
{
    // Cold shader/program/buffer preparation persists on each mesh wrapper, so
    // a small per-frame slice converges without exposing a partial scene. Warm
    // materialization is not charged to this budget and retains normal throughput.
    private static readonly long ColdMeshPreparationSliceTicks =
        Math.Max(1L, Stopwatch.Frequency / 250L);

    private VulkanPrimaryCommandRecordingResult RecordPreparedDesktopPrimary(
        ref VulkanFrameAttempt attempt,
        uint imageIndex,
        bool preserveSwapchainForImGuiOverlay)
    {
        _ = attempt.CompletePhase(
            EVulkanFrameStage.PlanBuild,
            EDesktopFrameFlow.Continue);
        CommandBuffer[] primaryBuffers = _commandRuntime.CommandBuffers.Buffers
            ?? throw new InvalidOperationException(
                "Desktop primary command buffers are not initialized.");
        CommandBuffer[] dynamicUiBuffers = _commandRuntime.CommandBuffers.DynamicUiSecondaries
            ?? throw new InvalidOperationException(
                "Desktop dynamic UI command buffers are not initialized.");
        VulkanPrimaryCommandPlan[] primaryPlans = _commandRuntime.CommandBuffers.PrimaryPlans
            ?? throw new InvalidOperationException(
                "Desktop primary command plans are not initialized.");
        if (imageIndex >= primaryBuffers.Length ||
            imageIndex >= dynamicUiBuffers.Length ||
            imageIndex >= primaryPlans.Length)
        {
            throw new InvalidOperationException(
                $"Desktop image index {imageIndex} has no command artifact slot.");
        }

        FrameOp[] textureUploadOperations;
        FrameOp[] staticOperations;
        FrameOp[] dynamicUiOperations;
        VulkanFramePlanningSnapshot planningSnapshot;
        bool meshMaterializationComplete;
        string meshMaterializationDeferredReason;
        using (VulkanCpuStageScope preparationStage = new(
                   _telemetry,
                   EVulkanCpuStage.FrameOpPreparation))
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.MaterializeQueuedMeshes"))
            {
                meshMaterializationComplete = DrainQueuedMeshRenderRequests(
                    out meshMaterializationDeferredReason);
            }

            FrameOp[] drainedOperations;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.Drain"))
            {
                drainedOperations = _framePlanner.Operations.DrainForPrimary(
                    out textureUploadOperations);
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.CapturePlanningSnapshot"))
            {
                planningSnapshot = _framePlanner.CaptureSnapshot();
            }

            FrameOp[] sortedOperations = drainedOperations;

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.CoalesceContexts"))
            {
                VulkanSwapchainContextCoalescer.Coalesce(sortedOperations);
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.SplitUi"))
            {
                SplitPreparedDynamicUiOperations(
                    sortedOperations,
                    out staticOperations,
                    out dynamicUiOperations);
                if (!meshMaterializationComplete)
                {
                    // No subset of a scene is publishable. Dynamic text remains
                    // eligible for a recovery secondary and texture uploads remain
                    // eligible for the recovery submit while cold resources converge.
                    staticOperations = [];
                }
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.NormalizePasses"))
            {
                _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
                    staticOperations);
                _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
                    dynamicUiOperations);
            }
        }
        bool preserveSwapchainForOverlay =
            preserveSwapchainForImGuiOverlay || dynamicUiOperations.Length > 0;

        _ = attempt.CompletePhase(
            EVulkanFrameStage.ResourcePrepare,
            EDesktopFrameFlow.Continue);
        VulkanComputePreparationResult computePreparation =
            _commandRuntime.PrepareComputeProgramsForFramePlan(
                staticOperations);
        if (computePreparation.Succeeded)
        {
            computePreparation = _commandRuntime.PrepareComputeProgramsForFramePlan(
                dynamicUiOperations);
        }
        if (!computePreparation.Succeeded)
            return VulkanPrimaryCommandRecordingResult.Deferred(
                computePreparation.FormatFailure());

        bool freshSerialRecording =
            RuntimeRenderingHostServices.Settings.VulkanCommandRecordingMode ==
            EVulkanCommandRecordingMode.FreshSerial;
        bool allowSynchronousResourceUploads =
            _resourceRuntime.AllowSynchronousResourceUploads;
        VulkanPrimaryCommandPlan primaryPlan = primaryPlans[imageIndex];
        string replanReason = string.Empty;
        for (int replanAttempt = 0; replanAttempt < 2; replanAttempt++)
        {
            ResourcePlannerRuntimeState plannerState =
                PublishedResourcePlannerRuntimeState;
            planningSnapshot = _framePlanner.CaptureSnapshot();
            if (!TryBindPreparedStreamlineUiImage(
                    imageIndex,
                    staticOperations,
                    out string streamlinePreparationFailure))
            {
                replanReason = streamlinePreparationFailure;
                continue;
            }
            if (planningSnapshot.RenderGraphPlan.Revision !=
                plannerState.ResourcePlannerRevision)
            {
                replanReason =
                    $"Planner publication changed while preparing resource revision " +
                    $"{plannerState.ResourcePlannerRevision}; captured graph revision " +
                    $"{planningSnapshot.RenderGraphPlan.Revision}.";
                continue;
            }
            if (!TryPrepareFrameOperationTargets(
                    staticOperations,
                    allowSynchronousResourceUploads,
                    out string targetPreparationFailure) ||
                !TryPrepareFrameOperationTargets(
                    dynamicUiOperations,
                    allowSynchronousResourceUploads,
                    out targetPreparationFailure))
            {
                replanReason = targetPreparationFailure;
                continue;
            }
            if (!TryFreezeNativeBarrierBindings(
                    in planningSnapshot,
                    in plannerState,
                    allowSynchronousResourceUploads,
                    out VulkanFramePlanningSnapshot frozenPlanningSnapshot,
                    out string resourcePreparationFailure))
            {
                replanReason = resourcePreparationFailure;
                continue;
            }

            if (!TryCapturePreparedPrimaryAuthority(
                    imageIndex,
                    in plannerState,
                    in frozenPlanningSnapshot,
                    preserveSwapchainForOverlay,
                    transitionSwapchainToPresent: true,
                    allowSynchronousResourceUploads,
                    freshSerialRecording,
                    _commandRuntime.StateTracker.ClearColor,
                    out VulkanPreparedPrimaryAuthority authority,
                    out string authorityFailure))
            {
                replanReason = authorityFailure;
                continue;
            }

            FramePlan framePlan;
            using (VulkanCpuStageScope packetConstructionStage = new(
                       _telemetry,
                       EVulkanCpuStage.PacketConstruction))
            {
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                           "Vulkan.BuildFramePlan.Seal"))
                {
                    framePlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                        CurrentFrameSlot,
                        plannerState.ResourcePlannerRevision,
                        staticOperationSignature: 0UL,
                        dynamicOverlaySignature: 0UL,
                        staticOperations,
                        dynamicUiOperations,
                        new VulkanFramePlanRenderGraphAuthority(
                            frozenPlanningSnapshot.RenderGraphPlan,
                            plannerState.FrameOpResourcePlannerSwitchingState),
                        textureUploadOperations: textureUploadOperations);
                }
            }
            FrameOperationSequence preparedOperations =
                framePlan.GetNativeStaticOperationsForRecording();
            computePreparation = _commandRuntime.PrepareComputeFrameOpsForRecording(
                imageIndex,
                preparedOperations);
            if (computePreparation.Succeeded)
            {
                computePreparation = _commandRuntime.PrepareComputeFrameOpsForRecording(
                    imageIndex,
                    framePlan.GetNativeDynamicOverlayOperationsForRecording());
            }
            if (!computePreparation.Succeeded)
                return VulkanPrimaryCommandRecordingResult.Deferred(
                    computePreparation.FormatFailure());

            _ = attempt.CompletePhase(
                EVulkanFrameStage.WorkSchedule,
                EDesktopFrameFlow.Continue);
            primaryPlan.Build(
                preparedOperations.Stream,
                framePlan.StaticOperationSignature,
                new VulkanPrimaryPlanTerminalContext(
                    preserveSwapchainForOverlay,
                    TransitionSwapchainToPresent: true,
                    ReleaseExternalImageOwnership: false),
                framePlan: framePlan);

            VulkanPreparedPrimaryCommandInput input =
                PreparePrimaryCommandInput(
                    imageIndex,
                    primaryBuffers[imageIndex],
                    dynamicUiBuffers[imageIndex],
                    framePlan,
                    primaryPlan,
                    in authority);

            _ = attempt.CompletePhase(
                EVulkanFrameStage.CommandRecord,
                EDesktopFrameFlow.Continue);
            VulkanPrimaryCommandRecordingResult result;
            using (VulkanCpuStageScope primaryRecordingStage = new(
                       _telemetry,
                       EVulkanCpuStage.PrimaryRecording))
            {
                result = _commandRuntime.RecordPrimary(in input);
            }
            if (!result.RequiresReplan)
            {
                if (meshMaterializationComplete)
                    return result;

                return result with
                {
                    Disposition = EVulkanPrimaryCommandRecordingDisposition.Deferred,
                    CommandBuffer = default,
                    SwapchainLayoutAfterCommandBuffer = ImageLayout.Undefined,
                    RecordedSwapchainWriteCount = 0,
                    Reason = meshMaterializationDeferredReason,
                };
            }
            replanReason = result.Reason ??
                "primary command recording requested a fresh plan";
        }

        return VulkanPrimaryCommandRecordingResult.Deferred(
            $"primary command recording exceeded the two-attempt replan limit: {replanReason}");
    }

    /// <summary>
    /// Materializes framebuffer wrappers during resource preparation. Command
    /// recording is lookup-only, so clear-only and newly rebuilt shadow targets
    /// must not be the first consumers to request their backend identity.
    /// </summary>
    private bool TryPrepareFrameOperationTargets(
        FrameOp[] operations,
        bool allowSynchronousResourceUploads,
        out string reason)
    {
        for (int index = 0; index < operations.Length; index++)
        {
            XRFrameBuffer? target = operations[index].Target;
            if (target is null)
                continue;

            VkFrameBuffer? wrapper = _resourceRuntime.CreateAPIRenderObject(target) as VkFrameBuffer;
            if (wrapper is null)
            {
                reason = $"Failed to create the Vulkan framebuffer wrapper for target '{target.GetDescribingName()}'.";
                return false;
            }

            if (!wrapper.IsGenerated && allowSynchronousResourceUploads)
                wrapper.Generate();
            if (!wrapper.IsGenerated)
            {
                reason = $"Vulkan framebuffer target '{target.GetDescribingName()}' is not ready for command recording.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Converts wrapper-owned render events into prepared mesh operations at the only
    /// point where frame, output, command, and planner state are jointly authoritative.
    /// </summary>
    private bool DrainQueuedMeshRenderRequests(out string deferredReason)
    {
        deferredReason = string.Empty;
        int requestCount = MeshOperationRequests.DrainTo(
            _meshOperationRequestScratch);
        if (requestCount == 0)
            return true;

        long coldPreparationTicks = 0;
        int deferredRequestCount = 0;
        int unavailableRequestCount = 0;
        int warmRequestCount = 0;
        int coldRequestCount = 0;
        int resumeRequestIndex = -1;
        int startRequestIndex = _meshOperationPreparationCursor % requestCount;
        ResourcePlannerRuntimeState plannerState =
            PublishedResourcePlannerRuntimeState;
        FrameOpContext? activeFrameOpContext =
            plannerState.LastActiveFrameOpContext;
        int descriptorViewFamilyIdentity =
            activeFrameOpContext is not { } activeContext
                ? 0
                : activeContext.OutputTargetIdentity != 0
                    ? activeContext.OutputTargetIdentity
                    : activeContext.ViewportIdentity;
        VulkanMeshMaterializationSnapshot materializationSnapshot = new(
            activeFrameOpContext,
            descriptorViewFamilyIdentity,
            _resourceRuntime.ShouldAvoidSynchronousImageAllocationForOpenXr(
                _deviceContext.Api,
                _deviceContext,
                RuntimeRenderingHostServices.Presentation,
                RuntimeRenderingHostServices.FrameTiming,
                out _),
            _telemetry);
        XRRenderPipelineInstance? scopedPipeline = null;
        XRCamera? scopedCamera = null;
        IDisposable? pipelineScope = null;
        IDisposable? cameraScope = null;
        try
        {
            for (int scanIndex = 0;
                 scanIndex < requestCount;
                 scanIndex++)
            {
                int requestIndex = startRequestIndex + scanIndex;
                if (requestIndex >= requestCount)
                    requestIndex -= requestCount;

                ref readonly VulkanMeshRenderRequest request =
                    ref _meshOperationRequestScratch[requestIndex];
                XRRenderPipelineInstance? pipeline = request.Pipeline;
                if (pipeline is null)
                    continue;

                XRCamera? camera = pipeline.LastRenderingCamera ??
                    pipeline.LastSceneCamera;
                if (!ReferenceEquals(scopedPipeline, pipeline) ||
                    !ReferenceEquals(scopedCamera, camera))
                {
                    cameraScope?.Dispose();
                    cameraScope = null;
                    pipelineScope?.Dispose();
                    pipelineScope =
                        RuntimeRenderingHostServices.Diagnostics
                            .PushRenderingPipeline(pipeline);
                    cameraScope =
                        pipeline.RenderState.PushRenderingCamera(camera);
                    scopedPipeline = pipeline;
                    scopedCamera = camera;
                }

                bool dynamicUiOverlay = IsQueuedDynamicUiOverlayRequest(
                    in request);
                bool previouslyMaterialized =
                    request.PreparationCompatibilitySignature != 0 &&
                    _meshOperationWarmPreparationSignatures.Contains(
                        request.PreparationCompatibilitySignature);
                if (previouslyMaterialized)
                    warmRequestCount++;
                else
                    coldRequestCount++;
                bool resourcesReady = previouslyMaterialized;
                if (!dynamicUiOverlay &&
                    !resourcesReady &&
                    coldPreparationTicks >= ColdMeshPreparationSliceTicks)
                {
                    deferredRequestCount++;
                    if (resumeRequestIndex < 0)
                        resumeRequestIndex = requestIndex;
                    continue;
                }

                long preparationStart = resourcesReady
                    ? 0L
                    : Stopwatch.GetTimestamp();
                bool materialized = TryMaterializeQueuedMeshRenderRequest(
                    in request,
                    pipeline,
                    in materializationSnapshot,
                    prewarmDescriptorAllocation: !previouslyMaterialized,
                    out VulkanMeshOperationRequest operationRequest);
                if (!resourcesReady)
                {
                    coldPreparationTicks +=
                        Stopwatch.GetTimestamp() - preparationStart;
                }

                if (!materialized)
                {
                    _meshOperationWarmPreparationSignatures.Remove(
                        request.PreparationCompatibilitySignature);
                    unavailableRequestCount++;
                    if (resumeRequestIndex < 0)
                        resumeRequestIndex = requestIndex;
                    continue;
                }

                if (request.PreparationCompatibilitySignature != 0)
                {
                    // Keep the hot-path cache finite and pre-sized. Reaching this
                    // bound requires several complete queue cohorts of distinct
                    // structural variants; clearing is a recoverable cold event.
                    if (_meshOperationWarmPreparationSignatures.Count >=
                            MaxWarmMeshPreparationSignatures &&
                        !_meshOperationWarmPreparationSignatures.Contains(
                            request.PreparationCompatibilitySignature))
                    {
                        _meshOperationWarmPreparationSignatures.Clear();
                    }

                    _meshOperationWarmPreparationSignatures.Add(
                        request.PreparationCompatibilitySignature);
                }
                EnqueueQueuedMeshDraw(in operationRequest);
            }
        }
        finally
        {
            cameraScope?.Dispose();
            pipelineScope?.Dispose();
            _meshOperationRequestScratch.AsSpan(0, requestCount).Clear();
        }

        // A stable visible cohort can contain hundreds of cold requests. Resume at
        // the first request that did not finish so a bounded slice cannot starve the
        // tail by repeatedly beginning at request zero.
        _meshOperationPreparationCursor = resumeRequestIndex >= 0
            ? resumeRequestIndex
            : 0;

        if (deferredRequestCount == 0 && unavailableRequestCount == 0)
            return true;

        deferredReason =
            $"Mesh resource preparation yielded before publishing a partial scene. " +
            $"deferred={deferredRequestCount} unavailable={unavailableRequestCount} " +
            $"requests={requestCount} warm={warmRequestCount} cold={coldRequestCount}.";
        Debug.VulkanWarningEvery(
            "Vulkan.MeshMaterialization.Deferred",
            TimeSpan.FromSeconds(1),
            "[Vulkan] {0}",
            deferredReason);
        return false;
    }

    private bool TryMaterializeQueuedMeshRenderRequest(
        in VulkanMeshRenderRequest request,
        XRRenderPipelineInstance pipeline,
        in VulkanMeshMaterializationSnapshot materializationSnapshot,
        bool prewarmDescriptorAllocation,
        out VulkanMeshOperationRequest operationRequest)
    {
        FrameOpContext requestContext =
            request.Context.PipelineInstance is not null
                ? request.Context
                : CreateFrameOpContext(
                    pipeline,
                    pipeline.LastWindowViewport);
        VulkanMeshProducerSnapshot producer = request.Producer with
        {
            Context = requestContext,
            IsExternalSwapchainTarget =
                TryResolveExternalSwapchainTargetExtent(out _),
            IsPrewarmingExternalSwapchainTarget =
                IsPrewarmingOpenXrExternalSwapchainTarget,
        };
        return request.Renderer.TryMaterializeQueuedRenderRequest(
            in request,
            in producer,
            in materializationSnapshot,
            prewarmDescriptorAllocation,
            out operationRequest);
    }

    private static bool IsQueuedDynamicUiOverlayRequest(
        in VulkanMeshRenderRequest request)
    {
        XRMeshRenderer meshRenderer = request.Renderer.MeshRenderer;
        XRMaterial? material = request.MaterialOverride ?? meshRenderer.Material;
        return string.Equals(
                   material?.Name,
                   "UIBatchTextMaterial",
                   StringComparison.Ordinal) ||
               string.Equals(
                   meshRenderer.Name,
                   "UIBatchTextRenderer",
                   StringComparison.Ordinal) ||
               string.Equals(
                   meshRenderer.Mesh?.Name,
                   "UIBatchTextQuadMesh",
                   StringComparison.Ordinal);
    }

    private void EnqueueQueuedMeshDraw(in VulkanMeshOperationRequest request)
    {
        int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(
            request.PassIndex,
            nameof(MeshDrawOp),
            request.Context.PassMetadata);
        if (passIndex == int.MinValue)
            return;

        MeshDrawOp operation = MeshDrawOp.Rent(
            passIndex,
            request.ExplicitTarget ?? request.ProducerSnapshot.Target,
            request.Draw,
            request.Context,
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth > 0);
        _commandRuntime.EnqueueFrameOperation(_frameOperationQueue, operation, passIndex);
    }

    /// <summary>
    /// Binds output-owned DLSS-G UI resources before plan sealing so command
    /// recording consumes only the frozen operation payload.
    /// </summary>
    private bool TryBindPreparedStreamlineUiImage(
        uint imageIndex,
        FrameOp[] staticOperations,
        out string reason)
    {
        reason = string.Empty;
        bool requiresUiImage = false;
        for (int index = 0; index < staticOperations.Length; index++)
        {
            if (staticOperations[index] is DlssFrameGenerationOp)
            {
                requiresUiImage = true;
                break;
            }
        }

        if (!requiresUiImage)
            return true;

        if (!OutputRuntime.TryCaptureStreamlineUiImage(
                imageIndex,
                out VulkanStreamlineImage uiImage))
        {
            reason =
                $"DLSS frame generation cannot freeze the UI attachment for acquired image {imageIndex}.";
            return false;
        }

        ImageSubresourceRange colorRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        if (_commandRuntime.Synchronization.TryGetSubmittedImageLayout(
                uiImage.Image,
                in colorRange,
                out ImageLayout submittedLayout))
        {
            uiImage = uiImage with { Layout = submittedLayout };
        }

        for (int index = 0; index < staticOperations.Length; index++)
        {
            if (staticOperations[index] is DlssFrameGenerationOp frameGeneration)
            {
                FrameOp preparedOperation = frameGeneration with
                {
                    UiColorAndAlpha = uiImage,
                };
                // The queued producer has already published its resource-use
                // declaration. A record copy preserves that immutable declaration;
                // only the output-owned UI image changes for this acquired target.
                staticOperations[index] = preparedOperation;
            }
        }

        return true;
    }

    private void SplitPreparedDynamicUiOperations(
        FrameOp[] operations,
        out FrameOp[] staticOperations,
        out FrameOp[] dynamicUiOperations)
    {
        int dynamicCount = 0;
        for (int index = 0; index < operations.Length; index++)
            if (IsPreparedDynamicUiOverlayOperation(operations[index]))
                dynamicCount++;
        if (dynamicCount == 0)
        {
            staticOperations = operations;
            dynamicUiOperations = [];
            return;
        }

        _framePlanner.Operations.Diagnostics.EnsureSplitBuffers(
            operations.Length - dynamicCount,
            dynamicCount,
            out staticOperations,
            out dynamicUiOperations);
        int staticIndex = 0;
        int dynamicIndex = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            if (IsPreparedDynamicUiOverlayOperation(operation))
                dynamicUiOperations[dynamicIndex++] = operation;
            else
                staticOperations[staticIndex++] = operation;
        }
    }

    private static bool IsPreparedDynamicUiOverlayOperation(FrameOp operation)
    {
        if (operation is not MeshDrawOp drawOperation)
            return false;
        XRMeshRenderer meshRenderer = drawOperation.Draw.Renderer.MeshRenderer;
        XRMaterial? material = drawOperation.Draw.MaterialOverride ??
            meshRenderer.Material;
        if (string.Equals(material?.Name, "UIBatchTextMaterial", StringComparison.Ordinal) ||
            string.Equals(meshRenderer.Name, "UIBatchTextRenderer", StringComparison.Ordinal) ||
            string.Equals(meshRenderer.Mesh?.Name, "UIBatchTextQuadMesh", StringComparison.Ordinal))
        {
            return true;
        }

        return drawOperation.Target is null &&
            drawOperation.PassIndex == (int)EDefaultRenderPass.OnTopForward &&
            drawOperation.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline;
    }

    /// <summary>
    /// Captures the command-visible output and planning state before primary
    /// recording starts. Neither command runtime nor worker code is permitted
    /// to read output/planner state after this point.
    /// </summary>
    private VulkanPreparedPrimaryCommandInput PreparePrimaryCommandInput(
        uint imageIndex,
        CommandBuffer primaryCommandBuffer,
        CommandBuffer dynamicUiSecondaryCommandBuffer,
        FramePlan framePlan,
        VulkanPrimaryCommandPlan primaryCommandPlan,
        in VulkanPreparedPrimaryAuthority authority)
    {
        FrameOpSignatureHasher resourceVersionHasher = new();
        resourceVersionHasher.Add(framePlan.ResourceVersionSignature);
        resourceVersionHasher.Add(
            authority.ResourcePlanStamp.ResourceAllocationSignature);
        CommandChainSchedule? commandChainSchedule =
            _commandRuntime.TryBuildCommandChainSchedule(
                imageIndex,
                framePlan.StaticOperations,
                authority.Policy.PreserveSwapchainForOverlay
                    ? FrameOperationStream.Empty
                    : framePlan.DynamicOverlayOperations,
                framePlan.StaticOperationSignature,
                authority.Policy.PreserveSwapchainForOverlay
                    ? 0UL
                    : framePlan.DynamicOverlaySignature,
                framePlan.PlannerRevision,
                allowExternalSwapchainTarget: false,
                out _,
                preparedRecordingTarget: authority.RecordingTargetSnapshot,
                // Frame-op resource generations do not cover replacement of the
                // planner's native images during a resize. Include the published
                // allocation signature so cached secondaries and primaries cannot
                // retain retired image/framebuffer handles after that replacement.
                resourceVersionSignature: resourceVersionHasher.ToHash(),
                sharedResourceVersionSignature:
                    authority.ResourcePlanStamp.ResourceAllocationSignature,
                descriptorVersionSignature: framePlan.DescriptorVersionSignature);
        return new VulkanPreparedPrimaryCommandInput(
            imageIndex,
            primaryCommandBuffer,
            dynamicUiSecondaryCommandBuffer,
            framePlan,
            primaryCommandPlan,
            authority.RecordingTarget,
            authority.PresentationSource,
            authority.ResourcePlanStamp,
            authority.ClearState,
            authority.Policy,
            authority.TrackedTargetLayout,
            CommandChainSchedule: commandChainSchedule);
    }

    private bool TryCapturePreparedPrimaryAuthority(
        uint imageIndex,
        in ResourcePlannerRuntimeState plannerState,
        in VulkanFramePlanningSnapshot frozenPlanningSnapshot,
        bool preserveSwapchainForOverlay,
        bool transitionSwapchainToPresent,
        bool allowSynchronousResourceUploads,
        bool freshSerialRecording,
        ColorF4 clearColor,
        out VulkanPreparedPrimaryAuthority authority,
        out string reason)
    {
        authority = default;
        reason = string.Empty;

        Image desktopImage = OutputRuntime.Desktop.Images is not null &&
            imageIndex < OutputRuntime.Desktop.Images.Length
                ? OutputRuntime.Desktop.Images[imageIndex]
                : default;
        ImageSubresourceRange colorRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        _commandRuntime.Synchronization.TryGetSubmittedImageLayout(
            desktopImage,
            in colorRange,
            out ImageLayout trackedTargetLayout);

        VulkanSwapchainRecordingTargetInput targetInput = new(
            imageIndex,
            OpenXrTargetContext: null,
            OutputRuntime.DesktopDepthResources,
            OpenXrInitialColorLayout: ImageLayout.Undefined,
            DesktopInitialColorLayout: trackedTargetLayout);
        SwapchainRecordingTarget target = OutputRuntime.ResolveRecordingTarget(
            in targetInput);
        if (!target.IsValid)
        {
            reason =
                "The acquired desktop image no longer has a valid frozen recording target.";
            return false;
        }

        target = target with
        {
            RenderPass = ResourceRuntime.SwapchainRenderPass,
            LoadRenderPass = ResourceRuntime.SwapchainLoadRenderPass,
            Framebuffer = OutputRuntime.Desktop.Framebuffers is not null &&
                imageIndex < OutputRuntime.Desktop.Framebuffers.Length
                    ? OutputRuntime.Desktop.Framebuffers[imageIndex]
                    : default,
        };
        VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
            frozenPlanningSnapshot,
            plannerState.ResourcePlannerRevision,
            plannerState.ResourcePlannerSignature,
            plannerState.ResourceAllocationSignature);
        VulkanCommandRecordingPolicySnapshot policy = new(
            UseDynamicRenderingRenderTargets,
            allowSynchronousResourceUploads,
            freshSerialRecording,
            IsExternalSwapchainTarget: false,
            preserveSwapchainForOverlay,
            transitionSwapchainToPresent,
            PreferKhrDynamicRendering:
                OutputRuntime.Desktop.StreamlineFrameGenerationActive);
        authority = new VulkanPreparedPrimaryAuthority(
            target,
            CapturePreparedRenderTargetSnapshot(
                in target,
                OutputRuntime.Desktop.Generation),
            _windowPresentSource.CaptureForDescriptorSlot(
                checked((int)imageIndex)),
            resourcePlanStamp,
            new VulkanCommandClearStateSnapshot(
                clearColor,
                _commandRuntime.StateTracker.ClearDepth,
                _commandRuntime.StateTracker.ClearStencil,
                XREngine.Rendering.RenderDiagnosticsFlags
                    .VkForceSwapchainMagenta),
            policy,
            trackedTargetLayout);
        return true;
    }

    /// <summary>
    /// Resolves logical image and buffer barriers to immutable native bindings
    /// before command recording. The command runtime is prohibited from looking
    /// through mutable physical groups, the live allocator, or the planner.
    /// </summary>
    private bool TryFreezeNativeBarrierBindings(
        in VulkanFramePlanningSnapshot planningSnapshot,
        in ResourcePlannerRuntimeState plannerState,
        bool allowSynchronousResourceUploads,
        out VulkanFramePlanningSnapshot frozenSnapshot,
        out string reason)
    {
        VulkanRenderGraphPlan sourcePlan = planningSnapshot.RenderGraphPlan;
        VulkanBarrierPlan sourceBarriers = sourcePlan.Barriers;
        if (sourceBarriers.HasCompleteNativeBindings)
        {
            frozenSnapshot = planningSnapshot;
            reason = string.Empty;
            return true;
        }

        frozenSnapshot = default;
        reason = $"Prepared resource plan {plannerState.ResourcePlannerRevision} contains unresolved frozen barrier bindings.";
        return false;
    }

    private bool TryResolveFrozenBarrierBuffer(
        string resourceName,
        in ResourcePlannerRuntimeState plannerState,
        bool allowSynchronousResourceUploads,
        out Silk.NET.Vulkan.Buffer nativeBuffer,
        out ulong nativeSize)
    {
        if (plannerState.ResourceAllocator.TryGetBuffer(
                resourceName,
                out nativeBuffer,
                out nativeSize) &&
            nativeBuffer.Handle != 0)
        {
            nativeSize = Math.Max(nativeSize, 1UL);
            return true;
        }

        if (!_framePlanner.TrackedBuffersByName.TryGetValue(
                resourceName,
                out XRDataBuffer? dataBuffer) ||
            _resourceRuntime.BackendObjectContext is not { } backendContext ||
            backendContext.GetOrCreateAPIRenderObject(
                dataBuffer,
                generateNow: allowSynchronousResourceUploads) is not VkDataBuffer vkBuffer)
        {
            nativeBuffer = default;
            nativeSize = 0;
            return false;
        }

        if (allowSynchronousResourceUploads)
            vkBuffer.Generate();
        if (vkBuffer.BufferHandle is not { } resolvedBuffer || resolvedBuffer.Handle == 0)
        {
            nativeBuffer = default;
            nativeSize = 0;
            return false;
        }

        nativeBuffer = resolvedBuffer;
        nativeSize = Math.Max(dataBuffer.Length, 1u);
        return true;
    }

    private VulkanRecordedRenderTargetSnapshot CapturePreparedRenderTargetSnapshot(
        in SwapchainRecordingTarget target,
        ulong targetGeneration)
    {
        VulkanRecordedRenderTargetSnapshot snapshot = default;
        snapshot.Initialize(
            target.Framebuffer.Handle,
            targetGeneration,
            target.Extent.Width,
            target.Extent.Height,
            viewMask: 0u,
            attachmentCount: 2);
        snapshot.SetAttachment(
            0,
            new VulkanNativeAttachmentIdentity(
                target.Image.Handle,
                _resourceRuntime.GetPublishedGeneration(
                    ObjectType.Image,
                    target.Image.Handle),
                target.ImageView.Handle,
                _resourceRuntime.GetPublishedGeneration(
                    ObjectType.ImageView,
                    target.ImageView.Handle),
                ImageLayout.ColorAttachmentOptimal));
        snapshot.SetAttachment(
            1,
            new VulkanNativeAttachmentIdentity(
                target.DepthImage.Handle,
                _resourceRuntime.GetPublishedGeneration(
                    ObjectType.Image,
                    target.DepthImage.Handle),
                target.DepthView.Handle,
                _resourceRuntime.GetPublishedGeneration(
                    ObjectType.ImageView,
                    target.DepthView.Handle),
                ImageLayout.DepthStencilAttachmentOptimal));
        return snapshot;
    }
}
