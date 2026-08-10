using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen primary-recording preparation owned by the desktop frame loop.</summary>
internal sealed unsafe partial class VulkanFrameLoop
{
    private VulkanPrimaryCommandRecordingResult RecordPreparedDesktopPrimary(
        uint imageIndex,
        bool preserveSwapchainForImGuiOverlay)
    {
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

        FrameOp[] drainedOperations = _framePlanner.Operations.DrainForPrimary(
            out FrameOp[] textureUploadOperations);
        VulkanFramePlanningSnapshot planningSnapshot = _framePlanner.CaptureSnapshot();
        FrameOp[] sortedOperations = drainedOperations.Length == 0
            ? drainedOperations
            : _framePlanner.FrameScheduler.SortFrameOpsCore(
                drainedOperations,
                planningSnapshot.RenderGraphPlan.CompiledGraph);
        VulkanSwapchainContextCoalescer.Coalesce(sortedOperations);
        SplitPreparedDynamicUiOperations(
            sortedOperations,
            out FrameOp[] staticOperations,
            out FrameOp[] dynamicUiOperations);
        bool preserveSwapchainForOverlay =
            preserveSwapchainForImGuiOverlay || dynamicUiOperations.Length > 0;
        _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
            staticOperations);
        _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
            dynamicUiOperations);

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
            _resourceRuntime.BackendObjectContext?.AllowSynchronousResourceUploads == true;
        VulkanPrimaryCommandPlan primaryPlan = primaryPlans[imageIndex];
        string replanReason = string.Empty;
        for (int attempt = 0; attempt < 2; attempt++)
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
            ulong preparationSignature =
                VulkanFrameOperationSignature.Compute(staticOperations);
            ulong dynamicSignature =
                VulkanFrameOperationSignature.Compute(dynamicUiOperations);
            FramePlan framePlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                CurrentFrameSlot,
                plannerState.ResourcePlannerRevision,
                preparationSignature,
                dynamicSignature,
                staticOperations,
                dynamicUiOperations);
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

            primaryPlan.Build(
                preparedOperations,
                framePlan.StaticOperationSignature,
                new VulkanPrimaryPlanTerminalContext(
                    preserveSwapchainForOverlay,
                    TransitionSwapchainToPresent: true,
                    ReleaseExternalImageOwnership: false),
                frozenPlanningSnapshot.RenderGraphPlan.Barriers);

            if (!TryPreparePrimaryCommandInput(
                    imageIndex,
                    primaryBuffers[imageIndex],
                    dynamicUiBuffers[imageIndex],
                    framePlan,
                    primaryPlan,
                    in plannerState,
                    in frozenPlanningSnapshot,
                    preserveSwapchainForOverlay,
                    transitionSwapchainToPresent: true,
                    allowSynchronousResourceUploads,
                    freshSerialRecording,
                    _commandRuntime.StateTracker.ClearColor,
                    out VulkanPreparedPrimaryCommandInput input,
                    out string preparationFailure))
            {
                replanReason = preparationFailure;
                continue;
            }

            input = input with
            {
                // Producer-owned authoring arrays are retained only for exact
                // cache identity and frame-data refresh. Native encoding still
                // consumes the sealed numeric FramePlan stream.
                NativeOperationsOverride = staticOperations,
                DynamicUiOperations = dynamicUiOperations,
                TextureUploadOperations = textureUploadOperations,
            };
            VulkanPrimaryCommandRecordingResult result =
                _commandRuntime.RecordPrimary(in input);
            if (!result.RequiresReplan)
                return result;
            replanReason = result.Reason ??
                "primary command recording requested a fresh plan";
        }

        return VulkanPrimaryCommandRecordingResult.Deferred(
            $"primary command recording exceeded the two-attempt replan limit: {replanReason}");
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
    private bool TryPreparePrimaryCommandInput(
        uint imageIndex,
        CommandBuffer primaryCommandBuffer,
        CommandBuffer dynamicUiSecondaryCommandBuffer,
        FramePlan framePlan,
        VulkanPrimaryCommandPlan primaryCommandPlan,
        in ResourcePlannerRuntimeState plannerState,
        in VulkanFramePlanningSnapshot frozenPlanningSnapshot,
        bool preserveSwapchainForOverlay,
        bool transitionSwapchainToPresent,
        bool allowSynchronousResourceUploads,
        bool freshSerialRecording,
        ColorF4 clearColor,
        out VulkanPreparedPrimaryCommandInput input,
        out string reason)
    {
        input = default;
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
            reason = "The acquired desktop image no longer has a valid frozen recording target.";
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
        VulkanPresentationSourceTuple presentationSource =
            _windowPresentSource.CaptureForDescriptorSlot(checked((int)imageIndex));
        VulkanRecordedRenderTargetSnapshot targetSnapshot =
            CapturePreparedRenderTargetSnapshot(in target);
        CommandChainSchedule? commandChainSchedule =
            _commandRuntime.TryBuildCommandChainSchedule(
                imageIndex,
                framePlan.StaticOperations,
                preserveSwapchainForOverlay
                    ? FrameOperationStream.Empty
                    : framePlan.DynamicOverlayOperations,
                framePlan.StaticOperationSignature,
                preserveSwapchainForOverlay
                    ? 0UL
                    : framePlan.DynamicOverlaySignature,
                framePlan.PlannerRevision,
                allowExternalSwapchainTarget: false,
                out _,
                preparedRecordingTarget: targetSnapshot,
                resourceVersionSignature: framePlan.ResourceVersionSignature,
                descriptorVersionSignature: framePlan.DescriptorVersionSignature);
        VulkanCommandRecordingPolicySnapshot policy = new(
            UseDynamicRenderingRenderTargets,
            allowSynchronousResourceUploads,
            freshSerialRecording,
            IsExternalSwapchainTarget: false,
            preserveSwapchainForOverlay,
            transitionSwapchainToPresent,
            PreferKhrDynamicRendering:
                OutputRuntime.Desktop.StreamlineFrameGenerationActive);
        input = new VulkanPreparedPrimaryCommandInput(
            imageIndex,
            primaryCommandBuffer,
            dynamicUiSecondaryCommandBuffer,
            framePlan,
            primaryCommandPlan,
            target,
            presentationSource,
            resourcePlanStamp,
            new VulkanCommandClearStateSnapshot(
                clearColor,
                _commandRuntime.StateTracker.ClearDepth,
                _commandRuntime.StateTracker.ClearStencil,
                XREngine.Rendering.RenderDiagnosticsFlags.VkForceSwapchainMagenta),
            policy,
            trackedTargetLayout,
            CommandChainSchedule: commandChainSchedule);
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
        int bufferBarrierCount = sourceBarriers.BufferBarriers.Count;
        VulkanBarrierPlanner.PlannedBufferBarrier[] frozenBufferBarriers =
            new VulkanBarrierPlanner.PlannedBufferBarrier[bufferBarrierCount];
        for (int index = 0; index < bufferBarrierCount; index++)
        {
            VulkanBarrierPlanner.PlannedBufferBarrier barrier =
                sourceBarriers.BufferBarriers[index];
            if (!TryResolveFrozenBarrierBuffer(
                    barrier.ResourceName,
                    in plannerState,
                    allowSynchronousResourceUploads,
                    out Silk.NET.Vulkan.Buffer nativeBuffer,
                    out ulong nativeSize))
            {
                frozenSnapshot = default;
                reason =
                    $"Prepared resource plan {plannerState.ResourcePlannerRevision} cannot resolve native buffer barrier '{barrier.ResourceName}'.";
                return false;
            }

            frozenBufferBarriers[index] = barrier with
            {
                NativeBuffer = nativeBuffer,
                NativeOffset = 0,
                NativeSize = nativeSize,
            };
        }

        VulkanBarrierPlanner.PlannedImageBarrier[] frozenImageBarriers =
            new VulkanBarrierPlanner.PlannedImageBarrier[sourceBarriers.ImageBarriers.Count];
        for (int index = 0; index < frozenImageBarriers.Length; index++)
        {
            VulkanBarrierPlanner.PlannedImageBarrier barrier =
                sourceBarriers.ImageBarriers[index];
            Image nativeImage = barrier.Group.Image;
            if (nativeImage.Handle == 0)
            {
                frozenSnapshot = default;
                reason =
                    $"Prepared resource plan {plannerState.ResourcePlannerRevision} cannot resolve native image barrier '{barrier.ResourceName}'.";
                return false;
            }

            frozenImageBarriers[index] = barrier with
            {
                NativeImage = nativeImage,
                NativeFormat = barrier.Group.Format,
            };
        }
        VulkanBarrierPlanner.PlannedSwapchainBarrier[] frozenSwapchainBarriers =
            new VulkanBarrierPlanner.PlannedSwapchainBarrier[sourceBarriers.SwapchainBarriers.Count];
        sourceBarriers.SwapchainBarriers.CopyTo(frozenSwapchainBarriers, 0);
        VulkanBarrierPlan frozenBarriers = new(
            sourceBarriers.Generation,
            frozenImageBarriers,
            frozenBufferBarriers,
            frozenSwapchainBarriers);
        VulkanRenderGraphPlan frozenPlan = new(
            sourcePlan.Revision,
            sourcePlan.CompiledGraph,
            frozenBarriers);
        frozenSnapshot = planningSnapshot with { RenderGraphPlan = frozenPlan };
        reason = string.Empty;
        return true;
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
        in SwapchainRecordingTarget target)
    {
        VulkanRecordedRenderTargetSnapshot snapshot = default;
        snapshot.Initialize(
            target.Framebuffer.Handle,
            _resourceRuntime.GetPublishedGeneration(
                ObjectType.Framebuffer,
                target.Framebuffer.Handle),
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
