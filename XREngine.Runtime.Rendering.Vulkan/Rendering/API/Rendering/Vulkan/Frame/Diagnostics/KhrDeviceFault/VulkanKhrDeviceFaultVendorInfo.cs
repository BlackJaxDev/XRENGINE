using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides information about the vendor-specific details of a KHR device fault.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VulkanKhrDeviceFaultVendorInfo
{
    public fixed byte Description[VulkanKhrDeviceFaultNativeConstants.DescriptionBytes];
    public ulong VendorFaultCode;
    public ulong VendorFaultData;
}
