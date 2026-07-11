namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanUpscaleBridgeProbeResult
{
    public bool ProbeSucceeded { get; init; }
    public bool HasVulkanExternalMemoryImport { get; init; }
    public bool HasVulkanExternalSemaphoreImport { get; init; }
    public string? SelectedDeviceName { get; init; }
    public uint SelectedVendorId { get; init; }
    public uint SelectedDeviceId { get; init; }
    public bool? SamePhysicalGpu { get; init; }
    public string? GpuIdentityReason { get; init; }
    public string? ProbeFailureReason { get; init; }
}
