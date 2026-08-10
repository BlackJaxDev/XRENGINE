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
using XREngine.Rendering.Vulkan.RenderGraph;

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
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
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
                _commandRuntime.PublishOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBufferCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBufferCompleted && !IsDeviceLost)
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer submit failed");

            if (hasRecorded)
                FreeOpenXrRecordedEyeCommandBuffer(recorded);

            OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye)
    {
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
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
                _commandRuntime.PublishOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffers did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffer submit failed");

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
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

        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
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
                _commandRuntime.PublishOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffers did not complete");
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
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffer submit failed");

            if (hasPublish)
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
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
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
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
                _commandRuntime.PublishOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffers did not complete");
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
                _commandRuntime.MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
                    in recorded,
                    "OpenXR true stereo render+publish batch was not submitted");
            }

            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffer submit failed");

            if (hasPublish)
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
            if (hasRecorded)
                FreeOpenXrRecordedEyeCommandBuffer(recorded);

            OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        }
    }

    private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(

        in OpenXrEyeMirrorRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        if (request.TargetFrameBuffer is null || request.Extent.Width == 0 || request.Extent.Height == 0)

            return false;

        bool drainedFrameOps = false;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(OutputRuntime.Desktop.Images?.Length ?? 0);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            OutputRuntime.Desktop.Images?.Length ?? 0);

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
            _commandRuntime.EnsureOpenXrDescriptorFrameSlotFloor(
                openXrFrameDataSlotCount);
            bool frameDataSlotCompletionProven =
                WaitForOpenXrFrameDataSlot(
                    recordImageIndex,
                    "eye mirror render");
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
                    $"OpenXR mapped frame-data slot {recordImageIndex} could not be reopened before mirror recording.");
            }

            using VulkanOpenXrThreadRenderStateScope renderStateScope =
                _commandRuntime.OpenXrRecording.EnterThreadRenderStateScope(
                    CreateOpenXrThreadRenderStateData(),
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
                ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.RecordMirror.PlanAndSchedule.Sort"))
                    ops = _frameOperationScheduler.SortFrameOpsCore(ops, plannerState.CompiledRenderGraph);
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
                ulong plannerRevision = plannerState.ResourcePlannerRevision;
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

                FrameOpContext fallbackContext = ops.Length > 0
                    ? ops[0].Context
                    : plannerContext;
                VulkanFramePlanningSnapshot planningSnapshot =
                    _framePlanner.CaptureSnapshot() with
                    {
                        RenderGraphPlan = plannerState.RenderGraphPlan,
                    };
                FramePlan framePlan = _framePlanner.FramePlanBuilder.BuildAndSeal(
                    checked((int)recordImageIndex),
                    plannerRevision,
                    frameOpsSignature,
                    dynamicOverlaySignature: 0,
                    ops,
                    Array.Empty<FrameOp>(),
                    new VulkanFramePlanRenderGraphAuthority(
                        planningSnapshot.RenderGraphPlan,
                        plannerState.FrameOpResourcePlannerSwitchingState),
                    openXrViewIndex: request.OpenXrViewIndex);
                FrameOperationSequence recordingOperations =
                    framePlan.GetNativeStaticOperationsForRecording();
                ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(
                    mirrorCommandChainImageIndex,
                    request);
                PrimaryCommandArtifactOwner owner =
                    _commandRuntime.GetOrCreateOpenXrPrimaryCommandBufferOwner(
                        cacheKey,
                        recordImageIndex,
                        _commandRuntime.Pools.PrimaryGraphics,
                        $"OpenXR mirror primary eye={request.OpenXrViewIndex}");
                owner.PrimaryCommandPlan.Build(
                    recordingOperations,
                    framePlan.StaticOperationSignature,
                    new VulkanPrimaryPlanTerminalContext(
                        PreserveSwapchainForOverlay: false,
                        TransitionSwapchainToPresent: false,
                        ReleaseExternalImageOwnership: false),
                    framePlan);
                VulkanStateTracker clearState =
                    CreateOpenXrPrewarmRenderStateTracker(request.Extent);
                VulkanPreparedPrimaryCommandInput commandInput = new(
                    recordImageIndex,
                    owner.PrimaryCommandBuffer,
                    default,
                    framePlan,
                    owner.PrimaryCommandPlan,
                    RecordingTarget: default,
                    PresentationSource: default,
                    new VulkanPreparedResourcePlanStamp(
                        planningSnapshot,
                        plannerState.ResourcePlannerRevision,
                        plannerState.ResourcePlannerSignature,
                        plannerState.ResourceAllocationSignature),
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

                        IsExternalSwapchainTarget:
                            request.RendersExternalSwapchainTarget,
                        PreserveSwapchainForOverlay: false,
                        TransitionSwapchainToPresent: false),
                    TrackedTargetLayout: ImageLayout.Undefined,
                    FrameDataImageIndexOverride: recordImageIndex,
                    OpenXrTargetContext: null,
                    CommandChainSchedule: commandChainSchedule,
                    ExcludeDesktopSwapchainBarriers: true,
                    NativeOperationsOverride: ops);
                if (!_commandRuntime.TryRecordPreparedOpenXrMirror(
                        in commandInput,
                        CreateOpenXrMirrorFrameContext(in request),
                        request.OpenXrViewIndex,
                        request.OpenXrImageIndex,
                        recordImageIndex,
                        frameOpsSignature,
                        plannerRevision,
                        plannerContext.ContextId,
                        fallbackContext.ResourceGeneration,
                        fallbackContext.DescriptorGeneration,
                        out recorded,
                        out VulkanImportedTexturePendingUpload[] uploads))
                {
                    return false;
                }

                if (uploads.Length != 0)
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit
                        .AddRange(uploads);
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
        => _commandRuntime.EnsureCommandBufferFrameDataSlotCapacity(
            frameDataSlotCount);

    private CommandChainSchedule? TryBuildOpenXrEyeCommandChainSchedule(
        uint commandChainImageIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Image openXrImage,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong resourcePlanRevision)
    {
        CommandChainSchedule? schedule = _commandRuntime.TryBuildCommandChainSchedule(
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

        if (_commandRuntime.IsOpenXrTraceEnabled)
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
