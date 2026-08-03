using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Provides information about the address involved in a KHR device fault.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrDeviceFaultAddressInfo
    {
        public VulkanKhrDeviceFaultAddressType AddressType;
        public ulong ReportedAddress;
        public ulong AddressPrecision;
    }
}
