using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Executes the queued production render graph against an acquired explicit
    /// target. The callback performs ordinary viewport/pipeline work; this method
    /// owns acquisition, primary recording, queue submission, and completion.
    /// </summary>
    internal unsafe VulkanExplicitProductionSubmissionReceipt ExecuteExplicitProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame)
    {
        ArgumentNullException.ThrowIfNull(buildFrame);
        if (!TryEnterExplicitFrameExecution())
            throw new ObjectDisposedException(nameof(VulkanFrameLoop));

        try
        {
            InvalidateExplicitProductionReadbackAuthority();
            return ExecuteExplicitProductionFrameCore(buildFrame);
        }
        finally
        {
            ExitExplicitFrameExecution();
        }
    }

    internal unsafe VulkanExplicitProductionSubmissionReceipt ExecuteExplicitProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame, VulkanExplicitProductionBufferStressProbeRequest probe)
    {
        ArgumentNullException.ThrowIfNull(buildFrame);
        ArgumentNullException.ThrowIfNull(probe);
        if (!TryEnterExplicitFrameExecution())
            throw new ObjectDisposedException(nameof(VulkanFrameLoop));
        try
        {
            InvalidateExplicitProductionReadbackAuthority();
            return ExecuteExplicitProductionFrameCore(buildFrame, probe);
        }
        finally
        {
            ExitExplicitFrameExecution();
        }
    }

    private unsafe VulkanExplicitProductionSubmissionReceipt ExecuteExplicitProductionFrameCore(
        Action<RenderFrameOutputDescription> buildFrame)
        => ExecuteExplicitProductionFrameCore(buildFrame, null);

    private unsafe VulkanExplicitProductionSubmissionReceipt ExecuteExplicitProductionFrameCore(
        Action<RenderFrameOutputDescription> buildFrame, VulkanExplicitProductionBufferStressProbeRequest? probe)
    {
        AbstractRenderer renderer = AbstractRenderer.Current
            ?? throw new InvalidOperationException(
                "A production explicit-target frame requires the Vulkan renderer to be current on the render thread.");
        IVulkanExplicitFrameTargetDriver target = RequireExplicitFrameTarget();
        VulkanFrameTargetLease lease = default;
        bool acquired = false;
        bool submitted = false;
        VulkanMappedFrameArena? mappedFrameArena = null;
        VulkanFrameDataArena? frameDataArena = null;
        ulong mappedFrameGeneration = 0;
        ulong frameDataGeneration = 0;
        bool mappedFrameSlotPrepared = false;
        bool frameDataSlotPrepared = false;
        CommandBuffer commandBuffer = default;
        ulong frameNumber = unchecked((ulong)OutputRuntime.NextExplicitTargetFrameNumber());
        VulkanAcceptedFramePlan? acceptedPlan = null;
        ulong engineFrameId = 0UL;

        try
        {
            if (!_deviceContext.StateMachine.IsOperational)
                throw CreateDeviceLostException("ExplicitProductionFrame", Result.ErrorDeviceLost);

            _telemetry.PublishDescriptorTableGeneration(_resourceRuntime.DescriptorTableGeneration);
            _resourceRuntime.BeginRetirementMeteringFrame(unchecked((long)frameNumber));
            _resourceRuntime.Descriptors.Heap.BeginFrame(frameNumber);
            VulkanExplicitFrameTargetPreview preview =
                target.PreviewNextFrameTarget();
            if (!preview.Output.IsValid)
            {
                throw new InvalidOperationException(
                    $"Vulkan target '{FrameExecutionLabel}' did not provide a valid non-acquiring output preview.");
            }

            int planIndex = checked((int)preview.ExpectedFrameSlotIndex);
            if ((uint)planIndex >= (uint)_explicitPrimaryPlans.Length)
                throw new InvalidOperationException(
                    $"Explicit frame slot {planIndex} has no reusable primary plan.");
            PublishFrameSlot(planIndex);
            uint frameSlot = preview.ExpectedFrameSlotIndex;
            ResourceRuntime.ResidentTemplateFrameSlotLifetimes.ReleaseFrameSlot(
                planIndex);
            // Drop cache ownership of superseded payloads before preparing this
            // frame. Recorded/in-flight references remain protected by the ledger;
            // the ordinary retirement drains reclaim only completed generations.
            ResourceRuntime.DrainPendingSupersededDescriptorOwners();
            _commandRuntime.DrainInvalidatedCommandBufferRecordings(Api, ResourceRuntime);
            ResourceRuntime.DrainRetiredDescriptorSets(Api, _deviceContext.Device, planIndex);
            ResourceRuntime.DrainRetiredDescriptorPools(Api, _deviceContext.Device, planIndex);
            ResourceRuntime.DrainRetiredBufferViews(Api, _deviceContext.Device, planIndex);
            ResourceRuntime.DrainRetiredBuffers(Api, _deviceContext.Device, _frameTelemetry, planIndex);
            mappedFrameArena = MappedFrameArena;
            mappedFrameGeneration = mappedFrameArena?.Generation ?? 0UL;
            frameDataArena = FrameDataArena;
            frameDataGeneration = frameDataArena?.Generation ?? 0UL;
            bool mappedFrameSlotWritable = mappedFrameArena is null ||
                mappedFrameArena.TryResetFrameSlot(
                    frameSlot,
                    mappedFrameGeneration,
                    submissionCompletionProven: true);
            bool frameDataSlotWritable = frameDataArena is null ||
                frameDataArena.TryResetFrameSlot(
                    frameSlot,
                    frameDataGeneration,
                    submissionCompletionProven: true);
            if (!mappedFrameSlotWritable || !frameDataSlotWritable)
            {
                throw new InvalidOperationException(
                    $"Explicit target '{FrameExecutionLabel}' previewed frame slot " +
                    $"{frameSlot}, but its previous frame-data ownership was not " +
                    "ready to reopen before logical collection.");
            }

            // The caller observes the same output description it did before this
            // split, but logical collection/readiness now runs before any target
            // image, semaphore, or WSI obligation is held.
            RenderFrameOutputDescription previewOutput = preview.Output;
            using (renderer.PushFrameOutput(in previewOutput))
                buildFrame(previewOutput);
            acceptedPlan = PrepareExplicitProductionLogicalPlan(
                in preview,
                frameNumber);
            engineFrameId = acceptedPlan.SceneEpoch;

            if (probe is
                {
                    Checkpoint: EVulkanExplicitProductionBufferStressCheckpoint.AfterLogicalSeal,
                })
            {
                ExecuteAfterLogicalSealBufferStressProbe(probe, acceptedPlan);
            }
            try
            {
                ValidateExplicitProductionLogicalPlanNativeBufferBindings(acceptedPlan);
            }
            catch (VulkanNativeBufferBindingSupersededException exception)
            {
                MarkAfterLogicalSealBufferStressProbeRejectedBeforeAcquire(exception);
                throw;
            }

            lease = target.AcquireFrameTarget(out commandBuffer);
            acquired = true;
            if (!lease.IsValid)
                throw new InvalidOperationException($"Vulkan target '{FrameExecutionLabel}' returned an invalid frame-target lease.");
            if (!preview.IsCompatible(in lease))
            {
                throw new InvalidOperationException(
                    $"Vulkan target '{FrameExecutionLabel}' changed between its non-acquiring preview " +
                    "and acquire. The acquired target will be settled and the next invocation will " +
                    "rebuild/reseal against a fresh preview without holding an image during readiness.");
            }

            ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                Api,
                _deviceContext,
                _commandRuntime,
                ResourceRuntime,
                IsDeviceLost);

            VulkanPrimaryCommandRecordingResult recording = RecordAcceptedExplicitPrimary(
                in lease,
                commandBuffer,
                _explicitPrimaryPlans[planIndex],
                acceptedPlan);
            if (!recording.Succeeded || recording.CommandBuffer.Handle == 0)
            {
                throw new InvalidOperationException(
                    $"The production render graph could not record against {FrameExecutionLabel}: " +
                    (recording.Reason ?? recording.Disposition.ToString()));
            }
            acceptedPlan.TransferSubmissionMarkerOwnershipToCommandBuffer();
            if (recording.SwapchainLayoutAfterCommandBuffer != lease.Target.RequiredFinalColorLayout)
            {
                throw new InvalidOperationException(
                    $"The production render graph completed in {recording.SwapchainLayoutAfterCommandBuffer}, " +
                    $"but target '{FrameExecutionLabel}' requires {lease.Target.RequiredFinalColorLayout}.");
            }

            SealedSubmissionContract? recordedContract = CaptureExplicitRecordedContract(recording.CommandBuffer);
            if (probe is
                {
                    Checkpoint: EVulkanExplicitProductionBufferStressCheckpoint.AfterNativeRecording,
                })
                ExecuteAfterNativeRecordingBufferStressProbe(probe, recording.CommandBuffer, recordedContract);

            ulong graphicsSignalValue;
            mappedFrameSlotPrepared = mappedFrameArena is null ||
                mappedFrameArena.TryPrepareFrameSlotForSubmission(frameSlot, mappedFrameGeneration);
            frameDataSlotPrepared = frameDataArena is null ||
                frameDataArena.TryPrepareFrameSlotForSubmission(frameSlot, frameDataGeneration);
            if (!mappedFrameSlotPrepared || !frameDataSlotPrepared)
            {
                throw new InvalidOperationException(
                    $"Explicit-target frame-data arenas were not ready for queue submission. " +
                    $"FrameSlot={frameSlot} MappedPrepared={mappedFrameSlotPrepared} " +
                    $"FrameDataPrepared={frameDataSlotPrepared}. " +
                    $"MappedState={mappedFrameArena?.DescribeSubmissionPreparationRejection(frameSlot, mappedFrameGeneration) ?? "not-enabled"}");
            }

            CommandBuffer submittedCommandBuffer = recording.CommandBuffer;
            VulkanSubmissionDiagnosticContext diagnosticContext =
                CreateFrameTargetSubmissionDiagnosticContext(
                    in lease,
                    frameNumber,
                    submittedCommandBuffer,
                    FrameSubmissionKind);
            VulkanSubmissionReceipt receipt;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(FrameSubmissionProfileName))
            using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.Submission))
            {
                receipt = SubmitFrameTargetLease(
                    in lease,
                    &submittedCommandBuffer,
                    commandBufferCount: 1,
                    signalGraphicsTimeline: true,
                    minimumGraphicsTimelineSignalValue: 1UL,
                    out graphicsSignalValue,
                    in diagnosticContext,
                    caller: nameof(ExecuteExplicitProductionFrame));
                // Queue acceptance is irreversible. Publish ownership before even
                // profiling-scope disposal or diagnostics can throw, and never
                // reopen these slots through the unsubmitted cancellation path.
                submitted = receipt.SubmissionAccepted;
                if (submitted)
                {
                    mappedFrameArena?.MarkFrameSlotSubmitted(frameSlot, mappedFrameGeneration);
                    mappedFrameSlotPrepared = false;
                    frameDataArena?.MarkFrameSlotSubmitted(frameSlot, frameDataGeneration);
                    frameDataSlotPrepared = false;
                    _commandRuntime.Synchronization._frameSlotTimelineValues![planIndex] = graphicsSignalValue;
                    target.NotifyFrameSubmitted(in lease);
                }
            }
            if (!receipt.SubmissionAccepted)
            {
                if (receipt.Result == Result.ErrorDeviceLost)
                    throw CreateDeviceLostException("Explicit production QueueSubmit", receipt.Result);
                throw new InvalidOperationException(
                    $"Vulkan {FrameExecutionLabel} production submission failed ({receipt.Result}).");
            }
            if (!receipt.LifetimePinsTransferred || !receipt.PostSubmissionPublicationSucceeded)
                throw new InvalidOperationException("The explicit production submission was accepted but its lifetime publication did not complete.");

            _lastExplicitProductionReceipt = CreateExplicitProductionReceipt(
                frameNumber,
                engineFrameId,
                frameSlot,
                preview.TargetGeneration,
                submittedCommandBuffer,
                graphicsSignalValue);
            // Observe real queue overlap before post-submit housekeeping can hide
            // a short GPU interval. No wait or artificial GPU hold is introduced.
            if (probe is not null)
                MarkExplicitProductionBufferStressSubmitted(in _lastExplicitProductionReceipt);

            ResourceRuntime.Uploads.PublicationState.QueueRecordedForTimeline(
                graphicsSignalValue,
                FrameSubmissionKind);
            target.CompleteFrameTarget(in lease);
            ResourceRuntime.Allocations.Staging.Trim(
                ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                    "The Vulkan backend object context is not initialized."));
            _currentExplicitProductionReadbackReceipt = _lastExplicitProductionReceipt;
            // Copy the exact validated closure into independent receipt authority;
            // auxiliary readback may reset and reuse the recording's seal storage.
            CaptureExplicitProductionReadbackResources(
                ReferenceEquals(recordedContract, CaptureExplicitRecordedContract(submittedCommandBuffer))
                    ? recordedContract : null);
            if (probe is null)
                ObserveExplicitProductionBufferStressSlotReuse(in _lastExplicitProductionReceipt);
            return _lastExplicitProductionReceipt;
        }
        catch (Exception exception)
        {
            if (!submitted)
            {
                acceptedPlan?.SettleUnsubmittedSubmissionMarkers();
                _commandRuntime.FailSubmissionMarkersForCommandBuffer(commandBuffer);
                CancelPendingImportedTextureUploadFrameOps(
                    $"{FrameExecutionLabel} production frame aborted before submission");
                ResourceRuntime.Uploads.CancelRecordedSubmitBatch(
                    IsDeviceLost,
                    $"{FrameExecutionLabel} production frame did not submit");
                if (mappedFrameSlotPrepared)
                    _ = mappedFrameArena?.TryCancelFrameSlotSubmission(lease.Target.FrameSlotIndex, mappedFrameGeneration);
                if (frameDataSlotPrepared)
                    _ = frameDataArena?.TryCancelFrameSlotSubmission(lease.Target.FrameSlotIndex, frameDataGeneration);
            }
            if (acquired)
                target.AbortFrameTarget(in lease, submitted);
            if (!acquired && !submitted && exception is VulkanPresentNowReadinessException
                { Disposition: EVulkanPresentNowFailureDisposition.RetryFrame } pending)
            {
                // Public explicit hosts must receive the same retry disposition
                // as desktop admission, including budget-limited required uploads.
                // Cleanup above settles only this unsubmitted attempt; globally
                // owned upload batches keep their completion authority.
                throw new VulkanExplicitProductionAdmissionPendingException(pending.Stage.ToString(), pending.Message);
            }
            throw;
        }
    }

    /// <summary>
    /// Captures the complete logical explicit-target transaction before acquire.
    /// The accepted arena owns the bounded operation arrays; native image/view
    /// authority is intentionally absent until the later reseal.
    /// </summary>
    private VulkanAcceptedFramePlan PrepareExplicitProductionLogicalPlan(
        in VulkanExplicitFrameTargetPreview preview,
        ulong frameNumber)
    {
        VulkanPresentNowReadinessWatchdog watchdog = new(frameNumber);
        int frameSlot = checked((int)preview.ExpectedFrameSlotIndex);
        VulkanPresentNowTargetCompatibilityKey compatibility = new(
            preview.TargetGeneration,
            VulkanFixedOutputFormatResolver.ResolveColorFormat(
                preview.Output.Properties.ColorFormat),
            VulkanFixedOutputFormatResolver.ResolveDepthFormat(
                preview.Output.Properties.DepthFormat),
            new Extent2D(preview.Output.Properties.Width, preview.Output.Properties.Height),
            UseDynamicRenderingRenderTargets,
            StreamlineFrameGeneration: false);
        VulkanAcceptedFramePlan acceptedPlan = _acceptedFramePlans.Begin(
            frameSlot,
            frameNumber,
            RuntimeEngine.Rendering.State.RenderFrameId,
            in compatibility);

        RenderOutputRequest provisionalOutputContract =
            RenderOutputRequest.CreateDefault(
                EVrOutputViewKind.Secondary,
                EFrameOutputKind.DesktopScene,
                frameNumber);
        RenderOutputTargetDescriptor outputTarget =
            provisionalOutputContract.Target with
            {
                TargetGeneration = preview.TargetGeneration,
                DisplayWidth = preview.Output.Properties.Width,
                DisplayHeight = preview.Output.Properties.Height,
                InternalWidth = preview.Output.Properties.Width,
                InternalHeight = preview.Output.Properties.Height,
                FormatCompatibilityKey =
                    ((ulong)(uint)preview.CompatibilityTarget.ImageFormat << 32) |
                    (uint)preview.CompatibilityTarget.DepthFormat,
                SampleCount = preview.Output.Properties.SampleCount,
            };
        provisionalOutputContract =
            provisionalOutputContract.WithTarget(in outputTarget);

        // Exact shadow production is part of the logical PresentNow closure.
        // It must complete before mesh and frame-operation queues are frozen.
        if (RuntimeEngine.Rendering.State.RenderingWorld?.Lights is { } lights)
        {
            ShadowAtlasReadinessManifest shadowManifest =
                lights.CaptureShadowReadiness(in provisionalOutputContract);
            ShadowAtlasReadinessResult shadowResult =
                lights.CompleteShadowReadiness(in shadowManifest);
            acceptedPlan.ShadowReadiness = shadowManifest;
            acceptedPlan.ShadowReadinessResult = shadowResult;
            if (!shadowResult.IsSatisfied)
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    $"shadow-plan:{shadowManifest.RenderPlanId}",
                    "ExplicitOutput -> required shadow atlas",
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
                    "ExplicitOutput -> visible mesh manifest arena",
                    detail);
            }
        }
        if (requestCount < 0)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.MeshMaterialization,
                "mesh-request-cohort",
                "ExplicitOutput -> visible meshes",
                "The bounded request queue rejected an accepted foreground cohort.");
        }

        // Capture the raw material snapshot first so cold imported textures can
        // be prepared before mesh materialization attempts descriptor binding.
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
            "ExplicitOutput -> visible material snapshot -> texture generation");

        if (!MaterializeQueuedMeshRenderRequests(
                requestCount,
                allowPreparedCohort: true,
                out string meshFailure,
                ref watchdog,
                sourceFrameId: frameNumber))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.MeshMaterialization,
                "visible-mesh-generation",
                "ExplicitOutput -> visible meshes -> program/buffer/descriptor",
                meshFailure);
        }

        FrameOp[] drainedOperations = _framePlanner.Operations.DrainForPrimary(
            out FrameOp[] textureUploadOperations);
        acceptedPlan.ClaimUnsubmittedSubmissionMarkers(drainedOperations);
        bool logicalPlanAccepted = false;
        try
        {
        VulkanSwapchainContextCoalescer.Coalesce(
            drainedOperations,
            _preparedMeshIngress);
        if (!_preparedMeshIngress.TryFinalize(
                ref _preparedMeshIngressResourceUseScratch))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "prepared-mesh-ingress",
                "ExplicitOutput -> prepared mesh dependency lowering",
                "Prepared mesh ingress exceeded its fixed resource-use capacity.");
        }
        if (_preparedMeshIngress.IsCohortHit)
            PublishPreparedMeshIngressCohortHit();
        _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(
            drainedOperations);
        VulkanComputePreparationResult computePreparation =
            _commandRuntime.PrepareComputeProgramsForFramePlan(drainedOperations);
        if (!computePreparation.Succeeded)
        {
            acceptedPlan.SettleUnsubmittedSubmissionMarkers();
            if (computePreparation.Pending)
            {
                throw new VulkanExplicitProductionAdmissionPendingException(
                    "explicit-compute-program",
                    computePreparation.FormatFailure());
            }
            throw new InvalidOperationException(computePreparation.FormatFailure());
        }
        watchdog.RecordProgress();

        ResourcePlannerRuntimeState plannerState =
            PublishedResourcePlannerRuntimeState;
        VulkanFramePlanningSnapshot planningSnapshot =
            _framePlanner.CaptureSnapshot();
        if (planningSnapshot.RenderGraphPlan.Revision !=
            plannerState.ResourcePlannerRevision)
        {
            acceptedPlan.SettleUnsubmittedSubmissionMarkers();
            throw new InvalidOperationException(
                $"Planner publication changed before explicit logical sealing. " +
                $"Planner={plannerState.ResourcePlannerRevision} " +
                $"Graph={planningSnapshot.RenderGraphPlan.Revision}.");
        }
        if (!TryPrepareFrameOperationTargets(
                drainedOperations,
                allowSynchronousResourceUploads: true,
                out string targetFailure) ||
            !TryPreparePreparedMeshIngressTargets(
                _preparedMeshIngress,
                allowSynchronousResourceUploads: true,
                out targetFailure))
        {
            acceptedPlan.SettleUnsubmittedSubmissionMarkers();
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "explicit-frame-operation-target",
                "ExplicitOutput -> framebuffer dependencies",
                targetFailure);
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
                acceptedPlan.SettleUnsubmittedSubmissionMarkers();
                throw new InvalidOperationException(freezeFailure);
            }
        }
        catch (VulkanNativeBufferBindingSupersededException exception)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "native-barrier-bindings",
                "ExplicitOutput -> resource barrier bindings",
                exception.Message,
                disposition: EVulkanPresentNowFailureDisposition.RetryFrame);
        }
        watchdog.RecordProgress();

        acceptedPlan.CaptureOperations(
            drainedOperations,
            dynamicUiOperations: [],
            textureUploadOperations);
        acceptedPlan.PreparedMeshIngress.CopyFrom(_preparedMeshIngress);
        _preparedMeshIngress.Clear();
        FramePlan logicalPlan;
        try
        {
            logicalPlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
            frameSlot,
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
            authoringDynamicOverlayOperationCount: 0,
            authoringTextureUploadOperationCount:
                acceptedPlan.TextureUploadOperationCount,
                emptyPresentNowOutputContract: provisionalOutputContract);
        }
        catch (VulkanNativeBufferBindingSupersededException exception)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "native-barrier-bindings",
                "ExplicitOutput -> resource barrier bindings",
                exception.Message,
                disposition: EVulkanPresentNowFailureDisposition.RetryFrame);
        }
        logicalPlan.PrepareRecordingPlannerGenerations(in plannerState);
        if (!logicalPlan.HasAnyExecutableOutput)
        {
            acceptedPlan.SettleUnsubmittedSubmissionMarkers();
            throw new InvalidOperationException(
                "The explicit-target logical output DAG admitted no executable output.");
        }

        if (!logicalPlan.TryGetExecutableOutputContract(
                in provisionalOutputContract,
                out RenderOutputRequest outputContract))
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "explicit-output-contract",
                "ExplicitOutput -> exact terminal output",
                "The sealed output DAG did not publish the required explicit PresentNow contract.");
        }
        outputTarget = outputContract.Target with
        {
            TargetGeneration = preview.TargetGeneration,
            DisplayWidth = preview.Output.Properties.Width,
            DisplayHeight = preview.Output.Properties.Height,
            InternalWidth = preview.Output.Properties.Width,
            InternalHeight = preview.Output.Properties.Height,
            FormatCompatibilityKey =
                ((ulong)(uint)preview.CompatibilityTarget.ImageFormat << 32) |
                (uint)preview.CompatibilityTarget.DepthFormat,
            SampleCount = preview.Output.Properties.SampleCount,
        };
        outputContract = outputContract.WithTarget(in outputTarget);
        if (outputContract.WorkClass == ERenderOutputWorkClass.PresentNow &&
            outputContract.ReadinessPolicy ==
                ERenderOutputReadinessPolicy.AllowDeferral)
        {
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.FramePlanSeal,
                "explicit-output-contract",
                "ExplicitOutput -> terminal present policy",
                "A PresentNow explicit output cannot admit AllowDeferral.");
        }

        if (!TryPrepareReadOnlyStorage(
                logicalPlan, checked((int)preview.ExpectedFrameSlotIndex),
                out VulkanReadOnlyStoragePreparedAuthority? storageAuthority,
                out bool materialPending,
                out string storageFailure))
        {
            if (materialPending)
                throw new VulkanExplicitProductionAdmissionPendingException("explicit-material-backing", storageFailure);
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                "explicit-immutable-storage", "ExplicitOutput -> immutable buffer descriptors", storageFailure);
        }
        using VulkanResourceRuntime.ReadOnlyStorageRecordingScope storageScope =
            ResourceRuntime.EnterReadOnlyStorageRecordingScope(storageAuthority);

        if (outputContract.WorkClass == ERenderOutputWorkClass.PresentNow)
        {
            VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
                frozenPlanningSnapshot,
                plannerState.ResourcePlannerRevision,
                plannerState.ResourcePlannerSignature,
                plannerState.ResourceAllocationSignature);
            VulkanRenderGraphPlan renderGraphPlan =
                frozenPlanningSnapshot.RenderGraphPlan;
            SwapchainRecordingTarget compatibilityTarget =
                preview.CompatibilityTarget;
            if (!_commandRuntime.TryPreparePresentNowPipelinesForSealedFramePlan(
                    logicalPlan,
                    logicalPlan.GetNativeStaticOperationsForRecording(),
                    logicalPlan.GetNativeDynamicOverlayOperationsForRecording(),
                    in compatibilityTarget,
                    in resourcePlanStamp,
                    in renderGraphPlan,
                    UseDynamicRenderingRenderTargets,
                    preserveSwapchainForOverlay: false,
                    ref watchdog,
                    out bool pipelineRetryable,
                    out string pipelineFailure))
            {
                throw watchdog.CreateFailure(
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    "explicit-graphics-pipeline",
                    "ExplicitOutput -> graphics pipeline manifest",
                    pipelineFailure,
                    disposition: pipelineRetryable
                        ? EVulkanPresentNowFailureDisposition.RetryFrame
                        : EVulkanPresentNowFailureDisposition.RendererTerminal);
            }
        }

        computePreparation =
            _commandRuntime.PrepareComputeFramePlanForRecording(
                preview.ExpectedFrameSlotIndex,
                logicalPlan,
                in plannerState);
        if (!computePreparation.Succeeded)
        {
            if (computePreparation.Pending)
            {
                throw new VulkanExplicitProductionAdmissionPendingException(
                    $"explicit-compute-frame-data:{preview.ExpectedFrameSlotIndex}",
                    computePreparation.FormatFailure());
            }
            throw watchdog.CreateFailure(
                EVulkanPresentNowReadinessStage.PipelineCompilation,
                $"explicit-compute-frame-data:{preview.ExpectedFrameSlotIndex}",
                "ExplicitOutput -> compute descriptors/uniforms",
                computePreparation.FormatFailure());
        }

        acceptedPlan.CaptureRequiredTextureReferences(
            logicalPlan,
            _resourceRuntime.Descriptors);
        _resourceRuntime.Uploads.CaptureRequiredTextureUploadManifest(
            acceptedPlan.RequiredTextureUploads,
            acceptedPlan.RequiredTextures,
            acceptedPlan.RequiredTextureGenerations,
            // The sealed explicit plan already owns descriptor bindings for
            // these published generations. Later streaming requests are next-
            // frame work rather than mutations of this accepted snapshot.
            requireExactDescriptorPublication: false);
        acceptedPlan.DeclareDependencies(logicalPlan);
        acceptedPlan.MarkNonTextureDependenciesReady();
        _ = acceptedPlan.SynchronizeTextureDependencies();
        acceptedPlan.Seal(
            in outputContract,
            logicalPlan,
            in plannerState,
            in frozenPlanningSnapshot);
        watchdog.RecordProgress();
        if (outputContract.WorkClass == ERenderOutputWorkClass.PresentNow)
        {
            CompleteAcceptedPresentNowTextureReadiness(
                acceptedPlan,
                ref watchdog,
                "ExplicitOutput -> material descriptor -> texture generation");
        }
        logicalPlanAccepted = true;
        return acceptedPlan;
        }
        finally
        {
            if (!logicalPlanAccepted)
                acceptedPlan.SettleUnsubmittedSubmissionMarkers();
        }
    }

    /// <summary>
    /// Reseals the previously accepted logical operation snapshot using the
    /// acquired image/view authority. No queue drain, planner snapshot capture,
    /// or cold resource preparation is repeated after acquire.
    /// </summary>
    private VulkanPrimaryCommandRecordingResult RecordAcceptedExplicitPrimary(
        in VulkanFrameTargetLease lease,
        CommandBuffer primaryCommandBuffer,
        VulkanPrimaryCommandPlan primaryPlan,
        VulkanAcceptedFramePlan acceptedPlan)
    {
        if (!UseDynamicRenderingRenderTargets)
        {
            string reason =
                "Lease-backed production output currently requires Vulkan dynamic " +
                "rendering; no legacy framebuffer belongs to an explicit target.";
            return acceptedPlan.OutputContract.WorkClass ==
                    ERenderOutputWorkClass.PresentNow
                ? VulkanPrimaryCommandRecordingResult.Failed(
                    reason,
                    acceptedPlan.OutputContract.ReadinessPolicy,
                    acceptedPlan.OutputContract.WorkClass,
                    acceptedPlan.FrameId)
                : VulkanPrimaryCommandRecordingResult.Deferred(reason);
        }

        SwapchainRecordingTarget recordingTarget = new(
            lease.Target.ColorImage,
            lease.Target.ColorView,
            lease.ColorFormat,
            lease.Target.Extent,
            lease.Target.DepthImage,
            lease.Target.DepthView,
            lease.DepthFormat,
            VulkanFixedOutputFormatResolver.DepthAspect(lease.DepthFormat),
            lease.Target.InitialColorLayout,
            ImageEverPresentedAtRecordStart:
                lease.Target.InitialColorLayout != ImageLayout.Undefined);
        VulkanPreparedResourcePlanStamp resourcePlanStamp = new(
            acceptedPlan.FrozenPlanningSnapshot,
            acceptedPlan.PlannerState.ResourcePlannerRevision,
            acceptedPlan.PlannerState.ResourcePlannerSignature,
            acceptedPlan.PlannerState.ResourceAllocationSignature);
        VulkanCommandRecordingPolicySnapshot policy = new(
            UseDynamicRendering: true,
            AllowSynchronousResourceUploads:
                _resourceRuntime.AllowSynchronousResourceUploads,
            FreshSerialRecording: true,
            IsExternalSwapchainTarget: lease.ImagesExternallyOwned,
            PreserveSwapchainForOverlay: false,
            TransitionSwapchainToPresent: true,
            PreferKhrDynamicRendering: false,
            FinalTargetLayout: lease.Target.RequiredFinalColorLayout,
            ReadinessPolicy: acceptedPlan.OutputContract.ReadinessPolicy,
            WorkClass: acceptedPlan.OutputContract.WorkClass,
            SourceFrameId: acceptedPlan.FrameId,
            AllowArtifactReuse:
                acceptedPlan.OutputContract.WorkClass !=
                    ERenderOutputWorkClass.PresentNow,
            AllowSecondaryDeferral:
                acceptedPlan.OutputContract.WorkClass !=
                    ERenderOutputWorkClass.PresentNow);
        VulkanPreparedPrimaryAuthority authority = new(
            recordingTarget,
            CapturePreparedRenderTargetSnapshot(
                in recordingTarget,
                lease.Target.TargetGeneration),
            default,
            resourcePlanStamp,
            new VulkanCommandClearStateSnapshot(
                _commandRuntime.StateTracker.ClearColor,
                _commandRuntime.StateTracker.ClearDepth,
                _commandRuntime.StateTracker.ClearStencil,
                RenderDiagnosticsFlags.VkForceSwapchainMagenta),
            policy,
            lease.Target.InitialColorLayout);
        FramePlan framePlan = acceptedPlan.LogicalPlan;
        FrameOperationSequence preparedOperations =
            framePlan.GetNativeStaticOperationsForRecording();

        primaryPlan.Build(
            preparedOperations.Stream,
            framePlan.StaticOperationSignature,
            new VulkanPrimaryPlanTerminalContext(
                PreserveSwapchainForOverlay: false,
                TransitionSwapchainToPresent: true,
                ReleaseExternalImageOwnership:
                    lease.CompletionKind ==
                    VulkanFrameTargetCompletionKind.OpenXrRuntimeRelease),
            framePlan: framePlan);
        VulkanPreparedPrimaryCommandInput input = PreparePrimaryCommandInput(
            lease.Target.FrameSlotIndex,
            primaryCommandBuffer,
            dynamicUiSecondaryCommandBuffer: default,
            framePlan,
            primaryPlan,
            in authority,
            callerOwnsSubmissionMarkersUntilRecordingSucceeds: true) with
        {
            FrameDataImageIndexOverride = lease.Target.FrameSlotIndex,
            ReadOnlyStorageAuthority = FrameDataArena is { } arena
                ? ResourceRuntime.ReadOnlyStoragePreparedMap.CreateAuthority(
                    arena, checked((int)lease.Target.FrameSlotIndex))
                : null,
            ExcludeDesktopSwapchainBarriers = true,
        };
        return _commandRuntime.RecordPrimary(in input);
    }

}
