using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents the version one header of the vendor binary for KHR device fault reporting.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne
    {
        public uint HeaderSize;
        public VulkanKhrDeviceFaultVendorBinaryHeaderVersion HeaderVersion;
        public uint VendorID;
        public uint DeviceID;
        public uint DriverVersion;
        public fixed byte PipelineCacheUUID[16];
        public uint ApplicationNameOffset;
        public uint ApplicationVersion;
        public uint EngineNameOffset;
        public uint EngineVersion;
        public uint ApiVersion;
    }
}
