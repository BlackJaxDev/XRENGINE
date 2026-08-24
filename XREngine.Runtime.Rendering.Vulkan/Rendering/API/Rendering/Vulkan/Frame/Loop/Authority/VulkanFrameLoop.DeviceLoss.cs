using System.Text;
using System.Threading;
using Silk.NET.Vulkan;

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
        if (submission.IsEmpty)
            return baseReason;

        StringBuilder builder = new(baseReason);
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
        return builder.ToString();
    }
}
