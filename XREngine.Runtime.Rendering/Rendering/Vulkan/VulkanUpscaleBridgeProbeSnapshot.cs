namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanUpscaleBridgeProbeSnapshot(
    bool ProbeSucceeded,
    bool HasVulkanExternalMemoryImport,
    bool HasVulkanExternalSemaphoreImport,
    string? SelectedDeviceName,
    uint SelectedVendorId,
    uint SelectedDeviceId,
    bool? SamePhysicalGpu,
    string? GpuIdentityReason,
    string? ProbeFailureReason);
