using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private const int PresentNowRecoveryProbeMaximumAttempts = 3;
    private VulkanPresentNowReadinessException? _presentNowTerminalFailure;
    private VulkanPresentNowReadinessException? _presentNowRecoverableFailure;
    private VulkanPresentNowReadinessException? _presentNowRecoverySourceFailure;
    private readonly VulkanTextureUploadManifest _presentNowPumpUploadManifest = new();
    private long _presentNowTerminalTransitionSequence;
    private long _presentNowFailureDiagnosticSequence;
    private long _presentNowRecoveryRequestSequence;
    private long _presentNowConsumedRecoveryRequestSequence;
    private string _presentNowRecoveryRequestReason = "renderer state changed";
    private int _presentNowRecoveryProbeAttempts;
    private int _presentNowAutomaticRecoveryPending;
    private int _presentNowRecoveryProbeActive;

    /// <summary>
    /// Publishes a cold-path recovery edge for a failure explicitly classified as
    /// recoverable. Hard capacity, invariant, memory, and device failures retain
    /// their terminal latch and ignore these requests.
    /// </summary>
    internal void RequestPresentNowRecovery(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
            Interlocked.Exchange(ref _presentNowRecoveryRequestReason, reason);
        Interlocked.Increment(ref _presentNowRecoveryRequestSequence);
    }

    /// <summary>
    /// Freezes and completes format-independent foreground work before the WSI
    /// image is acquired. Work published after this drain remains queued for the
    /// next accepted frame.
    /// </summary>
    private EDesktopFrameFlow DriveDesktopPresentNowReadiness(
        ref VulkanFrameAttempt attempt)
    {
        if (_presentNowTerminalFailure is { } terminalFailure)
        {
            attempt.RejectedFailure = terminalFailure;
            attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
            return EDesktopFrameFlow.Stop;
        }
        VulkanPresentNowReadinessException? recoverableFailure =
            _presentNowRecoverableFailure;
        if (recoverableFailure is not null &&
            !TryBeginPresentNowRecoveryProbe(ref attempt, recoverableFailure))
        {
            attempt.RejectedFailure = recoverableFailure;
            attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
            return EDesktopFrameFlow.Stop;
        }
        if (!TryEnterPresentNowRecoveryProbeAttempt(ref attempt))
            return EDesktopFrameFlow.Stop;

        using RenderForegroundWorkCoordinator.ExactForegroundScope foregroundScope =
            RenderForegroundWorkCoordinator.EnterExactForeground();
        using VulkanProgramLinkPreparationScope programPreparation =
            new(_resourceRuntime);
        VulkanPresentNowReadinessWatchdog watchdog = new(attempt.FrameNumber);
        attempt.AcceptedSceneEpoch = RuntimeEngine.Rendering.State.RenderFrameId;
        try
        {
            if (!TryAcceptDesktopPresentNowPlan(
                    ref attempt,
                    ref watchdog,
                    out VulkanAcceptedFramePlan acceptedPlan,
                    out VulkanPresentNowReadinessRetry retry))
            {
                RejectPresentNowFrame(ref attempt, in retry);
                return EDesktopFrameFlow.Stop;
            }
            watchdog.RecordProgress();

            ClassifyIncompleteResizeReleaseSuccessorBeforeAcquire(
                acceptedPlan,
                ref attempt);

            if (!CompleteAcceptedPresentNowTextureReadiness(
                    acceptedPlan,
                    ref watchdog,
                    "DesktopScene -> material descriptor -> texture generation",
                    out retry))
            {
                RejectPresentNowFrame(ref attempt, in retry);
                return EDesktopFrameFlow.Stop;
            }

            watchdog.RecordProgress();
            if (!TryRevalidateAcceptedDesktopTargetCompatibility(
                    acceptedPlan,
                    ref watchdog,
                    out retry))
            {
                RejectPresentNowFrame(ref attempt, in retry);
                return EDesktopFrameFlow.Stop;
            }
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
            HandlePresentNowFailureBeforeAcquire(ref attempt, failure);
            return EDesktopFrameFlow.Stop;
        }
        catch
        {
            ResetIncompleteAcceptedPresentNowPlan(ref attempt);
            throw;
        }
    }

    /// <summary>
    /// Accepts the complete logical desktop transaction into the already-retired
    /// CPU frame slot. No WSI image is owned while shadows, meshes, descriptors,
    /// frame operations, resource bindings, and pipeline requirements converge.
    /// </summary>
    private bool TryAcceptDesktopPresentNowPlan(
        ref VulkanFrameAttempt attempt,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        out VulkanAcceptedFramePlan acceptedPlan,
        out VulkanPresentNowReadinessRetry retry)
    {
        retry = default;
        VulkanPresentNowTargetCompatibilityKey compatibility =
            CaptureDesktopTargetCompatibility();
        acceptedPlan = _acceptedFramePlans.Begin(
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

        CapturePresentNowAuthoredOperations(acceptedPlan);

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
                _meshOperationRequestScratch.AsSpan(0, requestCount).Clear();
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
                    detail,
                    disposition: EVulkanPresentNowFailureDisposition.RendererTerminal);
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
        if (!CompleteAcceptedPresentNowTextureReadiness(
                acceptedPlan,
                ref watchdog,
                "DesktopScene -> visible material snapshot -> texture generation",
                out retry))
        {
            return false;
        }

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

        CapturePresentNowAuthoredOperations(acceptedPlan);
        VulkanFramePlanningSnapshot planningSnapshot =
            _framePlanner.CaptureSnapshot();
        VulkanSwapchainContextCoalescer.Coalesce(
            acceptedPlan.AuthoredOperations,
            _preparedMeshIngress);
        bool stableBinPrepared = _preparedMeshIngress.TryFinalize(
            ref _preparedMeshIngressResourceUseScratch);
        if (stableBinPrepared)
        {
            VulkanResidentDrawTemplateTable residentTemplates =
                _resourceRuntime.ResidentDrawTemplates;
            stableBinPrepared = _preparedMeshIngress.TryBuildStableBinStream(
                residentTemplates) && _preparedMeshIngress.StableBinStream
                    .TryResolveManifests(
                        residentTemplates.StableBinManifestCache,
                        residentTemplates.StableBinMembership.TopologyGeneration);
        }
        if (!stableBinPrepared)
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
            acceptedPlan.AuthoredOperations,
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
            if (computePreparation.Pending)
            {
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    "compute-program",
                    "DesktopScene -> compute programs",
                    computePreparation.FormatFailure());
                return false;
            }

            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "compute-program",
                "DesktopScene -> compute programs",
                computePreparation.FormatFailure(),
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }

        ResourcePlannerRuntimeState plannerState =
            CaptureResourcePlannerRuntimeState();
        if (planningSnapshot.RenderGraphPlan.Revision !=
            plannerState.ResourcePlannerRevision)
        {
            retry = watchdog.CreateRetry(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "planner-revision",
                "DesktopScene -> resource plan",
                $"Planner publication changed before logical seal. " +
                $"planner={plannerState.ResourcePlannerRevision} " +
                $"graph={planningSnapshot.RenderGraphPlan.Revision}.");
            return false;
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
            if (VulkanFrameBufferAttachmentNotReadyException.IsTransientReason(targetFailure))
            {
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    "frame-operation-target",
                    "DesktopScene -> framebuffer dependencies",
                    targetFailure);
                return false;
            }

            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "frame-operation-target",
                "DesktopScene -> framebuffer dependencies",
                targetFailure,
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }
        VulkanFramePlanningSnapshot frozenPlanningSnapshot;
        try
        {
            if (!TryFreezeNativeBarrierBindings(
                    in planningSnapshot,
                    ref plannerState,
                    allowSynchronousResourceUploads: true,
                    out frozenPlanningSnapshot,
                    out string freezeFailure))
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    "native-barrier-bindings",
                    "DesktopScene -> resource barrier bindings",
                    freezeFailure,
                    disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
            }
        }
        catch (VulkanNativeBufferBindingSupersededException exception)
        {
            retry = watchdog.CreateRetry(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "native-barrier-bindings",
                "DesktopScene -> resource barrier bindings",
                exception.Message);
            return false;
        }

        acceptedPlan.TransferAuthoredOperations(
            staticOperations,
            dynamicUiOperations);
        acceptedPlan.PreparedMeshIngress.CopyFrom(_preparedMeshIngress);
        _preparedMeshIngress.Clear();

        FramePlan logicalPlan;
        try
        {
            logicalPlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
            attempt.FrameSlot,
            plannerState.ResourcePlannerRevision,
            staticOperationSignature: 0UL,
            dynamicOverlaySignature: 0UL,
            acceptedPlan.StaticOperations,
            acceptedPlan.DynamicUiOperations,
            new VulkanFramePlanRenderGraphAuthority(
                frozenPlanningSnapshot.RenderGraphPlan,
                plannerState.FrameOpResourcePlannerSwitchingState,
                _framePlanner,
                _resourceRuntime.BackendObjectContext,
                AllowSynchronousResourceUploads: true),
            textureUploadOperations: acceptedPlan.TextureUploadOperations,
            preparedMeshIngress: acceptedPlan.PreparedMeshIngress,
            authoringOperationCount: acceptedPlan.StaticOperationCount,
            authoringDynamicOverlayOperationCount:
                acceptedPlan.DynamicUiOperationCount,
            authoringTextureUploadOperationCount:
                acceptedPlan.TextureUploadOperationCount,
                emptyPresentNowOutputContract: provisionalContract);
        }
        catch (VulkanNativeBufferBindingSupersededException exception)
        {
            retry = watchdog.CreateRetry(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "native-barrier-bindings",
                "DesktopScene -> resource barrier bindings",
                exception.Message);
            return false;
        }
        logicalPlan.PrepareRecordingPlannerGenerations(in plannerState);
        if (!logicalPlan.HasAnyExecutableOutput)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "output-dag",
                "DesktopScene -> terminal output",
                "The logical output DAG admitted no executable foreground output.",
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }

        if (!logicalPlan.TryGetExecutableOutputContract(
                in provisionalContract,
                out RenderOutputRequest outputContract))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "desktop-output-contract",
                "DesktopScene -> exact terminal output",
                "The sealed output DAG did not publish the required desktop PresentNow contract.",
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
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
                $"readiness={outputContract.ReadinessPolicy}.",
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
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
                compatibilityFailure,
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }
        VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
            frozenPlanningSnapshot,
            plannerState.ResourcePlannerRevision,
            plannerState.ResourcePlannerSignature,
            plannerState.ResourceAllocationSignature);
        VulkanRenderGraphPlan acceptedRenderGraphPlan =
            frozenPlanningSnapshot.RenderGraphPlan;
        acceptedPlan.CaptureRequiredTextureReferences(
            logicalPlan,
            _resourceRuntime.Descriptors);
        _resourceRuntime.Uploads.CaptureRequiredTextureUploadManifest(
            acceptedPlan.RequiredTextureUploads,
            acceptedPlan.RequiredTextures,
            acceptedPlan.RequiredTextureGenerations,
            requireExactDescriptorPublication: false);
        if (!CompleteAcceptedPresentNowTextureReadiness(
                acceptedPlan,
                ref watchdog,
                "DesktopScene -> canonical scene texture -> exact descriptor",
                out retry))
        {
            return false;
        }
        if (!TryPrepareReadOnlyStorage(
                logicalPlan, attempt.FrameSlot,
                out VulkanReadOnlyStoragePreparedAuthority? storageAuthority,
                out bool materialPending,
                out string storageFailure))
        {
            if (materialPending)
            {
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    "immutable-storage",
                    "DesktopScene -> immutable buffer descriptors",
                    storageFailure);
                return false;
            }

            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "immutable-storage", "DesktopScene -> immutable buffer descriptors", storageFailure,
                disposition: EVulkanPresentNowFailureDisposition.RendererTerminal);
        }
        using VulkanResourceRuntime.ReadOnlyStorageRecordingScope storageScope =
            ResourceRuntime.EnterReadOnlyStorageRecordingScope(storageAuthority);
        watchdog.RecordProgress();
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
                out bool pipelineRetryable,
                out string pipelineFailure))
        {
            if (pipelineRetryable)
            {
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    "graphics-pipeline",
                    "DesktopScene -> graphics pipeline manifest",
                    pipelineFailure);
                return false;
            }

            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "graphics-pipeline",
                "DesktopScene -> graphics pipeline manifest",
                pipelineFailure,
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }

        Image[]? desktopImages = OutputRuntime.Desktop.Images;
        if (desktopImages is null || desktopImages.Length == 0)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "desktop-frame-data-slots",
                "DesktopScene -> compute descriptors/uniforms",
                "The desktop swapchain has no frame-data slots to prepare before acquire.",
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }
        for (uint frameDataImageIndex = 0;
             frameDataImageIndex < (uint)desktopImages.Length;
             frameDataImageIndex++)
        {
            computePreparation =
                _commandRuntime.PrepareComputeFramePlanForRecording(
                    frameDataImageIndex,
                    logicalPlan,
                    in plannerState);
            if (!computePreparation.Succeeded)
            {
                if (computePreparation.Pending)
                {
                    retry = watchdog.CreateRetry(
                        EVulkanPresentNowReadinessStage.PipelineCompilation,
                        $"compute-frame-data:{frameDataImageIndex}",
                        "DesktopScene -> compute descriptors/uniforms",
                        computePreparation.FormatFailure());
                    return false;
                }

                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    $"compute-frame-data:{frameDataImageIndex}",
                    "DesktopScene -> compute descriptors/uniforms",
                    computePreparation.FormatFailure(),
                    disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
            }
        }

        acceptedPlan.DeclareDependencies(logicalPlan);
        acceptedPlan.MarkNonTextureDependenciesReady();
        _ = acceptedPlan.SynchronizeTextureDependencies();
        acceptedPlan.Seal(
            in outputContract,
            logicalPlan,
            in plannerState,
            in frozenPlanningSnapshot);
        return true;
    }

    private void CapturePresentNowAuthoredOperations(
        VulkanAcceptedFramePlan acceptedPlan)
    {
        FrameOp[] operations = _framePlanner.Operations.DrainForPrimary(
            out FrameOp[] textureUploadOperations);
        acceptedPlan.CaptureAuthoredOperations(
            operations,
            textureUploadOperations);
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

    private bool TryRevalidateAcceptedDesktopTargetCompatibility(
        VulkanAcceptedFramePlan acceptedPlan,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        out VulkanPresentNowReadinessRetry retry)
    {
        retry = default;
        VulkanPresentNowTargetCompatibilityKey current =
            CaptureDesktopTargetCompatibility();
        if (current == acceptedPlan.TargetCompatibility)
            return true;

        acceptedPlan.UpdateTargetCompatibility(in current);
        if (!TryCreateDesktopCompatibilityTarget(
                out SwapchainRecordingTarget compatibilityTarget,
                out string targetFailure))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "target-revalidation",
                "DesktopScene -> swapchain compatibility",
                targetFailure,
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }
        VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
            acceptedPlan.FrozenPlanningSnapshot,
            acceptedPlan.PlannerState.ResourcePlannerRevision,
            acceptedPlan.PlannerState.ResourcePlannerSignature,
            acceptedPlan.PlannerState.ResourceAllocationSignature);
        VulkanRenderGraphPlan acceptedRenderGraphPlan =
            acceptedPlan.FrozenPlanningSnapshot.RenderGraphPlan;
        FramePlan logicalPlan = acceptedPlan.LogicalPlan;
        if (!TryPrepareReadOnlyStorage(
                logicalPlan, CurrentFrameSlot,
                out VulkanReadOnlyStoragePreparedAuthority? storageAuthority,
                out bool materialPending,
                out string storageFailure))
        {
            if (materialPending)
            {
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    "immutable-storage-revalidation",
                    "DesktopScene -> immutable buffer descriptors",
                    storageFailure);
                return false;
            }

            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "immutable-storage-revalidation", "DesktopScene -> immutable buffer descriptors", storageFailure,
                disposition: EVulkanPresentNowFailureDisposition.RendererTerminal);
        }
        using VulkanResourceRuntime.ReadOnlyStorageRecordingScope storageScope =
            ResourceRuntime.EnterReadOnlyStorageRecordingScope(storageAuthority);
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
                out bool pipelineRetryable,
                out string pipelineFailure))
        {
            if (pipelineRetryable)
            {
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    "target-revalidation-pipeline",
                    "DesktopScene -> swapchain-compatible pipelines",
                    pipelineFailure);
                return false;
            }

            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "target-revalidation-pipeline",
                "DesktopScene -> swapchain-compatible pipelines",
                pipelineFailure,
                disposition: EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        }
        return true;
    }

    private VulkanTextureUploadSchedulingContext CreatePresentNowUploadContext()
        => CreateTextureUploadSchedulingContext();

    private bool CompleteAcceptedPresentNowTextureReadiness(
        VulkanAcceptedFramePlan acceptedPlan,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        string dependencyChain,
        out VulkanPresentNowReadinessRetry retry)
    {
        retry = default;
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
                    exception,
                    EVulkanPresentNowFailureDisposition.RendererTerminal);
            }
            bool dependencyProgress =
                acceptedPlan.SynchronizeTextureDependencies();
            if (uploadProgress || dependencyProgress)
                watchdog.RecordProgress();
            if (acceptedPlan.RequiredTextureUploads.TryGetTerminalFailure(
                    out VulkanTextureUploadTicket failedTicket,
                    out string failureDetail,
                    out EVulkanPresentNowFailureDisposition disposition))
            {
                if (disposition == EVulkanPresentNowFailureDisposition.RetryFrame)
                {
                    retry = watchdog.CreateRetry(
                        EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                        $"texture-upload:{failedTicket.Sequence}:" +
                        failedTicket.StreamingGeneration,
                        dependencyChain,
                        failureDetail);
                    return false;
                }

                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                    $"texture-upload:{failedTicket.Sequence}:" +
                    failedTicket.StreamingGeneration,
                    dependencyChain,
                    failureDetail,
                    disposition: disposition);
            }
            if ((acceptedPlan.RequiredTextureUploads.UnresolvedCount > 0 ||
                 _resourceRuntime.Uploads.HasRequiredUploadRegistrationPending(
                     acceptedPlan.RequiredTextureUploads)) &&
                !uploadProgress &&
                !dependencyProgress)
            {
                // The streaming generation is known, but its Vulkan upload
                // ticket is still waiting in an outer render-thread callback.
                // Reject this snapshot so the inter-frame job pump can publish
                // that ticket; blocking here would deadlock the callback behind
                // PresentNow and eventually pause the renderer.
                retry = watchdog.CreateRetry(
                    EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                    "texture-upload-registration",
                    dependencyChain,
                    "Required texture upload registration is pending at the outer frame boundary.");
                return false;
            }
            if (uploadsReady &&
                acceptedPlan.RequiredTextureUploads.AreAllReady)
            {
                return true;
            }

            if (uploadProgress || dependencyProgress)
                continue;

            VulkanTextureUploadService.TryDescribeActiveUploadWork(
                out string uploadDetail);
            retry = watchdog.CreateRetry(
                EVulkanPresentNowReadinessStage.RequiredUploadCompletion,
                "visible-now-texture-upload",
                dependencyChain,
                string.IsNullOrEmpty(uploadDetail)
                    ? "Required upload completion is pending asynchronous preparation or transfer publication."
                    : uploadDetail);
            return false;
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

    private void RejectPresentNowFrame(
        ref VulkanFrameAttempt attempt,
        in VulkanPresentNowReadinessRetry retry)
    {
        PublishPresentNowRetryDiagnostic(ref attempt, in retry);
        ResetIncompleteAcceptedPresentNowPlan(ref attempt);
        attempt.PresentNowReadinessRetry = retry;
        Debug.VulkanEvery(
            $"Vulkan.PresentNow.FrameRetry.{retry.Stage}.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan][PresentNow][FrameRetry] frame={0} stage={1} " +
            "ticket={2} detail={3}",
            retry.FrameId,
            retry.Stage,
            retry.ActiveTicket,
            retry.Detail);
        attempt.Stop(EDesktopFrameReason.PresentNowReadinessRetry);
    }

    private void PausePresentNowRenderer(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        StorePresentNowTerminalFailure(ref attempt, failure);
        ResetIncompleteAcceptedPresentNowPlan(ref attempt);
        attempt.DeferredFailure = _presentNowTerminalFailure;
        attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
    }

    private void StorePresentNowTerminalFailure(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        PublishPresentNowFailureDiagnostic(ref attempt, failure);
        Interlocked.Exchange(ref _presentNowRecoveryProbeActive, 0);
        Volatile.Write(ref _presentNowAutomaticRecoveryPending, 0);
        _presentNowRecoveryProbeAttempts = 0;
        _presentNowRecoverySourceFailure = null;
        _presentNowRecoverableFailure = null;

        if (_presentNowTerminalFailure is null)
        {
            VulkanPresentNowTerminalTransitionRecord transition =
                CapturePresentNowTerminalTransition(ref attempt, failure);
            _presentNowTerminalFailure = failure;
            _telemetry.PublishPresentNowTerminalTransition(in transition);
            PublishPresentNowTerminalTransitionDiagnostics(in transition);
        }
    }

    /// <summary>
    /// Explicit-production callers expose admission pending through their public
    /// API. Keep their boundary exception separate from the desktop frame loop's
    /// typed, non-throwing retry path.
    /// </summary>
    private void CompleteAcceptedPresentNowTextureReadiness(
        VulkanAcceptedFramePlan acceptedPlan,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        string dependencyChain)
    {
        if (CompleteAcceptedPresentNowTextureReadiness(
                acceptedPlan,
                ref watchdog,
                dependencyChain,
                out VulkanPresentNowReadinessRetry retry))
        {
            return;
        }

        throw new VulkanExplicitProductionAdmissionPendingException(
            retry.Stage.ToString(),
            retry.Detail);
    }

    private void HandlePresentNowFailureBeforeAcquire(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        switch (failure.Disposition)
        {
            case EVulkanPresentNowFailureDisposition.RetryFrame:
                // RetryFrame is not a valid exception disposition on desktop.
                // Treat an externally constructed violation as a terminal fault
                // instead of allowing exception-driven retry to return.
                PausePresentNowRenderer(ref attempt, failure);
                break;
            case EVulkanPresentNowFailureDisposition.RecoverAfterStateChange:
                DeferPresentNowUntilStateChange(ref attempt, failure);
                break;
            default:
                PausePresentNowRenderer(ref attempt, failure);
                break;
        }
    }

    private void DeferPresentNowUntilStateChange(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        PublishPresentNowFailureDiagnostic(ref attempt, failure);
        bool failedProbe =
            Interlocked.Exchange(ref _presentNowRecoveryProbeActive, 0) != 0;
        _presentNowRecoveryProbeAttempts = 0;
        _presentNowRecoverySourceFailure = null;
        _presentNowRecoverableFailure = failure;
        Volatile.Write(
            ref _presentNowAutomaticRecoveryPending,
            failedProbe ? 0 : 1);
        if (!failedProbe)
        {
            // Requests that predate the failure are stale. A request published
            // while a probe was active must remain newer than the sequence that
            // was consumed when the probe began so it can admit the next probe.
            Volatile.Write(
                ref _presentNowConsumedRecoveryRequestSequence,
                Volatile.Read(ref _presentNowRecoveryRequestSequence));
        }
        ResetIncompleteAcceptedPresentNowPlan(ref attempt);
        attempt.RejectedFailure = failure;
        attempt.Stop(EDesktopFrameReason.PresentNowReadinessFailed);
        Debug.VulkanWarning(
            "[Vulkan][PresentNow][RecoveryPending] frame={0} stage={1} automaticProbe={2} detail={3}",
            failure.FrameId,
            failure.Stage,
            !failedProbe,
            failure.Message);
    }

    private bool TryBeginPresentNowRecoveryProbe(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException recoverableFailure)
    {
        if (!_deviceContext.StateMachine.IsOperational)
            return false;

        long requestedSequence =
            Volatile.Read(ref _presentNowRecoveryRequestSequence);
        long consumedSequence =
            Volatile.Read(ref _presentNowConsumedRecoveryRequestSequence);
        bool automaticProbe =
            Interlocked.Exchange(ref _presentNowAutomaticRecoveryPending, 0) != 0;
        if (!automaticProbe && requestedSequence <= consumedSequence)
            return false;

        Volatile.Write(
            ref _presentNowConsumedRecoveryRequestSequence,
            requestedSequence);
        _presentNowRecoverableFailure = null;
        _presentNowRecoverySourceFailure = recoverableFailure;
        _presentNowRecoveryProbeAttempts = 0;
        Volatile.Write(ref _presentNowRecoveryProbeActive, 1);
        _commandRuntime.CommandBuffers.MarkDirty(
            "PresentNow state-change recovery probe");

        Debug.VulkanWarning(
            "[Vulkan][PresentNow][RecoveryProbe] frame={0} sourceFrame={1} stage={2} request={3} automatic={4} reason={5}",
            attempt.FrameNumber,
            recoverableFailure.FrameId,
            recoverableFailure.Stage,
            requestedSequence,
            automaticProbe,
            Volatile.Read(ref _presentNowRecoveryRequestReason));
        return true;
    }

    private bool TryEnterPresentNowRecoveryProbeAttempt(
        ref VulkanFrameAttempt attempt)
    {
        if (Volatile.Read(ref _presentNowRecoveryProbeActive) == 0)
            return true;

        int attemptCount = ++_presentNowRecoveryProbeAttempts;
        if (attemptCount <= PresentNowRecoveryProbeMaximumAttempts)
            return true;

        VulkanPresentNowReadinessException? source =
            _presentNowRecoverySourceFailure;
        VulkanPresentNowReadinessException failure = new(
            attempt.FrameNumber,
            source?.Stage ?? EVulkanPresentNowReadinessStage.PipelineCompilation,
            "state-change-recovery-probe",
            source?.DependencyChain ?? "DesktopScene -> PresentNow state-change recovery",
            TimeSpan.Zero,
            TimeSpan.Zero,
            $"PresentNow recovery exhausted its bounded budget of " +
            $"{PresentNowRecoveryProbeMaximumAttempts} frame attempts. " +
            $"Original failure: {source?.Message ?? "<unavailable>"}",
            source,
            EVulkanPresentNowFailureDisposition.RecoverAfterStateChange);
        DeferPresentNowUntilStateChange(ref attempt, failure);
        return false;
    }

    private void CompletePresentNowRecoveryProbe(ref VulkanFrameAttempt attempt)
    {
        Volatile.Write(
            ref _presentNowConsumedRecoveryRequestSequence,
            Volatile.Read(ref _presentNowRecoveryRequestSequence));
        if (Interlocked.Exchange(ref _presentNowRecoveryProbeActive, 0) == 0)
            return;

        VulkanPresentNowReadinessException? source =
            _presentNowRecoverySourceFailure;
        int recoveryAttempts = _presentNowRecoveryProbeAttempts;
        _presentNowRecoveryProbeAttempts = 0;
        _presentNowRecoverySourceFailure = null;
        _presentNowRecoverableFailure = null;
        Volatile.Write(ref _presentNowAutomaticRecoveryPending, 0);
        Debug.Vulkan(
            "[Vulkan][PresentNow][RendererRecovered] frame={0} sourceFrame={1} stage={2} attempts={3}",
            attempt.FrameNumber,
            source?.FrameId ?? 0UL,
            source?.Stage.ToString() ?? "<unknown>",
            recoveryAttempts);
    }

    private static bool IsSuccessfulPresentNowRecoveryFrame(
        ref VulkanFrameAttempt attempt,
        Result result,
        bool presentAccepted)
        => presentAccepted &&
           result is Result.Success or Result.SuboptimalKhr &&
           attempt.WorkClass == ERenderOutputWorkClass.PresentNow &&
           attempt.PresentNowReadinessCompleted &&
           attempt.ScenePrimaryRecordedThisFrame &&
           attempt.Submitted &&
           attempt.GraphicsSignalValue != 0UL &&
           attempt.PresentDispatched &&
           attempt.Presented &&
           attempt.RejectedFailure is null &&
           attempt.DeferredFailure is null;

    private void PublishPresentNowFailureDiagnostic(
        ref VulkanFrameAttempt attempt,
        VulkanPresentNowReadinessException failure)
    {
        _telemetry.PublishPresentNowFailureDiagnostic(
            new VulkanPresentNowFailureDiagnostic(
                Interlocked.Increment(ref _presentNowFailureDiagnosticSequence),
                attempt.FrameNumber,
                attempt.FrameSlot,
                attempt.AcceptedSceneEpoch,
                attempt.OutputGeneration,
                failure.Stage.ToString(),
                failure.ActiveTicket,
                failure.DependencyChain,
                failure.Disposition.ToString(),
                failure.Elapsed.TotalMilliseconds,
                failure.SinceLastProgress.TotalMilliseconds,
                attempt.PresentNowMeshRequestCount,
                failure.GetType().FullName ?? failure.GetType().Name,
                failure.Message));
    }

    private void PublishPresentNowRetryDiagnostic(
        ref VulkanFrameAttempt attempt,
        in VulkanPresentNowReadinessRetry retry)
    {
        _telemetry.PublishPresentNowFailureDiagnostic(
            new VulkanPresentNowFailureDiagnostic(
                Interlocked.Increment(ref _presentNowFailureDiagnosticSequence),
                attempt.FrameNumber,
                attempt.FrameSlot,
                attempt.AcceptedSceneEpoch,
                attempt.OutputGeneration,
                retry.Stage.ToString(),
                retry.ActiveTicket,
                retry.DependencyChain,
                EVulkanPresentNowFailureDisposition.RetryFrame.ToString(),
                retry.Elapsed.TotalMilliseconds,
                retry.SinceLastProgress.TotalMilliseconds,
                attempt.PresentNowMeshRequestCount,
                nameof(VulkanPresentNowReadinessRetry),
                retry.Detail));
    }

    private VulkanPresentNowTerminalTransitionRecord
        CapturePresentNowTerminalTransition(
            ref VulkanFrameAttempt attempt,
            VulkanPresentNowReadinessException failure)
    {
        VulkanAcceptedFramePlan? acceptedPlan = attempt.AcceptedFramePlan;
        RenderForegroundWorkSnapshot foreground =
            RenderForegroundWorkCoordinator.CaptureSnapshot();
        return new VulkanPresentNowTerminalTransitionRecord(
            Interlocked.Increment(ref _presentNowTerminalTransitionSequence),
            Stopwatch.GetTimestamp(),
            attempt.FrameNumber,
            attempt.FrameSlot,
            attempt.AcceptedSceneEpoch,
            attempt.OutputGeneration,
            failure.Stage,
            failure.ActiveTicket,
            failure.DependencyChain,
            failure.Elapsed,
            failure.SinceLastProgress,
            failure.Disposition,
            attempt.PresentNowMeshRequestCount,
            acceptedPlan?.RequiredTextureCount ?? 0,
            acceptedPlan?.RequiredTextureUploads.Count ?? 0,
            attempt.AcquireOwnership != EVulkanDesktopAcquireOwnership.None,
            attempt.Submitted,
            attempt.PresentDispatched,
            foreground.ForegroundEpoch,
            foreground.BackgroundYieldCount,
            foreground.BackgroundResumeCount,
            failure.GetType().FullName ?? failure.GetType().Name,
            failure.Message);
    }

    private static void PublishPresentNowTerminalTransitionDiagnostics(
        in VulkanPresentNowTerminalTransitionRecord transition)
    {
        Debug.VulkanError(
            "[Vulkan][PresentNow][RendererPaused][TerminalTransition] id={0} frame={1} slot={2} " +
            "sceneEpoch={3} outputGeneration={4} stage={5} ticket='{6}' " +
            "dependency='{7}' disposition={8} elapsedMs={9:F1} " +
            "lastProgressMs={10:F1} meshRequests={11} requiredTextures={12} " +
            "requiredUploads={13} acquired={14} submitted={15} " +
            "presentDispatched={16} foregroundEpoch={17} backgroundYields={18} " +
            "backgroundResumes={19} failureType={20} detail={21}",
            transition.TransitionId,
            transition.FrameId,
            transition.FrameSlot,
            transition.AcceptedSceneEpoch,
            transition.OutputGeneration,
            transition.ReadinessStage,
            transition.ActiveTicket,
            transition.DependencyChain,
            transition.Disposition,
            transition.Elapsed.TotalMilliseconds,
            transition.SinceLastProgress.TotalMilliseconds,
            transition.MeshRequestCount,
            transition.RequiredTextureCount,
            transition.RequiredUploadCount,
            transition.ImageAcquired,
            transition.Submitted,
            transition.PresentDispatched,
            transition.ForegroundEpoch,
            transition.BackgroundYieldCount,
            transition.BackgroundResumeCount,
            transition.FailureType,
            transition.Detail);
        Debug.Vulkan(
            "[Vulkan][PresentNow][ReproductionRecord] transition={0} " +
            "backend=Vulkan policy=BlockForExact workClass=PresentNow " +
            "watchdogMs={1:F1} meshQueueCapacity={2} acceptedMainCapacity={3} " +
            "acceptedShadowCapacity={4} acceptedUploadCapacity={5} " +
            "frame={6} sceneEpoch={7} outputGeneration={8} stage={9} " +
            "ticket='{10}' dependency='{11}'",
            transition.TransitionId,
            VulkanPresentNowReadinessWatchdog.StallTimeoutTicks * 1000.0 /
            Stopwatch.Frequency,
            VulkanMeshOperationRequestQueue.Capacity,
            VulkanAcceptedFramePlan.MainSceneCapacity,
            VulkanAcceptedFramePlan.ShadowCapacity,
            VulkanAcceptedFramePlan.UploadCapacity,
            transition.FrameId,
            transition.AcceptedSceneEpoch,
            transition.OutputGeneration,
            transition.ReadinessStage,
            transition.ActiveTicket,
            transition.DependencyChain);
    }

    private static void ResetIncompleteAcceptedPresentNowPlan(
        ref VulkanFrameAttempt attempt)
    {
        VulkanAcceptedFramePlan? acceptedPlan = attempt.AcceptedFramePlan;
        if (acceptedPlan is null)
            return;

        acceptedPlan.Reset();
        attempt.AcceptedFramePlan = null;
    }
}
