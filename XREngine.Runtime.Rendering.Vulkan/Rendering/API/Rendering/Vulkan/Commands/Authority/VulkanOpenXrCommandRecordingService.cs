using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-runtime owner for OpenXR recording-thread scopes. The renderer builds
/// immutable eye inputs, while this service installs only explicit output and
/// planner state on the thread which encodes them.
/// </summary>
internal sealed class VulkanOpenXrCommandRecordingService
{
    private VulkanCommandRuntime? _commandRuntime;
    private VulkanResourceRuntime? _resourceRuntime;
    private VulkanDeviceContext? _deviceContext;

    /// <summary>
    /// Binds stable command, resource, and device authorities. Output and planner
    /// services are deliberately absent because their frame-local observations
    /// arrive exclusively through frozen worker inputs.
    /// </summary>
    internal void Configure(
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        VulkanDeviceContext deviceContext)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        ArgumentNullException.ThrowIfNull(resourceRuntime);
        ArgumentNullException.ThrowIfNull(deviceContext);

        if (_commandRuntime is not null &&
            (!ReferenceEquals(_commandRuntime, commandRuntime) ||
             !ReferenceEquals(_resourceRuntime, resourceRuntime) ||
             !ReferenceEquals(_deviceContext, deviceContext)))
        {
            throw new InvalidOperationException(
                "The OpenXR command recording service cannot be rebound to different authorities.");
        }

        _commandRuntime = commandRuntime;
        _resourceRuntime = resourceRuntime;
        _deviceContext = deviceContext;
    }

    internal bool TryRecordPreparedEye(
        int workerIndex,
        in OpenXrPreparedEyeRecordWorkerInput prepared,
        out OpenXrRecordedEyeCommandBuffer recorded,
        out VulkanImportedTexturePendingUpload[] recordedUploads)
    {
        recorded = default;
        recordedUploads = [];

        VulkanCommandRuntime commandRuntime = _commandRuntime ??
            throw new InvalidOperationException("OpenXR command recording is not configured.");
        VulkanResourceRuntime resourceRuntime = _resourceRuntime ??
            throw new InvalidOperationException("OpenXR resource recording is not configured.");
        VulkanDeviceContext deviceContext = _deviceContext ??
            throw new InvalidOperationException("OpenXR device recording is not configured.");
        RenderOutputRequest outputContract = prepared.OutputContract;
        if (!prepared.IsValid)
        {
            if (outputContract.WorkClass == ERenderOutputWorkClass.PresentNow)
            {
                throw new VulkanPresentNowReadinessException(
                    outputContract.FrameId,
                    EVulkanPresentNowReadinessStage.FramePlanSeal,
                    $"openxr-eye-{prepared.OpenXrViewIndex}-frozen-input",
                    "OpenXREyeSubmit -> exact immutable worker input",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    "The foreground eye worker received an incomplete or stale frozen recording input.");
            }

            return false;
        }
        if (!deviceContext.IsOperational)
        {
            throw new InvalidOperationException(
                "OpenXR foreground recording cannot continue because the Vulkan device is not operational.");
        }

        List<VulkanImportedTexturePendingUpload> uploadBatch =
            resourceRuntime.Uploads.PublicationState.RecordedForSubmit;
        uploadBatch.Clear();
        long recordingStart = Stopwatch.GetTimestamp();
        try
        {
            ResourcePlannerRuntimeState plannerState = prepared.PlannerState;
            using VulkanPreparedResourcePlannerThreadScope plannerScope = new(
                commandRuntime.ThreadWorkspace.Current,
                commandRuntime,
                in plannerState);
            VulkanPreparedPrimaryCommandInput commandInput =
                prepared.CommandInput;
            VulkanPrimaryCommandRecordingResult result =
                commandRuntime.RecordPrimary(in commandInput);
            result = result with
            {
                ReadinessPolicy = outputContract.ReadinessPolicy,
                WorkClass = outputContract.WorkClass,
                SourceFrameId = commandInput.FramePlan.RenderFrameId,
            };
            if (outputContract.WorkClass == ERenderOutputWorkClass.PresentNow &&
                result.Disposition is not
                    EVulkanPrimaryCommandRecordingDisposition.Recorded and not
                    EVulkanPrimaryCommandRecordingDisposition.RecordedWithGpuFallback)
            {
                resourceRuntime.Uploads.CancelRecordedSubmitBatch(
                    deviceContext.State != EVulkanDeviceState.Healthy,
                    result.Reason ??
                        $"OpenXR eye worker {workerIndex} returned {result.Disposition}");
                TimeSpan elapsed = Stopwatch.GetElapsedTime(recordingStart);
                throw new VulkanPresentNowReadinessException(
                    result.SourceFrameId,
                    EVulkanPresentNowReadinessStage.PipelineCompilation,
                    $"openxr-eye-{prepared.OpenXrViewIndex}-primary",
                    "OpenXREyeSubmit -> newly recorded exact primary",
                    elapsed,
                    elapsed,
                    $"A foreground eye cannot complete as {result.Disposition}. " +
                    (result.Reason ?? "Primary recording produced no concrete failure detail."));
            }
            if (!result.Succeeded)
            {
                resourceRuntime.Uploads.CancelRecordedSubmitBatch(
                    deviceContext.State != EVulkanDeviceState.Healthy,
                    result.Reason ?? $"OpenXR eye worker {workerIndex} failed command recording");
                if (result.WorkClass == ERenderOutputWorkClass.PresentNow)
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(recordingStart);
                    throw new VulkanPresentNowReadinessException(
                        result.SourceFrameId,
                        EVulkanPresentNowReadinessStage.PipelineCompilation,
                        $"openxr-eye-{prepared.OpenXrViewIndex}-primary",
                        "OpenXREyeSubmit -> sealed primary pipeline/descriptor manifest",
                        elapsed,
                        elapsed,
                        $"XR deadline missed with no declared resident GPU fallback. " +
                        (result.Reason ?? "Primary recording failed."));
                }

                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.PrimaryRecordingDeferred.{prepared.OpenXrViewIndex}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Background eye recording deferred on worker {0}: {1}",
                    workerIndex,
                    result.Reason ?? "<no reason>");
                return false;
            }

            if (uploadBatch.Count != 0)
            {
                // Uploads are streaming/reload work rather than steady-state
                // frame work. Copy them because the source list is worker-local
                // and is reused by the next recording request on that thread.
                recordedUploads = [.. uploadBatch];
                uploadBatch.Clear();
            }

            recorded = new OpenXrRecordedEyeCommandBuffer(
                result.CommandBuffer,
                prepared.FrameContext,
                prepared.OpenXrViewIndex,
                prepared.OpenXrImageIndex,
                prepared.FrameDataSlotIndex,
                prepared.LogicalViewId,
                prepared.RequiredOutputIndex,
                outputContract,
                prepared.FrameOpsSignature,
                prepared.PlannerRevision,
                prepared.FrameOpContextId,
                prepared.ResourceGeneration,
                prepared.DescriptorGeneration,
                OwnedByOpenXrPrimaryCache: true);
            return true;
        }
        catch
        {
            resourceRuntime.Uploads.CancelRecordedSubmitBatch(
                deviceContext.State != EVulkanDeviceState.Healthy,
                $"OpenXR eye worker {workerIndex} command recording failed");
            throw;
        }
    }

    internal IDisposable EnterExternalSwapchainScope(
        VulkanOpenXrBackend backend,
        in VulkanOpenXrFrameContext frameContext)
        => new OpenXrExternalSwapchainRenderScope(backend, in frameContext);

    internal IDisposable EnterSynchronousUploadBlockScope(VulkanOpenXrBackend backend)
        => new SynchronousResourceUploadBlockScope(backend);

    internal VulkanOpenXrResourcePlannerThreadScope EnterPlannerScope(
        VulkanOpenXrResourcePlannerThreadData data,
        VulkanOpenXrViewResourcePlannerContextKey contextKey)
        => new(in data, in contextKey);

    internal VulkanOpenXrThreadRenderStateScope EnterThreadRenderStateScope(
        VulkanOpenXrThreadRenderStateData data,
        VulkanStateTracker state)
        => new(in data, state);
}
