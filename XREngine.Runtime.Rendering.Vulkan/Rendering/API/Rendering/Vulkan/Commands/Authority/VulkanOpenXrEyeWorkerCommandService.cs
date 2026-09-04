namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the renderer-independent OpenXR stereo-worker command transaction:
/// frozen-input dispatch, queue submission, and upload settlement. The renderer
/// prepares immutable eye inputs and consumes this explicit outcome only.
/// </summary>
internal sealed class VulkanOpenXrEyeWorkerCommandService : IDisposable
{
    private OpenXrEyeRecordWorkerScheduler? _scheduler;
    private VulkanCommandRuntime? _runtime;
    private VulkanOpenXrCommandRecordingService? _recording;
    private VulkanDeviceContext? _device;

    internal void Configure(
        VulkanCommandRuntime runtime,
        VulkanOpenXrCommandRecordingService recording,
        VulkanDeviceContext device)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(device);
        if (_runtime is not null &&
            (!ReferenceEquals(_runtime, runtime) ||
             !ReferenceEquals(_recording, recording) ||
             !ReferenceEquals(_device, device)))
        {
            throw new InvalidOperationException(
                "The OpenXR eye worker command service cannot be rebound to different authorities.");
        }

        _runtime = runtime;
        _recording = recording;
        _device = device;
    }

    internal VulkanOpenXrEyeWorkerCommandResult Execute(
        in OpenXrPreparedEyeRecordWorkerInput leftEye,
        in OpenXrPreparedEyeRecordWorkerInput rightEye,
        in OpenXrPreparedEyeCommandBufferInput leftPrepared,
        in OpenXrPreparedEyeCommandBufferInput rightPrepared,
        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket admissionTicket,
        ref bool trackerOwnsSubmission,
        OpenXrSubmissionMetadata submissionMetadata,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        VulkanCommandRuntime runtime = _runtime ?? throw new InvalidOperationException("OpenXR render-domain eye recording is not configured.");
        VulkanOpenXrCommandRecordingService recording = _recording ?? throw new InvalidOperationException("OpenXR eye recording is not configured.");
        VulkanDeviceContext device = _device ?? throw new InvalidOperationException("OpenXR render-domain eye recording has no device authority.");
        if (!device.IsOperational)
        {
            throw new InvalidOperationException(
                "OpenXR foreground eye recording cannot run because the Vulkan device is not operational.");
        }

        OpenXrEyeRecordWorkerBatchResult batch =
            (_scheduler ??= new OpenXrEyeRecordWorkerScheduler())
            .Record(runtime, recording, leftEye, rightEye);
        OpenXrEyeRecordWorkerResult leftResult = batch.Left;
        OpenXrEyeRecordWorkerResult rightResult = batch.Right;
        if (!batch.Left.Success || !batch.Right.Success)
        {
            Cancel(in leftResult, runtime, "OpenXR lane-affine eye command recording failed");
            Cancel(in rightResult, runtime, "OpenXR lane-affine eye command recording failed");
            RethrowWorkerFailure(in leftResult, in rightResult);
            if (leftEye.OutputContract.WorkClass == ERenderOutputWorkClass.PresentNow ||
                rightEye.OutputContract.WorkClass == ERenderOutputWorkClass.PresentNow)
            {
                RenderOutputRequest failedContract =
                    leftEye.OutputContract.WorkClass == ERenderOutputWorkClass.PresentNow
                        ? leftEye.OutputContract
                        : rightEye.OutputContract;
                throw new VulkanPresentNowReadinessException(
                    failedContract.FrameId,
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    "openxr-eye-parallel-primary",
                    "OpenXREyeSubmit -> parallel exact primary recording",
                    batch.WaitForWorkersTime,
                    batch.WaitForWorkersTime,
                    "A foreground eye lane returned failure without a typed recording exception.");
            }

            return new(batch, false, false, default);
        }

        try
        {
            int leftUploadCount = leftResult.RecordedUploads?.Length ?? 0;
            int rightUploadCount = rightResult.RecordedUploads?.Length ?? 0;
            if (leftUploadCount + rightUploadCount > OpenXrVulkanSubmissionTracker.MaxTrackedUploads)
            {
                throw new InvalidOperationException(
                    "OpenXR parallel-eye submission recorded more uploads than its bounded ownership slot can retain.");
            }
    
            OpenXrRecordedEyeCommandBuffer leftRecorded = leftResult.Recorded;
            OpenXrRecordedEyeCommandBuffer rightRecorded = rightResult.Recorded;
            Span<uint> frameSlots = stackalloc uint[2];
            frameSlots[0] = leftRecorded.FrameDataSlotIndex;
            frameSlots[1] = rightRecorded.FrameDataSlotIndex;
            trackerOwnsSubmission = runtime.OpenXrSubmissionTracker.RegisterSubmission(
                admissionTicket,
                submissionMetadata.FrameId,
                submissionMetadata.PredictedDisplayTime,
                3u,
                leftEye.OpenXrImageIndex,
                rightEye.OpenXrImageIndex,
                in leftRecorded,
                hasFirst: true,
                in rightRecorded,
                hasSecond: true,
                in leftPrepared,
                hasFirstPrepared: true,
                in rightPrepared,
                hasSecondPrepared: true,
                leftResult.RecordedUploads,
                rightResult.RecordedUploads,
                default,
                0UL,
                runtime.MappedFrameArena,
                runtime.MappedFrameArena?.Generation ?? 0UL,
                runtime.ResourceRuntime.FrameDataArena,
                runtime.ResourceRuntime.FrameDataArena?.Generation ?? 0UL,
                frameSlots,
                0L,
                0L);
            VulkanOpenXrSubmissionInput submissionInput = new(
                leftRecorded.CommandBuffer,
                rightRecorded.CommandBuffer,
                default,
                2,
                diagnosticContext,
                AdmissionTicket: admissionTicket);
            VulkanOpenXrSubmissionResult submission =
                runtime.SubmitAndWaitOpenXr(in submissionInput);
    
            return new(batch, submission.Succeeded, submission.CommandBuffersCompleted, submission);
        }
        finally
        {
            try
            {
                if (!trackerOwnsSubmission)
                {
                    Cancel(in leftResult, runtime, "OpenXR parallel-eye registration failed");
                    Cancel(in rightResult, runtime, "OpenXR parallel-eye registration failed");
                }
            }
            finally
            {
                // A registered payload remains solely tracker-owned even when
                // native submission or later diagnostics throw.
                runtime.OpenXrSubmissionTracker.CancelPreparedSubmission(admissionTicket);
            }
        }
    }

    private static void RethrowWorkerFailure(
        scoped in OpenXrEyeRecordWorkerResult left,
        scoped in OpenXrEyeRecordWorkerResult right)
    {
        if (left.Failure?.SourceException is VulkanPresentNowReadinessException)
            left.Failure.Throw();
        if (right.Failure?.SourceException is VulkanPresentNowReadinessException)
            right.Failure.Throw();
        left.Failure?.Throw();
        right.Failure?.Throw();
    }

    private static void Cancel(
        scoped in OpenXrEyeRecordWorkerResult result,
        VulkanCommandRuntime runtime,
        string reason)
    {
        if (result.RecordedUploads is { Length: > 0 } uploads)
            runtime.CancelOpenXrRecordedTextureUploads(uploads.AsSpan(), reason);
    }

    public void Dispose()
    {
        _scheduler?.Dispose();
        _scheduler = null;
    }
}
