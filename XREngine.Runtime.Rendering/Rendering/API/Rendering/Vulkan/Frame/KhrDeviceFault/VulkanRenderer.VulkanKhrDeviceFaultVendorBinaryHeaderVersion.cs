namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Specifies the version of the vendor binary header for KHR device fault reporting.
    /// </summary>
    private enum VulkanKhrDeviceFaultVendorBinaryHeaderVersion : int
    {
        One = 1,
    }
}
