using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides information about the address involved in a KHR device fault.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VulkanKhrDeviceFaultAddressInfo
{
    public VulkanKhrDeviceFaultAddressType AddressType;
    public ulong ReportedAddress;
    public ulong AddressPrecision;
}
