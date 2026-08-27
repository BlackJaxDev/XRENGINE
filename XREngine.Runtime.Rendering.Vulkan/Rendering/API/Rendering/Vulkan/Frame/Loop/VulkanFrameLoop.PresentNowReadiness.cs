using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private VulkanPresentNowReadinessException? _presentNowTerminalFailure;
    private readonly VulkanTextureUploadManifest _presentNowPumpUploadManifest = new();

    /// <summary>
    /// Freezes and completes format-independent foreground work before the WSI
    /// image is acquired. Work published after this drain remains queued for the
    /// next accepted frame.
    /// </summary>
    private EDesktopFrameFlow DriveDesktopPresentNowReadiness(
        ref VulkanFrameAttempt attempt)
    {
        if (_presentNowTerminalFailure is not null)
        {
            attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
            return EDesktopFrameFlow.Stop;
        }

        VulkanPresentNowReadinessWatchdog watchdog = new(attempt.FrameNumber);
        attempt.AcceptedSceneEpoch = RuntimeEngine.Rendering.State.RenderFrameId;
        try
        {
            VulkanAcceptedFramePlan acceptedPlan =
                AcceptDesktopPresentNowPlan(ref attempt, ref watchdog);
            watchdog.RecordProgress();

            CompleteAcceptedPresentNowTextureReadiness(
                acceptedPlan,
                ref watchdog,
                "DesktopScene -> material descriptor -> texture generation");

            watchdog.RecordProgress();
            RevalidateAcceptedDesktopTargetCompatibility(
                acceptedPlan,
                ref watchdog);
            attempt.PresentNowReadinessCompleted = true;
            Debug.VulkanEvery(
                $"Vulkan.PresentNow.Ready.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan][PresentNow] readiness=ready frame={0} sceneEpoch={1} " +
                "meshRequests={2} policy={3} workClass={4} acquireHeld=false",
                attempt.FrameNumber,
                attempt.AcceptedSceneEpoch,
                attempt.PresentNowMeshRequestCount,
                attempt.ReadinessPolicy,
                attempt.WorkClass);
            return EDesktopFrameFlow.Continue;
        }
        catch (VulkanPresentNowReadinessException failure)
        {
            PausePresentNowRenderer(ref attempt, failure);
            return EDesktopFrameFlow.Stop;
        }
        catch
        {
            attempt.AcceptedFramePlan?.SettleUnsubmittedSubmissionMarkers();
            throw;
        }
    }

    /// <summary>
    /// Accepts the complete logical desktop transaction into the already-retired
    /// CPU frame slot. No WSI image is owned while shadows, meshes, descriptors,
    /// frame operations, resource bindings, and pipeline requirements converge.
    /// </summary>
    private VulkanAcceptedFramePlan AcceptDesktopPresentNowPlan(
        ref VulkanFrameAttempt attempt,
        ref VulkanPresentNowReadinessWatchdog watchdog)
    {
        VulkanPresentNowTargetCompatibilityKey compatibility =
            CaptureDesktopTargetCompatibility();
        VulkanAcceptedFramePlan acceptedPlan = _acceptedFramePlans.Begin(
            attempt.FrameSlot,
            attempt.FrameNumber,
            attempt.AcceptedSceneEpoch,
            in compatibility);
        attempt.AcceptedFramePlan = acceptedPlan;

        RenderOutputRequest provisionalContract =
            CreateDesktopPresentNowOutputContract(attempt.FrameNumber, in compatibility);
        attempt.ReadinessPolicy = provisionalContract.ReadinessPolicy;
        attempt.WorkClass = provisionalContract.WorkClass;
        attempt.OutputGeneration = provisionalContract.Target.TargetGeneration;

        // Complete exact shadow production before freezing the mesh/FrameOp
        // queues. Shadow rendering may publish operations consumed by this frame.
        if (RuntimeEngine.Rendering.State.RenderingWorld?.Lights is { } lights)
        {
            ShadowAtlasReadinessManifest shadowManifest =
                lights.CaptureShadowReadiness(in provisionalContract);
            ShadowAtlasReadinessResult shadowResult =
                lights.CompleteShadowReadiness(in shadowManifest);
            acceptedPlan.ShadowReadiness = shadowManifest;
            acceptedPlan.ShadowReadinessResult = shadowResult;
            if (!shadowResult.IsSatisfied)
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    $"shadow-plan:{shadowManifest.RenderPlanId}",
                    "DesktopScene -> required shadow atlas",
                    $"Shadow readiness failed selection={shadowResult.Selection} " +
                    $"requiredTiles={shadowManifest.RequiredTileCount} " +
                    $"failedTiles={shadowResult.FailedTileCount} " +
                    $"unavailable={shadowManifest.UnavailableTileCount} " +
                    $"overflow={shadowManifest.RequestQueueOverflowCount}.");
            }
        }

        _preparedMeshIngress.Clear();
        int requestCount;
        using (VulkanCpuStageScope rawRequestDrainStage = new(
                   _telemetry,
                   EVulkanCpuStage.RawMeshRequestDrain))
        {
            requestCount = MeshOperationRequests.DrainTo(
                _meshOperationRequestScratch,
                foregroundRequired: true,
                out int acceptedRequestCount,
                out int capacityExceededCount,
                out VulkanMeshRequestLaneCapacityFailure capacityFailure,
                acceptedPlan.CanonicalPublicationPins);
            if (capacityExceededCount > 0)
            {
                string detail = capacityFailure.HasFailure
                    ? capacityFailure.FormatDiagnostic(capacityExceededCount)
                    : $"FramePlanCapacityExceeded lane=MainScene " +
                      $"actual={acceptedRequestCount + capacityExceededCount} " +
                      $"configured={VulkanMeshOperationRequestQueue.Capacity} " +
                      $"rejected={capacityExceededCount}.";
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.MeshMaterialization,
                    "frame-plan-capacity",
                    "DesktopScene -> visible mesh manifest arena",
                    detail);
            }
        }

        if (requestCount < 0)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.MeshMaterialization,
                "mesh-request-cohort",
                "DesktopScene -> visible meshes",
                "The bounded request queue rejected an accepted foreground cohort.");
        }

        attempt.PresentNowMeshRequestCount = requestCount;
        acceptedPlan.CaptureRequiredTextureReferences(
            _meshOperationRequestScratch.AsSpan(0, requestCount));
        _resourceRuntime.Uploads.CaptureRequiredTextureUploadManifest(
            acceptedPlan.RequiredTextureUploads,
            acceptedPlan.RequiredTextures,
            acceptedPlan.RequiredTextureGenerations,
            requireExactDescriptorPublication: false);
        CompleteAcceptedPresentNowTextureReadiness(
            acceptedPlan,
            ref watchdog,
            "DesktopScene -> visible material snapshot -> texture generation");

        if (!MaterializeQueuedMeshRenderRequests(
                requestCount,
                allowPreparedCohort: true,
                out string meshFailure,
                ref watchdog,
                sourceFrameId: attempt.FrameNumber))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.MeshMaterialization,
                "visible-mesh-generation",
                "DesktopScene -> visible meshes -> program/buffer/descriptor",
                meshFailure);
        }

        FrameOp[] drainedOperations =
            _framePlanner.Operations.DrainForPrimary(
                out FrameOp[] textureUploadOperations);
        acceptedPlan.ClaimUnsubmittedSubmissionMarkers(drainedOperations);
        VulkanFramePlanningSnapshot planningSnapshot =
            _framePlanner.CaptureSnapshot();
        VulkanSwapchainContextCoalescer.Coalesce(
            drainedOperations,
            _preparedMeshIngress);
        if (!_preparedMeshIngress.TryFinalize(
                ref _preparedMeshIngressResourceUseScratch))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "prepared-mesh-ingress",
                "DesktopScene -> prepared mesh dependency lowering",
                "Prepared mesh ingress exceeded its fixed resource-use capacity.");
        }
        if (_preparedMeshIngress.IsCohortHit)
            PublishPreparedMeshIngressCohortHit();

        SplitPreparedDynamicUiOperations(
            drainedOperations,
            out FrameOp[] staticOperations,
            out FrameOp[] dynamicUiOperations);
        _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
            staticOperations);
        _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
            dynamicUiOperations);

        VulkanComputePreparationResult computePreparation =
            _commandRuntime.PrepareComputeProgramsForFramePlan(staticOperations);
        if (computePreparation.Succeeded)
        {
            computePreparation = _commandRuntime.PrepareComputeProgramsForFramePlan(
                dynamicUiOperations);
        }
        if (!computePreparation.Succeeded)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "compute-program",
                "DesktopScene -> compute programs",
                computePreparation.FormatFailure());
        }

        ResourcePlannerRuntimeState plannerState =
            CaptureResourcePlannerRuntimeState();
        if (planningSnapshot.RenderGraphPlan.Revision !=
            plannerState.ResourcePlannerRevision)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "planner-revision",
                "DesktopScene -> resource plan",
                $"Planner publication changed before logical seal. " +
                $"planner={plannerState.ResourcePlannerRevision} " +
                $"graph={planningSnapshot.RenderGraphPlan.Revision}.");
        }
        if (!TryPrepareFrameOperationTargets(
                staticOperations,
                allowSynchronousResourceUploads: true,
                out string targetFailure) ||
            !TryPrepareFrameOperationTargets(
                dynamicUiOperations,
                allowSynchronousResourceUploads: true,
                out targetFailure) ||
            !TryPreparePreparedMeshIngressTargets(
                _preparedMeshIngress,
                allowSynchronousResourceUploads: true,
                out targetFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "frame-operation-target",
                "DesktopScene -> framebuffer dependencies",
                targetFailure);
        }
        if (!TryFreezeNativeBarrierBindings(
                in planningSnapshot,
                in plannerState,
                allowSynchronousResourceUploads: true,
                out VulkanFramePlanningSnapshot frozenPlanningSnapshot,
                out string freezeFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "native-barrier-bindings",
                "DesktopScene -> resource barrier bindings",
                freezeFailure);
        }

        acceptedPlan.CaptureOperations(
            staticOperations,
            dynamicUiOperations,
            textureUploadOperations);
        acceptedPlan.PreparedMeshIngress.CopyFrom(_preparedMeshIngress);
        _preparedMeshIngress.Clear();

        FramePlan logicalPlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
            attempt.FrameSlot,
            plannerState.ResourcePlannerRevision,
            staticOperationSignature: 0UL,
            dynamicOverlaySignature: 0UL,
            acceptedPlan.StaticOperations,
            acceptedPlan.DynamicUiOperations,
            new VulkanFramePlanRenderGraphAuthority(
                frozenPlanningSnapshot.RenderGraphPlan,
                plannerState.FrameOpResourcePlannerSwitchingState),
            textureUploadOperations: acceptedPlan.TextureUploadOperations,
            preparedMeshIngress: acceptedPlan.PreparedMeshIngress,
            authoringOperationCount: acceptedPlan.StaticOperationCount,
            authoringDynamicOverlayOperationCount:
                acceptedPlan.DynamicUiOperationCount,
            authoringTextureUploadOperationCount:
                acceptedPlan.TextureUploadOperationCount,
            emptyPresentNowOutputContract: provisionalContract);
        if (!logicalPlan.HasAnyExecutableOutput)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "output-dag",
                "DesktopScene -> terminal output",
                "The logical output DAG admitted no executable foreground output.");
        }

        if (!logicalPlan.TryGetExecutableOutputContract(
                in provisionalContract,
                out RenderOutputRequest outputContract))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "desktop-output-contract",
                "DesktopScene -> exact terminal output",
                "The sealed output DAG did not publish the required desktop PresentNow contract.");
        }
        if (outputContract.WorkClass != ERenderOutputWorkClass.PresentNow ||
            outputContract.ReadinessPolicy !=
                ERenderOutputReadinessPolicy.BlockForExact)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "output-contract",
                "DesktopScene -> terminal present policy",
                $"Foreground contract mismatch workClass={outputContract.WorkClass} " +
                $"readiness={outputContract.ReadinessPolicy}.");
        }

        attempt.ReadinessPolicy = outputContract.ReadinessPolicy;
        attempt.WorkClass = outputContract.WorkClass;
        attempt.OutputGeneration = outputContract.Target.TargetGeneration;

        if (!TryCreateDesktopCompatibilityTarget(
                out SwapchainRecordingTarget compatibilityTarget,
                out string compatibilityFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "swapchain-compatibility",
                "DesktopScene -> pipeline formats",
                compatibilityFailure);
        }
        VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
            frozenPlanningSnapshot,
            plannerState.ResourcePlannerRevision,
            plannerState.ResourcePlannerSignature,
            plannerState.ResourceAllocationSignature);
        VulkanRenderGraphPlan acceptedRenderGraphPlan =
            frozenPlanningSnapshot.RenderGraphPlan;
        if (!_commandRuntime.TryPreparePresentNowPipelinesForSealedFramePlan(
                logicalPlan,
                logicalPlan.GetNativeStaticOperationsForRecording(),
                logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
                in compatibilityTarget,
                in resourcePlanStamp,
                in acceptedRenderGraphPlan,
                UseDynamicRenderingRenderTargets,
                preserveSwapchainForOverlay: true,
                ref watchdog,
                out string pipelineFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "graphics-pipeline",
                "DesktopScene -> graphics pipeline manifest",
                pipelineFailure);
        }

        Image[]? desktopImages = OutputRuntime.Desktop.Images;
        if (desktopImages is null || desktopImages.Length == 0)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "desktop-frame-data-slots",
                "DesktopScene -> compute descriptors/uniforms",
                "The desktop swapchain has no frame-data slots to prepare before acquire.");
        }
        for (uint frameDataImageIndex = 0;
             frameDataImageIndex < (uint)desktopImages.Length;
             frameDataImageIndex++)
        {
            computePreparation =
                _commandRuntime.PrepareComputeFramePlanForRecording(
                    frameDataImageIndex,
                    logicalPlan);
            if (!computePreparation.Succeeded)
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    $"compute-frame-data:{frameDataImageIndex}",
                    "DesktopScene -> compute descriptors/uniforms",
                    computePreparation.FormatFailure());
            }
        }

        acceptedPlan.CaptureRequiredTextureReferences(
            logicalPlan,
            _resourceRuntime.Descriptors);
        _resourceRuntime.Uploads.CaptureRequiredTextureUploadManifest(
            acceptedPlan.RequiredTextureUploads,
            acceptedPlan.RequiredTextures,
            acceptedPlan.RequiredTextureGenerations,
            // Materialization already captured the exact descriptor bindings
            // for these published generations. Streaming requests admitted
            // after that boundary belong to the next accepted frame.
            requireExactDescriptorPublication: false);
        acceptedPlan.DeclareDependencies(logicalPlan);
        acceptedPlan.MarkNonTextureDependenciesReady();
        _ = acceptedPlan.SynchronizeTextureDependencies();
        acceptedPlan.Seal(
            in outputContract,
            logicalPlan,
            in plannerState,
            in frozenPlanningSnapshot);
        return acceptedPlan;
    }

    private VulkanPresentNowTargetCompatibilityKey
        CaptureDesktopTargetCompatibility()
        => new(
            OutputRuntime.Desktop.Generation,
            OutputRuntime.Desktop.ImageFormat,
            OutputRuntime.DesktopDepthFormat,
            OutputRuntime.Desktop.Extent,
            UseDynamicRenderingRenderTargets,
            OutputRuntime.Desktop.StreamlineFrameGenerationActive);

    private RenderOutputRequest CreateDesktopPresentNowOutputContract(
        ulong frameId,
        in VulkanPresentNowTargetCompatibilityKey compatibility)
    {
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.DesktopEditor,
            EFrameOutputKind.DesktopScene,
            frameId);
        ulong formatCompatibility =
            ((ulong)(uint)compatibility.ColorFormat << 32) |
            (uint)compatibility.DepthFormat;
        if (compatibility.DynamicRendering)
            formatCompatibility ^= 1UL << 63;
        RenderOutputTargetDescriptor target = request.Target with
        {
            TargetGeneration = compatibility.OutputGeneration,
            DisplayWidth = compatibility.Extent.Width,
            DisplayHeight = compatibility.Extent.Height,
            InternalWidth = compatibility.Extent.Width,
            InternalHeight = compatibility.Extent.Height,
            FormatCompatibilityKey = formatCompatibility,
        };
        return request.WithTarget(in target) with
        {
            ReadinessPolicy = ERenderOutputReadinessPolicy.BlockForExact,
            WorkClass = ERenderOutputWorkClass.PresentNow,
            FallbackPolicy = ERenderOutputFallbackPolicy.None,
        };
    }

    private bool TryCreateDesktopCompatibilityTarget(
        out SwapchainRecordingTarget target,
        out string reason)
    {
        VulkanSwapchainRecordingTargetInput targetInput = new(
            ImageIndex: 0u,
            OpenXrTargetContext: null,
            OutputRuntime.DesktopDepthResources,
            OpenXrInitialColorLayout: ImageLayout.Undefined,
            DesktopInitialColorLayout: ImageLayout.Undefined);
        target = OutputRuntime.ResolveRecordingTarget(in targetInput);
        if (!target.IsValid)
        {
            reason = "The desktop output has no symbolic format-compatible target.";
            return false;
        }
        target = target with
        {
            RenderPass = ResourceRuntime.SwapchainRenderPass,
            LoadRenderPass = ResourceRuntime.SwapchainLoadRenderPass,
            Framebuffer = OutputRuntime.Desktop.Framebuffers is { Length: > 0 }
                ? OutputRuntime.Desktop.Framebuffers[0]
                : default,
        };
        reason = string.Empty;
        return true;
    }

    private void RevalidateAcceptedDesktopTargetCompatibility(
        VulkanAcceptedFramePlan acceptedPlan,
        ref VulkanPresentNowReadinessWatchdog watchdog)
    {
        VulkanPresentNowTargetCompatibilityKey current =
            CaptureDesktopTargetCompatibility();
        if (current == acceptedPlan.TargetCompatibility)
            return;

        acceptedPlan.UpdateTargetCompatibility(in current);
        if (!TryCreateDesktopCompatibilityTarget(
                out SwapchainRecordingTarget compatibilityTarget,
                out string targetFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "target-revalidation",
                "DesktopScene -> swapchain compatibility",
                targetFailure);
        }
        VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
            acceptedPlan.FrozenPlanningSnapshot,
            acceptedPlan.PlannerState.ResourcePlannerRevision,
            acceptedPlan.PlannerState.ResourcePlannerSignature,
            acceptedPlan.PlannerState.ResourceAllocationSignature);
        VulkanRenderGraphPlan acceptedRenderGraphPlan =
            acceptedPlan.FrozenPlanningSnapshot.RenderGraphPlan;
        FramePlan logicalPlan = acceptedPlan.LogicalPlan;
        if (!_commandRuntime.TryPreparePresentNowPipelinesForSealedFramePlan(
                logicalPlan,
                logicalPlan.GetNativeStaticOperationsForRecording(),
                logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
                in compatibilityTarget,
                in resourcePlanStamp,
                in acceptedRenderGraphPlan,
                current.DynamicRendering,
                preserveSwapchainForOverlay: true,
                ref watchdog,
                out string pipelineFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "target-revalidation-pipeline",
                "DesktopScene -> swapchain-compatible pipelines",
                pipelineFailure);
        }
    }

    private VulkanTextureUploadSchedulingContext CreatePresentNowUploadContext()
        => new(
            BackendObjectContext,
            _resourceRuntime,
            _commandRuntime);

    private void CompleteAcceptedPresentNowTextureReadiness(
        VulkanAcceptedFramePlan acceptedPlan,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        string dependencyChain)
    {
        VulkanTextureUploadSchedulingContext uploadContext =
            CreatePresentNowUploadContext();
        while (true)
        {
            bool uploadsReady;
            bool uploadProgress;
            try
            {
                uploadsReady =
                    _resourceRuntime.Uploads.DrainRequiredTextureUploads(
                        uploadContext,
                        acceptedPlan.RequiredTextureUploads,
                        out uploadProgress);
            }
            catch (Exception exception)
            {
                string detail =
                    $"Required texture readiness raised " +
                    $"{exception.GetType().Name}: {exception.Message}";
                acceptedPlan.RequiredTextureUploads.FailUnresolved(detail);
                _ = acceptedPlan.SynchronizeTextureDependencies();
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                    "visible-now-texture-upload",
                    dependencyChain,
                    detail,
                    exception);
            }
            bool dependencyProgress =
                acceptedPlan.SynchronizeTextureDependencies();
            if (uploadProgress || dependencyProgress)
                watchdog.RecordProgress();
            if (acceptedPlan.RequiredTextureUploads.TryGetTerminalFailure(
                    out VulkanTextureUploadTicket failedTicket,
                    out string failureDetail))
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                    $"texture-upload:{failedTicket.Sequence}:" +
                    failedTicket.StreamingGeneration,
                    dependencyChain,
                    failureDetail);
            }
            if (uploadsReady &&
                acceptedPlan.RequiredTextureUploads.AreAllReady)
            {
                return;
            }

            if (watchdog.IsExpired)
            {
                VulkanTextureUploadService.TryDescribeActiveUploadWork(
                    out string uploadDetail);
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                    "visible-now-texture-upload",
                    dependencyChain,
                    string.IsNullOrEmpty(uploadDetail)
                        ? "Required upload completion did not advance."
                        : uploadDetail);
            }

            // Worker completions publish their generation state independently.
            // Do not pump arbitrary engine callbacks here: that would permit
            // render reentrancy while an accepted snapshot is frozen.
            Thread.Yield();
        }
    }

    /// <summary>
    /// Executes the render-thread-affine foreground upload lane. Mesh/index and
    /// pipeline workers publish independently, so arbitrary engine callbacks are
    /// intentionally not pumped while the accepted snapshot is frozen.
    /// </summary>
    private void PumpPresentNowRequiredJobs()
    {
        _resourceRuntime.Uploads.CaptureRequiredTextureUploadManifest(
            _presentNowPumpUploadManifest);
        _ = _resourceRuntime.Uploads.DrainRequiredTextureUploads(
            CreatePresentNowUploadContext(),
            _presentNowPumpUploadManifest,
            out _);
    }

    private void PausePresentNowRenderer(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        attempt.AcceptedFramePlan?.SettleUnsubmittedSubmissionMarkers();
        _presentNowTerminalFailure ??= failure;
        Debug.VulkanError($"[Vulkan][PresentNow][RendererPaused] {failure.Message}");
        attempt.DeferredFailure = failure;
        attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
    }
}
