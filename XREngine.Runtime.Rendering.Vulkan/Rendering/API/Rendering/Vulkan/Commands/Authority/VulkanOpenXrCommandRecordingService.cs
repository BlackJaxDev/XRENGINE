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
        if (!deviceContext.IsOperational || !prepared.IsValid)
            return false;

        List<VulkanImportedTexturePendingUpload> uploadBatch =
            resourceRuntime.Uploads.PublicationState.RecordedForSubmit;
        uploadBatch.Clear();
        try
        {
            VulkanPreparedPrimaryCommandInput commandInput =
                prepared.CommandInput;
            VulkanPrimaryCommandRecordingResult result =
                commandRuntime.RecordPrimary(in commandInput);
            if (!result.Succeeded)
            {
                resourceRuntime.Uploads.CancelRecordedSubmitBatch(
                    deviceContext.State != EVulkanDeviceState.Healthy,
                    result.Reason ?? $"OpenXR eye worker {workerIndex} deferred command recording");
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
