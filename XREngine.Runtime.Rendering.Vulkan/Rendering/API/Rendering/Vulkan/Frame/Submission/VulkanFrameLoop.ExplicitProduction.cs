using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Executes the queued production render graph against an acquired explicit
    /// target. The callback performs ordinary viewport/pipeline work; this method
    /// owns acquisition, primary recording, queue submission, and completion.
    /// </summary>
    internal unsafe void ExecuteExplicitProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame)
    {
        ArgumentNullException.ThrowIfNull(buildFrame);
        if (!TryEnterExplicitFrameExecution())
            throw new ObjectDisposedException(nameof(VulkanFrameLoop));

        try
        {
            ExecuteExplicitProductionFrameCore(buildFrame);
        }
        finally
        {
            ExitExplicitFrameExecution();
        }
    }

    private unsafe void ExecuteExplicitProductionFrameCore(
        Action<RenderFrameOutputDescription> buildFrame)
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

        try
        {
            if (!_deviceContext.StateMachine.IsOperational)
                throw CreateDeviceLostException("ExplicitProductionFrame", Result.ErrorDeviceLost);

            _telemetry.PublishDescriptorTableGeneration(_resourceRuntime.DescriptorTableGeneration);
            _resourceRuntime.Descriptors.Heap.BeginFrame(frameNumber);
            lease = target.AcquireFrameTarget(out commandBuffer);
            acquired = true;
            if (!lease.IsValid)
                throw new InvalidOperationException($"Vulkan target '{FrameExecutionLabel}' returned an invalid frame-target lease.");

            ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                Api,
                _deviceContext,
                _commandRuntime,
                ResourceRuntime,
                IsDeviceLost);

            int planIndex = checked((int)lease.Target.FrameSlotIndex);
            if ((uint)planIndex >= (uint)_explicitPrimaryPlans.Length)
                throw new InvalidOperationException($"Explicit frame slot {planIndex} has no reusable primary plan.");
            PublishFrameSlot(planIndex);
            uint frameSlot = lease.Target.FrameSlotIndex;
            ResourceRuntime.ResidentTemplateFrameSlotLifetimes.ReleaseFrameSlot(planIndex);
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
                    $"Explicit target '{FrameExecutionLabel}' acquired frame slot {frameSlot}, " +
                    "but its previous frame-data ownership was not ready to reopen.");
            }

            RenderFrameOutputDescription output = lease.ToOutputDescription(
                TargetExecutionMode,
                target.OutputProperties);
            using (renderer.PushFrameOutput(in output))
                buildFrame(output);

            VulkanPrimaryCommandRecordingResult recording = RecordPreparedExplicitPrimary(
                in lease,
                commandBuffer,
                _explicitPrimaryPlans[planIndex]);
            if (!recording.Succeeded || recording.CommandBuffer.Handle == 0)
            {
                throw new InvalidOperationException(
                    $"The production render graph could not record against {FrameExecutionLabel}: " +
                    (recording.Reason ?? recording.Disposition.ToString()));
            }
            if (recording.SwapchainLayoutAfterCommandBuffer != lease.Target.RequiredFinalColorLayout)
            {
                throw new InvalidOperationException(
                    $"The production render graph completed in {recording.SwapchainLayoutAfterCommandBuffer}, " +
                    $"but target '{FrameExecutionLabel}' requires {lease.Target.RequiredFinalColorLayout}.");
            }

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
            }
            submitted = receipt.SubmissionAccepted;
            if (!receipt.SubmissionAccepted)
            {
                if (receipt.Result == Result.ErrorDeviceLost)
                    throw CreateDeviceLostException("Explicit production QueueSubmit", receipt.Result);
                throw new InvalidOperationException(
                    $"Vulkan {FrameExecutionLabel} production submission failed ({receipt.Result}).");
            }

            target.NotifyFrameSubmitted(in lease);
            ResourceRuntime.Uploads.PublicationState.QueueRecordedForTimeline(
                graphicsSignalValue,
                FrameSubmissionKind);
            mappedFrameArena?.MarkFrameSlotSubmitted(frameSlot, mappedFrameGeneration);
            frameDataArena?.MarkFrameSlotSubmitted(frameSlot, frameDataGeneration);
            mappedFrameSlotPrepared = false;
            frameDataSlotPrepared = false;
            _commandRuntime.Synchronization._frameSlotTimelineValues![planIndex] = graphicsSignalValue;
            target.CompleteFrameTarget(in lease);
            ResourceRuntime.Allocations.Staging.Trim(
                ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                    "The Vulkan backend object context is not initialized."));
        }
        catch
        {
            if (!submitted)
            {
                _commandRuntime.FailSubmissionMarkersForCommandBuffer(commandBuffer);
                CancelPendingImportedTextureUploadFrameOps(
                    $"{FrameExecutionLabel} production frame aborted before submission");
                ResourceRuntime.Uploads.CancelRecordedSubmitBatch(
                    IsDeviceLost,
                    $"{FrameExecutionLabel} production frame did not submit");
            }
            if (mappedFrameSlotPrepared)
                _ = mappedFrameArena?.TryCancelFrameSlotSubmission(lease.Target.FrameSlotIndex, mappedFrameGeneration);
            if (frameDataSlotPrepared)
                _ = frameDataArena?.TryCancelFrameSlotSubmission(lease.Target.FrameSlotIndex, frameDataGeneration);
            if (acquired)
                target.AbortFrameTarget(in lease, submitted);
            throw;
        }
    }

    private VulkanPrimaryCommandRecordingResult RecordPreparedExplicitPrimary(
        in VulkanFrameTargetLease lease,
        CommandBuffer primaryCommandBuffer,
        VulkanPrimaryCommandPlan primaryPlan)
    {
        if (!UseDynamicRenderingRenderTargets)
        {
            return VulkanPrimaryCommandRecordingResult.Deferred(
                "Lease-backed production output currently requires Vulkan dynamic rendering; no legacy framebuffer belongs to an explicit target.");
        }

        // Direct prepared ingress is desktop-only until explicit targets define
        // their own final-context and UI partition policy.
        _preparedMeshIngress.Clear();
        bool meshMaterializationComplete = DrainQueuedMeshRenderRequests(
            allowPreparedCohort: false,
            out string deferredReason);
        FrameOp[] drainedOperations = _framePlanner.Operations.DrainForPrimary(out FrameOp[] textureUploadOperations);
        VulkanSwapchainContextCoalescer.Coalesce(drainedOperations);
        // Explicit targets have no desktop ImGui overlay, but render-graph UI is
        // ordinary portable work. Record it serially into the target primary so
        // browser and presentationless hosts do not lose engine-owned UI.
        FrameOp[] staticOperations = drainedOperations;
        bool submissionMarkersTransferred = false;
        try
        {
            if (!meshMaterializationComplete)
            {
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
                staticOperations = [];
            }
            _commandRuntime.NormalizePrimaryPlanPassIndicesForPublication(staticOperations);

            VulkanComputePreparationResult computePreparation =
                _commandRuntime.PrepareComputeProgramsForFramePlan(staticOperations);
            if (!computePreparation.Succeeded)
            {
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
                return VulkanPrimaryCommandRecordingResult.Deferred(computePreparation.FormatFailure());
            }

            ResourcePlannerRuntimeState plannerState = PublishedResourcePlannerRuntimeState;
            VulkanFramePlanningSnapshot planningSnapshot = _framePlanner.CaptureSnapshot();
            if (planningSnapshot.RenderGraphPlan.Revision != plannerState.ResourcePlannerRevision)
            {
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
                return VulkanPrimaryCommandRecordingResult.Deferred(
                    $"Planner publication changed before explicit-target recording. " +
                    $"Planner={plannerState.ResourcePlannerRevision} Graph={planningSnapshot.RenderGraphPlan.Revision}.");
            }
            if (!TryFreezeNativeBarrierBindings(
                    in planningSnapshot,
                    in plannerState,
                    _resourceRuntime.AllowSynchronousResourceUploads,
                    out VulkanFramePlanningSnapshot frozenPlanningSnapshot,
                    out string freezeFailure))
            {
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
                return VulkanPrimaryCommandRecordingResult.Deferred(freezeFailure);
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
                frozenPlanningSnapshot,
                plannerState.ResourcePlannerRevision,
                plannerState.ResourcePlannerSignature,
                plannerState.ResourceAllocationSignature);
            VulkanCommandRecordingPolicySnapshot policy = new(
                UseDynamicRendering: true,
                AllowSynchronousResourceUploads: _resourceRuntime.AllowSynchronousResourceUploads,
                FreshSerialRecording: true,
                IsExternalSwapchainTarget: lease.ImagesExternallyOwned,
                PreserveSwapchainForOverlay: false,
                TransitionSwapchainToPresent: true,
                PreferKhrDynamicRendering: false,
                FinalTargetLayout: lease.Target.RequiredFinalColorLayout);
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

            FramePlan framePlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                checked((int)lease.Target.FrameSlotIndex),
                plannerState.ResourcePlannerRevision,
                staticOperationSignature: 0UL,
                dynamicOverlaySignature: 0UL,
                staticOperations,
                dynamicOverlayOperations: [],
                new VulkanFramePlanRenderGraphAuthority(
                    frozenPlanningSnapshot.RenderGraphPlan,
                    plannerState.FrameOpResourcePlannerSwitchingState),
                textureUploadOperations: textureUploadOperations);
            FrameOperationSequence preparedOperations = framePlan.GetNativeStaticOperationsForRecording();
            computePreparation = _commandRuntime.PrepareComputeFrameOpsForRecording(
                lease.Target.FrameSlotIndex,
                preparedOperations);
            if (!computePreparation.Succeeded)
            {
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
                return VulkanPrimaryCommandRecordingResult.Deferred(computePreparation.FormatFailure());
            }

            primaryPlan.Build(
                preparedOperations.Stream,
                framePlan.StaticOperationSignature,
                new VulkanPrimaryPlanTerminalContext(
                    PreserveSwapchainForOverlay: false,
                    TransitionSwapchainToPresent: true,
                    ReleaseExternalImageOwnership:
                        lease.CompletionKind == VulkanFrameTargetCompletionKind.OpenXrRuntimeRelease),
                framePlan: framePlan);
            VulkanPreparedPrimaryCommandInput input = PreparePrimaryCommandInput(
                lease.Target.FrameSlotIndex,
                primaryCommandBuffer,
                dynamicUiSecondaryCommandBuffer: default,
                framePlan,
                primaryPlan,
                in authority) with
            {
                FrameDataImageIndexOverride = lease.Target.FrameSlotIndex,
                ExcludeDesktopSwapchainBarriers = true,
            };
            VulkanPrimaryCommandRecordingResult result = _commandRuntime.RecordPrimary(in input);
            if (!meshMaterializationComplete && result.Succeeded)
            {
                _commandRuntime.FailSubmissionMarkersForCommandBuffer(
                    input.PrimaryCommandBuffer);
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
                return result with
                {
                    Disposition = EVulkanPrimaryCommandRecordingDisposition.Deferred,
                    CommandBuffer = default,
                    Reason = deferredReason,
                };
            }

            if (!result.Succeeded)
            {
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
            }

            submissionMarkersTransferred =
                result.Succeeded && result.CommandBuffer.Handle != 0;
            return result;
        }
        finally
        {
            if (!submissionMarkersTransferred)
            {
                _commandRuntime.FailSubmissionMarkersForCommandBuffer(
                    primaryCommandBuffer);
                VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                    staticOperations);
            }
        }
    }
}
