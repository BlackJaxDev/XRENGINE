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

            return new(batch, false, false);
        }

        VulkanOpenXrSubmissionInput submissionInput = new(
            leftResult.Recorded.CommandBuffer,
            rightResult.Recorded.CommandBuffer,
            2,
            diagnosticContext);
        VulkanOpenXrSubmissionResult submission =
            runtime.SubmitAndWaitOpenXr(in submissionInput);
        if (submission.Succeeded)
        {
            Publish(in leftResult, runtime, "OpenXR eye parallel batch");
            Publish(in rightResult, runtime, "OpenXR eye parallel batch");
        }
        else if (!submission.CommandBuffersCompleted && device.IsOperational)
        {
            Cancel(in leftResult, runtime, "OpenXR eye parallel batch command buffers did not complete");
            Cancel(in rightResult, runtime, "OpenXR eye parallel batch command buffers did not complete");
        }

        if (!submission.Succeeded &&
            (leftEye.OutputContract.WorkClass == ERenderOutputWorkClass.PresentNow ||
             rightEye.OutputContract.WorkClass == ERenderOutputWorkClass.PresentNow))
        {
            if (submission.SubmissionReceipt.Result == Silk.NET.Vulkan.Result.ErrorDeviceLost)
            {
                throw new InvalidOperationException(
                    "OpenXR foreground queue submission failed because the Vulkan device was lost.");
            }

            RenderOutputRequest failedContract =
                leftEye.OutputContract.WorkClass == ERenderOutputWorkClass.PresentNow
                    ? leftEye.OutputContract
                    : rightEye.OutputContract;
            throw new VulkanPresentNowReadinessException(
                failedContract.FrameId,
                EVulkanPresentNowReadinessStage.QueueSubmission,
                "openxr-eye-parallel-submit",
                "OpenXREyeSubmit -> graphics queue submission -> timeline completion",
                TimeSpan.Zero,
                TimeSpan.Zero,
                $"The exact foreground eye submission failed with {submission.SubmissionReceipt.Result}; " +
                $"disposition={submission.SubmissionDisposition} completed={submission.CommandBuffersCompleted}.");
        }

        return new(batch, submission.Succeeded, submission.CommandBuffersCompleted);
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

    private static void Publish(
        scoped in OpenXrEyeRecordWorkerResult result,
        VulkanCommandRuntime runtime,
        string source)
    {
        if (result.RecordedUploads is { Length: > 0 } uploads)
            runtime.PublishOpenXrRecordedTextureUploads([.. uploads], source);
    }

    private static void Cancel(
        scoped in OpenXrEyeRecordWorkerResult result,
        VulkanCommandRuntime runtime,
        string reason)
    {
        if (result.RecordedUploads is { Length: > 0 } uploads)
            runtime.CancelOpenXrRecordedTextureUploads([.. uploads], reason);
    }

    public void Dispose()
    {
        _scheduler?.Dispose();
        _scheduler = null;
    }
}
