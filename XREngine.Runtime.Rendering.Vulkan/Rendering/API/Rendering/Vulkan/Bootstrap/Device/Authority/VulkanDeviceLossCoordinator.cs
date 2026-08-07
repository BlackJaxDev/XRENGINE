using System.Text;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinates the one terminal device-loss transition across device, command,
/// resource, output, and telemetry authorities.
/// </summary>
internal sealed class VulkanDeviceLossCoordinator(
    VulkanDeviceContext deviceContext,
    VulkanCommandRuntime commandRuntime,
    VulkanResourceRuntime resourceRuntime,
    VulkanOutputRuntime outputRuntime,
    VulkanFrameTelemetry telemetry)
{
    internal void MarkDeviceLost(
        string? reason,
        string? operation,
        Result result)
    {
        DeviceBootstrap.VulkanNativeDeviceFault? nativeFault =
            deviceContext.FirstNativeDeviceFault;
        operation ??= nativeFault?.Operation ?? "<unknown>";
        if (nativeFault is not null && result == Result.ErrorDeviceLost)
            result = nativeFault.Result;
        reason ??= nativeFault is null
            ? null
            : $"{nativeFault.Operation} returned {nativeFault.Result}";

        bool firstObservation;
        lock (commandRuntime.CommandBuffers.OneTimeSubmitGate)
        {
            lock (telemetry._deviceLostTransitionLock)
            {
                deviceContext.ObserveNativeResult(operation, result);
                _ = deviceContext.TryBeginDeviceLossCollection();
                firstObservation = deviceContext.TryClaimDeviceLossDiagnostics();
                if (firstObservation)
                {
                    CaptureFirstDeviceLossRecord(operation, result, reason);
                    commandRuntime.Synchronization.FailAllSubmissionMarkers();
                    resourceRuntime.Lifetime.Tracker.DeviceLost = true;

                    if (commandRuntime.Synchronization._frameSlotTimelineValues is not null)
                        Array.Clear(commandRuntime.Synchronization._frameSlotTimelineValues);
                    if (outputRuntime.Desktop.ImageTimelineValues is not null)
                        Array.Clear(outputRuntime.Desktop.ImageTimelineValues);
                    commandRuntime.Synchronization._acquireTimelineValue = 0;
                    commandRuntime.Synchronization._graphicsTimelineValue = 0;
                }
                else
                {
                    deviceContext.DeviceFaultFacility.RecordDeviceLossFallout();
                }
            }
        }

        if (!firstObservation)
            return;

        string deviceLostReason = BuildDeviceLostReason(reason);
        lock (telemetry._deviceLostTransitionLock)
        {
            deviceContext.DeviceFaultFacility.CompleteDeviceLoss(deviceLostReason);
            deviceContext.CompleteDeviceLossCollection();
        }

        Debug.VulkanWarning(
            "[Vulkan] Logical device lost. Reason={0}. The current Vulkan renderer cannot submit more work; recreate the renderer/window to recover.",
            deviceLostReason);
        outputRuntime.Capture.FailPendingScreenshotReadbacksForDeviceLoss(
            deviceLostReason);
    }

    internal InvalidOperationException CreateException(
        string operation,
        Result result)
    {
        MarkDeviceLost(
            $"{operation} returned {result}",
            operation,
            result);
        return new InvalidOperationException(
            $"Vulkan device lost during {operation} ({result}). " +
            $"Reason={deviceContext.DeviceFaultFacility.DeviceLostReason ?? "<unknown>"}. " +
            "The logical device is terminal and the renderer/window must be recreated before Vulkan can render again.");
    }

    private void CaptureFirstDeviceLossRecord(
        string operation,
        Result result,
        string? reason)
    {
        string? provisionalOperation =
            Volatile.Read(ref telemetry._firstFailingVulkanApi);
        string resolvedOperation = !string.IsNullOrWhiteSpace(operation)
            ? operation
            : !string.IsNullOrWhiteSpace(provisionalOperation)
                ? provisionalOperation
                : "<unknown>";
        string resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "<unknown>"
            : reason;
        Interlocked.Exchange(
            ref telemetry._firstFailingVulkanApi,
            $"{resolvedOperation}:{result}");

        VulkanDeviceLossRecord record = new(
            resolvedOperation,
            result,
            resolvedReason,
            DateTimeOffset.UtcNow,
            deviceContext.SnapshotSubmissionDiagnostics(),
            resourceRuntime.Lifetime.Tracker.CaptureSnapshot(
                includeExactLiveResourceGenerations: true));
        _ = Interlocked.CompareExchange(
            ref telemetry._firstDeviceLossRecord,
            record,
            null);
    }

    private string BuildDeviceLostReason(string? reason)
    {
        string baseReason = string.IsNullOrWhiteSpace(reason)
            ? "<unknown>"
            : reason.Trim();
        VulkanSubmissionDiagnosticContext submission =
            deviceContext.SnapshotSubmissionDiagnostics();
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