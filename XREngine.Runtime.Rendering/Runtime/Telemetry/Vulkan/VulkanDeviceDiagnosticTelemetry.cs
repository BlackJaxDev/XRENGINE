using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free device and last-successful-submission facts correlated with
/// one Vulkan frame publication. Native driver calls exceeding one second are
/// exposed as a TDR-risk signal, not as a device-hang diagnosis.
/// </summary>
public readonly record struct VulkanDeviceDiagnosticTelemetry(
    bool DeviceOperational,
    bool DeviceLost,
    bool DeviceFaultSupported,
    bool DeviceFaultCaptureActive,
    bool MemoryBudgetSupported,
    bool MemoryBudgetSampleValid,
    long DeviceLocalUsageBytes,
    long DeviceLocalBudgetBytes,
    long LargestDeviceLocalHeapBytes,
    int ActiveAllocationCount,
    ulong LastSuccessfulSubmissionSerial,
    ulong LastSuccessfulSubmissionFrameId,
    int LastSuccessfulSubmissionFrameSlot,
    int LastSuccessfulSubmissionImageIndex,
    ulong LastSuccessfulWaitTimelineValue,
    ulong LastSuccessfulSignalTimelineValue,
    ulong LastCommandMarkerSerial,
    ulong LastCommandMarkerGeneration,
    ulong DescriptorTableGeneration,
    TimeSpan LongestNativeDriverCall,
    bool NativeDriverCallExceededOneSecond);
