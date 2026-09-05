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

internal sealed partial class VulkanFrameLoop
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
        if (!_commandRuntime.OpenXrSubmissionTracker.TryReserveSubmission(
                out OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? admissionTicket))
            return false;
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        bool hasRecorded = false;
        bool submitted = false;
        bool commandBufferCompleted = false;
        bool trackerOwnsSubmission = false;
        OpenXrRecordedEyeCommandBuffer recorded = default;

        try
        {
            hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(in request, out recorded);
            if (!hasRecorded)
            {
                if (request.RendersExternalSwapchainTarget)
                {
                    throw CreateOpenXrEyePresentNowFailure(
                        request.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "mirror-record",
                        "DesktopMirror -> logical plan -> exact primary",
                        "Foreground external mirror recording returned no command buffer.");
                }

                return false;
            }

            VulkanOpenXrSubmissionResult submission = SubmitTrackedOpenXrMirrorSubmission(
                admissionTicket!.Value, ref trackerOwnsSubmission, request.SubmissionMetadata, in recorded, hasFirst: true, secondRecorded: default,
                hasSecond: false, temporaryCommandBuffer: default,
                _commandRuntime.CreateOpenXrSubmissionDiagnosticContext(
                    AcceptedAttemptCount,
                    ResolveOpenXrExternalSwapchainTargetName(recorded.OpenXrViewIndex),
                    "OpenXrEyeMirrorSubmit",
                    "OpenXrEyeMirror",
                    recorded.OpenXrImageIndex,
                    recorded.FrameDataSlotIndex,
                    request.Extent,
                    recorded.FrameOpsSignature,
                    recorded.PlannerRevision,
                    recorded.FrameOpContextId,
                    recorded.ResourceGeneration,
                    recorded.DescriptorGeneration));
            submitted = submission.Succeeded;
            commandBufferCompleted = submission.CommandBuffersCompleted;
            if (!trackerOwnsSubmission && !commandBufferCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer did not complete");
            }

            if (!submitted)
                ThrowOpenXrRecordedPresentNowSubmissionFailure(
                    in recorded,
                    commandBufferCompleted,
                    "mirror-submit");
            return submitted;
        }
        finally
        {
            try
            {
                if (!trackerOwnsSubmission && !submitted && !commandBufferCompleted && !IsDeviceLost)
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer submit failed");
    
                if (!trackerOwnsSubmission && hasRecorded)
                    FreeOpenXrRecordedEyeCommandBuffer(recorded);
    
                if (!trackerOwnsSubmission)
                {
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
                }
            }
            finally
            {
                // After registration, the tracker is the sole settlement owner.
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye)
    {
        if (!_commandRuntime.OpenXrSubmissionTracker.TryReserveSubmission(
                out OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? admissionTicket))
            return false;
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        bool hasFirst = false;

        bool hasSecond = false;
        bool submitted = false;

        bool commandBuffersCompleted = false;
        bool trackerOwnsSubmission = false;

        try
        {
            hasFirst = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
            {
                if (firstEye.RendersExternalSwapchainTarget)
                {
                    throw CreateOpenXrEyePresentNowFailure(
                        firstEye.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "left-mirror-record",
                        "DesktopMirror -> left logical plan -> exact primary",
                        "Foreground left external mirror recording returned no command buffer.");
                }

                return false;
            }

            hasSecond = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
            {
                if (secondEye.RendersExternalSwapchainTarget)
                {
                    throw CreateOpenXrEyePresentNowFailure(
                        secondEye.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "right-mirror-record",
                        "DesktopMirror -> right logical plan -> exact primary",
                        "Foreground right external mirror recording returned no command buffer.");
                }

                return false;
            }

            VulkanOpenXrSubmissionResult submission = SubmitTrackedOpenXrMirrorSubmission(
                admissionTicket!.Value, ref trackerOwnsSubmission, firstEye.SubmissionMetadata, in firstRecorded, hasFirst: true, in secondRecorded,
                hasSecond: true, temporaryCommandBuffer: default,
                _commandRuntime.CreateOpenXrBatchSubmissionDiagnosticContext(
                    AcceptedAttemptCount,
                    "OpenXrEyeMirrorBatchSubmit",
                    "OpenXrEyeMirrorBatch",
                    in firstRecorded,
                    firstEye.Extent));
            submitted = submission.Succeeded;
            commandBuffersCompleted = submission.CommandBuffersCompleted;
            if (!trackerOwnsSubmission && !commandBuffersCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffers did not complete");
            }

            if (!submitted)
                ThrowOpenXrRecordedPresentNowSubmissionFailure(
                    in firstRecorded,
                    commandBuffersCompleted,
                    "mirror-batch-submit");
            return submitted;
        }
        finally
        {
            try
            {
                if (!trackerOwnsSubmission && !submitted && !commandBuffersCompleted && !IsDeviceLost)
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffer submit failed");
    
                if (!trackerOwnsSubmission && hasSecond)
                    FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
                if (!trackerOwnsSubmission && hasFirst)
                    FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);
    
                if (!trackerOwnsSubmission)
                {
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
                }
            }
            finally
            {
                // After registration, the tracker is the sole settlement owner.
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    internal unsafe bool TryRenderAndPublishOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye,
        in OpenXrEyeMirrorPublishRequest firstPublish,
        in OpenXrEyeMirrorPublishRequest secondPublish,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        if (!_commandRuntime.OpenXrSubmissionTracker.TryReserveSubmission(
                out OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? admissionTicket))
            return false;
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
        bool trackerOwnsSubmission = false;

        try
        {
            hasFirst = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
            {
                if (firstEye.RendersExternalSwapchainTarget)
                {
                    throw CreateOpenXrEyePresentNowFailure(
                        firstEye.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "left-mirror-publish-record",
                        "DesktopMirror -> left render/publish primary",
                        "Foreground left mirror render/publish recording returned no command buffer.");
                }

                return false;
            }

            hasSecond = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
            {
                if (secondEye.RendersExternalSwapchainTarget)
                {
                    throw CreateOpenXrEyePresentNowFailure(
                        secondEye.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "right-mirror-publish-record",
                        "DesktopMirror -> right render/publish primary",
                        "Foreground right mirror render/publish recording returned no command buffer.");
                }

                return false;
            }

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


            VulkanOpenXrSubmissionResult submission = SubmitTrackedOpenXrMirrorSubmission(
                admissionTicket!.Value, ref trackerOwnsSubmission, firstEye.SubmissionMetadata, in firstRecorded, hasFirst: true, in secondRecorded,
                hasSecond: true, publishCommandBuffer,
                _commandRuntime.CreateOpenXrBatchSubmissionDiagnosticContext(
                    AcceptedAttemptCount,
                    "OpenXrEyeMirrorRenderPublishSubmit",
                    "OpenXrEyeMirrorRenderPublish",
                    in firstRecorded,
                    firstPublish.Extent));
            submitted = submission.Succeeded;
            commandBuffersCompleted = submission.CommandBuffersCompleted;
            submissionDisposition = submission.SubmissionDisposition;
            if (!trackerOwnsSubmission && !commandBuffersCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffers did not complete");
            }

            if (!submitted)
                ThrowOpenXrRecordedPresentNowSubmissionFailure(
                    in firstRecorded,
                    commandBuffersCompleted,
                    "mirror-render-publish-submit");
            return submitted;
        }
        catch (VulkanPresentNowReadinessException)
        {
            throw;
        }
        catch (Exception) when (IsDeviceLost)
        {
            throw;
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
            try
            {
                if (!trackerOwnsSubmission && !submitted && !commandBuffersCompleted && !IsDeviceLost)
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffer submit failed");
    
                if (!trackerOwnsSubmission && hasPublish)
                    FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
                if (!trackerOwnsSubmission && hasSecond)
                    FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
                if (!trackerOwnsSubmission && hasFirst)
                    FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);
    
                if (!trackerOwnsSubmission)
                {
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
                }
            }
            finally
            {
                // After registration, the tracker is the sole settlement owner.
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    internal unsafe bool TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages(
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
        if (!_commandRuntime.OpenXrSubmissionTracker.TryReserveSubmission(
                out OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket? admissionTicket))
            return false;
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer recorded = default;
        CommandBuffer publishCommandBuffer = default;
        bool hasRecorded = false;
        bool hasPublish = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;
        EVulkanQueueSubmissionDisposition submissionDisposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;
        bool trackerOwnsSubmission = false;

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
            {
                if (renderRequest.RendersExternalSwapchainTarget)
                {
                    throw CreateOpenXrEyePresentNowFailure(
                        renderRequest.OpenXrViewIndex,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        "stereo-mirror-record",
                        "DesktopMirror -> stereo render/publish primary",
                        "Foreground stereo mirror recording returned no command buffer.");
                }

                return false;
            }


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

            hasPublish = TryRecordStereoLayerBlitCommandBuffer(
                in plan,
                recorded.CommandBuffer,
                out publishCommandBuffer);
            if (!hasPublish)
                return false;

            VulkanOpenXrSubmissionResult submission = SubmitTrackedOpenXrMirrorSubmission(
                admissionTicket!.Value, ref trackerOwnsSubmission, renderRequest.SubmissionMetadata, in recorded, hasFirst: true, secondRecorded: default,
                hasSecond: false, publishCommandBuffer,
                _commandRuntime.CreateOpenXrPublishBatchSubmissionDiagnosticContext(
                    AcceptedAttemptCount,
                    "OpenXrStereoLayerRenderPublishSubmit",
                    "OpenXrStereoLayerRenderPublish",
                    in recorded,
                    leftDestinationExtent,
                    leftDestinationLabel) with
                {
                    OpenXrStrictSpsFaultInjectionStage = faultInjectionStage,
                });
            submitted = submission.Succeeded;
            commandBuffersCompleted = submission.CommandBuffersCompleted;
            submissionDisposition = submission.SubmissionDisposition;
            injectedFailureStage = submission.InjectedFailureStage;
            if (submission.SubmissionReceipt.SubmissionAccepted)
            {
                UpdateStereoLayerBlitTrackedLayouts(in plan);
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffers did not complete");
            }

            if (!submitted &&
                injectedFailureStage == EOpenXrStrictSpsFaultInjectionStage.None)
            {
                ThrowOpenXrRecordedPresentNowSubmissionFailure(
                    in recorded,
                    commandBuffersCompleted,
                    "stereo-mirror-submit");
            }
            return submitted;
        }
        catch (VulkanPresentNowReadinessException)
        {
            throw;
        }
        catch (Exception) when (IsDeviceLost)
        {
            throw;
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
            try
            {
                if (!trackerOwnsSubmission && !submitted &&
                    submissionDisposition == EVulkanQueueSubmissionDisposition.NotSubmitted &&
                    hasRecorded)
                {
                    _commandRuntime.MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
                        in recorded,
                        "OpenXR true stereo render+publish batch was not submitted");
                }
    
                if (!trackerOwnsSubmission && !submitted && !commandBuffersCompleted && !IsDeviceLost)
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffer submit failed");
    
                if (!trackerOwnsSubmission && hasPublish)
                    FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
                if (!trackerOwnsSubmission && hasRecorded)
                    FreeOpenXrRecordedEyeCommandBuffer(recorded);
    
                if (!trackerOwnsSubmission)
                {
                    OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
                }
            }
            finally
            {
                // After registration, the tracker is the sole settlement owner.
                _commandRuntime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    private VulkanOpenXrSubmissionResult SubmitTrackedOpenXrMirrorSubmission(
        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket admissionTicket,
        ref bool trackerOwnsSubmission,
        in OpenXrSubmissionMetadata submissionMetadata,
        in OpenXrRecordedEyeCommandBuffer firstRecorded,
        bool hasFirst,
        in OpenXrRecordedEyeCommandBuffer secondRecorded,
        bool hasSecond,
        CommandBuffer temporaryCommandBuffer,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        Span<uint> frameSlots = stackalloc uint[2];
        int frameSlotCount = 0;
        if (hasFirst)
            frameSlots[frameSlotCount++] = firstRecorded.FrameDataSlotIndex;
        if (hasSecond && secondRecorded.FrameDataSlotIndex != firstRecorded.FrameDataSlotIndex)
            frameSlots[frameSlotCount++] = secondRecorded.FrameDataSlotIndex;
        trackerOwnsSubmission = _commandRuntime.OpenXrSubmissionTracker.RegisterSubmission(
            admissionTicket, submissionMetadata.FrameId, submissionMetadata.PredictedDisplayTime,
            hasSecond ? 3U : 1U,
            hasFirst ? firstRecorded.OpenXrImageIndex : 0U,
            hasSecond ? secondRecorded.OpenXrImageIndex : 0U,
            in firstRecorded, hasFirst, in secondRecorded, hasSecond,
            firstPrepared: default, hasFirstPrepared: false,
            secondPrepared: default, hasSecondPrepared: false,
            OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit,
            additionalUploads: null,
            default, 0UL,
            _commandRuntime.MappedFrameArena, _commandRuntime.MappedFrameArena?.Generation ?? 0UL,
            _commandRuntime.ResourceRuntime.FrameDataArena, _commandRuntime.ResourceRuntime.FrameDataArena?.Generation ?? 0UL,
            frameSlots[..frameSlotCount], 0L, 0L,
            temporaryCommandBuffer);
        OutputRuntime.OpenXrBackend.RecordedTextureUploadsForSubmit.Clear();
        return _commandRuntime.SubmitAndWaitOpenXr(new VulkanOpenXrSubmissionInput(
            firstRecorded.CommandBuffer,
            // A single layered render followed by its publish command occupies
            // slots 0 and 1. Leaving slot 1 empty rejects the batch before submit.
            hasSecond ? secondRecorded.CommandBuffer : temporaryCommandBuffer,
            hasSecond ? temporaryCommandBuffer : default,
            temporaryCommandBuffer.Handle != 0 ? (hasSecond ? 3U : 2U) : (hasSecond ? 2U : 1U),
            diagnosticContext,
            AdmissionTicket: admissionTicket));
    }

    private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(

        in OpenXrEyeMirrorRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        if (request.TargetFrameBuffer is null || request.Extent.Width == 0 || request.Extent.Height == 0)

            return false;

        bool drainedFrameOps = false;
        FrameOp[]? capturedOps = null;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(OutputRuntime.Desktop.Images?.Length ?? 0);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            OutputRuntime.Desktop.Images?.Length ?? 0);

        VulkanOpenXrFrameContext openXrFrameContext =
            CreateOpenXrMirrorFrameContext(in request);
        using VulkanOpenXrFrameContextScope frameContextScope =
            request.RendersExternalSwapchainTarget
                ? default
                : _commandRuntime.OpenXrRecording.EnterFrameContextScope(
                    OutputRuntime.OpenXrBackend,
                    in openXrFrameContext);
        using IDisposable? externalScope = request.RendersExternalSwapchainTarget
            ? EnterOpenXrExternalSwapchainRenderScope(in openXrFrameContext)
            : null;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            _commandRuntime.EnsureOpenXrDescriptorFrameSlotFloor(
                openXrFrameDataSlotCount);
            if (!TryPrepareOpenXrFrameDataSlot(
                    recordImageIndex,
                    "eye mirror render",
                    out bool frameDataSlotCompletionProven))
            {
                return false;
            }
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            ResourceRuntime.Uploads.DrainCompletedRecordedTextureUploadPublications(
                Api!, _deviceContext, _commandRuntime, ResourceRuntime, IsDeviceLost);

            ReopenOpenXrFrameDataSlot(recordImageIndex, frameDataSlotCompletionProven);

            using VulkanOpenXrThreadRenderStateScope renderStateScope =
                _commandRuntime.OpenXrRecording.EnterThreadRenderStateScope(
                    CreateOpenXrThreadRenderStateData(),
                    CreateOpenXrPrewarmRenderStateTracker(request.Extent));
            using (EnterOpenXrResourcePlannerThreadScope(
                request.ResourcePlannerStateIndex,
                EVulkanOpenXrResourcePlannerPurpose.Mirror))
            {
                capturedOps = CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);
                drainedFrameOps = true;
                FrameOp[] ops = VulkanCommandRuntime.FilterDiagnosticSkippedFrameOps(capturedOps);
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
                ulong frameOpsSignature = 0UL;
                uint mirrorCommandChainImageIndex = recordImageIndex;

                FrameOpContext fallbackContext = ops.Length > 0
                    ? ops[0].Context
                    : plannerContext;
                ulong logicalViewId = GetSingleOpenXrLogicalViewId(ops);
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
                        plannerState.FrameOpResourcePlannerSwitchingState,
                        _framePlanner,
                        _resourceRuntime.BackendObjectContext),
                    openXrViewIndex: request.OpenXrViewIndex);
                framePlan.PrepareRecordingPlannerGenerations(in plannerState);
                EVrOutputViewKind viewKind = default;
                EVrOutputViewKind indexedViewKind = default;
                int outputIndex = -1;
                RenderOutputRequest outputContract = default;
                EFrameOutputKind expectedOutputKind =
                    request.RendersExternalSwapchainTarget
                        ? EFrameOutputKind.DesktopMirror
                        : EFrameOutputKind.OpenXREyeSubmit;
                bool hasOutputContract =
                    logicalViewId != 0UL &&
                    framePlan.ViewSet.TryGetLocatedOpenXrViewKindByLogicalViewId(
                        logicalViewId,
                        out viewKind) &&
                    framePlan.ViewSet.TryGetLocatedOpenXrViewKind(
                        request.OpenXrViewIndex,
                        out indexedViewKind) &&
                    viewKind == indexedViewKind &&
                    framePlan.TryGetExecutableOutputContractForLogicalView(
                        logicalViewId,
                        expectedOutputKind,
                        viewKind,
                        out outputIndex,
                        out outputContract);
                bool hasForegroundContract =
                    hasOutputContract &&
                    outputContract.WorkClass == ERenderOutputWorkClass.PresentNow;
                if (!hasForegroundContract ||
                    outputContract.ReadinessPolicy == ERenderOutputReadinessPolicy.AllowDeferral)
                {
                    throw new VulkanPresentNowReadinessException(
                        framePlan.RenderFrameId,
                        EVulkanPresentNowReadinessStage.FramePlanSeal,
                        $"openxr-mirror-{request.OpenXrViewIndex}-output-bind",
                        $"{expectedOutputKind} -> exact logical-view output terminal",
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        "The OpenXR render plan did not expose exactly one executable non-deferrable terminal for this located view.");
                }
                frameOpsSignature = framePlan.StaticOperationSignature;
                FrameOperationSequence recordingOperations =
                    framePlan.GetNativeStaticOperationsForRecording();
                VulkanReadOnlyStoragePreparedAuthority? readOnlyStorageAuthority =
                    PrepareOpenXrImmutableStorage(
                        recordingOperations.Stream,
                        recordImageIndex,
                        request.OpenXrViewIndex);
                using VulkanResourceRuntime.ReadOnlyStorageRecordingScope storageScope =
                    ResourceRuntime.EnterReadOnlyStorageRecordingScope(readOnlyStorageAuthority);
                VulkanComputePreparationResult computePreparation =
                    _commandRuntime.PrepareComputeFrameOpsForRecording(
                        recordImageIndex,
                        recordingOperations,
                        framePlan);
                if (!computePreparation.Succeeded)
                {
                    throw new VulkanPresentNowReadinessException(
                        framePlan.RenderFrameId,
                        EVulkanPresentNowReadinessStage.PipelineCompilation,
                        $"openxr-mirror-{request.OpenXrViewIndex}-compute",
                        $"{expectedOutputKind} -> sealed compute program",
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        "XR deadline missed with no declared resident GPU fallback. " +
                        computePreparation.FormatFailure());
                }
                CommandChainSchedule? commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                    mirrorCommandChainImageIndex,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    default,
                    framePlan.StaticOperations,
                    frameOpsSignature,
                    plannerRevision);
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
                    recordingOperations.Stream,
                    framePlan.StaticOperationSignature,
                    new VulkanPrimaryPlanTerminalContext(
                        PreserveSwapchainForOverlay: false,
                        TransitionSwapchainToPresent: false,
                        ReleaseExternalImageOwnership: false),
                    framePlan: framePlan);
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
                        FreshSerialRecording: hasForegroundContract,

                        // This command transaction publishes an OpenXR output.
                        // Strict SPS records its first pass into an engine-owned
                        // array, so it intentionally has no top-level native
                        // swapchain target even though its terminal is external.
                        IsExternalSwapchainTarget: true,
                        PreserveSwapchainForOverlay: false,
                        TransitionSwapchainToPresent: false,
                        ReadinessPolicy: hasOutputContract
                            ? outputContract.ReadinessPolicy
                            : ERenderOutputReadinessPolicy.AllowDeferral,
                        WorkClass: hasOutputContract
                            ? outputContract.WorkClass
                            : ERenderOutputWorkClass.Background,
                        SourceFrameId: framePlan.RenderFrameId,
                        AllowArtifactReuse: !hasForegroundContract,
                        AllowSecondaryDeferral: !hasForegroundContract),
                    TrackedTargetLayout: ImageLayout.Undefined,
                    FrameDataImageIndexOverride: recordImageIndex,
                    ReadOnlyStorageAuthority: readOnlyStorageAuthority,
                    OpenXrTargetContext: null,
                    CommandChainSchedule: commandChainSchedule,
                    ExcludeDesktopSwapchainBarriers: true);
                if (!_commandRuntime.TryRecordPreparedOpenXrMirror(
                        in commandInput,
                        CreateOpenXrMirrorFrameContext(in request),
                        request.OpenXrViewIndex,
                        request.OpenXrImageIndex,
                        recordImageIndex,
                        logicalViewId,
                        outputIndex,
                        outputContract,
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
                DrainAndReleaseFrameOpsExcludingTextureUploads();
            if (IsOpenXrStrictExtentFailure(ex))
                throw;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderEyeMirrorFailed.{GetHashCode()}",

                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye mirror render failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            if (capturedOps is not null)
                VulkanAdvancedVisibilityInputLease.ReleaseOperations(capturedOps);
        }
    }

    private static int ResolveOpenXrFrameDataSlotCount(int desktopSwapchainImageCount)
        => ResolveOpenXrDesktopFrameDataSlotCount(desktopSwapchainImageCount) + OpenXrEyeResourcePlannerStateCount;

    private static int ResolveOpenXrDesktopFrameDataSlotCount(int desktopSwapchainImageCount)
        => Math.Max(Math.Max(desktopSwapchainImageCount, DesktopFrameSlotCount), 1);

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
        _commandRuntime.EnsureCommandBufferFrameDataSlotCapacity(frameDataSlotCount);
        ResourceRuntime.EnsureMappedFrameArenaFrameSlotCapacity(frameDataSlotCount);
        if (FrameDataArena is not { } storage || !storage.TryEnsureFrameSlotCount(frameDataSlotCount))
            throw new InvalidOperationException($"OpenXR could not reserve {frameDataSlotCount} immutable storage slots within the mapped-memory budget.");
    }

    private CommandChainSchedule? TryBuildOpenXrEyeCommandChainSchedule(
        uint commandChainImageIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Image openXrImage,
        FrameOperationStream staticOperations,
        ulong frameOpsSignature,
        ulong resourcePlanRevision)
    {
        CommandChainSchedule? schedule = _commandRuntime.TryBuildCommandChainSchedule(
            imageIndex: commandChainImageIndex,
            staticOps: staticOperations,
            volatileOps: FrameOperationStream.Empty,
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
