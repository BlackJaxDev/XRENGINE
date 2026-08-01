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
            emitFrameOps);

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
                PublishRecordedTextureUploadsAfterCompletedSubmit(eyeUploads, "OpenXR eye");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                if (OpenXrVulkanTraceEnabled)
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
                CancelRecordedTextureUploads(eyeUploads, "OpenXR eye command buffer did not complete");
                if (OpenXrVulkanTraceEnabled)
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
                CancelRecordedTextureUploads(eyeUploads, "OpenXR eye command buffer submit failed");
                if (OpenXrVulkanTraceEnabled)
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
                if (OpenXrVulkanTraceEnabled)
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
                if (OpenXrVulkanTraceEnabled)
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
                if (OpenXrVulkanTraceEnabled)
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
        return TryPrepareOpenXrEyeSwapchainCommandBuffer(request, out OpenXrPreparedEyeCommandBufferInput prepared) &&
               TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded);
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

        int desktopSwapchainImageCount = swapChainImages?.Length ?? 0;
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
                EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
                WaitForOpenXrFrameDataSlot(recordImageIndex, "eye swapchain render");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                DrainCompletedRecordedTextureUploadPublications();
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
            }

            using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
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

                ResetDynamicUniformRingBuffer(recordImageIndex);
                FrameOp[] ops;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.EmitFrameOps"))
                    ops = CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);
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
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule"))
                {
                    using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule.Sort"))
                        ops = _frameOperationScheduler.SortFrameOpsCore(ops, CompiledRenderGraph);
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
                    plannerRevision = ResourcePlannerRevision;
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

                prepared = new OpenXrPreparedEyeCommandBufferInput(
                    request,
                    frameContext,
                    targetContext,
                    CloneFrameOpsForPreparedOpenXrEye(ops),
                    _openXrBackend.EyeFrameDataRefreshRequests[
                        ResolveOpenXrEyeUploadPublicationBufferIndex(
                            targetContext.OpenXrViewIndex)].Publish(
                            _commandBufferRecordingScratch.Value!
                                .PrimaryReusableFrameDataRefreshRequests,
                            _commandBufferRecordingScratch.Value!
                                .PrimaryReusableFrameDataOwnerWorkRequests,
                            _commandBufferRecordingScratch.Value!
                                .PrimaryReusableFrameDataRefreshBatchInfo),
                    plannerContext,
                    frameOpsSignature,
                    plannerRevision,
                    commandChainSchedule);

                if (OpenXrVulkanTraceEnabled)
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
        OpenXrEyeRenderTargetContext targetContext = prepared.TargetContext;
        if (!targetContext.IsValid)
            return false;

        VulkanOpenXrFrameContext frameContext = prepared.FrameContext;
        using IDisposable externalScope =
            EnterOpenXrExternalSwapchainRenderScope(in frameContext);
        using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
            CreateOpenXrEyeRenderStateTracker(in targetContext));
        VulkanOpenXrViewResourcePlannerContextKey plannerContextKey =
            VulkanOpenXrViewResourcePlannerContextKey.FromTarget(in targetContext);

        try
        {
            using (EnterOpenXrResourcePlannerThreadScope(in plannerContextKey))
            {
                CommandBuffer commandBuffer;
                bool reusedPrimary;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.ReuseOrRecordPrimary"))
                {
                    PublishOpenXrExternalImageAcquireState(
                        targetContext.Image,
                        CreateOpenXrRuntimeColorSubresourceRange());
                    ulong imageLayoutStartSignature = ComputeImageLayoutStateSignature();
                    FrameOpContext fallbackContext = prepared.Ops.Length > 0
                        ? prepared.Ops[0].Context
                        : prepared.PlannerContext;
                    ulong frameOpContextFingerprint = ComputeCommandBufferFrameOpContextFingerprint(
                        prepared.Ops,
                        Array.Empty<FrameOp>(),
                        fallbackContext);
                    ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
                        prepared.Ops,
                        Array.Empty<FrameOp>(),
                        fallbackContext);
                    if (!prepared.FrameDataRefreshLease.TryAcquire(
                            out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                                frameDataRefreshRequests,
                            out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                                frameDataOwnerWorkRequests,
                            out VulkanReusableFrameDataRefreshBatchInfo
                                frameDataRefreshBatchInfo))
                    {
                        return false;
                    }

                    try
                    {
                        reusedPrimary = TryReuseOpenXrPrimaryCommandBuffer(
                            targetContext.FrameDataSlotIndex,
                            targetContext.CommandChainImageKey,
                            targetContext,
                            prepared.Request,
                            prepared.Ops,
                            frameDataRefreshRequests,
                            frameDataOwnerWorkRequests,
                            frameDataRefreshBatchInfo,
                            prepared.FrameOpsSignature,
                            frameOpContextFingerprint,
                            frameOpContextId,
                            prepared.PlannerRevision,
                            imageLayoutStartSignature,
                            prepared.CommandChainSchedule,
                            out commandBuffer);
                    }
                    finally
                    {
                        prepared.FrameDataRefreshLease.Release();
                    }

                    if (!reusedPrimary)
                    {
                        commandBuffer = RecordOpenXrPrimaryCommandBuffer(
                            targetContext.FrameDataSlotIndex,
                            targetContext.CommandChainImageKey,
                            targetContext,
                            prepared.Request,
                            prepared.Ops,
                            prepared.FrameOpsSignature,
                            frameOpContextFingerprint,
                            frameOpContextId,
                            prepared.PlannerRevision,
                            imageLayoutStartSignature,
                            prepared.CommandChainSchedule);
                        if (commandBuffer.Handle == 0)
                            return false;
                    }
                }

                List<VulkanImportedTexturePendingUpload> eyeUploads = GetOpenXrEyeRecordedTextureUploads(targetContext.OpenXrViewIndex);
                MoveRecordedTextureUploadsForSubmitTo(eyeUploads);
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye={0} swapchainImage={1} commandBuffer=0x{2:X} cached={3} pendingUploads={4}",
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        commandBuffer.Handle,
                        reusedPrimary,
                        eyeUploads.Count);
                }

                recorded = new OpenXrRecordedEyeCommandBuffer(
                    commandBuffer,
                    prepared.FrameContext,
                    targetContext.OpenXrViewIndex,
                    targetContext.OpenXrImageIndex,
                    targetContext.FrameDataSlotIndex,
                    prepared.FrameOpsSignature,
                    prepared.PlannerRevision,
                    prepared.PlannerContext.ContextId,
                    prepared.PlannerContext.ResourceGeneration,
                    prepared.PlannerContext.DescriptorGeneration,
                    OwnedByOpenXrPrimaryCache: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderPreparedEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan prepared eye record failed. Target={0}. Error={1}",
                DescribeOpenXrEyeRenderTargetContext(in targetContext),
                ex.Message);
            return false;
        }
    }

    internal static int ResolveOpenXrEyeUploadPublicationBufferIndex(uint openXrViewIndex)
        => (int)Math.Min(openXrViewIndex, (uint)(OpenXrEyeResourcePlannerStateCount - 1));

    private List<VulkanImportedTexturePendingUpload> GetOpenXrEyeRecordedTextureUploads(uint openXrViewIndex)
        => _openXrBackend.EyeRecordedTextureUploadsForSubmit[ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex)];

    private void ClearOpenXrEyeRecordedTextureUploads()
    {
        for (int i = 0; i < _openXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            _openXrBackend.EyeRecordedTextureUploadsForSubmit[i].Clear();
    }

    private int CountOpenXrEyeRecordedTextureUploads()
    {
        int count = 0;
        for (int i = 0; i < _openXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            count += _openXrBackend.EyeRecordedTextureUploadsForSubmit[i].Count;
        return count;
    }

    private CommandPool GetOrCreateOpenXrEyeCommandPool(uint openXrViewIndex)
    {
        int poolIndex = ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex);
        lock (_openXrBackend.EyeCommandPoolsLock)
        {
            CommandPool existing = _openXrBackend.EyeCommandPools[poolIndex];
            if (existing.Handle != 0)
                return existing;

            uint graphicsFamily = FamilyQueueIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            CommandPool created = CreateCommandPoolForFamily(graphicsFamily);
            _openXrBackend.EyeCommandPools[poolIndex] = created;
            SetDebugObjectName(
                ObjectType.CommandPool,
                unchecked((ulong)created.Handle),
                $"OpenXR eye primary command pool[{poolIndex}]");
            return created;
        }
    }

    private void DestroyOpenXrEyeCommandPools()
    {
        lock (_openXrBackend.EyeCommandPoolsLock)
        {
            for (int i = 0; i < _openXrBackend.EyeCommandPools.Length; i++)
            {
                CommandPool pool = _openXrBackend.EyeCommandPools[i];
                if (pool.Handle == 0)
                    continue;

                DestroyCommandPoolHostSynchronized(pool);
                _openXrBackend.EyeCommandPools[i] = default;
            }
        }
    }

    private void PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(string uploadSource)
    {
        for (int i = 0; i < _openXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrBackend.EyeRecordedTextureUploadsForSubmit[i], uploadSource);
    }

    private void CancelOpenXrEyeRecordedTextureUploads(string reason)
    {
        for (int i = 0; i < _openXrBackend.EyeRecordedTextureUploadsForSubmit.Length; i++)
            CancelRecordedTextureUploads(_openXrBackend.EyeRecordedTextureUploadsForSubmit[i], reason);
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

    private bool TryReuseOpenXrPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeRenderTargetContext targetContext,
            in OpenXrEyeSwapchainRenderRequest request,
            FrameOp[] ops,
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                frameDataRefreshRequests,
            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                frameDataOwnerWorkRequests,
            in VulkanReusableFrameDataRefreshBatchInfo
                frameDataRefreshBatchInfo,
            ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!OpenXrVulkanPrimaryReuseEnabled)
        {
            if (OpenXrVulkanTraceEnabled)
                RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:disabled");
            return false;
        }

        // CpuQueryAsync makes visibility decisions while mesh frame operations are
        // lowered. Reusing a previously recorded primary would freeze that decision
        // set (and can preserve an empty startup frame after commands are published).
        // Re-recording keeps the stereo POV query lifecycle current; the normal
        // reusable-primary path remains available for static/GPU-owned visibility.
        if (RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync)
        {
            if (OpenXrVulkanTraceEnabled)
                RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:cpu-query-async");
            return false;
        }

        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, targetContext);
        lock (_openXrBackend.PrimaryCommandBufferVariantsLock)
        {
            if (!OpenXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                if (OpenXrVulkanTraceEnabled)
                    RecordOpenXrPrimaryReuseMiss($"openxr-primary-miss:no-variants key=0x{cacheKey:X16}");
                else
                    RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:no-variants");
                return false;
            }

            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
            bool usingCommandChains = commandChainSchedule is not null;
            bool requiresExactFrameOps = true;
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: true,
                    out global::System.UInt64 commandChainPrimaryGroupSignature,
                    out global::System.Int32 commandChainPrimaryGroupCount))
            {
                if (OpenXrVulkanTraceEnabled)
                {
                    RecordOpenXrPrimaryReuseMiss(
                        $"openxr-primary-miss:chains-not-reusable key=0x{cacheKey:X16} {DescribeOpenXrPrimaryReusableChainMiss(commandChainImageIndex, commandChainSchedule)}");
                }
                else
                {
                    RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:chains-not-reusable");
                }
                return false;
            }

            bool swapchainImageEverPresented = IsSwapchainImageEverPresented(OpenXrExternalSwapchainTargetImageIndex);
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                bool imageEntryStateDirty =
                    IsCommandBufferVariantImageLayoutStateDirty(
                        variant,
                        imageLayoutStartSignature,
                        out VulkanImageEntryStateMismatch imageEntryStateMismatch);
                if (imageEntryStateDirty)
                    RecordPrimaryImageEntryStateMismatch(imageEntryStateMismatch);
                if (variant.Dirty ||
                    variant.PrimaryCommandBuffer.Handle == 0 ||
                    (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature) ||
                    !TryValidateCommandBufferVariantContext(
                        recordImageIndex,
                        variant,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        "openxr-eye-primary") ||
                    (!usingCommandChains && variant.PlannerRevision != plannerRevision) ||
                    imageEntryStateDirty ||
                    variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented ||
                    variant.CommandChainScheduleSignature != (commandChainSchedule?.StructuralSignature ?? ulong.MaxValue) ||
                    variant.CommandChainPrimaryGroupSignature != (commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature) ||
                    variant.CommandChainPrimaryGroupCount != (commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount) ||
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    continue;
                }

                _lastReusableFrameDataRefreshFailureReason = null;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordEye.RefreshFrameData"))
                {
                    if (!TryRefreshReusableCommandBufferFrameData(
                            recordImageIndex,
                            frameDataRefreshRequests,
                            frameDataOwnerWorkRequests,
                            frameDataRefreshBatchInfo,
                            variant.PrimaryFrameDataRefreshState,
                            dynamicUi: false))
                        return false;
                }

                variant.GpuProfilerActive = gpuPipelineProfilingActive;
                variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;

                if (HasQueryFrameOps(ops) &&
                    !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops))
                {
                    if (OpenXrVulkanTraceEnabled)
                        RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:query-pool-prepare");
                    return false;
                }

                variant.LastUsedFrameId = VulkanFrameCounter;
                StoreFrameOpSignatureDebugParts(variant, ops);
                RestoreRecordedImageLayoutEndState(variant);
                PrepareVulkanGpuProfilerReusableSubmission(
                    commandBufferImageSlot,
                    variant,
                    gpuPipelineProfilingActive);
                UpdateVulkanGpuProfilerCommandBufferState(
                    recordImageIndex,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                    reusedClean: true,
                    recorded: false,
                    forcedDirty: false,
                    frameOpSignatureDirty: false,
                    plannerDirty: false,
                    profilerDirty: false,
                    dirtyReason: null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersReused: 1);

                EnsureCommandBufferVariantContextBeforeSubmit(
                    recordImageIndex,
                    variant,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    "openxr-eye-primary");
                commandBuffer = variant.PrimaryCommandBuffer;
                PrepareSubmissionMarkersForCommandBufferReuse(commandBuffer, ops);
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] reused primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} commandBuffer=0x{4:X}",
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        commandChainImageIndex,
                        recordImageIndex,
                        commandBuffer.Handle);
                }

                return true;
            }

            string compactMissReason = ClassifyOpenXrPrimaryVariantMismatch(
                variants,
                false,
                requiresExactFrameOps,
                usingCommandChains,
                frameOpsSignature,
                frameOpContextFingerprint,
                plannerRevision,
                imageLayoutStartSignature,
                ContainsQueryFrameOp(ops),
                true,
                swapchainImageEverPresented,
                commandChainSchedule,
                commandChainPrimaryGroupSignature,
                commandChainPrimaryGroupCount,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            if (OpenXrVulkanTraceEnabled)
            {
                RecordOpenXrPrimaryReuseMiss(
                    $"openxr-primary-miss:no-matching-variant key=0x{cacheKey:X16} variants={variants.Count} first={DescribeOpenXrPrimaryVariantMismatch(
                        variants,
                        requiresExactFrameOps,
                        usingCommandChains,
                        frameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        plannerRevision,
                        imageLayoutStartSignature,
                        true,
                        swapchainImageEverPresented,
                        commandChainSchedule,
                        commandChainPrimaryGroupSignature,
                        commandChainPrimaryGroupCount,
                        gpuPipelineProfilingActive,
                        commandBufferImageSlot)}");
            }
            else
            {
                RecordOpenXrPrimaryReuseMiss(compactMissReason);
            }
            return false;
        }
    }

    private CommandBuffer RecordOpenXrPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeRenderTargetContext targetContext,
        in OpenXrEyeSwapchainRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule)
    {
        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, targetContext);
        CommandBufferCacheVariant variant = GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex,
            targetContext);

        bool gpuPipelineProfilingActive =
            IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;
        int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
        ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
        int commandChainPrimaryGroupCount = -1;
        if (commandChainSchedule is not null)
        {
            _ = TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                commandChainImageIndex,
                commandChainSchedule,
                requireReusableChains: false,
                out commandChainPrimaryGroupSignature,
                out commandChainPrimaryGroupCount);
            commandChainPrimaryGroupCount = commandChainSchedule.Groups.Length;
        }

        long recordStart = Stopwatch.GetTimestamp();
        _commandRecorder.EnterRecordingScope();
        int recordedSwapchainWriteCount = 0;
        bool queryFrameOpsRequireRerecord = false;
        ImageLayout swapchainLayoutAfterCommandBuffer;
        try
        {
            BeginRecordedTextureUploadSubmitBatch();
            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] record primary target=({0}) targetSlot={1} ops={2}",
                    DescribeOpenXrEyeRenderTargetContext(in targetContext),
                    OpenXrExternalSwapchainTargetImageIndex,
                    ops.Length);
            }

            if (!TryRecordCommandBuffer(
                imageIndex: OpenXrExternalSwapchainTargetImageIndex,
                variant.PrimaryCommandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer: default,
                ops,
                dynamicUiBatchTextOpCount: 0,
                commandChainSchedule,
                preserveSwapchainForOverlay: false,
                recordedSwapchainWriteCount: out recordedSwapchainWriteCount,
                recordedSwapchainFinalLayout: out swapchainLayoutAfterCommandBuffer,
                recordingDeferredReason: out string recordingDeferredReason,
                queryFrameOpsRequireRerecord: out queryFrameOpsRequireRerecord,
                transitionSwapchainToPresent: false,
                frameDataImageIndexOverride: recordImageIndex,
                openXrTargetContext: targetContext))
            {
                CancelRecordedTextureUploadSubmitBatch(
                    $"OpenXR eye command buffer recording deferred: {recordingDeferredReason}");
                variant.Dirty = true;
                variant.DirtyReason = recordingDeferredReason;
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.EyePrimaryRecordDeferred.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye primary command buffer recording before vkBeginCommandBuffer: {0}",
                    recordingDeferredReason);
                return default;
            }
        }
        catch (Exception ex)
        {
            CancelRecordedTextureUploadSubmitBatch("OpenXR eye command buffer recording failed before upload submit");
            _ = TryAbandonCommandBufferRecording(variant.PrimaryCommandBuffer);
            variant.Dirty = true;
            variant.DirtyReason = ex.Message;
            throw;
        }
        finally
        {
            _commandRecorder.ExitRecordingScope();
        }

        bool wasDirty = variant.Dirty;
        variant.Dirty = false;
        variant.FrameOpsSignature = frameOpsSignature;
        variant.DynamicUiSignature = 0;
        variant.DynamicUiOpCount = 0;
        variant.DynamicUiSecondaryRecorded = false;
        variant.PreserveSwapchainForOverlay = false;
        variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
        variant.RecordedFrameOpContextId = frameOpContextId;
        variant.RecordedSwapchainImageEverPresented = false;
        variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
        variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
        variant.RecordedSwapchainRefreshFromLastPresentSource = false;
        variant.RecordedImageLayoutStartSignature = imageLayoutStartSignature;
        CaptureCommandBufferVariantImageLayoutEndState(variant);
        variant.CommandChainScheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        if (commandChainSchedule is not null)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache =
                GetCommandChainCache(commandChainImageIndex);
            if (!TryValidatePrimaryCommandBufferGroupSharedDependencies(
                    commandChainSchedule,
                    commandChainCache,
                    out CommandRecordingDependencyMismatch sharedDependencyMismatch))
            {
                throw new InvalidOperationException(
                    $"Recorded OpenXR primary command buffer contains a secondary artifact whose " +
                    $"shared dependency identity is not executable or disagrees with its command " +
                    $"chain. Field={sharedDependencyMismatch.Field} " +
                    $"Class={sharedDependencyMismatch.InvalidationClass}.");
            }
        }

        if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                commandChainImageIndex,
                commandChainSchedule,
                requireReusableChains: false,
                out commandChainPrimaryGroupSignature,
                out commandChainPrimaryGroupCount))
        {
            commandChainPrimaryGroupSignature = ulong.MaxValue;
            commandChainPrimaryGroupCount = -1;
        }
        variant.CommandChainPrimaryGroupSignature = commandChainPrimaryGroupSignature;
        variant.CommandChainPrimaryGroupCount = commandChainPrimaryGroupCount;
        variant.PlannerRevision = plannerRevision;
        variant.GpuProfilerActive = gpuPipelineProfilingActive;
        variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;
        variant.LastUsedFrameId = VulkanFrameCounter;
        CaptureVulkanGpuProfilerVariantScopes(commandBufferImageSlot, variant);
        StoreFrameOpSignatureDebugParts(variant, ops);
        if (queryFrameOpsRequireRerecord)
            MarkCommandBufferVariantTransient(variant, "query draw was not recorded");
        UpdateVulkanGpuProfilerCommandBufferState(
            recordImageIndex,
            gpuPipelineProfilingActive,
            commandBufferImageSlot);

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
            reusedClean: false,
            recorded: true,
            forcedDirty: wasDirty,
            frameOpSignatureDirty: false,
            plannerDirty: false,
            profilerDirty: false,
            dirtyReason: wasDirty ? "forced" : null);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersRecorded: 1);

        if (OpenXrVulkanTraceEnabled)
        {
            double recordMs = (Stopwatch.GetTimestamp() - recordStart) * 1000.0 / Stopwatch.Frequency;
            Debug.Vulkan(
                "[OpenXrVulkan] recorded primary target=({0}) recorderSlot={1} commandBuffer=0x{2:X} recordMs={3:F3}",
                DescribeOpenXrEyeRenderTargetContext(in targetContext),
                recordImageIndex,
                variant.PrimaryCommandBuffer.Handle,
                recordMs);
        }

        EnsureCommandBufferVariantContextBeforeSubmit(
            recordImageIndex,
            variant,
            frameOpContextFingerprint,
            frameOpContextId,
            "recorded-openxr-eye-primary");
        return variant.PrimaryCommandBuffer;
    }

    private CommandBufferCacheVariant GetOrCreateOpenXrPrimaryCommandBufferVariant(
        ulong cacheKey,
        CommandChainSchedule? commandChainSchedule,
        uint commandChainImageIndex,
        uint recordImageIndex)
        => GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex,
            commandPool,
            "OpenXR mirror primary command buffer variant");

    private CommandBufferCacheVariant GetOrCreateOpenXrPrimaryCommandBufferVariant(
        ulong cacheKey,
        CommandChainSchedule? commandChainSchedule,
        uint commandChainImageIndex,
        uint recordImageIndex,
        in OpenXrEyeRenderTargetContext targetContext)
    {
        CommandPool eyeCommandPool = GetOrCreateOpenXrEyeCommandPool(targetContext.OpenXrViewIndex);
        return GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex,
            eyeCommandPool,
            $"OpenXR eye primary command buffer variant eye={targetContext.OpenXrViewIndex}");
    }

    private CommandBufferCacheVariant GetOrCreateOpenXrPrimaryCommandBufferVariant(
        ulong cacheKey,
        CommandChainSchedule? commandChainSchedule,
        uint commandChainImageIndex,
        uint recordImageIndex,
        CommandPool ownerPool,
        string allocationLabel)
    {
        lock (_openXrBackend.PrimaryCommandBufferVariantsLock)
        {
            if (!OpenXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                variants = [];
                OpenXrPrimaryCommandBufferVariants[cacheKey] = variants;
            }

            ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
            ulong groupSignature = ulong.MaxValue;
            int groupCount = -1;
            _ = TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                commandChainImageIndex,
                commandChainSchedule,
                requireReusableChains: false,
                out groupSignature,
                out groupCount);

            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.CommandChainScheduleSignature == scheduleSignature &&
                    variant.CommandChainPrimaryGroupSignature == groupSignature &&
                    variant.CommandChainPrimaryGroupCount == groupCount)
                {
                    RegisterCommandBufferImageIndex(variant.PrimaryCommandBuffer, recordImageIndex);
                    return variant;
                }
            }

            CommandBuffer primary = AllocateCommandBuffer(
                CommandBufferLevel.Primary,
                allocationLabel,
                ownerPool);
            RegisterCommandBufferImageIndex(primary, recordImageIndex);
            CommandBufferCacheVariant created = new(
                primary,
                dynamicUiSecondaryCommandBuffer: default,
                ownerPool,
                dynamicUiSecondaryCommandPool: default,
                ownsPrimaryCommandBuffer: true,
                ownsDynamicUiSecondaryCommandBuffer: false);
            variants.Add(created);
            return created;
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
        hash.Add(request.ViewBatchStructuralIdentity);
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

        Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
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

        Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
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

    private string ClassifyOpenXrPrimaryVariantMismatch(
        List<CommandBufferCacheVariant> variants,
        bool mirror,
        bool requiresExactFrameOps,
        bool usingCommandChains,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        bool hasQueryFrameOps,
        bool compareSwapchainImageEverPresented,
        bool swapchainImageEverPresented,
        CommandChainSchedule? commandChainSchedule,
        ulong commandChainPrimaryGroupSignature,
        int commandChainPrimaryGroupCount,
        bool gpuPipelineProfilingActive,
        int commandBufferImageSlot)
    {
        if (variants.Count == 0)
            return mirror ? "openxr-mirror-primary-miss:no-variants" : "openxr-primary-miss:no-variants";

        CommandBufferCacheVariant variant = variants[0];
        if (variant.Dirty)
            return mirror ? "openxr-mirror-primary-miss:dirty" : "openxr-primary-miss:dirty";
        if (variant.PrimaryCommandBuffer.Handle == 0)
            return mirror ? "openxr-mirror-primary-miss:empty-handle" : "openxr-primary-miss:empty-handle";
        if (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature)
        {
            if (hasQueryFrameOps)
                return mirror ? "openxr-mirror-primary-miss:frame-ops-query" : "openxr-primary-miss:frame-ops-query";

            return mirror ? "openxr-mirror-primary-miss:frame-ops" : "openxr-primary-miss:frame-ops";
        }
        if (IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
            return mirror ? "openxr-mirror-primary-miss:context" : "openxr-primary-miss:context";
        if (!usingCommandChains && variant.PlannerRevision != plannerRevision)
            return mirror ? "openxr-mirror-primary-miss:planner" : "openxr-primary-miss:planner";
        if (IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature))
            return mirror ? "openxr-mirror-primary-miss:image-layout" : "openxr-primary-miss:image-layout";
        if (compareSwapchainImageEverPresented && variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented)
            return mirror ? "openxr-mirror-primary-miss:swapchain-presented" : "openxr-primary-miss:swapchain-presented";

        ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        ulong groupSignature = commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature;
        int groupCount = commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount;
        if (variant.CommandChainScheduleSignature != scheduleSignature)
            return mirror ? "openxr-mirror-primary-miss:schedule" : "openxr-primary-miss:schedule";
        if (variant.CommandChainPrimaryGroupSignature != groupSignature)
            return mirror ? "openxr-mirror-primary-miss:group" : "openxr-primary-miss:group";
        if (variant.CommandChainPrimaryGroupCount != groupCount)
            return mirror ? "openxr-mirror-primary-miss:group-count" : "openxr-primary-miss:group-count";
        if (variant.GpuProfilerActive != gpuPipelineProfilingActive ||
            (gpuPipelineProfilingActive && variant.GpuProfilerFrameSlot != commandBufferImageSlot))
        {
            return mirror ? "openxr-mirror-primary-miss:profiler" : "openxr-primary-miss:profiler";
        }

        return mirror ? "openxr-mirror-primary-miss:unknown" : "openxr-primary-miss:unknown";
    }

    private string DescribeOpenXrPrimaryVariantMismatch(
        List<CommandBufferCacheVariant> variants,
        bool requiresExactFrameOps,
        bool usingCommandChains,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        bool compareSwapchainImageEverPresented,
        bool swapchainImageEverPresented,
        CommandChainSchedule? commandChainSchedule,
        ulong commandChainPrimaryGroupSignature,
        int commandChainPrimaryGroupCount,
        bool gpuPipelineProfilingActive,
        int commandBufferImageSlot)
    {
        if (variants.Count == 0)
            return "none";

        CommandBufferCacheVariant variant = variants[0];
        if (variant.Dirty)
            return $"dirty:{variant.DirtyReason ?? "unknown"}";
        if (variant.PrimaryCommandBuffer.Handle == 0)
            return "empty-handle";
        if (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature)
            return $"frame-ops recorded=0x{variant.FrameOpsSignature:X16} current=0x{frameOpsSignature:X16}";
        if (IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
            return $"context recordedId={variant.RecordedFrameOpContextId} recorded=0x{variant.RecordedFrameOpContextFingerprint:X16} currentId={frameOpContextId} current=0x{frameOpContextFingerprint:X16}";
        if (!usingCommandChains && variant.PlannerRevision != plannerRevision)
            return $"planner recorded={variant.PlannerRevision} current={plannerRevision}";
        if (IsCommandBufferVariantImageLayoutStateDirty(
                variant,
                imageLayoutStartSignature,
                out VulkanImageEntryStateMismatch imageEntryStateMismatch))
        {
            return DescribePrimaryImageEntryStateMismatch(
                imageEntryStateMismatch,
                variant.RecordedImageLayoutStartSignature,
                imageLayoutStartSignature);
        }
        if (compareSwapchainImageEverPresented && variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented)
            return $"swapchain-presented recorded={variant.RecordedSwapchainImageEverPresented} current={swapchainImageEverPresented}";

        ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        ulong groupSignature = commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature;
        int groupCount = commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount;
        if (variant.CommandChainScheduleSignature != scheduleSignature)
            return $"schedule recorded=0x{variant.CommandChainScheduleSignature:X16} current=0x{scheduleSignature:X16}";
        if (variant.CommandChainPrimaryGroupSignature != groupSignature)
            return $"group recorded=0x{variant.CommandChainPrimaryGroupSignature:X16} current=0x{groupSignature:X16}";
        if (variant.CommandChainPrimaryGroupCount != groupCount)
            return $"group-count recorded={variant.CommandChainPrimaryGroupCount} current={groupCount}";
        if (variant.GpuProfilerActive != gpuPipelineProfilingActive ||
            (gpuPipelineProfilingActive && variant.GpuProfilerFrameSlot != commandBufferImageSlot))
            return $"profiler recorded=({variant.GpuProfilerActive},{variant.GpuProfilerFrameSlot}) current=({gpuPipelineProfilingActive},{commandBufferImageSlot})";

        return "unknown";
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
