using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Provides debug information related to a KHR device fault.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrDeviceFaultDebugInfo
    {
        public StructureType SType;
        public void* PNext;
        public uint VendorBinarySize;
        public void* PVendorBinaryData;
    }
}
