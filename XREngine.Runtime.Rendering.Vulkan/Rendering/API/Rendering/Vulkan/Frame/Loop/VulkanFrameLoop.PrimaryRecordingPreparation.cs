using System.Diagnostics;
using System.Threading;
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
        // Native output mutations publish exact CommandArtifact dependency
        // records. Apply them before this frame captures a reusable-primary
        // authority so a stale seal cannot be admitted for recording.
        _commandRuntime.DrainNativeCommandArtifactDependencyInvalidations(
            ResourceRuntime);
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

        FrameOp[] textureUploadOperations = [];
        FrameOp[] drainedOperations = [];
        FrameOp[] staticOperations = [];
        FrameOp[] dynamicUiOperations = [];
        VulkanFramePlanningSnapshot planningSnapshot = default;
        bool meshMaterializationComplete = false;
        string meshMaterializationDeferredReason = string.Empty;
        VulkanPreparedMeshIngress preparedMeshIngress = _preparedMeshIngress;
        int staticOperationCount = 0;
        int dynamicUiOperationCount = 0;
        int textureUploadOperationCount = 0;
        int drainedOperationCount = 0;
        bool drainedOperationsTransferred = false;
        VulkanAcceptedFramePlan? acceptedPlan =
            attempt.PresentNowReadinessCompleted
                ? attempt.AcceptedFramePlan
                : null;
        bool submissionMarkersTransferred = false;
        try
        {
            if (acceptedPlan is not null)
            {
                staticOperations = acceptedPlan.StaticOperations;
                dynamicUiOperations = acceptedPlan.DynamicUiOperations;
                textureUploadOperations = acceptedPlan.TextureUploadOperations;
                staticOperationCount = acceptedPlan.StaticOperationCount;
                dynamicUiOperationCount = acceptedPlan.DynamicUiOperationCount;
                textureUploadOperationCount =
                    acceptedPlan.TextureUploadOperationCount;
                planningSnapshot = acceptedPlan.FrozenPlanningSnapshot;
                preparedMeshIngress = acceptedPlan.PreparedMeshIngress;
                meshMaterializationComplete = true;
                meshMaterializationDeferredReason = string.Empty;
            }
            else
            using (VulkanCpuStageScope preparationStage = new(
                   _telemetry,
                   EVulkanCpuStage.FrameOpPreparation))
            {
            _preparedMeshIngress.Clear();
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.MaterializeQueuedMeshes"))
            {
                if (attempt.PresentNowReadinessCompleted)
                {
                    // The accepted raw cohort was frozen and materialized before
                    // swapchain acquire. Requests published after that boundary
                    // belong to the next frame and must remain queued.
                    meshMaterializationComplete = true;
                    meshMaterializationDeferredReason = string.Empty;
                }
                else
                {
                    meshMaterializationComplete = DrainQueuedMeshRenderRequests(
                        allowPreparedCohort: true,
                        out meshMaterializationDeferredReason);
                }
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                        "Vulkan.PrepareFrameOps.Drain"))
            {
                using (VulkanCpuStageScope drainStage = new(
                           _telemetry,
                           EVulkanCpuStage.FrameOpDrain))
                {
                    drainedOperations = _framePlanner.Operations.DrainForPrimary(
                        out textureUploadOperations);
                    drainedOperationCount = drainedOperations.Length;
                    textureUploadOperationCount = textureUploadOperations.Length;
                }
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
                VulkanSwapchainContextCoalescer.Coalesce(
                    sortedOperations,
                    _preparedMeshIngress);
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.FinalizePreparedMeshIngress"))
            {
                using VulkanCpuStageScope resourceUseLoweringStage = new(
                    _telemetry,
                    EVulkanCpuStage.FrameOpResourceUseLowering,
                    meshMaterializationComplete);
                bool ingressFinalized = true;
                if (meshMaterializationComplete)
                {
                    try
                    {
                        ingressFinalized = _preparedMeshIngress.TryFinalize(
                            ref _preparedMeshIngressResourceUseScratch);
                        if (ingressFinalized)
                        {
                            ingressFinalized = _preparedMeshIngress
                                .TryBuildStableBinStream(
                                    _resourceRuntime.ResidentDrawTemplates);
                            if (ingressFinalized)
                            {
                                VulkanResidentDrawTemplateTable residentTemplates =
                                    _resourceRuntime.ResidentDrawTemplates;
                                ingressFinalized = _preparedMeshIngress.StableBinStream
                                    .TryResolveManifests(
                                        residentTemplates.StableBinManifestCache,
                                        residentTemplates.StableBinMembership
                                            .TopologyGeneration);
                            }
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        ingressFinalized = false;
                        Debug.VulkanWarningEvery(
                            "Vulkan.PreparedMeshIngress.Finalization",
                            TimeSpan.FromSeconds(2),
                            "[Vulkan] Prepared mesh ingress finalization failed: {0}",
                            ex.Message);
                    }
                }

                if (meshMaterializationComplete && !ingressFinalized)
                {
                    _preparedMeshOperationCohort.Invalidate();
                    _preparedMeshIngress.Clear();
                    meshMaterializationComplete = false;
                    meshMaterializationDeferredReason =
                        "Prepared mesh ingress exceeded its dependency budget or could not resolve a final pass.";
                }
                else if (meshMaterializationComplete &&
                         _preparedMeshIngress.IsCohortHit)
                {
                    PublishPreparedMeshIngressCohortHit();
                }
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.PrepareFrameOps.SplitUi"))
            {
                using (VulkanCpuStageScope splitStage = new(
                           _telemetry,
                           EVulkanCpuStage.FrameOpSplit))
                {
                    SplitPreparedDynamicUiOperations(
                        sortedOperations,
                        out staticOperations,
                        out dynamicUiOperations);
                    // The split either aliases the drained storage or copies its
                    // operation references into the two output arrays. From this
                    // point the outputs own all authoring snapshot cleanup.
                    drainedOperationsTransferred = true;
                    staticOperationCount = staticOperations.Length;
                    dynamicUiOperationCount = dynamicUiOperations.Length;
                }
                if (!meshMaterializationComplete)
                {
                    // No subset of a scene is publishable. Dynamic text remains
                    // eligible for a recovery secondary and texture uploads remain
                    // eligible for the recovery submit while cold resources converge.
                    VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                        staticOperations);
                    VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                        staticOperations);
                    staticOperations = [];
                    staticOperationCount = 0;
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
            staticOperationCount = staticOperations.Length;
            dynamicUiOperationCount = dynamicUiOperations.Length;
            textureUploadOperationCount = textureUploadOperations.Length;
            preparedMeshIngress = _preparedMeshIngress;
            }
            FrameOp[] plannerOperations = staticOperationCount > 0
                ? staticOperations
                : dynamicUiOperations;
            int plannerOperationCount = staticOperationCount > 0
                ? staticOperationCount
                : dynamicUiOperationCount;
            using IDisposable? recordingPlannerScope = plannerOperationCount > 0
                ? RentPipelineResourcePlannerScope(
                    VulkanFramePlanner.SelectPrimaryPlannerContext(
                        plannerOperations,
                        plannerOperationCount))
                : null;

            bool preserveSwapchainForOverlay =
                preserveSwapchainForImGuiOverlay ||
                dynamicUiOperationCount > 0 ||
                preparedMeshIngress.HasDynamicUiEntries;

            _ = attempt.CompletePhase(
                EVulkanFrameStage.ResourcePrepare,
                EDesktopFrameFlow.Continue);
            bool allowSynchronousResourceUploads =
                !attempt.InteractiveResize &&
                _resourceRuntime.AllowSynchronousResourceUploads;
            VulkanComputePreparationResult computePreparation =
                acceptedPlan is not null
                    ? VulkanComputePreparationResult.Success
                    : _commandRuntime.PrepareComputeProgramsForFramePlan(
                        staticOperations,
                        allowSynchronousResourceUploads);
            if (acceptedPlan is null && computePreparation.Succeeded)
            {
                computePreparation = _commandRuntime.PrepareComputeProgramsForFramePlan(
                    dynamicUiOperations,
                    allowSynchronousResourceUploads);
            }
            if (!computePreparation.Succeeded)
            {
                return CreateDesktopRecordingReadinessFailure(
                    ref attempt,
                    computePreparation.FormatFailure());
            }

            // Desktop presentation is a PresentNow contract. Reusing a clean
            // command artifact would make a new frame claim old GPU work.
            const bool freshSerialRecording = true;
            // A modal resize callback must not create replacement internal
            // images, compile synchronously, or wait for a cold dependency.
            // Existing published generations remain eligible for this attempt;
            // anything else returns the acquired image through normal deferral.
            VulkanPrimaryCommandPlan primaryPlan = primaryPlans[imageIndex];
            string replanReason = string.Empty;
            bool nativeBarrierBindingsSuperseded = false;
            int recordingAttemptLimit = attempt.InteractiveResize ? 1 : 2;
            for (int replanAttempt = 0; replanAttempt < recordingAttemptLimit; replanAttempt++)
            {
                nativeBarrierBindingsSuperseded = false;
                ResourcePlannerRuntimeState plannerState = acceptedPlan is null
                    ? CaptureResourcePlannerRuntimeState()
                    : acceptedPlan.PlannerState;
                planningSnapshot = acceptedPlan is null
                    ? new VulkanFramePlanningSnapshot(
                        plannerState.RenderGraphPlan,
                        _framePlanner.FrozenResourcePlanRevision,
                        _framePlanner.IsResourcePlanFrozen)
                    : acceptedPlan.FrozenPlanningSnapshot;
                if (!TryBindPreparedStreamlineUiImage(
                        imageIndex,
                        staticOperations,
                        acceptedPlan,
                        out string streamlinePreparationFailure))
                {
                    replanReason = streamlinePreparationFailure;
                    continue;
                }
                if (acceptedPlan is null &&
                    planningSnapshot.RenderGraphPlan.Revision !=
                        plannerState.ResourcePlannerRevision)
                {
                    replanReason =
                        $"Planner publication changed while preparing resource revision " +
                        $"{plannerState.ResourcePlannerRevision}; captured graph revision " +
                        $"{planningSnapshot.RenderGraphPlan.Revision}.";
                    continue;
                }
                if (acceptedPlan is null &&
                    (!TryPrepareFrameOperationTargets(
                        staticOperations,
                        allowSynchronousResourceUploads,
                        out string targetPreparationFailure) ||
                    !TryPrepareFrameOperationTargets(
                        dynamicUiOperations,
                        allowSynchronousResourceUploads,
                        out targetPreparationFailure) ||
                    !TryPreparePreparedMeshIngressTargets(
                        preparedMeshIngress,
                        allowSynchronousResourceUploads,
                        out targetPreparationFailure)))
                {
                    replanReason = targetPreparationFailure;
                    continue;
                }
                VulkanFramePlanningSnapshot frozenPlanningSnapshot =
                    planningSnapshot;
                try
                {
                    // Even a fixed image generation can observe a newer buffer
                    // publication. Refresh only its native binding metadata;
                    // the false upload policy forbids native creation and a
                    // modal callback gets one attempt before deferring.
                    if (acceptedPlan is null &&
                        !TryFreezeNativeBarrierBindings(
                            in planningSnapshot,
                            ref plannerState,
                            allowSynchronousResourceUploads,
                            out frozenPlanningSnapshot,
                            out string resourcePreparationFailure,
                            maximumAttempts: recordingAttemptLimit))
                    {
                        replanReason = resourcePreparationFailure;
                        continue;
                    }
                }
                catch (VulkanNativeBufferBindingSupersededException exception)
                {
                    nativeBarrierBindingsSuperseded = true;
                    replanReason = exception.Message;
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
                        attempt.ReadinessPolicy,
                        attempt.WorkClass,
                        attempt.FrameNumber,
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
                        using (VulkanCpuStageScope planStage = new(
                                   _telemetry,
                                   EVulkanCpuStage.FrameOpPlan))
                        {
                            try
                            {
                                framePlan = acceptedPlan?.LogicalPlan ??
                                _framePlanner.FramePlanBuilder.BuildAndSeal(
                                    CurrentFrameSlot,
                                    plannerState.ResourcePlannerRevision,
                                    staticOperationSignature: 0UL,
                                    dynamicOverlaySignature: 0UL,
                                    staticOperations,
                                    dynamicUiOperations,
                                    new VulkanFramePlanRenderGraphAuthority(
                                        frozenPlanningSnapshot.RenderGraphPlan,
                                        plannerState.FrameOpResourcePlannerSwitchingState,
                                        _framePlanner,
                                        _resourceRuntime.BackendObjectContext,
                                        allowSynchronousResourceUploads),
                                    textureUploadOperations: textureUploadOperations,
                                    preparedMeshIngress: preparedMeshIngress,
                                    authoringOperationCount: staticOperationCount,
                                    authoringDynamicOverlayOperationCount:
                                        dynamicUiOperationCount,
                                    authoringTextureUploadOperationCount:
                                        textureUploadOperationCount,
                                    desktopReadinessPolicyOverride:
                                        attempt.InteractiveResize
                                            ? ERenderOutputReadinessPolicy
                                                .AllowDeferral
                                            : null,
                                    desktopWorkClassOverride:
                                        attempt.InteractiveResize
                                            ? ERenderOutputWorkClass.Background
                                            : null);
                            }
                            catch (VulkanNativeBufferBindingSupersededException exception)
                            {
                                nativeBarrierBindingsSuperseded = true;
                                replanReason = exception.Message;
                                continue;
                            }
                        }
                    }
                }
                if (acceptedPlan is null)
                    framePlan.PrepareRecordingPlannerGenerations(in plannerState);
                else if (!framePlan.HasPreparedRecordingPlannerGenerations)
                    return CreateDesktopRecordingReadinessFailure(ref attempt, "The accepted plan has no frozen physical-resource generation.");
                FrameOperationSequence preparedOperations =
                    framePlan.GetNativeStaticOperationsForRecording();
                if (!TryPrepareReadOnlyStorage(
                        framePlan,
                        CurrentFrameSlot,
                        out VulkanReadOnlyStoragePreparedAuthority? readOnlyStorageAuthority,
                        out _,
                        out string storageFailure))
                {
                    return CreateDesktopRecordingReadinessFailure(ref attempt, storageFailure);
                }
                if (acceptedPlan is null)
                {
                    using VulkanResourceRuntime.ReadOnlyStorageRecordingScope storageScope =
                        ResourceRuntime.EnterReadOnlyStorageRecordingScope(
                            readOnlyStorageAuthority);
                    computePreparation = _commandRuntime.PrepareComputeFramePlanForRecording(
                        imageIndex,
                        framePlan,
                        in plannerState,
                        allowSynchronousResourceUploads);
                }
                if (!computePreparation.Succeeded)
                {
                    return CreateDesktopRecordingReadinessFailure(
                        ref attempt,
                        computePreparation.FormatFailure());
                }

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
                        in authority,
                        callerOwnsSubmissionMarkersUntilRecordingSucceeds: true);

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
                result = ApplyDesktopPresentNowResultContract(
                    ref attempt,
                    result,
                    framePlan);
                // A sealed accepted packet cannot be rebound in place after a
                // native resource generation race. Return the typed retry so
                // the after-acquire path discards it and publishes a fresh plan.
                if (acceptedPlan is not null && result.RequiresReplan)
                    return result with
                    {
                        Disposition = EVulkanPrimaryCommandRecordingDisposition.Failed,
                        FailureKind = EVulkanCommandRecordingFailureKind.RetryFrame,
                    };
                if (!result.RequiresReplan)
                {
                    if (meshMaterializationComplete)
                    {
                        submissionMarkersTransferred =
                            result.Succeeded && result.CommandBuffer.Handle != 0;
                        return result;
                    }

                    return CreateDesktopRecordingReadinessFailure(
                        ref attempt,
                        meshMaterializationDeferredReason);
                }
                replanReason = result.Reason ??
                    "primary command recording requested a fresh plan";
            }

            if (nativeBarrierBindingsSuperseded)
            {
                return VulkanPrimaryCommandRecordingResult.Failed(
                    $"primary command recording exhausted the native buffer binding attempt limit ({recordingAttemptLimit}): {replanReason}",
                    attempt.ReadinessPolicy,
                    attempt.WorkClass,
                    attempt.FrameNumber,
                    EVulkanCommandRecordingFailureKind.RetryFrame);
            }

            return CreateDesktopRecordingReadinessFailure(
                ref attempt,
                $"primary command recording exceeded the attempt limit ({recordingAttemptLimit}): {replanReason}");
        }
        finally
        {
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                staticOperations.AsSpan(0, staticOperationCount));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                dynamicUiOperations.AsSpan(0, dynamicUiOperationCount));
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                textureUploadOperations.AsSpan(0, textureUploadOperationCount));
            if (!drainedOperationsTransferred)
            {
                VulkanAdvancedVisibilityInputLease.ReleaseOperations(
                    drainedOperations.AsSpan(0, drainedOperationCount));
            }
            if (acceptedPlan is null)
                _preparedMeshIngress.Clear();
            if (!submissionMarkersTransferred)
            {
                _commandRuntime.DiscardSubmissionMarkersForCommandBuffer(
                    primaryBuffers[imageIndex]);
                if (acceptedPlan is not null)
                {
                    acceptedPlan.SettleUnsubmittedSubmissionMarkers();
                }
                else
                {
                    FailPreparedSubmissionMarkers(
                        staticOperations.AsSpan(0, staticOperationCount),
                        dynamicUiOperations.AsSpan(0, dynamicUiOperationCount));
                }
            }
        }
    }

    private static VulkanPrimaryCommandRecordingResult ApplyDesktopPresentNowResultContract(
        ref VulkanFrameAttempt attempt,
        in VulkanPrimaryCommandRecordingResult result,
        FramePlan framePlan)
    {
        if (attempt.WorkClass == ERenderOutputWorkClass.PresentNow &&
            result.Disposition is EVulkanPrimaryCommandRecordingDisposition.Reused or
                EVulkanPrimaryCommandRecordingDisposition.Deferred)
        {
            Debug.VulkanError(
                "[Vulkan][PresentNow] Source recording invariant violated: disposition={0}, frame={1}, reason={2}.",
                result.Disposition,
                attempt.FrameNumber,
                result.Reason ?? "<none>");
        }

        return result with
        {
            OutputExecutionPlan = framePlan,
            ReadinessPolicy = attempt.ReadinessPolicy,
            WorkClass = attempt.WorkClass,
            SourceFrameId = attempt.FrameNumber,
        };
    }

    private static VulkanPrimaryCommandRecordingResult CreateDesktopRecordingReadinessFailure(
        ref VulkanFrameAttempt attempt,
        string reason)
        => attempt.WorkClass == ERenderOutputWorkClass.PresentNow
            ? VulkanPrimaryCommandRecordingResult.Failed(
                reason,
                attempt.ReadinessPolicy,
                attempt.WorkClass,
                attempt.FrameNumber)
            : VulkanPrimaryCommandRecordingResult.Deferred(reason);

    private static void FailPreparedSubmissionMarkers(
        ReadOnlySpan<FrameOp> staticOperations,
        ReadOnlySpan<FrameOp> dynamicUiOperations)
    {
        VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
            staticOperations);
        VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
            dynamicUiOperations);
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
            if (!TryPrepareFrameOperationTarget(
                    target,
                    allowSynchronousResourceUploads,
                    out reason))
                return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryPreparePreparedMeshIngressTargets(
        VulkanPreparedMeshIngress ingress,
        bool allowSynchronousResourceUploads,
        out string reason)
    {
        for (int index = 0; index < ingress.Count; index++)
        {
            XRFrameBuffer? target = ingress.GetEntry(index).Target;
            if (target is null)
                continue;
            if (!TryPrepareFrameOperationTarget(
                    target,
                    allowSynchronousResourceUploads,
                    out reason))
                return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryPrepareFrameOperationTarget(
        XRFrameBuffer target,
        bool allowSynchronousResourceUploads,
        out string reason)
    {
        VkFrameBuffer? wrapper =
            _resourceRuntime.CreateAPIRenderObject(target) as VkFrameBuffer;
        if (wrapper is null)
        {
            reason =
                $"Failed to create the Vulkan framebuffer wrapper for target '{target.GetDescribingName()}'.";
            return false;
        }

        if (allowSynchronousResourceUploads)
        {
            if (!wrapper.TryPrepareAttachmentBackings(
                    allowSynchronousResourceUploads,
                    out reason))
            {
                return false;
            }
        }
        else if (!wrapper.TryCaptureRecordedRenderTargetSnapshot(out _))
        {
            reason =
                $"Vulkan framebuffer target '{target.GetDescribingName()}' has no published native attachment snapshot for non-blocking command recording.";
            return false;
        }

        try
        {
            if (wrapper.IsGenerated)
            {
                // The logical framebuffer is shared by desktop and OpenXR render plans,
                // while its attachments resolve through the currently active physical
                // resource generation. A resize attempt only admits an already-current
                // snapshot; EnsureCurrent can publish a replacement internal image.
                if (allowSynchronousResourceUploads)
                    wrapper.EnsureCurrent();
            }
            else if (allowSynchronousResourceUploads)
            {
                wrapper.Generate();
            }
        }
        catch (VulkanFrameBufferAttachmentNotReadyException exception)
        {
            reason = exception.Message;
            return false;
        }
        if (!wrapper.IsGenerated)
        {
            reason =
                $"Vulkan framebuffer target '{target.GetDescribingName()}' is not ready for command recording.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Converts wrapper-owned render events into prepared mesh operations at the only
    /// point where frame, output, command, and planner state are jointly authoritative.
    /// </summary>
    private bool DrainQueuedMeshRenderRequests(
        bool allowPreparedCohort,
        out string deferredReason)
    {
        int requestCount;
        using (VulkanCpuStageScope rawRequestDrainStage = new(
                   _telemetry,
                   EVulkanCpuStage.RawMeshRequestDrain))
        {
            requestCount = MeshOperationRequests.DrainTo(
                _meshOperationRequestScratch);
        }

        if (requestCount < 0)
        {
            deferredReason =
                "The bounded mesh request cohort was rejected atomically before materialization.";
            return false;
        }

        return MaterializeQueuedMeshRenderRequests(
            requestCount,
            allowPreparedCohort,
            out deferredReason);
    }

    /// <summary>
    /// Materializes the request cohort already captured in
    /// <see cref="_meshOperationRequestScratch"/>. OpenXR uses this entry point while
    /// its external-target and frame-operation capture scopes are still active so
    /// eye requests cannot escape into a later desktop frame.
    /// </summary>
    private bool MaterializeQueuedMeshRenderRequests(
        int requestCount,
        bool allowPreparedCohort,
        out string deferredReason,
        bool foregroundRequired = false,
        long readinessDeadlineTimestamp = long.MaxValue,
        ulong sourceFrameId = 0UL)
    {
        VulkanPresentNowReadinessWatchdog inactiveWatchdog = default;
        return MaterializeQueuedMeshRenderRequestsCore(
            requestCount,
            allowPreparedCohort,
            out deferredReason,
            foregroundRequired,
            readinessDeadlineTimestamp,
            sourceFrameId,
            trackPresentNowProgress: false,
            ref inactiveWatchdog);
    }

    private bool MaterializeQueuedMeshRenderRequests(
        int requestCount,
        bool allowPreparedCohort,
        out string deferredReason,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        ulong sourceFrameId)
        => MaterializeQueuedMeshRenderRequestsCore(
            requestCount,
            allowPreparedCohort,
            out deferredReason,
            foregroundRequired: true,
            readinessDeadlineTimestamp: long.MaxValue,
            sourceFrameId,
            trackPresentNowProgress: true,
            ref watchdog);

    private bool MaterializeQueuedMeshRenderRequestsCore(
        int requestCount,
        bool allowPreparedCohort,
        out string deferredReason,
        bool foregroundRequired,
        long readinessDeadlineTimestamp,
        ulong sourceFrameId,
        bool trackPresentNowProgress,
        ref VulkanPresentNowReadinessWatchdog watchdog)
    {
        deferredReason = string.Empty;
        if (requestCount == 0)
            return true;

        NormalizeQueuedMeshRenderRequests(requestCount);
        ApplyResidentTemplateProjectionDeltas(requestCount);
        InjectResidentTemplateDeviceLossIfRequested();

        long coldPreparationTicks = 0;
        int deferredRequestCount = 0;
        int unavailableRequestCount = 0;
        int quarantinedRequestCount = 0;
        int warmRequestCount = 0;
        int coldRequestCount = 0;
        int resumeRequestIndex = -1;
        int reusableCohortEntryCount = 0;
        int cohortResourceUseCount = 0;
        bool cohortMaterializationComplete = true;
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
        bool preparedCohortMatched = false;
        bool stagedPreparedCohort = false;
        if (allowPreparedCohort)
        {
            using VulkanCpuStageScope cohortStage = new(
                _telemetry,
                EVulkanCpuStage.FrameOpCohort);
            if (TryStageResidentMeshTemplates(requestCount, out deferredReason))
            {
                _meshOperationRequestScratch.AsSpan(0, requestCount).Clear();
                if (trackPresentNowProgress)
                    watchdog.RecordProgress();
                return true;
            }
            stagedPreparedCohort = TryStagePreparedMeshOperationCohort(
                requestCount,
                in materializationSnapshot,
                out preparedCohortMatched,
                out deferredReason);
        }
        if (stagedPreparedCohort)
        {
            _meshOperationRequestScratch.AsSpan(0, requestCount).Clear();
            if (trackPresentNowProgress)
                watchdog.RecordProgress();
            return true;
        }

        if (allowPreparedCohort &&
            preparedCohortMatched &&
            !foregroundRequired)
        {
            _meshOperationRequestScratch.AsSpan(0, requestCount).Clear();
            return false;
        }

        // A structurally matching prepared cohort may still contain a cold
        // non-reusable entry. Its first materialization attempt can start a
        // successful asynchronous program or pipeline build. PresentNow must
        // retain the accepted request cohort and drive that work to completion;
        // treating this fast-path miss as terminal would permanently pause the
        // renderer merely because the newly visible variant was still compiling.
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
                ulong preparationSignature =
                    request.PreparationCompatibilitySignature;
                if (preparationSignature != 0 &&
                    _quarantinedMeshOperationSignatures.Contains(
                        preparationSignature))
                {
                    quarantinedRequestCount++;
                    cohortMaterializationComplete = false;
                    continue;
                }

                XRRenderPipelineInstance? pipeline = request.Pipeline;
                if (pipeline is null)
                {
                    cohortMaterializationComplete = false;
                    continue;
                }

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
                if (!foregroundRequired &&
                    !dynamicUiOverlay &&
                    !resourcesReady &&
                    coldPreparationTicks >= ColdMeshPreparationSliceTicks)
                {
                    deferredRequestCount++;
                    cohortMaterializationComplete = false;
                    if (resumeRequestIndex < 0)
                        resumeRequestIndex = requestIndex;
                    continue;
                }

                long preparationStart = resourcesReady
                    ? 0L
                    : Stopwatch.GetTimestamp();
                bool materialized;
                VulkanMeshOperationRequest operationRequest;
                do
                {
                    materialized = TryMaterializeQueuedMeshRenderRequest(
                        in request,
                        pipeline,
                        in materializationSnapshot,
                        prewarmDescriptorAllocation: !previouslyMaterialized,
                        out operationRequest);
                    if (materialized || !foregroundRequired)
                        break;

                    if (HasMeshReadinessExpired(
                            trackPresentNowProgress,
                            ref watchdog,
                            readinessDeadlineTimestamp))
                    {
                        deferredReason =
                            $"PresentNow mesh readiness watchdog expired for frame={sourceFrameId} " +
                            $"request={requestIndex}/{requestCount} " +
                            $"mesh='{request.Renderer.Mesh?.Name ?? "<unnamed>"}' " +
                            $"detail='{request.Renderer.LastPrepareDetail}'.";
                        break;
                    }

                    PumpPresentNowRequiredJobs();
                    Thread.Yield();
                }
                while (true);
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
                    cohortMaterializationComplete = false;
                    if (resumeRequestIndex < 0)
                        resumeRequestIndex = requestIndex;
                    if (foregroundRequired &&
                        HasMeshReadinessExpired(
                            trackPresentNowProgress,
                            ref watchdog,
                            readinessDeadlineTimestamp))
                    {
                        break;
                    }
                    continue;
                }

                if (!EnqueueQueuedMeshDraw(
                        in operationRequest,
                        out MeshDrawOp? enqueuedOperation))
                {
                    _meshOperationWarmPreparationSignatures.Remove(
                        preparationSignature);
                    if (preparationSignature != 0)
                    {
                        if (_quarantinedMeshOperationSignatures.Count >=
                                MaxWarmMeshPreparationSignatures &&
                            !_quarantinedMeshOperationSignatures.Contains(
                                preparationSignature))
                        {
                            _quarantinedMeshOperationSignatures.Clear();
                        }

                        _quarantinedMeshOperationSignatures.Add(
                            preparationSignature);
                    }

                    quarantinedRequestCount++;
                    cohortMaterializationComplete = false;
                    continue;
                }

                if (trackPresentNowProgress)
                    watchdog.RecordProgress();

                bool reusable = IsPreparedMeshOperationCohortEligible(
                        in request,
                        in operationRequest);
                if (reusable && enqueuedOperation!.PreserveSubmissionOrder)
                    reusable = false;
                _meshOperationMaterializationScratch[requestIndex] =
                    operationRequest;
                _meshOperationCohortEntryScratch[requestIndex] =
                    CreatePreparedMeshOperationCohortEntry(
                        in request,
                        reusable);
                cohortResourceUseCount = checked(
                    cohortResourceUseCount +
                    enqueuedOperation!.ResourceUsesReference.Count);
                if (reusable)
                    reusableCohortEntryCount++;

                if (preparationSignature != 0)
                {
                    // Keep the hot-path cache finite and pre-sized. Reaching this
                    // bound requires several complete queue cohorts of distinct
                    // structural variants; clearing is a recoverable cold event.
                    if (_meshOperationWarmPreparationSignatures.Count >=
                            MaxWarmMeshPreparationSignatures &&
                        !_meshOperationWarmPreparationSignatures.Contains(
                            preparationSignature))
                    {
                        _meshOperationWarmPreparationSignatures.Clear();
                    }

                    _meshOperationWarmPreparationSignatures.Add(
                        preparationSignature);
                }
            }
        }
        finally
        {
            cameraScope?.Dispose();
            pipelineScope?.Dispose();
        }

        // A stable visible cohort can contain hundreds of cold requests. Resume at
        // the first request that did not finish so a bounded slice cannot starve the
        // tail by repeatedly beginning at request zero.
        _meshOperationPreparationCursor = resumeRequestIndex >= 0
            ? resumeRequestIndex
            : 0;

        if (allowPreparedCohort &&
            cohortMaterializationComplete &&
            reusableCohortEntryCount > 0 &&
            cohortResourceUseCount <= _preparedMeshIngress.ResourceUseCapacity &&
            deferredRequestCount == 0 &&
            unavailableRequestCount == 0 &&
            quarantinedRequestCount == 0)
        {
            _preparedMeshOperationCohort.Publish(
                _meshOperationCohortEntryScratch.AsSpan(0, requestCount),
                _meshOperationMaterializationScratch.AsSpan(0, requestCount));
            Interlocked.Increment(ref _preparedMeshOperationCohortBuilds);
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanPreparedMeshOperationCohort(
                    hit: false,
                    built: true,
                    fullyMaterialized: false);
        }
        else if (allowPreparedCohort)
        {
            _preparedMeshOperationCohort.Invalidate();
        }
        _meshOperationMaterializationScratch.AsSpan(0, requestCount).Clear();
        _meshOperationCohortEntryScratch.AsSpan(0, requestCount).Clear();
        _meshOperationRequestScratch.AsSpan(0, requestCount).Clear();
        Interlocked.Increment(ref _preparedMeshOperationFullMaterializations);
        RuntimeEngine.Rendering.Stats.Vulkan
            .RecordVulkanPreparedMeshOperationCohort(
                hit: false,
                built: false,
                fullyMaterialized: true);

        if (quarantinedRequestCount > 0)
        {
            Debug.VulkanWarningEvery(
                "Vulkan.MeshMaterialization.Quarantined",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Skipped {0} quarantined mesh draw request(s); the remaining scene will continue rendering.",
                quarantinedRequestCount);
        }

        if (deferredRequestCount == 0 && unavailableRequestCount == 0)
            return true;

        if (string.IsNullOrEmpty(deferredReason))
        {
            deferredReason =
                $"Mesh resource preparation yielded before publishing a partial scene. " +
                $"deferred={deferredRequestCount} unavailable={unavailableRequestCount} " +
                $"requests={requestCount} warm={warmRequestCount} cold={coldRequestCount}.";
        }
        Debug.VulkanWarningEvery(
            "Vulkan.MeshMaterialization.Deferred",
            TimeSpan.FromSeconds(1),
            "[Vulkan] {0}",
            deferredReason);
        return false;
    }

    private static bool HasMeshReadinessExpired(
        bool trackPresentNowProgress,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        long readinessDeadlineTimestamp)
        => trackPresentNowProgress
            ? watchdog.IsExpired
            : Stopwatch.GetTimestamp() >= readinessDeadlineTimestamp;

    /// <summary>
    /// Captures frame-loop-owned context and output facts exactly once per raw
    /// request cohort. Later matching, cache publication, and legacy fallback
    /// consume these normalized values rather than independently sampling output
    /// state at different points in the frame.
    /// </summary>
    private void NormalizeQueuedMeshRenderRequests(int requestCount)
    {
        uint requiredTemplateCapacity =
            _resourceRuntime.ResidentDrawTemplates.PrimaryCapacity;
        for (int index = 0; index < requestCount; index++)
        {
            ref VulkanMeshRenderRequest request =
                ref _meshOperationRequestScratch[index];
            AdvancedGpuHandle canonicalHandle =
                request.CanonicalDrawIdentitySnapshot.Primary.Handle;
            if (canonicalHandle.IsValid)
                requiredTemplateCapacity = Math.Max(
                    requiredTemplateCapacity,
                    canonicalHandle.Index);
            XRRenderPipelineInstance? pipeline = request.Pipeline;
            if (pipeline is null)
                continue;

            FrameOpContext context = CreateQueuedMeshRequestContext(
                in request,
                pipeline);
            VulkanMeshProducerSnapshot producer = CreateQueuedMeshProducer(
                in request,
                in context);
            request = request with
            {
                Context = context,
                Producer = producer,
            };
        }

        if (requiredTemplateCapacity >
            _resourceRuntime.ResidentDrawTemplates.PrimaryCapacity)
        {
            uint grownCapacity =
                _resourceRuntime.ResidentDrawTemplates.PrimaryCapacity;
            while (grownCapacity < requiredTemplateCapacity &&
                   grownCapacity <= uint.MaxValue / 2u)
            {
                grownCapacity *= 2u;
            }
            _ = _resourceRuntime.ResidentDrawTemplates.GrowAtBoundary(
                Math.Max(grownCapacity, requiredTemplateCapacity));
        }
    }

    /// <summary>
    /// Consumes each canonical package journal once on the render thread before
    /// any resident lookup. Structural draw mutations detach the exact primary
    /// slot; data-only owners remain generation-driven and do not rebuild native
    /// templates.
    /// </summary>
    private void ApplyResidentTemplateProjectionDeltas(int requestCount)
    {
        for (int index = 0; index < requestCount; ++index)
        {
            XRRenderPipelineInstance? pipeline =
                _meshOperationRequestScratch[index].Pipeline;
            if (pipeline is null)
                continue;

            BackendReadyFramePackage package =
                pipeline.ActiveMeshRenderCommands.RenderingBackendReadyPackage;
            BackendReadyCanonicalScenePublication publication =
                package.CanonicalScenePublication;
            _resourceRuntime.ResidentDrawTemplates.ApplyProjectionDeltas(
                publication.DatabaseEpoch,
                publication.Sequence,
                package.TemplateProjectionDeltas);
        }
    }

    private bool TryStagePreparedMeshOperationCohort(
        int requestCount,
        in VulkanMeshMaterializationSnapshot materializationSnapshot,
        out bool cacheMatched,
        out string deferredReason)
    {
        cacheMatched = false;
        deferredReason = string.Empty;
        if (!_preparedMeshOperationCohort.IsValid ||
            _preparedMeshOperationCohort.Count != requestCount ||
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth != 0)
        {
            return false;
        }

        for (int index = 0; index < requestCount; index++)
        {
            ref readonly VulkanMeshRenderRequest current =
                ref _meshOperationRequestScratch[index];
            ref readonly VulkanPreparedMeshOperationCohortEntry cached =
                ref _preparedMeshOperationCohort.GetEntry(index);
            ref readonly VulkanMeshOperationRequest template =
                ref _preparedMeshOperationCohort.GetOperation(index);
            if (!IsPreparedMeshOperationCohortMatch(
                    in current,
                    in cached,
                    in template))
                return false;
        }
        cacheMatched = true;
        _preparedMeshIngress.Clear();

        XRRenderPipelineInstance? scopedPipeline = null;
        XRCamera? scopedCamera = null;
        IDisposable? pipelineScope = null;
        IDisposable? cameraScope = null;
        int reusedOperationCount = 0;
        int legacyHoleMaterializationCount = 0;
        bool ingressPublished = false;
        try
        {
            // A retained immutable artifact is only a candidate. Reacquire it
            // from the exact program-owned cache under the current render scope
            // before invoking any legacy-hole callbacks.
            using (VulkanCpuStageScope bindingValidationStage = new(
                       _telemetry,
                       EVulkanCpuStage.PreparedMeshBindingValidation))
            {
                for (int index = 0; index < requestCount; index++)
                {
                    ref readonly VulkanPreparedMeshOperationCohortEntry cachedEntry =
                        ref _preparedMeshOperationCohort.GetEntry(index);
                    if (!cachedEntry.IsReusable)
                        continue;

                    VulkanMeshOperationRequest template =
                        _preparedMeshOperationCohort.GetOperation(index);
                    ComputeDispatchSnapshot? cachedBindings =
                        template.Draw.ProgramBindingSnapshot;
                    if (cachedBindings is null)
                        continue;

                    ref readonly VulkanMeshRenderRequest request =
                        ref _meshOperationRequestScratch[index];
                    XRRenderPipelineInstance pipeline = request.Pipeline!;
                    XRCamera? camera = pipeline.LastRenderingCamera ??
                        pipeline.LastSceneCamera;
                    if (!ReferenceEquals(scopedPipeline, pipeline) ||
                        !ReferenceEquals(scopedCamera, camera))
                    {
                        cameraScope?.Dispose();
                        pipelineScope?.Dispose();
                        pipelineScope = RuntimeRenderingHostServices.Diagnostics
                            .PushRenderingPipeline(pipeline);
                        cameraScope = pipeline.RenderState.PushRenderingCamera(camera);
                        scopedPipeline = pipeline;
                        scopedCamera = camera;
                    }

                    LayeredShadowUniformState shadowUniformState =
                        request.ViewSnapshot.ShadowUniformState;
                    if (template.Draw.PreparedProgram is not { } expectedProgram ||
                        !request.Renderer.TryGetCurrentPersistentProgramBindingArtifact(
                            request.ResolvedMaterial.Material,
                            expectedProgram,
                            in shadowUniformState,
                            out ComputeDispatchSnapshot? currentBindings) ||
                        currentBindings is null ||
                        !currentBindings.IsImmutableBindingArtifact ||
                        currentBindings.HasMutableFrameSourceSamplerBindings ||
                        !ReferenceEquals(currentBindings, cachedBindings))
                    {
                        _preparedMeshOperationCohort.Invalidate();
                        cacheMatched = false;
                        return false;
                    }
                }
            }

            for (int index = 0; index < requestCount; index++)
            {
                ref readonly VulkanMeshRenderRequest request =
                    ref _meshOperationRequestScratch[index];
                ref readonly VulkanPreparedMeshOperationCohortEntry cachedEntry =
                    ref _preparedMeshOperationCohort.GetEntry(index);
                XRRenderPipelineInstance pipeline = request.Pipeline!;
                XRCamera? camera = pipeline.LastRenderingCamera ??
                    pipeline.LastSceneCamera;
                if (!ReferenceEquals(scopedPipeline, pipeline) ||
                    !ReferenceEquals(scopedCamera, camera))
                {
                    cameraScope?.Dispose();
                    pipelineScope?.Dispose();
                    pipelineScope = RuntimeRenderingHostServices.Diagnostics
                        .PushRenderingPipeline(pipeline);
                    cameraScope = pipeline.RenderState.PushRenderingCamera(camera);
                    scopedPipeline = pipeline;
                    scopedCamera = camera;
                }

                VulkanMeshOperationRequest template =
                    _preparedMeshOperationCohort.GetOperation(index);
                VulkanMeshOperationRequest operation;
                if (!cachedEntry.IsReusable)
                {
                    bool previouslyMaterialized =
                        request.PreparationCompatibilitySignature != 0 &&
                        _meshOperationWarmPreparationSignatures.Contains(
                            request.PreparationCompatibilitySignature);
                    bool holeMaterialized;
                    using (VulkanCpuStageScope holeMaterializationStage = new(
                               _telemetry,
                               EVulkanCpuStage.PreparedMeshHoleMaterialization))
                    {
                        holeMaterialized = TryMaterializeQueuedMeshRenderRequest(
                            in request,
                            pipeline,
                            in materializationSnapshot,
                            prewarmDescriptorAllocation: !previouslyMaterialized,
                            out operation);
                    }
                    if (!holeMaterialized)
                    {
                        _preparedMeshOperationCohort.Invalidate();
                        deferredReason = "Prepared mesh-operation cohort legacy hole could not be materialized.";
                        return false;
                    }

                    legacyHoleMaterializationCount++;
                }
                else
                {
                    PendingMeshDraw draw = template.Draw with
                    {
                        ModelMatrix = request.ModelMatrix,
                        PreviousModelMatrix = request.PreviousModelMatrix,
                        MaterialOverride = request.ResolvedMaterial.Material,
                        Instances = request.ExpandedInstances,
                        BillboardMode = request.BillboardMode,
                        TransformId = request.TransformId,
                        ViewSnapshot = request.ViewSnapshot,
                        ShadowCasterRelevance = request.ShadowCasterRelevance,
                    };
                    draw = draw with
                    {
                        AutoUniformPublication =
                            VulkanAutoUniformPublicationSnapshot.Capture(
                                draw,
                                pipeline),
                    };
                    operation = template with
                    {
                        Draw = draw,
                        ProducerSnapshot = request.Producer,
                    };
                    reusedOperationCount++;
                }

                if (!TryStagePreparedMeshOperation(in operation))
                {
                    _preparedMeshOperationCohort.Invalidate();
                    deferredReason =
                        "Prepared mesh-operation cohort could not be lowered into the current frame ingress.";
                    return false;
                }
            }

            _preparedMeshIngress.MarkCohortHit(
                reusedOperationCount,
                legacyHoleMaterializationCount);
            ingressPublished = true;
        }
        finally
        {
            if (!ingressPublished)
                _preparedMeshIngress.Clear();
            cameraScope?.Dispose();
            pipelineScope?.Dispose();
        }

        return true;
    }

    /// <summary>
    /// Projects a complete stable request cohort through direct canonical slots.
    /// A miss leaves the retained cohort/full-materialization compatibility path
    /// untouched; a hit never hashes, compares structure, or reacquires the
    /// persistent program-binding artifact.
    /// </summary>
    private bool TryStageResidentMeshTemplates(
        int requestCount,
        out string deferredReason)
    {
        deferredReason = string.Empty;
        if (requestCount <= 0 ||
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth != 0)
        {
            return false;
        }

        for (int index = 0; index < requestCount; ++index)
        {
            ref readonly VulkanMeshRenderRequest request =
                ref _meshOperationRequestScratch[index];
            if (!request.Renderer.TryGetResidentDrawTemplate(
                    request,
                    out VulkanResidentDrawTemplateHandle handle,
                    out VulkanResidentDrawTemplate? template) ||
                template is null)
            {
                _residentTemplateHitScratch.AsSpan(0, index).Clear();
                _residentTemplateHandleScratch.AsSpan(0, index).Clear();
                return false;
            }

            _residentTemplateHitScratch[index] = template;
            _residentTemplateHandleScratch[index] = handle;
        }

        _preparedMeshIngress.Clear();
        XRRenderPipelineInstance? scopedPipeline = null;
        XRCamera? scopedCamera = null;
        IDisposable? pipelineScope = null;
        IDisposable? cameraScope = null;
        bool published = false;
        try
        {
            for (int index = 0; index < requestCount; ++index)
            {
                ref readonly VulkanMeshRenderRequest request =
                    ref _meshOperationRequestScratch[index];
                VulkanResidentDrawTemplate template =
                    _residentTemplateHitScratch[index]!;
                XRRenderPipelineInstance pipeline = request.Pipeline!;
                XRCamera? camera = pipeline.LastRenderingCamera ??
                    pipeline.LastSceneCamera;
                if (!ReferenceEquals(scopedPipeline, pipeline) ||
                    !ReferenceEquals(scopedCamera, camera))
                {
                    cameraScope?.Dispose();
                    pipelineScope?.Dispose();
                    pipelineScope = RuntimeRenderingHostServices.Diagnostics
                        .PushRenderingPipeline(pipeline);
                    cameraScope = pipeline.RenderState.PushRenderingCamera(camera);
                    scopedPipeline = pipeline;
                    scopedCamera = camera;
                }

                PendingMeshDraw draw = template.NativeState.DrawTemplate with
                {
                    ModelMatrix = request.ModelMatrix,
                    PreviousModelMatrix = request.PreviousModelMatrix,
                    MaterialOverride = request.ResolvedMaterial.Material,
                    Instances = request.ExpandedInstances,
                    BillboardMode = request.BillboardMode,
                    TransformId = request.TransformId,
                    ViewSnapshot = request.ViewSnapshot,
                    ShadowCasterRelevance = request.ShadowCasterRelevance,
                    CanonicalDrawIdentitySnapshot =
                        request.CanonicalDrawIdentitySnapshot,
                    ResidentTemplateHandle =
                        _residentTemplateHandleScratch[index],
                };
                draw = draw with
                {
                    AutoUniformPublication =
                        VulkanAutoUniformPublicationSnapshot.Capture(
                            draw,
                            pipeline),
                };
                if (!_preparedMeshIngress.TryAppend(
                        request.PassIndex,
                        request.Producer.Target,
                        draw,
                        request.Context,
                        preserveSubmissionOrder: false,
                        isDynamicUi: false))
                {
                    deferredReason =
                        "Resident mesh templates exceeded prepared-ingress capacity before sealing.";
                    return false;
                }
            }

            _preparedMeshIngress.MarkCohortHit(
                reusedOperationCount: requestCount,
                legacyHoleMaterializationCount: 0);
            published = true;
            return true;
        }
        finally
        {
            _residentTemplateHitScratch.AsSpan(0, requestCount).Clear();
            _residentTemplateHandleScratch.AsSpan(0, requestCount).Clear();
            if (!published)
                _preparedMeshIngress.Clear();
            cameraScope?.Dispose();
            pipelineScope?.Dispose();
        }
    }

    private void PublishPreparedMeshIngressCohortHit()
    {
        int reusedOperationCount = _preparedMeshIngress.ReusedOperationCount;
        int legacyHoleMaterializationCount =
            _preparedMeshIngress.LegacyHoleMaterializationCount;
        _preparedMeshIngress.PublishDrawStats();
        if (reusedOperationCount > 0)
        {
            Interlocked.Add(
                ref _preparedMeshOperationReusedOperations,
                reusedOperationCount);
        }
        if (legacyHoleMaterializationCount > 0)
        {
            Interlocked.Add(
                ref _preparedMeshOperationLegacyHoleMaterializations,
                legacyHoleMaterializationCount);
        }
        Interlocked.Increment(ref _preparedMeshOperationCohortHits);
        RuntimeEngine.Rendering.Stats.Vulkan
            .RecordVulkanPreparedMeshOperationCohort(
                hit: true,
                built: false,
                fullyMaterialized: false,
                reusedOperationCount,
                legacyHoleMaterializationCount);
    }

    private bool IsPreparedMeshOperationCohortEligible(
        in VulkanMeshRenderRequest request,
        in VulkanMeshOperationRequest operation)
    {
        XRMaterial material = request.ResolvedMaterial.Material;
        VkRenderProgram? program = operation.Draw.PreparedProgram;
        ComputeDispatchSnapshot? bindings =
            operation.Draw.ProgramBindingSnapshot;
        bool reusableBindings = bindings is null ||
            (bindings.IsImmutableBindingArtifact &&
             !bindings.HasMutableFrameSourceSamplerBindings);
        return request.Pipeline is not null &&
               request.Renderer.IsActive &&
               request.Renderer.BackendContext.IsDeviceOperational &&
               ReferenceEquals(
                   request.Renderer.BackendContext.Resources,
                   _resourceRuntime) &&
               request.DeferredBindings.IsEmpty &&
               !request.ResolvedMaterial.IsShadowVariant &&
               request.RenderOptionsOverride is null &&
               !request.Context.PreserveSubmissionOrderBlock &&
               !request.ViewSnapshot.ShadowUniformState.IsShadowPass &&
               !request.Producer.IsExternalSwapchainTarget &&
               !request.Producer.IsPrewarmingExternalSwapchainTarget &&
               request.Producer.IndexedViewportScissors.Count <= 1 &&
               operation.Draw.IndexedViewports is null &&
               operation.Draw.IndexedScissors is null &&
               !request.Renderer.MeshRenderer.HasRenderDataPreparation &&
               !request.Renderer.MeshRenderer.HasSettingUniformsHandlers &&
               !material.HasSettingUniformsHandlers &&
               material.BindingPublishers.Count == 0 &&
               request.Renderer.MeshRenderer.BindingPublishers.Count == 0 &&
               RuntimeEngine.Rendering.State.RenderingPipelineState
                   ?.HasActiveScopedBindings != true &&
               program is { IsActive: true, IsLinked: true } &&
               operation.Draw.PreparedProgramLinkGeneration != 0 &&
               reusableBindings;
    }

    private bool IsPreparedMeshOperationCohortMatch(
        in VulkanMeshRenderRequest current,
        in VulkanPreparedMeshOperationCohortEntry cached,
        in VulkanMeshOperationRequest template)
    {
        if ((cached.IsReusable &&
             !IsPreparedMeshOperationCohortEligible(in current, in template)) ||
            !ReferenceEquals(current.Renderer, cached.Renderer) ||
            current.PassIndex != cached.PassIndex ||
            !ReferenceEquals(current.Pipeline, cached.Pipeline) ||
            !ReferenceEquals(current.ResolvedMaterial.Material, cached.Material) ||
            current.PreparationCompatibilitySignature !=
                cached.PreparationCompatibilitySignature ||
            !ReferenceEquals(current.MaterialOverride, cached.MaterialOverride) ||
            !ReferenceEquals(current.RenderOptionsOverride,
                cached.RenderOptionsOverride) ||
            current.ForceNoStereo != cached.ForceNoStereo)
        {
            return false;
        }

        XRMaterial material = current.ResolvedMaterial.Material;
        FrameOpContext context = current.Context;
        VulkanMeshProducerSnapshot producer = current.Producer;
        PendingMeshDraw draw = template.Draw;
        VkRenderProgram? program = draw.PreparedProgram;
        bool reusableDrawMatches = !cached.IsReusable ||
            (ReferenceEquals(draw.Renderer, current.Renderer) &&
             ReferenceEquals(draw.MaterialOverride, material) &&
             program is not null &&
             program.LinkGeneration == draw.PreparedProgramLinkGeneration);
        return reusableDrawMatches &&
               ReferenceEquals(producer.Target, cached.Target) &&
               producer.TargetExtent.Equals(cached.TargetExtent) &&
               producer.Viewport.Equals(cached.Viewport) &&
               producer.Scissor.Equals(cached.Scissor) &&
               producer.IndexedViewportScissors == cached.IndexedViewportScissors &&
               producer.FixedFunctionState == cached.FixedFunctionState &&
               context.PipelineIdentity == cached.PipelineIdentity &&
               context.ViewportIdentity == cached.ViewportIdentity &&
               context.OutputTargetIdentity == cached.OutputTargetIdentity &&
               context.OutputFrameBufferIdentity == cached.OutputFrameBufferIdentity &&
               context.RecordingFingerprint == cached.RecordingFingerprint &&
               context.ResourceGeneration == cached.ResourceGeneration &&
               context.DescriptorGeneration == cached.DescriptorGeneration &&
               context.InternalWidth == cached.InternalWidth &&
               context.InternalHeight == cached.InternalHeight &&
               context.StereoEnabled == cached.StereoEnabled &&
               context.MultiviewEnabled == cached.MultiviewEnabled &&
               material.BindingLayoutVersion ==
                   cached.MaterialBindingLayoutVersion &&
               material.BindingValueVersion ==
                   cached.MaterialBindingValueVersion &&
               material.BindingResourceVersion ==
                   cached.MaterialBindingResourceVersion &&
               material.ShaderStateRevision ==
                   cached.MaterialShaderStateRevision &&
               material.UberStateRevision ==
                   cached.MaterialUberStateRevision;
    }

    private static VulkanPreparedMeshOperationCohortEntry
        CreatePreparedMeshOperationCohortEntry(
            in VulkanMeshRenderRequest request,
            bool isReusable)
    {
        XRMaterial material = request.ResolvedMaterial.Material;
        VulkanMeshProducerSnapshot producer = request.Producer;
        FrameOpContext context = request.Context;
        return new VulkanPreparedMeshOperationCohortEntry(
            isReusable,
            request.Renderer,
            request.PassIndex,
            request.Pipeline,
            material,
            request.MaterialOverride,
            request.RenderOptionsOverride,
            request.PreparationCompatibilitySignature,
            request.ForceNoStereo,
            producer.Target,
            producer.TargetExtent,
            producer.Viewport,
            producer.Scissor,
            producer.IndexedViewportScissors,
            producer.FixedFunctionState,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.OutputTargetIdentity,
            context.OutputFrameBufferIdentity,
            context.RecordingFingerprint,
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.InternalWidth,
            context.InternalHeight,
            context.StereoEnabled,
            context.MultiviewEnabled,
            material.BindingLayoutVersion,
            material.BindingValueVersion,
            material.BindingResourceVersion,
            material.ShaderStateRevision,
            material.UberStateRevision);
    }

    private FrameOpContext CreateQueuedMeshRequestContext(
        in VulkanMeshRenderRequest request,
        XRRenderPipelineInstance pipeline)
        => request.Context.PipelineInstance is not null
            ? request.Context
            : CreateFrameOpContext(pipeline, pipeline.LastWindowViewport);

    private VulkanMeshProducerSnapshot CreateQueuedMeshProducer(
        in VulkanMeshRenderRequest request,
        in FrameOpContext requestContext)
        => request.Producer with
        {
            Context = requestContext,
            IsExternalSwapchainTarget =
                TryResolveExternalSwapchainTargetExtent(out _),
            IsPrewarmingExternalSwapchainTarget =
                IsPrewarmingOpenXrExternalSwapchainTarget,
        };

    private bool TryMaterializeQueuedMeshRenderRequest(
        in VulkanMeshRenderRequest request,
        XRRenderPipelineInstance pipeline,
        in VulkanMeshMaterializationSnapshot materializationSnapshot,
        bool prewarmDescriptorAllocation,
        out VulkanMeshOperationRequest operationRequest)
    {
        FrameOpContext requestContext = request.Context;
        VulkanMeshProducerSnapshot producer = request.Producer;
        int descriptorViewFamilyIdentity =
            requestContext.OutputTargetIdentity != 0
                ? requestContext.OutputTargetIdentity
                : requestContext.ViewportIdentity;
        VulkanMeshMaterializationSnapshot requestMaterializationSnapshot =
            materializationSnapshot with
            {
                ActiveFrameOpContext = requestContext,
                DescriptorViewFamilyIdentity = descriptorViewFamilyIdentity,
            };
        return request.Renderer.TryMaterializeQueuedRenderRequest(
            in request,
            in producer,
            in requestMaterializationSnapshot,
            prewarmDescriptorAllocation,
            out operationRequest);
    }

    private static bool IsQueuedDynamicUiOverlayRequest(
        in VulkanMeshRenderRequest request)
        => IsNamedDynamicUiOverlayOperation(
            request.Renderer.MeshRenderer,
            request.ResolvedMaterial.Material);

    private bool EnqueueQueuedMeshDraw(
        in VulkanMeshOperationRequest request,
        out MeshDrawOp? operation)
    {
        operation = null;
        int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(
            request.PassIndex,
            nameof(MeshDrawOp),
            request.Context.PassMetadata);
        if (passIndex == int.MinValue)
            return false;

        operation = MeshDrawOp.Rent(
            passIndex,
            request.ExplicitTarget ?? request.ProducerSnapshot.Target,
            request.Draw,
            request.Context,
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth > 0);
        return _commandRuntime.TryEnqueueContentFrameOperation(
            _frameOperationQueue,
            operation,
            passIndex,
            out _);
    }

    private bool TryStagePreparedMeshOperation(
        in VulkanMeshOperationRequest request)
    {
        int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(
            request.PassIndex,
            nameof(MeshDrawOp),
            request.Context.PassMetadata);
        if (passIndex == int.MinValue)
            return false;

        XRFrameBuffer? target =
            request.ExplicitTarget ?? request.ProducerSnapshot.Target;
        PendingMeshDraw draw = request.Draw;
        FrameOpContext context = request.Context;
        bool preserveSubmissionOrder =
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth > 0 ||
            context.PreserveSubmissionOrderBlock;
        // Cold authoring operations are classified after context coalescing.
        // Only the name-owned UI batch contract is invariant across that boundary;
        // generic UI-pipeline draws remain static until both paths share capture-time
        // classification metadata.
        bool dynamicUi = IsNamedDynamicUiOverlayOperation(in draw);
        return _preparedMeshIngress.TryAppend(
            passIndex,
            target,
            in draw,
            in context,
            preserveSubmissionOrder,
            dynamicUi);
    }

    /// <summary>
    /// Binds output-owned DLSS-G UI resources before plan sealing so command
    /// recording consumes only the frozen operation payload.
    /// </summary>
    private bool TryBindPreparedStreamlineUiImage(
        uint imageIndex,
        FrameOp[] staticOperations,
        VulkanAcceptedFramePlan? acceptedPlan,
        out string reason)
    {
        reason = string.Empty;
        bool requiresUiImage = acceptedPlan is not null
            ? acceptedPlan.LogicalPlan.RequiresAcquiredStreamlineUiImage
            : false;
        if (acceptedPlan is null)
        {
            for (int index = 0; index < staticOperations.Length; index++)
            {
                if (staticOperations[index] is DlssFrameGenerationOp)
                {
                    requiresUiImage = true;
                    break;
                }
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

        if (acceptedPlan is not null)
        {
            if (!acceptedPlan.LogicalPlan.BindAcquiredStreamlineUiImage(
                    in uiImage))
            {
                reason =
                    "The sealed accepted plan lost its admitted DLSS frame " +
                    "generation payload before acquired-image rebinding.";
                return false;
            }

            return true;
        }

        for (int index = 0; index < staticOperations.Length; index++)
        {
            if (staticOperations[index] is not DlssFrameGenerationOp frameGeneration)
                continue;

            FrameOp preparedOperation = frameGeneration with
            {
                UiColorAndAlpha = uiImage,
            };
            // The queued producer has already published its resource-use
            // declaration. A record copy preserves that immutable declaration;
            // only the output-owned UI image changes for this acquired target.
            staticOperations[index] = preparedOperation;
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
        PendingMeshDraw draw = drawOperation.Draw;
        FrameOpContext context = drawOperation.Context;
        return IsPreparedDynamicUiOverlayOperation(
            drawOperation.Target,
            drawOperation.PassIndex,
            in draw,
            in context);
    }

    private static bool IsPreparedDynamicUiOverlayOperation(
        XRFrameBuffer? target,
        int passIndex,
        in PendingMeshDraw draw,
        in FrameOpContext context)
    {
        if (IsNamedDynamicUiOverlayOperation(in draw))
            return true;

        return target is null &&
            passIndex == (int)EDefaultRenderPass.OnTopForward &&
            context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline;
    }

    private static bool IsNamedDynamicUiOverlayOperation(
        in PendingMeshDraw draw)
    {
        XRMeshRenderer meshRenderer = draw.Renderer.MeshRenderer;
        XRMaterial? material = draw.MaterialOverride ?? meshRenderer.Material;
        return IsNamedDynamicUiOverlayOperation(meshRenderer, material);
    }

    private static bool IsNamedDynamicUiOverlayOperation(
        XRMeshRenderer meshRenderer,
        XRMaterial? material)
        => string.Equals(
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
        in VulkanPreparedPrimaryAuthority authority,
        bool callerOwnsSubmissionMarkersUntilRecordingSucceeds = false)
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
            ReadOnlyStorageAuthority: FrameDataArena is { } arena
                ? ResourceRuntime.ReadOnlyStoragePreparedMap.CreateAuthority(
                    arena,
                    CurrentFrameSlot)
                : null,
            CommandChainSchedule: commandChainSchedule,
            CallerOwnsSubmissionMarkersUntilRecordingSucceeds:
                callerOwnsSubmissionMarkersUntilRecordingSucceeds);
    }

    private bool TryCapturePreparedPrimaryAuthority(
        uint imageIndex,
        in ResourcePlannerRuntimeState plannerState,
        in VulkanFramePlanningSnapshot frozenPlanningSnapshot,
        bool preserveSwapchainForOverlay,
        bool transitionSwapchainToPresent,
        bool allowSynchronousResourceUploads,
        bool freshSerialRecording,
        ERenderOutputReadinessPolicy readinessPolicy,
        ERenderOutputWorkClass workClass,
        ulong sourceFrameId,
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
                OutputRuntime.Desktop.StreamlineFrameGenerationActive,
            ReadinessPolicy: readinessPolicy,
            WorkClass: workClass,
            SourceFrameId: sourceFrameId,
            AllowArtifactReuse: workClass != ERenderOutputWorkClass.PresentNow,
            AllowSecondaryDeferral: workClass != ERenderOutputWorkClass.PresentNow);
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
        ref ResourcePlannerRuntimeState plannerState,
        bool allowSynchronousResourceUploads,
        out VulkanFramePlanningSnapshot frozenSnapshot,
        out string reason,
        int maximumAttempts = 2)
    {
        VulkanRenderGraphPlan sourcePlan = planningSnapshot.RenderGraphPlan;
        VulkanBarrierPlan sourceBarriers = sourcePlan.Barriers;
        ulong currentNativeBufferRevision = _resourceRuntime.NativeBufferBindingRevision;
        if (sourceBarriers.HasCompleteNativeBindings &&
            sourceBarriers.NativeBufferBindingRevision == currentNativeBufferRevision)
        {
            frozenSnapshot = planningSnapshot;
            reason = string.Empty;
            return true;
        }

        // A switching state can own frame-operation plans distinct from the
        // fallback plan. Refresh every currently required owner at the same
        // native-buffer epoch; otherwise command-plan resolution can select an
        // old cached barrier plan after the fallback has been refrozen.
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            ulong revisionBeforeRefresh = _resourceRuntime.NativeBufferBindingRevision;
            if (!_framePlanner.TryFreezeResourcePlannerRenderGraphPlan(
                    ref plannerState,
                    _resourceRuntime.BackendObjectContext,
                    allowSynchronousResourceUploads,
                    out reason,
                    out bool nativeBindingsSuperseded))
            {
                if (nativeBindingsSuperseded)
                    continue;
                frozenSnapshot = default;
                return false;
            }

            VulkanBarrierPlan refreshedBarriers = plannerState.RenderGraphPlan.Barriers;
            if (refreshedBarriers.HasCompleteNativeBindings &&
                refreshedBarriers.NativeBufferBindingRevision == revisionBeforeRefresh &&
                revisionBeforeRefresh == _resourceRuntime.NativeBufferBindingRevision)
            {
                PublishResourcePlannerRuntimeState(plannerState, commitReusedImageMetadata: false);
                _framePlanner.PublishPlan(plannerState.RenderGraphPlan);
                frozenSnapshot = planningSnapshot with { RenderGraphPlan = plannerState.RenderGraphPlan };
                reason = string.Empty;
                return true;
            }
        }

        throw new VulkanNativeBufferBindingSupersededException(
            $"Native buffer barrier refreeze for resource plan {plannerState.ResourcePlannerRevision} was superseded before sealing.");
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
