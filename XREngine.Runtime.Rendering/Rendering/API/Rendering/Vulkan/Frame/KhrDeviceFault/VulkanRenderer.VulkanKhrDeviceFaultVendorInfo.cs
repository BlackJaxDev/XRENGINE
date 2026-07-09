using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Provides information about the vendor-specific details of a KHR device fault.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VulkanKhrDeviceFaultVendorInfo
    {
        public fixed byte Description[VulkanDeviceFaultDescriptionBytes];
        public ulong VendorFaultCode;
        public ulong VendorFaultData;
    }
}
