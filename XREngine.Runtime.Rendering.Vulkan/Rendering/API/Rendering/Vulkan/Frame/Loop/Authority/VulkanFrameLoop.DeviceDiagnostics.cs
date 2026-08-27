using System;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private const ulong DeviceMemoryBudgetSampleIntervalFrames = 120UL;
    private ulong _lastDeviceMemoryBudgetSampleFrame;
    private bool _deviceMemoryBudgetSampleValid;
    private long _deviceLocalUsageBytes;
    private long _deviceLocalBudgetBytes;
    private long _largestDeviceLocalHeapBytes;
    private int _activeDeviceAllocationCount;

    private void PopulateDeviceDiagnosticTelemetry(
        ref VulkanFrameAttempt attempt)
    {
        RefreshDeviceMemoryBudgetSample(attempt.FrameNumber);
        bool hasSuccessfulSubmission =
            _telemetry.TryGetLastSuccessfulSubmissionBreadcrumb(
                out VulkanCrashBreadcrumb submission);
        TimeSpan longestNativeDriverCall =
            attempt.Timing.NativeQueueSubmit >= attempt.Timing.NativeQueuePresent
                ? attempt.Timing.NativeQueueSubmit
                : attempt.Timing.NativeQueuePresent;
        bool deviceLost = !_deviceContext.StateMachine.IsOperational ||
            _telemetry._firstDeviceLossRecord is not null;

        attempt.Timing.DeviceDiagnostics = new VulkanDeviceDiagnosticTelemetry(
            _deviceContext.IsOperational,
            deviceLost,
            _deviceContext.SupportsDeviceFault,
            _telemetry._diagnosticOptions.RequestDeviceFault &&
                _deviceContext.SupportsDeviceFault,
            _deviceContext.SupportsMemoryBudget,
            _deviceMemoryBudgetSampleValid,
            _deviceLocalUsageBytes,
            _deviceLocalBudgetBytes,
            _largestDeviceLocalHeapBytes,
            _activeDeviceAllocationCount,
            hasSuccessfulSubmission ? submission.Serial : 0UL,
            hasSuccessfulSubmission ? submission.FrameId : 0UL,
            hasSuccessfulSubmission ? submission.FrameSlot ?? -1 : -1,
            hasSuccessfulSubmission
                ? unchecked((int)(submission.SwapchainImageIndex ?? uint.MaxValue))
                : -1,
            hasSuccessfulSubmission ? submission.WaitTimelineValue : 0UL,
            hasSuccessfulSubmission ? submission.SignalTimelineValue : 0UL,
            hasSuccessfulSubmission ? submission.LastCommandMarkerSerial : 0UL,
            hasSuccessfulSubmission ? submission.LastCommandMarkerGeneration : 0UL,
            hasSuccessfulSubmission
                ? submission.DescriptorTableGeneration
                : _resourceRuntime.DescriptorTableGeneration,
            longestNativeDriverCall,
            longestNativeDriverCall >= TimeSpan.FromSeconds(1));
    }

    private void RefreshDeviceMemoryBudgetSample(ulong frameNumber)
    {
        if (_lastDeviceMemoryBudgetSampleFrame != 0 &&
            frameNumber - _lastDeviceMemoryBudgetSampleFrame <
                DeviceMemoryBudgetSampleIntervalFrames)
        {
            return;
        }

        _lastDeviceMemoryBudgetSampleFrame = frameNumber;
        try
        {
            _deviceMemoryBudgetSampleValid =
                _resourceRuntime.TryGetAllocatorBudgetSnapshot(
                    _api,
                    _deviceContext,
                    budgetRatio: 1.0,
                    reserveBytes: 0L,
                    out _deviceLocalUsageBytes,
                    out _deviceLocalBudgetBytes,
                    out _largestDeviceLocalHeapBytes,
                    out _activeDeviceAllocationCount);
        }
        catch (InvalidOperationException)
        {
            _deviceMemoryBudgetSampleValid = false;
            _deviceLocalUsageBytes = 0L;
            _deviceLocalBudgetBytes = 0L;
            _largestDeviceLocalHeapBytes = 0L;
            _activeDeviceAllocationCount = 0;
        }
    }
}
