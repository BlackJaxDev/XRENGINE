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
        VulkanCommandRuntime runtime = _runtime ?? throw new InvalidOperationException("OpenXR eye workers are not configured.");
        VulkanOpenXrCommandRecordingService recording = _recording ?? throw new InvalidOperationException("OpenXR eye recording is not configured.");
        VulkanDeviceContext device = _device ?? throw new InvalidOperationException("OpenXR eye workers have no device authority.");
        if (!device.IsOperational)
            return new(default, false, false);

        OpenXrEyeRecordWorkerBatchResult batch =
            (_scheduler ??= new OpenXrEyeRecordWorkerScheduler())
            .Record(recording, leftEye, rightEye);
        OpenXrEyeRecordWorkerResult leftResult = batch.Left;
        OpenXrEyeRecordWorkerResult rightResult = batch.Right;
        if (!batch.Left.Success || !batch.Right.Success)
        {
            Cancel(in leftResult, runtime, "OpenXR eye worker command recording failed");
            Cancel(in rightResult, runtime, "OpenXR eye worker command recording failed");
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

        return new(batch, submission.Succeeded, submission.CommandBuffersCompleted);
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

/// <summary>Outcome of the renderer-free OpenXR stereo command transaction.</summary>
internal readonly record struct VulkanOpenXrEyeWorkerCommandResult(
    OpenXrEyeRecordWorkerBatchResult Batch,
    bool Submitted,
    bool CommandBuffersCompleted);
