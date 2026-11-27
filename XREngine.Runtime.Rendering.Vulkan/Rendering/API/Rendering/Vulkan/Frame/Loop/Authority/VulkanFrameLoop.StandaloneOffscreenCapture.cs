using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.OutputDomains;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    // Hold 4 stereo VR frames (2 eyes per frame) of standalone mirror captures in flight so
    // back-to-back eye mirror submissions never stall waiting on frame-data slot reuse.
    private const int StandaloneOffscreenFrameDataSlotCount = 8;

    private int _standaloneOffscreenSlotCursor;
    private Func<bool>? _standaloneOffscreenEmitCallback;
    private XRFrameBuffer? _standaloneOffscreenTargetFrameBuffer;
    private bool _standaloneOffscreenEmitSucceeded;
    private XRGpuFence? _standaloneOffscreenCapturedFence;
    private Action? _standaloneOffscreenEmitAction;

    /// <summary>
    /// Captures, records, and submits a standalone offscreen framebuffer render directly on the
    /// graphics queue without waiting for a desktop swapchain present. Used for in-world VR
    /// mirrors so held desktop frames never throttle mirror updates.
    /// </summary>
    internal bool TryExecuteStandaloneOffscreenCapture(
        XRFrameBuffer targetFrameBuffer,
        uint width,
        uint height,
        Func<bool> emitOperations,
        out XRGpuFence? completionFence)
    {
        completionFence = null;
        if (targetFrameBuffer is null || emitOperations is null || width == 0u || height == 0u ||
            IsDeviceLost || Api is null || !_deviceContext.IsOperational)
            return false;

        using RenderOutputDomainContext.Scope outputDomainScope = RenderOutputDomainContext.Enter(ERenderOutputDomain.Xr);
        _commandRuntime.OpenXrSubmissionTracker.PollCompletions();
        if (!_commandRuntime.OpenXrSubmissionTracker.TryReserveSubmission(
                out OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? admissionTicket))
            return false;

        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        Extent2D extent = new(width, height);
        bool hasRecorded = false;
        bool submitted = false;
        bool commandBufferCompleted = false;
        bool trackerOwnsSubmission = false;
        OpenXrRecordedEyeCommandBuffer recorded = default;
        XRGpuFence? capturedFence = null;

        try
        {
            hasRecorded = TryRecordStandaloneOffscreenCommandBuffer(
                targetFrameBuffer,
                extent,
                emitOperations,
                out recorded,
                out capturedFence);
            if (!hasRecorded || capturedFence is null)
                return false;

            ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
            OpenXrSubmissionMetadata metadata = new(
                renderFrameId,
                PredictedDisplayTime: 0L);
            VulkanOpenXrSubmissionResult submission = SubmitTrackedOpenXrMirrorSubmission(
                admissionTicket!.Value,
                ref trackerOwnsSubmission,
                in metadata,
                in recorded,
                hasFirst: true,
                secondRecorded: default,
                hasSecond: false,
                temporaryCommandBuffer: default,
                _commandRuntime.CreateOpenXrSubmissionDiagnosticContext(
                    AcceptedAttemptCount,
                    "StandaloneOffscreenCapture",
                    "StandaloneOffscreenSubmit",
                    "StandaloneOffscreen",
                    recorded.OpenXrImageIndex,
                    recorded.FrameDataSlotIndex,
                    extent,
                    recorded.FrameOpsSignature,
                    recorded.PlannerRevision,
                    recorded.FrameOpContextId,
                    recorded.ResourceGeneration,
                    recorded.DescriptorGeneration));
            submitted = submission.Succeeded;
            commandBufferCompleted = submission.CommandBuffersCompleted;
            if (!trackerOwnsSubmission && !commandBufferCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit,
                    "Standalone offscreen capture command buffer did not complete");
            }

            if (!submitted)
                return false;

            completionFence = capturedFence;
            capturedFence = null;
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.StandaloneOffscreenCaptureFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Standalone offscreen capture failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            try
            {
                if (capturedFence is not null)
                {
                    if (capturedFence is VulkanTimelineGpuFence timelineFence &&
                        capturedFence.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
                    {
                        timelineFence.Fail(
                            EGpuFenceFailureSite.NativeSubmitRejected,
                            EGpuFenceNativeSubmission.NotCalled);
                    }
                    capturedFence.Dispose();
                }

                if (!trackerOwnsSubmission && !submitted && !commandBufferCompleted && !IsDeviceLost)
                {
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(
                        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit,
                        "Standalone offscreen capture submit failed");
                }

                if (!trackerOwnsSubmission && hasRecorded)
                    FreeOpenXrRecordedEyeCommandBuffer(recorded);

                if (!trackerOwnsSubmission)
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
            }
            finally
            {
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    private bool TryRecordStandaloneOffscreenCommandBuffer(
        XRFrameBuffer targetFrameBuffer,
        Extent2D extent,
        Func<bool> emitOperations,
        out OpenXrRecordedEyeCommandBuffer recorded,
        out XRGpuFence? capturedFence)
    {
        recorded = default;
        capturedFence = null;

        int desktopSwapchainImageCount = OutputRuntime.Desktop.Images?.Length ?? 0;
        int totalFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(desktopSwapchainImageCount);
        int startSlotIndex = _standaloneOffscreenSlotCursor % StandaloneOffscreenFrameDataSlotCount;
        int selectedSlotIndex = startSlotIndex;
        uint recordImageIndex = ResolveStandaloneOffscreenFrameDataSlotIndex(desktopSwapchainImageCount, selectedSlotIndex);

        bool drainedFrameOps = false;
        bool ownsFrameDataSlot = false;
        bool recordingPublished = false;
        FrameOp[]? capturedOps = null;
        IDisposable? planAndScheduleProfileScope = null;

        EnsureOpenXrFrameDataSlotCapacity(totalFrameDataSlotCount);
        _commandRuntime.EnsureOpenXrDescriptorFrameSlotFloor(totalFrameDataSlotCount);

        bool preparedFrameDataSlot = false;
        bool frameDataSlotCompletionProven = false;
        for (int attempt = 0; attempt < StandaloneOffscreenFrameDataSlotCount; ++attempt)
        {
            int candidateSlot = (startSlotIndex + attempt) % StandaloneOffscreenFrameDataSlotCount;
            uint candidateImageIndex = ResolveStandaloneOffscreenFrameDataSlotIndex(desktopSwapchainImageCount, candidateSlot);
            if (!TryPrepareOpenXrFrameDataSlot(
                    candidateImageIndex,
                    "standalone offscreen capture",
                    out frameDataSlotCompletionProven))
                continue;

            selectedSlotIndex = candidateSlot;
            recordImageIndex = candidateImageIndex;
            preparedFrameDataSlot = true;
            break;
        }

        if (!preparedFrameDataSlot)
            return false;

        _standaloneOffscreenSlotCursor = (selectedSlotIndex + 1) % StandaloneOffscreenFrameDataSlotCount;

        // All standalone offscreen captures execute sequentially in FIFO order on the primary
        // graphics queue and write their final output into caller-owned framebuffers. Sharing one
        // resource-planner key across the rotating frame-data slots avoids duplicating the full
        // internal render-target pool per frame slot (8x VRAM bloat).
        VulkanOpenXrFrameContext frameContext = new(
            ResourcePlannerStateIndex: 0,
            ViewIndex: 0u,
            ImageIndex: 0u,
            extent,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u, 0UL),
            "StandaloneOffscreenCapture",
            EVulkanFrameOpContextKind.OpenXrMirror);

        using VulkanOpenXrFrameContextScope frameContextScope =
            _commandRuntime.OpenXrRecording.EnterFrameContextScope(
                OutputRuntime.OpenXrBackend,
                in frameContext);

        try
        {
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                Api!, _deviceContext, _commandRuntime, ResourceRuntime, IsDeviceLost);

            ReopenOpenXrFrameDataSlot(recordImageIndex, frameDataSlotCompletionProven);
            ownsFrameDataSlot = true;

            using VulkanOpenXrThreadRenderStateScope renderStateScope =
                _commandRuntime.OpenXrRecording.EnterThreadRenderStateScope(
                    CreateOpenXrThreadRenderStateData(),
                    CreateOpenXrPrewarmRenderStateTracker(extent));
            VulkanOpenXrViewResourcePlannerContextKey plannerKey = new(
                EVulkanOpenXrResourcePlannerPurpose.StandaloneOffscreen,
                ResourcePlannerStateIndex: 0,
                OpenXrViewIndex: 0u,
                OpenXrImageIndex: 0u,
                CommandChainImageKey: 0u,
                FrameDataSlotIndex: 0u,
                FoveationResourceKey: 0UL,
                FoveationAttachmentKind: default,
                FoveationAttachmentOwnedByResourcePlanner: false);
            using (EnterOpenXrResourcePlannerThreadScope(in plannerKey))
            {
                _standaloneOffscreenEmitCallback = emitOperations;
                _standaloneOffscreenTargetFrameBuffer = targetFrameBuffer;
                _standaloneOffscreenEmitSucceeded = false;
                _standaloneOffscreenCapturedFence = null;
                _standaloneOffscreenEmitAction ??= ExecuteStandaloneOffscreenEmit;
                try
                {
                    // CaptureFrameOpsExcludingTextureUploads already wraps the callback in
                    // EmitOpenXrFrameOpsWithCapturedMeshRequests; do not open a second capture
                    // or required producers such as compute skinning will reject nested capture.
                    // Push a deformation cohort so compute skinning inside standalone offscreen
                    // captures tracks output slot rotation cleanly across concurrent views.
                    using (SkinningPrepassDispatcher.PushDeformationCohort())
                    {
                        capturedOps = CaptureFrameOpsExcludingTextureUploads(
                            _standaloneOffscreenEmitAction, out _);
                    }
                }
                finally
                {
                    _standaloneOffscreenEmitCallback = null;
                    _standaloneOffscreenTargetFrameBuffer = null;
                }

                PinAdvancedVisibilityOperations(capturedOps);
                drainedFrameOps = true;
                capturedFence = _standaloneOffscreenCapturedFence;
                _standaloneOffscreenCapturedFence = null;
                if (!_standaloneOffscreenEmitSucceeded || capturedFence is null)
                    return false;

                FrameOp[] ops = VulkanCommandRuntime.FilterDiagnosticSkippedFrameOps(capturedOps);
                if (ops.Length == 0)
                    return false;

                planAndScheduleProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope(
                    "Vulkan.StandaloneOffscreen.PlanAndSchedule");
                FrameOpContext plannerContext = VulkanFramePlanner.SelectPrimaryPlannerContext(ops);
                using PooledExternalResourcePlannerReadbackScope plannerScope =
                    RentPipelineResourcePlannerScope(in plannerContext);
                if (TryDescribeRecentResourceAllocationFailure(out _))
                    return false;

                if (!TryRefreshFrameOpResourceWrappers(
                        ops,
                        plannerContext,
                        "Vulkan standalone offscreen capture resource refresh",
                        AllowSynchronousResourceUploads,
                        out _))
                    return false;

                if (!PrewarmOpenXrFrameOpResources(
                        ops,
                        recordImageIndex,
                        sealFrameManifest: true,
                        capacityOnly: false))
                    return false;

                _commandRuntime.ReconcileResourcePlannerImageLayouts(
                    CaptureResourcePlannerRuntimeState().ResourceAllocator);
                ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
                planAndScheduleProfileScope?.Dispose();
                planAndScheduleProfileScope = null;

                ulong plannerRevision = plannerState.ResourcePlannerRevision;
                FrameOpContext fallbackContext = ops[0].Context;
                VulkanFramePlanningSnapshot planningSnapshot =
                    _framePlanner.CaptureSnapshot() with
                    {
                        RenderGraphPlan = plannerState.RenderGraphPlan,
                    };

                FramePlan framePlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                    checked((int)recordImageIndex),
                    plannerRevision,
                    staticOperationSignature: 0UL,
                    dynamicOverlaySignature: 0UL,
                    ops,
                    Array.Empty<FrameOp>(),
                    new VulkanFramePlanRenderGraphAuthority(
                        planningSnapshot.RenderGraphPlan,
                        plannerState.FrameOpResourcePlannerSwitchingState,
                        _framePlanner,
                        _resourceRuntime.BackendObjectContext),
                    openXrViewIndex: null,
                    openXrImagesAcquired: false,
                    logicalFrameSlot: 0);
                framePlan.PrepareRecordingPlannerGenerations(in plannerState);

                VulkanFrameOpPlannerStateKey recordingPlannerKey =
                    VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(plannerContext);
                VulkanPreparedResourcePlanStamp originalResourceStamp = new(
                    planningSnapshot,
                    plannerState.ResourcePlannerRevision,
                    plannerState.ResourcePlannerSignature,
                    plannerState.ResourceAllocationSignature);
                ResourcePlannerRuntimeGeneration recordingPlannerGeneration =
                    ResolveOpenXrSealedRecordingPlannerGeneration(
                        in plannerState,
                        in originalResourceStamp,
                        plannerRevision,
                        0u,
                        framePlan,
                        in recordingPlannerKey);
                VulkanPreparedResourcePlanStamp recordingResourceStamp = originalResourceStamp with
                {
                    PlanningSnapshot = planningSnapshot with
                    {
                        RenderGraphPlan = recordingPlannerGeneration.State.RenderGraphPlan,
                    },
                };

                ulong frameOpsSignature = framePlan.StaticOperationSignature;
                FrameOperationSequence recordingOperations =
                    framePlan.GetNativeStaticOperationsForRecording();
                VulkanReadOnlyStoragePreparedAuthority? readOnlyStorageAuthority =
                    PrepareOpenXrImmutableStorage(
                        recordingOperations.Stream,
                        recordImageIndex,
                        openXrViewIndex: 0u);
                using VulkanResourceRuntime.ReadOnlyStorageRecordingScope storageScope =
                    ResourceRuntime.EnterReadOnlyStorageRecordingScope(readOnlyStorageAuthority);
                VulkanComputePreparationResult computePreparation =
                    _commandRuntime.PrepareComputeFrameOpsForRecording(
                        recordImageIndex,
                        recordingOperations,
                        framePlan);
                if (!computePreparation.Succeeded)
                    return false;

                CommandChainSchedule? commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                    recordImageIndex,
                    openXrViewIndex: 0u,
                    openXrImageIndex: 0u,
                    default,
                    framePlan.StaticOperations,
                    frameOpsSignature,
                    plannerRevision);
                ulong cacheKey = BuildStandaloneOffscreenPrimaryCommandBufferCacheKey(
                    recordImageIndex,
                    targetFrameBuffer,
                    extent);
                PrimaryCommandArtifactOwner owner = _commandRuntime.GetOrCreateOpenXrPrimaryCommandBufferOwner(
                    cacheKey,
                    recordImageIndex,
                    _commandRuntime.Pools.PrimaryGraphics,
                    "Vulkan standalone offscreen capture");
                owner.PrimaryCommandPlan.Build(
                    recordingOperations.Stream,
                    framePlan.StaticOperationSignature,
                    new VulkanPrimaryPlanTerminalContext(
                        PreserveSwapchainForOverlay: false,
                        TransitionSwapchainToPresent: false,
                        ReleaseExternalImageOwnership: false),
                    framePlan: framePlan);

                // Standalone offscreen mirrors have no top-level desktop swapchain target, so
                // mark the policy as a targetless external recording and bind the exact planner
                // logical view ID required by TryValidatePreparedPrimaryInput.
                ulong logicalViewId = recordingPlannerKey.LogicalViewId;
                RenderOutputRequest backgroundOutputContract = new()
                {
                    ReadinessPolicy = ERenderOutputReadinessPolicy.AllowDeferral,
                    WorkClass = ERenderOutputWorkClass.Background,
                };
                VulkanStateTracker clearState = CreateOpenXrPrewarmRenderStateTracker(extent);
                VulkanPreparedPrimaryCommandInput commandInput = new(
                    recordImageIndex,
                    owner.PrimaryCommandBuffer,
                    default,
                    framePlan,
                    owner.PrimaryCommandPlan,
                    RecordingTarget: default,
                    PresentationSource: default,
                    recordingResourceStamp,
                    new VulkanCommandClearStateSnapshot(
                        clearState.ClearColor,
                        clearState.ClearDepth,
                        clearState.ClearStencil,
                        XREngine.Rendering.RenderDiagnosticsFlags.VkForceSwapchainMagenta),
                    new VulkanCommandRecordingPolicySnapshot(
                        UseDynamicRenderingRenderTargets,
                        AllowSynchronousResourceUploads,
                        FreshSerialRecording: true,
                        IsExternalSwapchainTarget: true,
                        PreserveSwapchainForOverlay: false,
                        TransitionSwapchainToPresent: false,
                        ReadinessPolicy: ERenderOutputReadinessPolicy.AllowDeferral,
                        WorkClass: ERenderOutputWorkClass.Background,
                        SourceFrameId: framePlan.RenderFrameId,
                        AllowArtifactReuse: false,
                        AllowSecondaryDeferral: false,
                        QueueOverlapMode: RuntimeEngine.EffectiveSettings.VulkanQueueOverlapMode),
                    TrackedTargetLayout: ImageLayout.Undefined,
                    FrameDataSlotIndex: recordImageIndex,
                    TimingQuerySlotIndex: recordImageIndex,
                    ReadOnlyStorageAuthority: readOnlyStorageAuthority,
                    OpenXrTargetContext: null,
                    CommandChainSchedule: commandChainSchedule,
                    ExcludeDesktopSwapchainBarriers: true,
                    LogicalViewOperationsOverride: recordingOperations.Stream,
                    LogicalViewId: logicalViewId,
                    RecordingPlannerKey: logicalViewId != 0UL ? recordingPlannerKey : null);

                bool primaryRecorded = _commandRuntime.TryRecordPreparedOpenXrMirror(
                    in commandInput,
                    in frameContext,
                    openXrViewIndex: 0u,
                    openXrImageIndex: 0u,
                    recordImageIndex,
                    logicalViewId,
                    requiredOutputIndex: -1,
                    backgroundOutputContract,
                    frameOpsSignature,
                    plannerRevision,
                    plannerContext.ContextId,
                    fallbackContext.ResourceGeneration,
                    fallbackContext.DescriptorGeneration,
                    out recorded,
                    out VulkanImportedTexturePendingUpload[] uploads);
                if (!primaryRecorded)
                    return false;

                if (uploads.Length != 0)
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.AddRange(uploads);

                recordingPublished = true;
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!drainedFrameOps)
                DrainAndReleaseFrameOpsExcludingTextureUploads();
            Debug.VulkanWarningEvery(
                $"Vulkan.StandaloneOffscreenRecordFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Standalone offscreen recording failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            planAndScheduleProfileScope?.Dispose();
            if (capturedOps is not null)
            {
                if (!recordingPublished)
                    VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(capturedOps);
                VulkanAdvancedVisibilityInputLease.ReleaseOperations(capturedOps);
                UnpinAdvancedVisibilityOperations(capturedOps);
                Array.Clear(capturedOps);
            }

            if (!recordingPublished && capturedFence is not null)
            {
                if (capturedFence is VulkanTimelineGpuFence timelineFence &&
                    capturedFence.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
                {
                    timelineFence.Fail(
                        EGpuFenceFailureSite.PlanUnsubmitted,
                        EGpuFenceNativeSubmission.NotCalled);
                }
                capturedFence.Dispose();
                capturedFence = null;
            }

            if (ownsFrameDataSlot && !recordingPublished && !IsDeviceLost)
            {
                if (recorded.CommandBuffer.Handle != 0)
                {
                    _commandRuntime.MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
                        in recorded,
                        "Standalone offscreen recording was not published to its caller");
                }
                ResourceRuntime.ResidentTemplateFrameSlotLifetimes.ReleaseFrameSlot(
                    checked((int)recordImageIndex));
            }
        }
    }

    private void ExecuteStandaloneOffscreenEmit()
    {
        bool authored = _standaloneOffscreenEmitCallback!();
        _standaloneOffscreenEmitSucceeded = authored;
        if (!authored)
            return;

        if (_standaloneOffscreenTargetFrameBuffer is { } target)
            PublishFrameBufferAttachmentsForSampling(target);
        EnqueueMemoryBarrier(
            EMemoryBarrierMask.Framebuffer |
            EMemoryBarrierMask.TextureFetch |
            EMemoryBarrierMask.TextureUpdate);
        _standaloneOffscreenCapturedFence = InsertOrderedComputeFence();
    }

    private static uint ResolveStandaloneOffscreenFrameDataSlotIndex(
        int desktopSwapchainImageCount,
        int slotIndex)
    {
        int baseSlot = ResolveOpenXrDesktopFrameDataSlotCount(desktopSwapchainImageCount) +
            OpenXrEyeResourcePlannerStateCount * Math.Max(OpenXrEyeFrameDataSlotsPerEye, OpenXrMaxStereoPairCount);
        return (uint)(baseSlot + (slotIndex % StandaloneOffscreenFrameDataSlotCount));
    }

    private static ulong BuildStandaloneOffscreenPrimaryCommandBufferCacheKey(
        uint commandChainImageIndex,
        XRFrameBuffer targetFrameBuffer,
        Extent2D extent)
    {
        HashCode hash = new();
        hash.Add(0x534F4646);
        hash.Add(commandChainImageIndex);
        hash.Add(RuntimeHelpers.GetHashCode(targetFrameBuffer));
        hash.Add(extent.Width);
        hash.Add(extent.Height);
        return unchecked((ulong)hash.ToHashCode());
    }
}
