using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.NV;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the terminal device-loss settlement for the composed frame authority.
/// Native authorities publish typed loss observations through the device context;
/// this authority performs the cross-runtime transition exactly once.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Destructive, opt-in lifetime validation. The injection waits for a real
    /// resident native template so teardown exercises detached table ownership,
    /// frame-slot uses, and device-loss-aware dependency release together.
    /// </summary>
    private void InjectResidentTemplateDeviceLossIfRequested()
    {
        if (!_injectResidentTemplateDeviceLoss ||
            _resourceRuntime.ResidentDrawTemplates.ResidentCount == 0 ||
            Interlocked.Exchange(
                ref _residentTemplateDeviceLossInjected,
                1) != 0)
        {
            return;
        }

        throw CreateDeviceLostException(
            "ResidentTemplateLifetimeFaultInjection",
            Result.ErrorDeviceLost);
    }

    internal void MarkDeviceLost(string? reason, string? operation, Result result)
    {
        DeviceBootstrap.VulkanNativeDeviceFault? nativeFault =
            _deviceContext.FirstNativeDeviceFault;
        operation ??= nativeFault?.Operation ?? "<unknown>";
        if (nativeFault is not null && result == Result.ErrorDeviceLost)
            result = nativeFault.Result;
        reason ??= nativeFault is null
            ? null
            : $"{nativeFault.Operation} returned {nativeFault.Result}";

        bool firstObservation;
        lock (_commandRuntime.CommandBuffers.OneTimeSubmitGate)
        {
            lock (_telemetry._deviceLostTransitionLock)
            {
                _deviceContext.ObserveNativeResult(operation, result);
                _ = _deviceContext.TryBeginDeviceLossCollection();
                firstObservation = _deviceContext.TryClaimDeviceLossDiagnostics();
                if (firstObservation)
                {
                    CaptureFirstDeviceLossRecord(operation, result, reason);
                    _commandRuntime.Synchronization.FailAllSubmissionMarkers();
                    _resourceRuntime.Lifetime.Tracker.DeviceLost = true;

                    if (_commandRuntime.Synchronization._frameSlotTimelineValues is not null)
                        Array.Clear(_commandRuntime.Synchronization._frameSlotTimelineValues);
                    if (_outputRuntime.Desktop.ImageTimelineValues is not null)
                        Array.Clear(_outputRuntime.Desktop.ImageTimelineValues);
                    _commandRuntime.Synchronization._acquireTimelineValue = 0;
                    _commandRuntime.Synchronization._graphicsTimelineValue = 0;
                }
                else
                {
                    _deviceContext.DeviceFaultFacility.RecordDeviceLossFallout();
                }
            }
        }

        if (!firstObservation)
            return;

        _commandRuntime.AbandonRetiredSynchronousSubmissionsAfterDeviceLoss();
        _commandRuntime.AbandonAdvancedQueueOverlapResourcesAfterDeviceLoss();

        string deviceLostReason = BuildDeviceLostReason(reason);
        lock (_telemetry._deviceLostTransitionLock)
        {
            _deviceContext.DeviceFaultFacility.CompleteDeviceLoss(deviceLostReason);
            _deviceContext.CompleteDeviceLossCollection();
        }

        Debug.VulkanWarning(
            "[Vulkan] Logical device lost. Reason={0}. The current Vulkan renderer cannot submit more work; recreate the renderer/window to recover.",
            deviceLostReason);
        _outputRuntime.Capture.FailPendingScreenshotReadbacksForDeviceLoss(deviceLostReason);
    }

    internal InvalidOperationException CreateDeviceLostException(string operation, Result result)
    {
        DeviceBootstrap.VulkanNativeDeviceFault? nativeFault =
            _deviceContext.FirstNativeDeviceFault;
        MarkDeviceLost(
            nativeFault is null ? $"{operation} returned {result}" : null,
            nativeFault?.Operation ?? operation,
            nativeFault?.Result ?? result);
        return new InvalidOperationException(
            $"Vulkan device lost during {operation} ({result}). " +
            $"Reason={_deviceContext.DeviceFaultFacility.DeviceLostReason ?? "<unknown>"}. " +
            "The logical device is terminal and the renderer/window must be recreated before Vulkan can render again.");
    }

    private void CaptureFirstDeviceLossRecord(string operation, Result result, string? reason)
    {
        string? provisionalOperation = Volatile.Read(ref _telemetry._firstFailingVulkanApi);
        string resolvedOperation = !string.IsNullOrWhiteSpace(operation)
            ? operation
            : !string.IsNullOrWhiteSpace(provisionalOperation)
                ? provisionalOperation
                : "<unknown>";
        string resolvedReason = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason;
        Interlocked.Exchange(ref _telemetry._firstFailingVulkanApi, $"{resolvedOperation}:{result}");

        VulkanDeviceLossRecord record = new(
            resolvedOperation,
            result,
            resolvedReason,
            DateTimeOffset.UtcNow,
            _deviceContext.SnapshotSubmissionDiagnostics(),
            _resourceRuntime.Lifetime.Tracker.CaptureSnapshot(
                includeExactLiveResourceGenerations: true));
        _ = Interlocked.CompareExchange(ref _telemetry._firstDeviceLossRecord, record, null);
    }

    private string BuildDeviceLostReason(string? reason)
    {
        string baseReason = string.IsNullOrWhiteSpace(reason) ? "<unknown>" : reason.Trim();
        VulkanSubmissionDiagnosticContext submission = _deviceContext.SnapshotSubmissionDiagnostics();
        StringBuilder builder = new(baseReason);
        if (!submission.IsEmpty)
        {
            builder.Append("; LastSubmission kind=")
                .Append(submission.SubmissionKind ?? "<unknown>")
                .Append(" caller=")
                .Append(submission.Caller ?? "<unknown>")
                .Append(" queue=")
                .Append(submission.QueueKind ?? "<unknown>")
                .Append(" frame=")
                .Append(submission.FrameId)
                .Append(" commandBuffer=0x")
                .Append(submission.FirstCommandBufferHandle.ToString("X"));
        }

        AppendNvCheckpointDiagnostics(builder, in submission);

        if (_deviceContext.TryAppendPersistedDeviceFaultSummary(
            builder,
            _telemetry._diagnosticOptions))
        {
            builder.Append("; DeviceFaultCapture=persisted");
        }

        AppendDeviceAddressBindingDiagnostics(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Persists the bounded native address-bind callbacks from
    /// VK_EXT_device_address_binding_report during the single terminal
    /// diagnostic pass. Callback collection remains allocation-free; this is
    /// deliberately cold-path-only evidence for correlating a fault address.
    /// </summary>
    private void AppendDeviceAddressBindingDiagnostics(StringBuilder deviceLostReason)
    {
        if (!_telemetry._diagnosticOptions.RequestDeviceAddressBindingReport)
            return;

        try
        {
            VulkanValidationDeviceAddressBinding[] bindings =
                _deviceContext.ValidationDiagnostics.DrainDeviceAddressBindings(
                    out int overflowCount);
            StringBuilder artifact = new();
            artifact.Append("Vulkan Device Address Binding Events\n")
                .Append("Count=").Append(bindings.Length)
                .Append(" Overflow=").Append(overflowCount)
                .Append('\n');
            for (int index = 0; index < bindings.Length; index++)
            {
                VulkanValidationDeviceAddressBinding binding = bindings[index];
                artifact.Append("Binding[").Append(index).Append("] serial=")
                    .Append(binding.Serial)
                    .Append(" base=0x")
                    .Append(binding.BaseAddress.ToString("X"))
                    .Append(" size=").Append(binding.Size)
                    .Append(" type=").Append(binding.BindingType)
                    .Append(" flags=").Append(binding.Flags)
                    .Append('\n');
            }

            Debug.WriteAuxiliaryLog(
                "vulkan-device-address-bindings.log",
                artifact.ToString().TrimEnd());
            deviceLostReason.Append("; DeviceAddressBindings=")
                .Append(bindings.Length)
                .Append(" overflow=").Append(overflowCount)
                .Append(" artifact=vulkan-device-address-bindings.log");
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[VulkanDiag] Device-address binding artifact persistence failed: {0}:{1}.",
                exception.GetType().Name,
                exception.Message);
            deviceLostReason.Append("; DeviceAddressBindings=persistence-failed");
        }
    }

    /// <summary>
    /// Resolves only opaque checkpoint tokens published by this renderer. The driver-owned
    /// token is never dereferenced; pointer identity is matched against the append-only table.
    /// </summary>
    private unsafe void AppendNvCheckpointDiagnostics(
        StringBuilder deviceLostReason,
        in VulkanSubmissionDiagnosticContext submission)
    {
        if (!_telemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints ||
            !_deviceContext.SupportsNvDiagnosticCheckpoints ||
            _deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is not { } checkpoints)
        {
            return;
        }

        try
        {
            VulkanNvCheckpointMarker[] publishedMarkers =
                _telemetry.CaptureNvCheckpointMarkers(
                    out long publicationAttempts,
                    out long nativeCallCount,
                    out int markerCapacity,
                    out bool capacityExhausted);
            StringBuilder artifact = new();
            artifact.Append("Vulkan NV Diagnostic Checkpoints\n")
                .Append(" PublicationAttempts=").Append(publicationAttempts)
                .Append(" NativeCalls=").Append(nativeCallCount)
                .Append(" DistinctIdentities=").Append(publishedMarkers.Length)
                .Append(" Capacity=").Append(markerCapacity)
                .Append(" CapacityExhausted=").Append(capacityExhausted)
                .Append(" LastSubmissionQueue=0x").Append(submission.QueueHandle.ToString("X"))
                .Append('\n');
            uint capturedCount = AppendNvCheckpointQueueDiagnostics(
                checkpoints, _deviceContext.GraphicsQueue, "Graphics", artifact);
            if (_deviceContext.SecondaryGraphicsQueue.Handle != _deviceContext.GraphicsQueue.Handle)
                capturedCount += AppendNvCheckpointQueueDiagnostics(
                    checkpoints, _deviceContext.SecondaryGraphicsQueue, "SecondaryGraphics", artifact);
            if (_deviceContext.ComputeQueue.Handle != _deviceContext.GraphicsQueue.Handle &&
                _deviceContext.ComputeQueue.Handle != _deviceContext.SecondaryGraphicsQueue.Handle)
                capturedCount += AppendNvCheckpointQueueDiagnostics(
                    checkpoints, _deviceContext.ComputeQueue, "Compute", artifact);
            if (_deviceContext.TransferQueue.Handle != _deviceContext.GraphicsQueue.Handle &&
                _deviceContext.TransferQueue.Handle != _deviceContext.SecondaryGraphicsQueue.Handle &&
                _deviceContext.TransferQueue.Handle != _deviceContext.ComputeQueue.Handle)
                capturedCount += AppendNvCheckpointQueueDiagnostics(
                    checkpoints, _deviceContext.TransferQueue, "Transfer", artifact);

            artifact.Append("Published identities:\n");
            for (int index = 0; index < publishedMarkers.Length; index++)
            {
                VulkanNvCheckpointMarker marker = publishedMarkers[index];
                artifact.Append("Identity[").Append(index).Append("] serial=")
                    .Append(marker.Serial).Append(" op=").Append(marker.OpKind)
                    .Append(" program=").Append(marker.ProgramName ?? "<none>")
                    .Append(" phase=").Append(marker.Phase)
                    .Append(" pass=").Append(marker.PassIndex)
                    .Append(" batch=").Append(marker.BatchIndex)
                    .Append(" pipeline=").Append(marker.PipelineIdentity)
                    .Append(" viewport=").Append(marker.ViewportIdentity)
                    .Append('\n');
            }

            Debug.WriteAuxiliaryLog(
                "vulkan-nv-diagnostic-checkpoints.log",
                artifact.ToString().TrimEnd());
            deviceLostReason.Append("; NvCheckpoints=")
                .Append(capturedCount)
                .Append(" calls=").Append(nativeCallCount)
                .Append(" identities=").Append(publishedMarkers.Length)
                .Append(" artifact=vulkan-nv-diagnostic-checkpoints.log");
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[VulkanDiag] NV checkpoint artifact persistence failed: {0}:{1}.",
                exception.GetType().Name,
                exception.Message);
            deviceLostReason.Append("; NvCheckpoints=persistence-failed");
        }
    }

    private unsafe uint AppendNvCheckpointQueueDiagnostics(
        NVDeviceDiagnosticCheckpoints checkpoints,
        Queue queue,
        string queueName,
        StringBuilder artifact)
    {
        if (queue.Handle == 0)
            return 0;

        uint reportedCount = 0;
        checkpoints.GetQueueCheckpointData(queue, ref reportedCount, (CheckpointDataNV*)null);
        const uint maximumCheckpointResults = 64;
        uint requestedCount = Math.Min(reportedCount, maximumCheckpointResults);
        CheckpointDataNV* checkpointData = stackalloc CheckpointDataNV[(int)maximumCheckpointResults];
        for (int index = 0; index < requestedCount; index++)
            checkpointData[index] = new CheckpointDataNV { SType = StructureType.CheckpointDataNV };
        uint returnedCount = requestedCount;
        if (requestedCount != 0)
            checkpoints.GetQueueCheckpointData(queue, ref returnedCount, checkpointData);

        uint capturedCount = Math.Min(returnedCount, requestedCount);
        artifact.Append("Queue=").Append(queueName)
            .Append(" Handle=0x").Append(unchecked((ulong)queue.Handle).ToString("X"))
            .Append(" Reported=").Append(reportedCount)
            .Append(" Captured=").Append(capturedCount)
            .Append('\n');
        for (int index = 0; index < capturedCount; index++)
        {
            CheckpointDataNV data = checkpointData[index];
            artifact.Append("Checkpoint[").Append(queueName).Append(':').Append(index)
                .Append("] stage=").Append(data.Stage)
                .Append(" token=0x").Append(((nuint)data.PCheckpointMarker).ToString("X"));
            if (_telemetry.TryResolveNvCheckpointMarker(
                    data.PCheckpointMarker,
                    out VulkanNvCheckpointMarker marker))
            {
                artifact.Append(" staticIdentity serial=").Append(marker.Serial)
                    .Append(" firstRecordedFrame=").Append(marker.FirstRecordedFrameId)
                    .Append(" op=").Append(marker.OpKind)
                    .Append(" program=").Append(marker.ProgramName ?? "<none>")
                    .Append(" phase=").Append(marker.Phase)
                    .Append(" pass=").Append(marker.PassIndex)
                    .Append(" batch=").Append(marker.BatchIndex)
                    .Append(" pipeline=").Append(marker.PipelineIdentity)
                    .Append(" viewport=").Append(marker.ViewportIdentity)
                    .Append(" target=").Append(marker.OutputTargetName ?? "<none>")
                    .Append(" firstCommandBuffer=0x").Append(marker.FirstCommandBufferHandle.ToString("X"))
                    .Append(" firstRecordingGeneration=").Append(marker.FirstCommandBufferRecordingGeneration);
            }
            else
            {
                artifact.Append(" unresolved-token");
            }
            artifact.Append('\n');
        }
        return capturedCount;
    }
}
