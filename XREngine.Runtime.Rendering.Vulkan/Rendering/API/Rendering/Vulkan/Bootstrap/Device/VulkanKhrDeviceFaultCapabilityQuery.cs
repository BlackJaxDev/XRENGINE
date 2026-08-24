namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanKhrDeviceFaultCapabilityQuery(
    bool DeviceFault,
    bool VendorBinary,
    bool ReportMasked,
    bool DeviceLostOnMasked,
    uint MaxReportCount);
