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
    internal bool TryRenderOpenXrEyeMirrorFrameBuffer(
        XRFrameBuffer targetFrameBuffer,
        Extent2D extent,
        int resourcePlannerStateIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Action emitFrameOps)
    {
        var request = new OpenXrEyeMirrorRenderRequest(
            targetFrameBuffer,
            extent,
            resourcePlannerStateIndex,
            openXrViewIndex,
            openXrImageIndex,
            emitFrameOps);

        return TryRenderOpenXrEyeMirrorFrameBuffer(in request);
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffer(
        in OpenXrEyeMirrorRenderRequest request)
    {
        _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        bool hasRecorded = false;
        bool submitted = false;
        bool commandBufferCompleted = false;
        OpenXrRecordedEyeCommandBuffer recorded = default;

        try
        {
            hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(in request, out recorded);
            if (!hasRecorded)
                return false;

            submitted = SubmitAndWaitOpenXrCommandBuffer(
                recorded.CommandBuffer,
                out commandBufferCompleted,
                CreateOpenXrSubmissionDiagnosticContext(
                    "OpenXrEyeMirrorSubmit",
                    "OpenXrEyeMirror",
                    recorded.OpenXrViewIndex,
                    recorded.OpenXrImageIndex,
                    recorded.FrameDataSlotIndex,
                    request.Extent,
                    recorded.FrameOpsSignature,
                    recorded.PlannerRevision,
                    recorded.FrameOpContextId,
                    recorded.ResourceGeneration,
                    recorded.DescriptorGeneration));
            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in recorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBufferCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBufferCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer submit failed");

            if (hasRecorded)
                FreeOpenXrRecordedEyeCommandBuffer(recorded);

            _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye)
    {
        _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;

        try
        {
            hasFirst = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
                return false;

            hasSecond = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
                return false;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                firstRecorded.CommandBuffer,
                secondRecorded.CommandBuffer,
                out commandBuffersCompleted,
                CreateOpenXrBatchSubmissionDiagnosticContext(
                    "OpenXrEyeMirrorBatchSubmit",
                    "OpenXrEyeMirrorBatch",
                    in firstRecorded,
                    in secondRecorded,
                    firstEye.Extent));

            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffers did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffer submit failed");

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderAndPublishOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye,
        in OpenXrEyeMirrorPublishRequest firstPublish,
        in OpenXrEyeMirrorPublishRequest secondPublish,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        CommandBuffer publishCommandBuffer = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool hasPublish = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;
        EVulkanQueueSubmissionDisposition submissionDisposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;

        try
        {
            hasFirst = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
                return false;

            hasSecond = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
                return false;

            if (!TryPrepareOpenXrEyeMirrorPublish(firstPublish, out OpenXrEyeMirrorPublishPlan firstPlan) ||
                !TryPrepareOpenXrEyeMirrorPublish(secondPublish, out OpenXrEyeMirrorPublishPlan secondPlan))
                return false;

            hasPublish = TryRecordOpenXrEyeMirrorPublishCommandBuffer(
                in firstPlan,
                in secondPlan,
                out publishCommandBuffer,
                out firstPreviewCopied,
                out secondPreviewCopied);
            if (!hasPublish)
                return false;

            CommandBuffer* commandBuffers = stackalloc CommandBuffer[3];
            commandBuffers[0] = firstRecorded.CommandBuffer;
            commandBuffers[1] = secondRecorded.CommandBuffer;
            commandBuffers[2] = publishCommandBuffer;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                commandBuffers,
                3,
                out commandBuffersCompleted,
                out submissionDisposition,
                out _,
                CreateOpenXrBatchSubmissionDiagnosticContext(
                    "OpenXrEyeMirrorRenderPublishSubmit",
                    "OpenXrEyeMirrorRenderPublish",
                    in firstRecorded,
                    in secondRecorded,
                    firstPublish.Extent));

            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffers did not complete");
            }

            return submitted;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.RenderPublishBatchFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror render+publish batch failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffer submit failed");

            if (hasPublish)
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages(
        in OpenXrEyeMirrorRenderRequest renderRequest,
        XRRenderPipelineInstance? renderPipelineInstance,
        XRTexture2DArray? sourceTexture,
        Image leftDestinationImage,
        Format leftDestinationFormat,
        Extent2D leftDestinationExtent,
        string leftDestinationLabel,
        Image rightDestinationImage,
        Format rightDestinationFormat,
        Extent2D rightDestinationExtent,
        string rightDestinationLabel,
        bool flipY,
        EOpenXrStrictSpsFaultInjectionStage faultInjectionStage,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage)
    {
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer recorded = default;
        CommandBuffer publishCommandBuffer = default;
        bool hasRecorded = false;
        bool hasPublish = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;
        EVulkanQueueSubmissionDisposition submissionDisposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;

        try
        {
            // Keep the same planner context active until the array-layer publish
            // command has captured its source image. Leaving the mirror-record
            // scope first can refresh the logical texture wrapper back to its
            // dedicated fallback image even though the recorded render targeted
            // the planner-owned physical image.
            using IDisposable sourcePlannerScope = EnterOpenXrResourcePlannerThreadScope(
                renderRequest.ResourcePlannerStateIndex,
                EVulkanOpenXrResourcePlannerPurpose.Mirror);

            hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(in renderRequest, out recorded);
            if (!hasRecorded)
                return false;

            if (IsOpenXrStrictSpsFaultBoundary(
                    faultInjectionStage,
                    EOpenXrStrictSpsFaultInjectionStage.Recording))
            {
                injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.Recording;
                return false;
            }

            if (renderPipelineInstance?.SkippedResizeCatchUpThisFrame == true)
                return false;

            if (!TryPrepareStereoLayerBlit(
                    sourceTexture,
                    recorded.CommandBuffer,
                    leftDestinationImage,
                    leftDestinationFormat,
                    leftDestinationExtent,
                    leftDestinationLabel,
                    rightDestinationImage,
                    rightDestinationFormat,
                    rightDestinationExtent,
                    rightDestinationLabel,
                    flipY,
                    out OpenXrStereoLayerBlitPlan plan))
            {
                return false;
            }

            hasPublish = TryRecordStereoLayerBlitCommandBuffer(in plan, out publishCommandBuffer);
            if (!hasPublish)
                return false;

            CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
            commandBuffers[0] = recorded.CommandBuffer;
            commandBuffers[1] = publishCommandBuffer;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                commandBuffers,
                2,
                out commandBuffersCompleted,
                out submissionDisposition,
                out injectedFailureStage,
                CreateOpenXrPublishBatchSubmissionDiagnosticContext(
                    "OpenXrStereoLayerRenderPublishSubmit",
                    "OpenXrStereoLayerRenderPublish",
                    in recorded,
                    leftDestinationExtent,
                    leftDestinationLabel) with
                {
                    OpenXrStrictSpsFaultInjectionStage = faultInjectionStage,
                });

            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in recorded);
                UpdateStereoLayerBlitTrackedLayouts(in plan);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffers did not complete");
            }

            return submitted;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.TrueStereo.RenderPublishBatchFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan true stereo render+publish batch failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            if (!submitted &&
                submissionDisposition == EVulkanQueueSubmissionDisposition.NotSubmitted &&
                hasRecorded)
            {
                MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
                    in recorded,
                    "OpenXR true stereo render+publish batch was not submitted");
            }

            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffer submit failed");

            if (hasPublish)
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
            if (hasRecorded)
                FreeOpenXrRecordedEyeCommandBuffer(recorded);

            _openXrBackend.RecordedTextureUploadsForSubmit.Clear();
        }
    }

    private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(
        in OpenXrEyeMirrorRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        if (request.TargetFrameBuffer is null || request.Extent.Width == 0 || request.Extent.Height == 0)
            return false;

        CommandBuffer commandBuffer = default;
        bool drainedFrameOps = false;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            swapChainImages?.Length ?? 0);

        using IDisposable? externalScope = request.RendersExternalSwapchainTarget
            ? EnterOpenXrExternalSwapchainRenderScope(
                request.Extent.Width,
                request.Extent.Height,
                BuildOpenXrExternalSwapchainPlannerTargetIdentity(
                    request.OpenXrViewIndex,
                    request.ViewBatchStructuralIdentity),
                ResolveOpenXrExternalSwapchainTargetName(request.OpenXrViewIndex),
                EVulkanFrameOpContextKind.OpenXrMirror)
            : null;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            WaitForOpenXrFrameDataSlot(recordImageIndex, "eye mirror render");
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            DrainCompletedRecordedTextureUploadPublications();

            using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
                CreateOpenXrPrewarmRenderStateTracker(request.Extent));
            using (EnterOpenXrResourcePlannerThreadScope(
                request.ResourcePlannerStateIndex,
                EVulkanOpenXrResourcePlannerPurpose.Mirror))
            {
                FrameOp[] ops = CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);
                drainedFrameOps = true;
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.NoEyeMirrorFrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan eye mirror rendering produced no frame operations.");
                    return false;
                }
                if (request.RendersExternalSwapchainTarget)
                {
                    ops = NormalizeOpenXrExternalSwapchainFrameOps(ops, request.Extent);
                    ValidateOpenXrExternalFrameOpContexts(
                        ops,
                        request.Extent,
                        request.OpenXrViewIndex,
                        "eye mirror render");
                }

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordMirror.PlanAndSchedule.Sort"))
                    ops = _frameOperationScheduler.SortFrameOpsCore(ops, CompiledRenderGraph);
                if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.EyeMirrorFrameOpPlanDeferred.{GetHashCode()}.{request.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror command buffer preparation: {0}",
                        prePlanFailureReason);
                    return false;
                }

                FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(ops);
                if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.EyeMirrorFrameOpPlanFailed.{GetHashCode()}.{request.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror command buffer preparation: {0}",
                        postPlanFailureReason);
                    return false;
                }

                if (!TryRefreshFrameOpResourceWrappers(
                    ops,
                    plannerContext,
                    "OpenXR eye mirror prepared frame-op resource refresh",
                    AllowSynchronousResourceUploads,
                    out string refreshFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.EyeMirrorFrameOpRefreshDeferred.{GetHashCode()}.{request.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror command buffer preparation: {0}",
                        refreshFailureReason);
                    return false;
                }
                // This is the render-to-array path used by strict SPS. Reserve
                // every repeated direct and indirect use before command-chain
                // workers or the primary command buffer record any dependency.
                if (!PrewarmOpenXrFrameOpResources(
                        ops,
                        recordImageIndex,
                        sealFrameManifest: true))
                {
                    return false;
                }
                ulong plannerRevision = ResourcePlannerRevision;
                ulong frameOpsSignature;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordMirror.PlanAndSchedule.Signature"))
                {
                    frameOpsSignature = ComputeFrameOpsSignature(ops);
                }
                uint mirrorCommandChainImageIndex = recordImageIndex;

                CommandChainSchedule? commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                    mirrorCommandChainImageIndex,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    default,
                    ops,
                    frameOpsSignature,
                    plannerRevision);

                ulong imageLayoutStartSignature = ComputeImageLayoutStateSignature();
                FrameOpContext fallbackContext = ops.Length > 0 ? ops[0].Context : plannerContext;
                ulong frameOpContextFingerprint = ComputeCommandBufferFrameOpContextFingerprint(
                    ops,
                    Array.Empty<FrameOp>(),
                    fallbackContext);
                ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
                    ops,
                    Array.Empty<FrameOp>(),
                    fallbackContext);
                bool reusedPrimary = TryReuseOpenXrMirrorPrimaryCommandBuffer(
                    recordImageIndex,
                    mirrorCommandChainImageIndex,
                    request,
                    ops,
                    frameOpsSignature,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    plannerRevision,
                    imageLayoutStartSignature,
                    commandChainSchedule,
                    out commandBuffer);

                if (!reusedPrimary)
                {
                    commandBuffer = RecordOpenXrMirrorPrimaryCommandBuffer(
                        recordImageIndex,
                        mirrorCommandChainImageIndex,
                        request,
                        ops,
                        frameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        plannerRevision,
                        imageLayoutStartSignature,
                        commandChainSchedule);
                    if (commandBuffer.Handle == 0)
                        return false;
                }

                recorded = new OpenXrRecordedEyeCommandBuffer(
                    commandBuffer,
                    CreateOpenXrMirrorFrameContext(in request),
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    recordImageIndex,
                    frameOpsSignature,
                    plannerRevision,
                    frameOpContextId,
                    fallbackContext.ResourceGeneration,
                    fallbackContext.DescriptorGeneration,
                    OwnedByOpenXrPrimaryCache: true);
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
                $"OpenXR.Vulkan.RenderEyeMirrorFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye mirror render failed: {0}",
                ex.Message);
            return false;
        }
    }

    private bool TryReuseOpenXrMirrorPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request,
        FrameOp[] ops,
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
                RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:disabled");
            return false;
        }

        ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        lock (_openXrBackend.PrimaryCommandBufferVariantsLock)
        {
            if (!OpenXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                if (OpenXrVulkanTraceEnabled)
                    RecordOpenXrPrimaryReuseMiss($"openxr-mirror-primary-miss:no-variants key=0x{cacheKey:X16}");
                else
                    RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:no-variants");
                return false;
            }

            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
            ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
            int commandChainPrimaryGroupCount = -1;
            bool usingCommandChains = commandChainSchedule is not null;
            bool requiresExactFrameOps = true;
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: true,
                    out commandChainPrimaryGroupSignature,
                    out commandChainPrimaryGroupCount))
            {
                if (OpenXrVulkanTraceEnabled)
                {
                    RecordOpenXrPrimaryReuseMiss(
                        $"openxr-mirror-primary-miss:chains-not-reusable key=0x{cacheKey:X16} {DescribeOpenXrPrimaryReusableChainMiss(commandChainImageIndex, commandChainSchedule)}");
                }
                else
                {
                    RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:chains-not-reusable");
                }
                return false;
            }

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
                        "openxr-mirror-primary") ||
                    (!usingCommandChains && variant.PlannerRevision != plannerRevision) ||
                    imageEntryStateDirty ||
                    variant.CommandChainScheduleSignature != (commandChainSchedule?.StructuralSignature ?? ulong.MaxValue) ||
                    variant.CommandChainPrimaryGroupSignature != (commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature) ||
                    variant.CommandChainPrimaryGroupCount != (commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount) ||
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    continue;
                }

                _lastReusableFrameDataRefreshFailureReason = null;
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.MirrorPrimary.RefreshFrameData"))
                {
                    if (!TryRefreshReusableCommandBufferFrameData(recordImageIndex, ops))
                        return false;
                }

                variant.GpuProfilerActive = gpuPipelineProfilingActive;
                variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;

                if (HasQueryFrameOps(ops) &&
                    !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops))
                {
                    if (OpenXrVulkanTraceEnabled)
                        RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:query-pool-prepare");
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
                    "openxr-mirror-primary");
                commandBuffer = variant.PrimaryCommandBuffer;
                PrepareSubmissionMarkersForCommandBufferReuse(commandBuffer, ops);
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] mirror reused primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} target='{4}' commandBuffer=0x{5:X}",
                        request.OpenXrViewIndex,
                        request.OpenXrImageIndex,
                        commandChainImageIndex,
                        recordImageIndex,
                        request.TargetFrameBuffer.Name ?? "<unnamed FBO>",
                        commandBuffer.Handle);
                }

                return true;
            }

            string compactMissReason = ClassifyOpenXrPrimaryVariantMismatch(
                variants,
                true,
                requiresExactFrameOps,
                usingCommandChains,
                frameOpsSignature,
                frameOpContextFingerprint,
                plannerRevision,
                imageLayoutStartSignature,
                ContainsQueryFrameOp(ops),
                false,
                false,
                commandChainSchedule,
                commandChainPrimaryGroupSignature,
                commandChainPrimaryGroupCount,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            if (OpenXrVulkanTraceEnabled)
            {
                RecordOpenXrPrimaryReuseMiss(
                    $"openxr-mirror-primary-miss:no-matching-variant key=0x{cacheKey:X16} variants={variants.Count} first={DescribeOpenXrPrimaryVariantMismatch(
                        variants,
                        requiresExactFrameOps,
                        usingCommandChains,
                        frameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        plannerRevision,
                        imageLayoutStartSignature,
                        false,
                        false,
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

    private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule)
    {
        ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        CommandBufferCacheVariant variant = GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex);

        bool gpuPipelineProfilingActive =
            IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;
        int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
        ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
        int commandChainPrimaryGroupCount = -1;
        _ = TryComputeOpenXrPrimaryCommandBufferGroupSignature(
            commandChainImageIndex,
            commandChainSchedule,
            requireReusableChains: false,
            out commandChainPrimaryGroupSignature,
            out commandChainPrimaryGroupCount);

        long recordStart = Stopwatch.GetTimestamp();
        _commandRecorder.EnterRecordingScope();
        bool queryFrameOpsRequireRerecord = false;
        try
        {
            BeginRecordedTextureUploadSubmitBatch();
            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] mirror record eye={0} swapchainImage={1} commandKey={2} commandSlot={3} target='{4}' extent={5}x{6} ops={7}",
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    commandChainImageIndex,
                    recordImageIndex,
                    request.TargetFrameBuffer.Name ?? "<unnamed FBO>",
                    request.Extent.Width,
                    request.Extent.Height,
                    ops.Length);
            }

            // Strict SPS renders into the engine-owned layered FBO. This command
            // buffer must not inherit desktop swapchain image 0 ownership or a
            // present transition merely because it reuses the primary recorder.
            bool swapchainImageEverPresented = false;
            if (!TryRecordCommandBuffer(
                OpenXrExternalSwapchainTargetImageIndex,
                variant.PrimaryCommandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer: default,
                ops,
                dynamicUiBatchTextOpCount: 0,
                commandChainSchedule,
                preserveSwapchainForOverlay: false,
                recordedSwapchainWriteCount: out int recordedSwapchainWriteCount,
                recordedSwapchainFinalLayout: out ImageLayout swapchainLayoutAfterCommandBuffer,
                recordingDeferredReason: out string recordingDeferredReason,
                queryFrameOpsRequireRerecord: out queryFrameOpsRequireRerecord,
                transitionSwapchainToPresent: false,
                frameDataImageIndexOverride: recordImageIndex,
                excludeDesktopSwapchainBarriers: true))
            {
                CancelRecordedTextureUploadSubmitBatch(
                    $"OpenXR eye mirror command buffer recording deferred: {recordingDeferredReason}");
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.EyeMirrorPrimaryRecordDeferred.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye mirror primary command buffer recording before vkBeginCommandBuffer: {0}",
                    recordingDeferredReason);
                return default;
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
            variant.RecordedSwapchainImageEverPresented = swapchainImageEverPresented;
            variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
            variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
            variant.RecordedSwapchainRefreshFromLastPresentSource = false;
            variant.RecordedImageLayoutStartSignature = imageLayoutStartSignature;
            CaptureCommandBufferVariantImageLayoutEndState(variant);
            variant.CommandChainScheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
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
        }
        catch
        {
            CancelRecordedTextureUploadSubmitBatch("OpenXR eye mirror command buffer recording failed before upload submit");
            throw;
        }
        finally
        {
            _commandRecorder.ExitRecordingScope();
        }

        MoveRecordedTextureUploadsForSubmitTo(_openXrBackend.RecordedTextureUploadsForSubmit);

        if (OpenXrVulkanTraceEnabled)
        {
            double recordMs = (Stopwatch.GetTimestamp() - recordStart) * 1000.0 / Stopwatch.Frequency;
            Debug.Vulkan(
                "[OpenXrVulkan] mirror recorded primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} target='{4}' commandBuffer=0x{5:X} recordMs={6:F3} pendingUploads={7}",
                request.OpenXrViewIndex,
                request.OpenXrImageIndex,
                commandChainImageIndex,
                recordImageIndex,
                request.TargetFrameBuffer.Name ?? "<unnamed FBO>",
                variant.PrimaryCommandBuffer.Handle,
                recordMs,
                _openXrBackend.RecordedTextureUploadsForSubmit.Count);
        }

        EnsureCommandBufferVariantContextBeforeSubmit(
            recordImageIndex,
            variant,
            frameOpContextFingerprint,
            frameOpContextId,
            "recorded-openxr-mirror-primary");
        return variant.PrimaryCommandBuffer;
    }

    private static int ResolveOpenXrFrameDataSlotCount(int desktopSwapchainImageCount)
        => ResolveOpenXrDesktopFrameDataSlotCount(desktopSwapchainImageCount) + OpenXrEyeResourcePlannerStateCount;

    private static int ResolveOpenXrDesktopFrameDataSlotCount(int desktopSwapchainImageCount)
        => Math.Max(Math.Max(desktopSwapchainImageCount, MAX_FRAMES_IN_FLIGHT), 1);

    private static uint ResolveOpenXrRecordImageIndex(
        int resourcePlannerStateIndex,
        int desktopSwapchainImageCount)
    {
        int eyeIndex = NormalizeOpenXrResourcePlannerStateIndex(resourcePlannerStateIndex);
        int desktopFrameDataSlotCount = ResolveOpenXrDesktopFrameDataSlotCount(desktopSwapchainImageCount);
        return (uint)(desktopFrameDataSlotCount + eyeIndex);
    }

    private void EnsureOpenXrFrameDataSlotCapacity(int frameDataSlotCount)
    {
        EnsureCommandBufferFrameDataSlotCapacity(frameDataSlotCount);
    }

    private CommandChainSchedule? TryBuildOpenXrEyeCommandChainSchedule(
        uint commandChainImageIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Image openXrImage,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong resourcePlanRevision)
    {
        CommandChainSchedule? schedule = TryBuildCommandChainSchedule(
            imageIndex: commandChainImageIndex,
            staticOps: ops,
            volatileOps: Array.Empty<FrameOp>(),
            frameOpsSignature: frameOpsSignature,
            volatileSignature: 0,
            resourcePlanRevision: resourcePlanRevision,
            allowExternalSwapchainTarget: true,
            stats: out CommandChainLoweringStats stats);
        if (schedule is null)
            return null;

        if (OpenXrVulkanTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXrVulkan] schedule eye={0} swapchainImage={1} image=0x{2:X} commandKey={3} chains={4} groups={5} recorded={6} reused={7}",
                openXrViewIndex,
                openXrImageIndex,
                openXrImage.Handle,
                commandChainImageIndex,
                stats.ChainsScheduled,
                schedule.Groups.Length,
                stats.ChainsRecorded,
                stats.ChainsReused);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
            chainsScheduled: stats.ChainsScheduled,
            chainsRecorded: stats.ChainsRecorded,
            chainsReused: stats.ChainsReused,
            chainsFrameDataRefreshed: stats.ChainsFrameDataRefreshed,
            volatileChainsRecorded: stats.VolatileChainsRecorded,
            secondaryCommandBuffers: stats.SecondaryCommandBuffers,
            visibilityPackets: stats.VisibilityPackets,
            renderPackets: stats.RenderPackets,
            firstStructuralDirtyReason: stats.FirstStructuralDirtyReason,
            firstDescriptorGenerationMismatch: stats.FirstDescriptorGenerationMismatch,
            firstResourcePlanRevisionMismatch: stats.FirstResourcePlanRevisionMismatch);

        return schedule;
    }

    private static uint BuildOpenXrCommandChainImageIndex(uint viewIndex, uint imageIndex, Image image)
    {
        int hash = HashCode.Combine("OpenXR", viewIndex, imageIndex);
        return 1_000_000u + (uint)(hash & 0x0FFF_FFFF);
    }

}
