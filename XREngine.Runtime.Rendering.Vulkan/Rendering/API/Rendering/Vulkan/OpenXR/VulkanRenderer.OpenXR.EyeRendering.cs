using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool TryRenderOpenXrEyeSwapchain(
        Image image,
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        ViewFoveationContext foveation,
        Action emitFrameOps)
    {
        var request = new OpenXrEyeSwapchainRenderRequest(
            image,
            format,
            extent,
            resourcePlannerStateIndex,
            openXrViewIndex,
            openXrImageIndex,
            foveation,
            new OpenXrEyeFrameOpDelegateEmitter(emitFrameOps));

        List<VulkanImportedTexturePendingUpload> eyeUploads = GetOpenXrEyeRecordedTextureUploads(openXrViewIndex);
        eyeUploads.Clear();
        if (!TryRecordOpenXrEyeSwapchainCommandBuffer(request, out OpenXrRecordedEyeCommandBuffer recorded))
            return false;

        bool submitted = false;
        bool commandBufferCompleted = false;
        try
        {
            VulkanSubmissionDiagnosticContext diagnosticContext =
                CreateOpenXrSubmissionDiagnosticContext(
                    "OpenXrEyeSubmit",
                    "OpenXrEye",
                    recorded.OpenXrViewIndex,
                    recorded.OpenXrImageIndex,
                    recorded.FrameDataSlotIndex,
                    request.Extent,
                    recorded.FrameOpsSignature,
                    recorded.PlannerRevision,
                    recorded.FrameOpContextId,
                    recorded.ResourceGeneration,
                    recorded.DescriptorGeneration);
            submitted = SubmitAndWaitOpenXrCommandBuffer(recorded.CommandBuffer, out commandBufferCompleted, diagnosticContext);
            if (submitted)
            {
                int publishCount = eyeUploads.Count;
                CompleteOpenXrGpuProfilerSubmission(in recorded);
                _commandRuntime.PublishOpenXrRecordedTextureUploads(
                    eyeUploads,
                    "OpenXR eye");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye submit completed eye={0} imageIndex={1} frameSlot={2} publishedUploads={3} retiredFlushSlots={4}",
                        recorded.OpenXrViewIndex,
                        recorded.OpenXrImageIndex,
                        recorded.FrameDataSlotIndex,
                        publishCount,
                        MAX_FRAMES_IN_FLIGHT);
                }
            }
            else if (!commandBufferCompleted && !IsDeviceLost)
            {
                int cancelCount = eyeUploads.Count;
                _commandRuntime.CancelOpenXrRecordedTextureUploads(
                    eyeUploads,
                    "OpenXR eye command buffer did not complete");
                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye submit did not complete eye={0} imageIndex={1} frameSlot={2} cancelledUploads={3}",
                        recorded.OpenXrViewIndex,
                        recorded.OpenXrImageIndex,
                        recorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBufferCompleted && !IsDeviceLost)
            {
                int cancelCount = eyeUploads.Count;
                _commandRuntime.CancelOpenXrRecordedTextureUploads(
                    eyeUploads,
                    "OpenXR eye command buffer submit failed");
                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye submit failed eye={0} imageIndex={1} frameSlot={2} cancelledUploads={3}",
                        recorded.OpenXrViewIndex,
                        recorded.OpenXrImageIndex,
                        recorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            FreeOpenXrRecordedEyeCommandBuffer(recorded);
            eyeUploads.Clear();
        }
    }

    internal bool TryRenderOpenXrEyeSwapchains(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
    {
        ClearOpenXrEyeRecordedTextureUploads();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        OpenXrPreparedEyeCommandBufferInput firstPrepared = default;
        OpenXrPreparedEyeCommandBufferInput secondPrepared = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;

        try
        {
            // Planner replacement can retire descriptor references globally. Finish both
            // eyes' resource preparation before either command buffer captures descriptors.
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.PrepareLeftEye"))
            {
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out firstPrepared))
                    return false;
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.PrepareRightEye"))
            {
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out secondPrepared))
                    return false;
            }

            if (!TryCreatePairedOpenXrLogicalPlan(
                    in firstPrepared,
                    in secondPrepared,
                    out FramePlan pairedLogicalPlan))
                return false;
            firstPrepared = firstPrepared with { PairedLogicalPlan = pairedLogicalPlan };
            secondPrepared = secondPrepared with { PairedLogicalPlan = pairedLogicalPlan };

            // Preparing the second eye can grow shared mesh-renderer descriptor/
            // uniform capacity. Re-prewarm both complete op streams only after
            // both reservations are known and before either command buffer is
            // recorded, so no recorded generation can retire between eyes.
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.FinalizeSharedCapacity"))
            {
                PrewarmOpenXrFrameOpResources(firstPrepared.Ops, firstPrepared.TargetContext.FrameDataSlotIndex);
                PrewarmOpenXrFrameOpResources(secondPrepared.Ops, secondPrepared.TargetContext.FrameDataSlotIndex);
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.RecordLeftEye"))
                hasFirst = TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in firstPrepared, out firstRecorded);
            if (!hasFirst)
                return false;

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.RecordRightEye"))
                hasSecond = TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in secondPrepared, out secondRecorded);
            if (!hasSecond)
                return false;

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.SubmitAndWait"))
            {
                submitted = SubmitAndWaitOpenXrCommandBuffers(
                    firstRecorded.CommandBuffer,
                    secondRecorded.CommandBuffer,
                    out commandBuffersCompleted,
                    CreateOpenXrBatchSubmissionDiagnosticContext(
                        "OpenXrEyeBatchSubmit",
                        "OpenXrEyeBatch",
                        in firstRecorded,
                        in secondRecorded,
                        firstEye.Extent));
            }

            if (submitted)
            {
                int publishCount = CountOpenXrEyeRecordedTextureUploads();
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.PublishUploads"))
                    PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit("OpenXR eye batch");
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.Batch.FlushRetired"))
                    DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye batch submit completed leftFrameSlot={0} rightFrameSlot={1} publishedUploads={2} retiredFlushSlots={3}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        publishCount,
                        MAX_FRAMES_IN_FLIGHT);
                }
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                int cancelCount = CountOpenXrEyeRecordedTextureUploads();
                CancelOpenXrEyeRecordedTextureUploads("OpenXR eye batch command buffers did not complete");
                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye batch submit did not complete leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
            {
                int cancelCount = CountOpenXrEyeRecordedTextureUploads();
                CancelOpenXrEyeRecordedTextureUploads("OpenXR eye batch command buffer submit failed");
                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye batch submit failed leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            ClearOpenXrEyeRecordedTextureUploads();
        }
    }

    internal bool TryRenderOpenXrEyeSwapchainsSinglePassStereo(
        in OpenXrEyeSwapchainRenderRequest leftEye,
        in OpenXrEyeSwapchainRenderRequest rightEye)
    {
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.SinglePassStereo.RecordSubmit"))
            return TryRenderOpenXrEyeSwapchains(leftEye, rightEye);
    }

    internal bool TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording(
        in OpenXrEyeSwapchainRenderRequest leftEye,
        in OpenXrEyeSwapchainRenderRequest rightEye)
    {
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.RecordSubmit"))
            return TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(leftEye, rightEye);
    }

    private bool TryRecordOpenXrEyeSwapchainCommandBuffer(
        in OpenXrEyeSwapchainRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(
                request,
                out OpenXrPreparedEyeCommandBufferInput prepared) ||
            !TryCreateSingleOpenXrLogicalPlan(
                in prepared,
                out FramePlan logicalPlan))
        {
            return false;
        }

        prepared = prepared with { PairedLogicalPlan = logicalPlan };
        return TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(
            in prepared,
            out recorded);
    }

    private bool TryPrepareOpenXrEyeSwapchainCommandBuffer(
        in OpenXrEyeSwapchainRenderRequest request,
        out OpenXrPreparedEyeCommandBufferInput prepared)
    {
        prepared = default;
        if (request.Image.Handle == 0 || request.Extent.Width == 0 || request.Extent.Height == 0)
            return false;

        if (!UseDynamicRenderingRenderTargets)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.DynamicRenderingRequired.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan OpenXR eye rendering requires dynamic rendering render targets.");
            return false;
        }

        bool drainedFrameOps = false;

        int desktopSwapchainImageCount = OutputRuntime.Desktop.Images?.Length ?? 0;
        VulkanOpenXrFrameContext frameContext = CreateOpenXrEyeFrameContext(in request);
        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(in frameContext);
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(desktopSwapchainImageCount);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            desktopSwapchainImageCount);
        uint openXrCommandChainImageIndex = BuildOpenXrCommandChainImageIndex(
            request.OpenXrViewIndex,
            request.OpenXrImageIndex,
            request.Image);
        OpenXrEyeRenderTargetContext targetContext = default;

        try
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PrepareFrameSlot"))
            {
                EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
                _commandRuntime.EnsureOpenXrDescriptorFrameSlotFloor(
                    openXrFrameDataSlotCount);
                bool frameDataSlotCompletionProven =
                    WaitForOpenXrFrameDataSlot(
                        recordImageIndex,
                        "eye swapchain render");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                    Api!, _deviceContext, _commandRuntime, ResourceRuntime, IsDeviceLost);

                if (MappedFrameArena is { } arena &&
                    !arena.TryResetFrameSlot(
                        recordImageIndex,
                        arena.Generation,
                        frameDataSlotCompletionProven))
                {
                    throw new InvalidOperationException(
                        $"OpenXR mapped frame-data slot {recordImageIndex} could not be reopened before eye recording.");
                }
            }

            if (ShouldDeferOpenXrEyeRenderingWork(out string resourceWorkReason))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.DeferEyeResourceWork.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                    resourceWorkReason);
                return false;
            }

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PrepareTargets"))
            {
                ImageView openXrImageView = GetOrCreateOpenXrSwapchainImageView(request.Image, request.Format);
                VulkanOpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(request.OpenXrViewIndex, request.Extent);

                targetContext = CreateOpenXrEyeRenderTargetContext(
                    request,
                    openXrImageView,
                    depthTarget,
                    recordImageIndex,
                    openXrCommandChainImageIndex);
                OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.NativeTargetContext =
                    targetContext;
            }

            using VulkanOpenXrThreadRenderStateScope renderStateScope =
                _commandRuntime.OpenXrRecording.EnterThreadRenderStateScope(
                    CreateOpenXrThreadRenderStateData(),
                    CreateOpenXrEyeRenderStateTracker(in targetContext));
            using (EnterOpenXrResourcePlannerThreadScope(VulkanOpenXrViewResourcePlannerContextKey.FromTarget(in targetContext)))
            {
                if (ShouldDeferOpenXrEyeRenderingWork(out string scopedResourceWorkReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.DeferEyeScopedResourceWork.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                        scopedResourceWorkReason);
                    return false;
                }

                FrameOp[] ops;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.EmitFrameOps"))
                {
                    OpenXrEyeFrameOpEmission emission = new(
                        request.OpenXrViewIndex,
                        request.ResourcePlannerStateIndex);
                    ops = CaptureFrameOpsExcludingTextureUploads(
                        request.FrameOpEmitter,
                        in emission,
                        out _);
                }
                drainedFrameOps = true;
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.NoEyeFrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan eye rendering produced no frame operations.");
                    return false;
                }
                ops = NormalizeOpenXrExternalSwapchainFrameOps(ops, request.Extent);
                ValidateOpenXrExternalFrameOpContexts(
                    ops,
                    request.Extent,
                    request.OpenXrViewIndex,
                    "eye swapchain render");

                ulong plannerRevision;
                ulong frameOpsSignature;
                CommandChainSchedule? commandChainSchedule;
                FrameOpContext plannerContext;
                ResourcePlannerRuntimeState plannerState;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule"))
                {
                    if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"OpenXR.Vulkan.EyeFrameOpPlanDeferred.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                            prePlanFailureReason);
                        return false;
                    }

                    plannerContext = PrepareResourcePlannerForFrameOps(ops);
                    plannerState = CaptureResourcePlannerRuntimeState();
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule.Sort"))
                        ops = _frameOperationScheduler.SortFrameOpsCore(ops, plannerState.CompiledRenderGraph);
                    if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"OpenXR.Vulkan.EyeFrameOpPlanFailed.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                            postPlanFailureReason);
                        return false;
                    }

                    if (!TryRefreshFrameOpResourceWrappers(
                        ops,
                        plannerContext,
                        "OpenXR eye prepared frame-op resource refresh",
                        AllowSynchronousResourceUploads,
                        out string refreshFailureReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"OpenXR.Vulkan.EyeFrameOpRefreshDeferred.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                            refreshFailureReason);
                        return false;
                    }
                    if (!PrewarmOpenXrFrameOpResources(
                            ops,
                            targetContext.FrameDataSlotIndex,
                            sealFrameManifest: true))
                    {
                        return false;
                    }
                    plannerRevision = plannerState.ResourcePlannerRevision;
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule.Signature"))
                    {
                        frameOpsSignature = ComputeFrameOpsSignature(ops);
                    }
                    if (RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync)
                    {
                        // Query probes and the visible mesh subset change as prior
                        // results arrive. The current per-draw secondary cache is
                        // neither bounded nor sufficiently reusable for that
                        // external-image workload, so keep the OpenXR primary inline.
                        // CpuQueryAsync itself remains enabled and submits fresh probes.
                        commandChainSchedule = null;
                    }
                    else
                    {
                        commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                            targetContext.CommandChainImageKey,
                            targetContext.OpenXrViewIndex,
                            targetContext.OpenXrImageIndex,
                            targetContext.Image,
                            ops,
                            frameOpsSignature,
                            plannerRevision);
                    }
                }

                VulkanFramePlanningSnapshot planningSnapshot =
                    _framePlanner.CaptureSnapshot() with
                    {
                        RenderGraphPlan = plannerState.RenderGraphPlan,
                    };
                prepared = new OpenXrPreparedEyeCommandBufferInput(
                    frameContext,
                    targetContext,
                    CloneFrameOpsForPreparedOpenXrEye(ops),
                    plannerContext,
                    new VulkanPreparedResourcePlanStamp(
                        planningSnapshot,
                        plannerState.ResourcePlannerRevision,
                        plannerState.ResourcePlannerSignature,
                        plannerState.ResourceAllocationSignature),
                    frameOpsSignature,
                    plannerRevision,
                    commandChainSchedule);

                if (_commandRuntime.IsOpenXrTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] prepared eye={0} swapchainImage={1} ops={2} plannerRevision={3} frameOps=0x{4:X16}",
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        ops.Length,
                        plannerRevision,
                        frameOpsSignature);
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            if (!drainedFrameOps)
                _ = DrainFrameOpsExcludingTextureUploads(out _);
            if (IsOpenXrStrictExtentFailure(ex))
                throw;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye render failed. Target={0}. Error={1}",
                targetContext.IsValid ? DescribeOpenXrEyeRenderTargetContext(in targetContext) : "<not prepared>",
                ex.Message);
            return false;
        }
    }

    private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(
        in OpenXrPreparedEyeCommandBufferInput prepared,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        if (!TryFreezeOpenXrEyeRecordWorkerInput(
                in prepared,
                out OpenXrPreparedEyeRecordWorkerInput frozen))
            return false;

        try
        {
            VulkanOpenXrCommandRecordingService recordingService =
                _commandRuntime.OpenXrRecording;
            recordingService.Configure(
                _commandRuntime,
                ResourceRuntime,
                _deviceContext);
            if (!recordingService.TryRecordPreparedEye(
                    workerIndex: -1,
                    in frozen,
                    out recorded,
                    out VulkanImportedTexturePendingUpload[] uploads))
            {
                return false;
            }

            if (uploads.Length != 0)
                GetOpenXrEyeRecordedTextureUploads(frozen.OpenXrViewIndex)
                    .AddRange(uploads);
            return true;
        }
        catch (Exception ex)
        {
            OpenXrEyeRenderTargetContext failedTarget =
                prepared.TargetContext;
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderPreparedEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan prepared eye record failed. Target={0}. Error={1}",
                DescribeOpenXrEyeRenderTargetContext(in failedTarget),
                ex.Message);
            return false;
        }
    }

    private bool TryFreezeOpenXrEyeRecordWorkerInput(
        in OpenXrPreparedEyeCommandBufferInput prepared,
        out OpenXrPreparedEyeRecordWorkerInput frozen)
    {
        frozen = default;
        OpenXrEyeRenderTargetContext targetContext = prepared.TargetContext;
        FramePlan? framePlan = prepared.PairedLogicalPlan;
        if (!targetContext.IsValid || framePlan is null || !framePlan.IsSealed)
            return false;

        ulong logicalViewId = GetSingleOpenXrLogicalViewId(prepared.Ops);
        if (logicalViewId == 0UL)
            return false;

        FrameOperationSequence nativeOperations =
            framePlan.GetNativeStaticOperationsForLogicalView(
                logicalViewId,
                prepared.Ops);
        PublishOpenXrExternalImageAcquireState(
            targetContext.Image,
            CreateOpenXrRuntimeColorSubresourceRange());
        ImageSubresourceRange colorRange =
            CreateOpenXrRuntimeColorSubresourceRange();
        _commandRuntime.Synchronization.TryGetSubmittedImageLayout(
            targetContext.Image,
            in colorRange,
            out ImageLayout trackedTargetLayout);

        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(
            targetContext.CommandChainImageKey,
            in targetContext);
        PrimaryCommandArtifactOwner owner =
            GetOrCreateOpenXrPrimaryCommandBufferOwner(
                cacheKey,
                targetContext.FrameDataSlotIndex,
                in targetContext);
        owner.PrimaryCommandPlan.Build(
            nativeOperations,
            framePlan.StaticOperationSignature,
            new VulkanPrimaryPlanTerminalContext(
                PreserveSwapchainForOverlay: false,
                TransitionSwapchainToPresent: false,
                ReleaseExternalImageOwnership: true),
            framePlan);

        SwapchainRecordingTarget recordingTarget = new(
            targetContext.Image,
            targetContext.ImageView,
            targetContext.ImageFormat,
            targetContext.Extent,
            targetContext.DepthImage,
            targetContext.DepthView,
            targetContext.DepthFormat,
            targetContext.DepthAspect,
            trackedTargetLayout,
            ImageEverPresentedAtRecordStart: false);
        VulkanStateTracker clearState =
            CreateOpenXrEyeRenderStateTracker(in targetContext);
        VulkanPreparedPrimaryCommandInput commandInput = new(
            OpenXrExternalSwapchainTargetImageIndex,
            owner.PrimaryCommandBuffer,
            default,
            framePlan,
            owner.PrimaryCommandPlan,
            recordingTarget,
            default,
            prepared.ResourcePlanStamp,
            new VulkanCommandClearStateSnapshot(
                clearState.ClearColor,
                clearState.ClearDepth,
                clearState.ClearStencil,
                XREngine.Rendering.RenderDiagnosticsFlags.VkForceSwapchainMagenta),
            new VulkanCommandRecordingPolicySnapshot(
                UseDynamicRenderingRenderTargets,
                AllowSynchronousResourceUploads,
                RuntimeRenderingHostServices.Settings.VulkanCommandRecordingMode ==
                    EVulkanCommandRecordingMode.FreshSerial,
                IsExternalSwapchainTarget: true,
                PreserveSwapchainForOverlay: false,
                TransitionSwapchainToPresent: false),
            trackedTargetLayout,
            FrameDataImageIndexOverride: targetContext.FrameDataSlotIndex,
            OpenXrTargetContext: targetContext,
            CommandChainSchedule: prepared.CommandChainSchedule,
            ExcludeDesktopSwapchainBarriers: true,
            NativeOperationsOverride: prepared.Ops,
            LogicalViewId: logicalViewId);
        frozen = new OpenXrPreparedEyeRecordWorkerInput(
            commandInput,
            prepared.FrameContext,
            targetContext.OpenXrViewIndex,
            targetContext.OpenXrImageIndex,
            targetContext.FrameDataSlotIndex,
            prepared.FrameOpsSignature,
            prepared.PlannerRevision,
            prepared.PlannerContext.ContextId,
            prepared.PlannerContext.ResourceGeneration,
            prepared.PlannerContext.DescriptorGeneration);
        return true;
    }

    internal static int ResolveOpenXrEyeUploadPublicationBufferIndex(uint openXrViewIndex)
        => (int)Math.Min(openXrViewIndex, (uint)(OpenXrEyeResourcePlannerStateCount - 1));

    private List<VulkanImportedTexturePendingUpload> GetOpenXrEyeRecordedTextureUploads(uint openXrViewIndex)
        => OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit[ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex)];

    private void ClearOpenXrEyeRecordedTextureUploads()
    {
        for (int i = 0; i < OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit[i].Clear();
    }

    private int CountOpenXrEyeRecordedTextureUploads()
    {
        int count = 0;
        for (int i = 0; i < OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            count += OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit[i].Count;
        return count;
    }

    private void DestroyOpenXrEyeCommandPools()
        => _commandRuntime.DestroyOpenXrEyeCommandPools();

    private void PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(string uploadSource)
    {
        for (int i = 0; i < OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            _commandRuntime.PublishOpenXrRecordedTextureUploads(
                OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit[i],
                uploadSource);
    }

    private void CancelOpenXrEyeRecordedTextureUploads(string reason)
    {
        for (int i = 0; i < OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            _commandRuntime.CancelOpenXrRecordedTextureUploads(
                OutputRuntime.OpenXrBackend.EyeRecordedTextureUploadsForSubmit[i],
                reason);
    }

    internal OpenXrEyeRenderTargetContext CreateOpenXrEyeRenderTargetContext(
        in OpenXrEyeSwapchainRenderRequest request,
        ImageView imageView,
        in VulkanOpenXrDepthTarget depthTarget,
        uint frameDataSlotIndex,
        uint commandChainImageKey)
    {
        BoundingRectangle externalTargetRegion = new(
            0,
            0,
            (int)Math.Min(request.Extent.Width, (uint)int.MaxValue),
            (int)Math.Min(request.Extent.Height, (uint)int.MaxValue));
        return new OpenXrEyeRenderTargetContext(
            request.OpenXrViewIndex,
            request.OpenXrImageIndex,
            request.Image,
            imageView,
            request.Format,
            request.Extent,
            depthTarget.Image,
            depthTarget.Memory,
            depthTarget.View,
            depthTarget.Format,
            depthTarget.Aspect,
            externalTargetRegion,
            commandChainImageKey,
            frameDataSlotIndex,
            request.ResourcePlannerStateIndex,
            FoveationResourceKey: request.Foveation.BackendResourceKey,
            FoveationAttachmentKind: request.Foveation.Attachment.Kind,
            FoveationAttachmentOwnedByResourcePlanner: request.Foveation.Attachment.OwnedByResourcePlanner);
    }

    private static VulkanStateTracker CreateOpenXrEyeRenderStateTracker(
        in OpenXrEyeRenderTargetContext context)
        => CreateOpenXrRenderStateTracker(context.Extent);

    private static VulkanStateTracker CreateOpenXrPrewarmRenderStateTracker(Extent2D extent)
        => CreateOpenXrRenderStateTracker(extent);

    private static VulkanStateTracker CreateOpenXrRenderStateTracker(Extent2D extent)
    {
        VulkanStateTracker state = new();
        state.SetSwapchainExtent(extent);
        state.SetCurrentTargetExtent(extent);
        return state;
    }

    private static string DescribeOpenXrEyeRenderTargetContext(in OpenXrEyeRenderTargetContext context)
        => $"eye={context.OpenXrViewIndex} imageIndex={context.OpenXrImageIndex} image=0x{context.Image.Handle:X} " +
           $"view=0x{context.ImageView.Handle:X} depth=0x{context.DepthImage.Handle:X}/0x{context.DepthView.Handle:X} " +
           $"format={context.ImageFormat} extent={context.Extent.Width}x{context.Extent.Height} " +
           $"frameSlot={context.FrameDataSlotIndex} planner={context.ResourcePlannerStateIndex} " +
           $"foveationKey=0x{context.FoveationResourceKey:X} foveationAttachment={context.FoveationAttachmentKind} " +
           $"foveationOwned={context.FoveationAttachmentOwnedByResourcePlanner} commandKey={context.CommandChainImageKey}";

    private static ImageSubresourceRange CreateOpenXrRuntimeColorSubresourceRange()
        => new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

    /// <summary>
    /// Publishes one immutable logical DAG for both eyes before either native
    /// image is recorded. Per-eye target handles stay outside this publication.
    /// </summary>
    private bool TryCreatePairedOpenXrLogicalPlan(
        in OpenXrPreparedEyeCommandBufferInput firstEye,
        in OpenXrPreparedEyeCommandBufferInput secondEye,
        out FramePlan plan)
    {
        plan = null!;
        if (firstEye.Ops.Length == 0 || secondEye.Ops.Length == 0 ||
            firstEye.PlannerRevision != secondEye.PlannerRevision)
            return false;

        ulong firstViewId = GetSingleOpenXrLogicalViewId(firstEye.Ops);
        ulong secondViewId = GetSingleOpenXrLogicalViewId(secondEye.Ops);
        if (firstViewId == 0UL || secondViewId == 0UL || firstViewId == secondViewId)
            return false;

        FrameOp[] combined = new FrameOp[firstEye.Ops.Length + secondEye.Ops.Length];
        CopyTargetNeutralLogicalOperations(firstEye.Ops, combined, 0);
        CopyTargetNeutralLogicalOperations(secondEye.Ops, combined, firstEye.Ops.Length);
        ResourcePlannerRuntimeState publishedPlannerState =
            PublishedResourcePlannerRuntimeState;
        try
        {
            plan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                frameSlot: 0,
                firstEye.PlannerRevision,
                ComputeFrameOpsSignature(combined),
                dynamicOverlaySignature: 0UL,
                combined,
                Array.Empty<FrameOp>(),
                new VulkanFramePlanRenderGraphAuthority(
                    firstEye.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan,
                    publishedPlannerState.FrameOpResourcePlannerSwitchingState));
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                "OpenXR.Vulkan.PairedLogicalPlanFailed",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Rejecting paired-eye recording because the shared logical plan could not be sealed: {0}",
                ex.Message);
            plan = null!;
            return false;
        }
    }

    private static ulong GetSingleOpenXrLogicalViewId(FrameOp[] operations)
    {
        if (operations.Length == 0)
            return 0UL;

        ulong logicalViewId = operations[0].Context.LogicalViewId;
        if (logicalViewId == 0UL)
            return 0UL;
        for (int index = 1; index < operations.Length; index++)
            if (operations[index].Context.LogicalViewId != logicalViewId)
                return 0UL;
        return logicalViewId;
    }

    private static void CopyTargetNeutralLogicalOperations(
        FrameOp[] source,
        FrameOp[] destination,
        int destinationIndex)
    {
        for (int index = 0; index < source.Length; index++)
        {
            FrameOp operation = source[index];
            FrameOpContext context = operation.Context with
            {
                OutputTargetIdentity = 0,
                OutputTargetName = null,
                OutputFrameBufferIdentity = 0,
                OutputFrameBufferName = null,
                OutputFrameBuffer = null,
            };
            destination[destinationIndex + index] = operation with { Context = context };
        }
    }

    private PrimaryCommandArtifactOwner GetOrCreateOpenXrPrimaryCommandBufferOwner(
        ulong targetSlotKey,
        uint recordImageIndex,
        in OpenXrEyeRenderTargetContext targetContext)
    {
        return _commandRuntime.GetOrCreateOpenXrPrimaryCommandBufferOwner(
            targetSlotKey,
            recordImageIndex,
            in targetContext);
    }

    private bool TryCreateSingleOpenXrLogicalPlan(
        in OpenXrPreparedEyeCommandBufferInput eye,
        out FramePlan plan)
    {
        plan = null!;
        if (eye.Ops.Length == 0 ||
            GetSingleOpenXrLogicalViewId(eye.Ops) == 0UL)
        {
            return false;
        }

        try
        {
            ResourcePlannerRuntimeState publishedPlannerState =
                PublishedResourcePlannerRuntimeState;
            plan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                frameSlot: 0,
                eye.PlannerRevision,
                eye.FrameOpsSignature,
                dynamicOverlaySignature: 0UL,
                eye.Ops,
                Array.Empty<FrameOp>(),
                new VulkanFramePlanRenderGraphAuthority(
                    eye.ResourcePlanStamp.PlanningSnapshot.RenderGraphPlan,
                    publishedPlannerState.FrameOpResourcePlannerSwitchingState));
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                "OpenXR.Vulkan.SingleLogicalPlanFailed",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Rejecting eye recording because its logical plan could not be sealed: {0}",
                ex.Message);
            plan = null!;
            return false;
        }
    }

    internal static ulong BuildOpenXrPrimaryCommandBufferCacheKey(
        uint commandChainImageIndex,
        in OpenXrEyeRenderTargetContext targetContext)
    {
        HashCode hash = new();
        hash.Add(0x53574150);
        hash.Add(commandChainImageIndex);
        hash.Add(targetContext.Image.Handle);
        hash.Add(targetContext.ImageView.Handle);
        hash.Add((int)targetContext.ImageFormat);
        hash.Add(targetContext.Extent.Width);
        hash.Add(targetContext.Extent.Height);
        hash.Add(targetContext.DepthImage.Handle);
        hash.Add(targetContext.DepthView.Handle);
        hash.Add((int)targetContext.DepthFormat);
        hash.Add((uint)targetContext.DepthAspect);
        hash.Add(targetContext.OpenXrViewIndex);
        hash.Add(targetContext.OpenXrImageIndex);
        hash.Add(targetContext.FrameDataSlotIndex);
        hash.Add(targetContext.ResourcePlannerStateIndex);
        hash.Add(targetContext.FoveationResourceKey);
        hash.Add((int)targetContext.FoveationAttachmentKind);
        hash.Add(targetContext.FoveationAttachmentOwnedByResourcePlanner);
        return unchecked((ulong)hash.ToHashCode());
    }

    private static ulong BuildOpenXrMirrorPrimaryCommandBufferCacheKey(
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request)
    {
        HashCode hash = new();
        hash.Add(0x4D495252);
        hash.Add(commandChainImageIndex);
        hash.Add(RuntimeHelpers.GetHashCode(request.TargetFrameBuffer));
        hash.Add(request.Extent.Width);
        hash.Add(request.Extent.Height);
        hash.Add(request.OpenXrViewIndex);
        return unchecked((ulong)hash.ToHashCode());
    }

    private bool TryComputeOpenXrPrimaryCommandBufferGroupSignature(
        uint commandChainImageIndex,
        CommandChainSchedule? schedule,
        bool requireReusableChains,
        out ulong signature,
        out int groupCount)
    {
        signature = ulong.MaxValue;
        groupCount = -1;
        if (schedule is null)
            return true;

        Dictionary<CommandChainKey, CommandChain> commandChainCache =
            _commandRuntime.GetOpenXrCommandChainCache(commandChainImageIndex);
        if (requireReusableChains && !OpenXrPrimaryCommandChainScheduleIsReusable(schedule, commandChainCache))
            return false;

        signature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(schedule, commandChainCache);
        groupCount = schedule.Groups.Length;
        return true;
    }

    private static bool OpenXrPrimaryCommandChainScheduleIsReusable(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> chains)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int i = 0; i < groups.Length; i++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[i].ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                if (!chains.TryGetValue(keys[keyIndex], out CommandChain? chain) ||
                    chain.SecondaryCommandBuffer.Handle == 0 ||
                    !chain.SecondaryCommandBufferExecutable ||
                    !chain.RecordedArtifact.TryValidateSharedDependency(
                        chain.DependencySignature,
                        out _) ||
                    chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed) ||
                    (chain.State == CommandChainState.FrameDataRefreshed && chain.FrameDataRefreshTouchedDescriptors))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void RecordOpenXrPrimaryReuseMiss(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
            reusedClean: false,
            recorded: false,
            forcedDirty: false,
            frameOpSignatureDirty: false,
            plannerDirty: false,
            profilerDirty: false,
            dirtyReason: reason);
    }

    private string DescribeOpenXrPrimaryReusableChainMiss(
        uint commandChainImageIndex,
        CommandChainSchedule? schedule)
    {
        if (schedule is null)
            return "schedule=null";

        Dictionary<CommandChainKey, CommandChain> commandChainCache =
            _commandRuntime.GetOpenXrCommandChainCache(commandChainImageIndex);
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[groupIndex].ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                if (!commandChainCache.TryGetValue(key, out CommandChain? chain))
                    return $"group={groupIndex} key={keyIndex} missing chain={key}";
                if (chain.SecondaryCommandBuffer.Handle == 0)
                    return $"group={groupIndex} key={keyIndex} no-secondary chain={key} state={chain.State} dirty={chain.DirtyReason}";
                if (!chain.SecondaryCommandBufferExecutable)
                    return $"group={groupIndex} key={keyIndex} secondary-not-executable chain={key} state={chain.State} dirty={chain.DirtyReason}";
                if (chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed))
                    return $"group={groupIndex} key={keyIndex} state={chain.State} dirty={chain.DirtyReason} chain={key}";
                if (chain.State == CommandChainState.FrameDataRefreshed && chain.FrameDataRefreshTouchedDescriptors)
                    return $"group={groupIndex} key={keyIndex} descriptor-refresh chain={key} state={chain.State} dirty={chain.DirtyReason}";
            }
        }

        return "all-reusable";
    }

    private static ulong ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> chains)
    {
        FrameOpSignatureHasher hash = new();
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        hash.Add(groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            RenderPassChainGroup group = groups[i];
            hash.Add(group.PassIndex);
            hash.Add(group.TargetIdentity);
            hash.Add(group.StructuralSignature);
            hash.Add(group.SupportsSecondaryCommandBuffers);
            hash.Add(group.DynamicOverlay);

            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            hash.Add(keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                hash.Add(key.FrameSlot);
                hash.Add(key.PassIndex);
                hash.Add(key.TargetIdentity);
                hash.Add(key.ChainOrdinal);
                hash.Add(key.ViewKey.PipelineIdentity);
                hash.Add(key.ViewKey.ViewportIdentity);
                hash.Add(key.ViewKey.ViewIndex);
                hash.Add((int)key.ViewKey.Kind);
                hash.Add(key.ViewKey.LightIdentity);
                hash.Add(key.ViewKey.CascadeIndex);
                if (chains.TryGetValue(key, out CommandChain? chain))
                {
                    VulkanRecordedCommandArtifactReference artifact =
                        chain.RecordedArtifact.CreateReference();
                    artifact.AddTo(ref hash);
                }
                else
                {
                    default(VulkanRecordedCommandArtifactReference)
                        .AddTo(ref hash);
                }
            }
        }

        return hash.ToHash();
    }

    private void FreeOpenXrRecordedEyeCommandBuffer(OpenXrRecordedEyeCommandBuffer recorded)
    {
        if (recorded.OwnedByOpenXrPrimaryCache)
            return;

        CommandBuffer commandBuffer = recorded.CommandBuffer;
        if (commandBuffer.Handle != 0)
            FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "OpenXR.RecordedEye");
    }

    private void CompleteOpenXrGpuProfilerSubmission(in OpenXrRecordedEyeCommandBuffer recorded)
    {
        if (recorded.CommandBuffer.Handle == 0)
            return;

        int frameSlot = unchecked((int)Math.Min(recorded.FrameDataSlotIndex, int.MaxValue));
        MarkVulkanGpuProfilerSubmitted(frameSlot);
        SampleVulkanGpuProfilerQueries(frameSlot);
    }

}
