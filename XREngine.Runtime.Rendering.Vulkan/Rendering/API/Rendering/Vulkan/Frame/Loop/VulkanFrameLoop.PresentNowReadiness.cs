using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private VulkanPresentNowReadinessException? _presentNowTerminalFailure;

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

            VulkanTextureUploadSchedulingContext uploadContext =
                CreatePresentNowUploadContext();
            while (!_resourceRuntime.Uploads.DrainRequiredTextureUploads(
                       uploadContext,
                       acceptedPlan.RequiredTextureUploads))
            {
                if (watchdog.IsExpired)
                {
                    VulkanTextureUploadService.TryDescribeActiveUploadWork(
                        out string uploadDetail);
                    throw watchdog.CreateFailure(
                        EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                        "visible-now-texture-upload",
                        "DesktopScene -> material descriptor -> texture generation",
                        string.IsNullOrEmpty(uploadDetail)
                            ? "Required upload timeline did not advance."
                            : uploadDetail);
                }

                // Worker completions publish their generation state independently.
                // Do not pump arbitrary engine callbacks here: that would permit
                // render reentrancy while an accepted snapshot is frozen.
                Thread.Yield();
            }

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
                out int capacityExceededCount);
            if (capacityExceededCount > 0)
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.MeshMaterialization,
                    "frame-plan-capacity",
                    "DesktopScene -> visible mesh manifest arena",
                    $"FramePlanCapacityExceeded lane=Mesh actual=" +
                    $"{acceptedRequestCount + capacityExceededCount} " +
                    $"configured={VulkanMeshOperationRequestQueue.Capacity} " +
                    $"rejected={capacityExceededCount}.");
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
        if (!MaterializeQueuedMeshRenderRequests(
                requestCount,
                allowPreparedCohort: true,
                out string meshFailure,
                foregroundRequired: true,
                readinessDeadlineTimestamp: watchdog.DeadlineTimestamp,
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
                acceptedPlan.TextureUploadOperationCount);
        if (!logicalPlan.HasAnyExecutableOutput)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "output-dag",
                "DesktopScene -> terminal output",
                "The logical output DAG admitted no executable foreground output.");
        }

        RenderOutputRequest outputContract = logicalPlan.TryGetPresentNowContract(
            out RenderOutputRequest sealedContract)
                ? sealedContract
                : provisionalContract;
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

        acceptedPlan.Seal(
            in outputContract,
            logicalPlan.Generation,
            in plannerState,
            in frozenPlanningSnapshot);
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
                out string pipelineFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "graphics-pipeline",
                "DesktopScene -> graphics pipeline manifest",
                pipelineFailure);
        }

        _resourceRuntime.Uploads.CaptureRequiredTextureUploadManifest(
            acceptedPlan.RequiredTextureUploads);
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
        FramePlan logicalPlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
            acceptedPlan.FrameSlot,
            acceptedPlan.PlannerState.ResourcePlannerRevision,
            staticOperationSignature: 0UL,
            dynamicOverlaySignature: 0UL,
            acceptedPlan.StaticOperations,
            acceptedPlan.DynamicUiOperations,
            new VulkanFramePlanRenderGraphAuthority(
                acceptedPlan.FrozenPlanningSnapshot.RenderGraphPlan,
                acceptedPlan.PlannerState.FrameOpResourcePlannerSwitchingState),
            textureUploadOperations: acceptedPlan.TextureUploadOperations,
            preparedMeshIngress: acceptedPlan.PreparedMeshIngress,
            authoringOperationCount: acceptedPlan.StaticOperationCount,
            authoringDynamicOverlayOperationCount:
                acceptedPlan.DynamicUiOperationCount,
            authoringTextureUploadOperationCount:
                acceptedPlan.TextureUploadOperationCount);
        if (!_commandRuntime.TryPreparePresentNowPipelinesForSealedFramePlan(
                logicalPlan,
                logicalPlan.GetNativeStaticOperationsForRecording(),
                logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
                in compatibilityTarget,
                in resourcePlanStamp,
                in acceptedRenderGraphPlan,
                current.DynamicRendering,
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
            _commandRuntime,
            _frameOperationQueue,
            CaptureFrameOpContextOrLastActive());

    /// <summary>
    /// Executes the render-thread-affine foreground upload lane. Mesh/index and
    /// pipeline workers publish independently, so arbitrary engine callbacks are
    /// intentionally not pumped while the accepted snapshot is frozen.
    /// </summary>
    private void PumpPresentNowRequiredJobs()
    {
        _ = _resourceRuntime.Uploads.DrainRequiredTextureUploads(
            CreatePresentNowUploadContext());
    }

    private void PausePresentNowRenderer(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        _presentNowTerminalFailure ??= failure;
        Debug.VulkanError($"[Vulkan][PresentNow][RendererPaused] {failure.Message}");
        attempt.DeferredFailure = failure;
        attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
    }
}
