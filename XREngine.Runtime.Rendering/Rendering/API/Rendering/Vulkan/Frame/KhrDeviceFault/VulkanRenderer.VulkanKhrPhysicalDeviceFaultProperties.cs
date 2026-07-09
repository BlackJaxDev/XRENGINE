using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Specifies the properties related to KHR device fault reporting supported by the physical device.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrPhysicalDeviceFaultProperties
    {
        public StructureType SType;
        public void* PNext;
        public uint MaxDeviceFaultCount;
    }
}
