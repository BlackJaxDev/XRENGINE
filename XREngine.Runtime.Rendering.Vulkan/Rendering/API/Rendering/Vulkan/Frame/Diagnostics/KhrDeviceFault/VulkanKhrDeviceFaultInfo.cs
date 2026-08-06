using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides detailed information about a KHR device fault.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VulkanKhrDeviceFaultInfo
{
    public StructureType SType;
    public void* PNext;
    public VulkanKhrDeviceFaultFlags Flags;
    public ulong GroupId;
    public fixed byte Description[VulkanKhrDeviceFaultNativeConstants.DescriptionBytes];
    public VulkanKhrDeviceFaultAddressInfo FaultAddressInfo;
    public VulkanKhrDeviceFaultAddressInfo InstructionAddressInfo;
    public VulkanKhrDeviceFaultVendorInfo VendorInfo;
}
