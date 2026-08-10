using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>Device-owned RTX IO capability and address policy.</summary>
internal sealed unsafe partial class VulkanDeviceContext
{
    internal bool SupportsNvMemoryDecompressionCommands
        => Capabilities.Supports(EVulkanDeviceCapability.NvMemoryDecompression)
            && MutableCapabilities._supportsNvMemoryDecompression
            && ExtensionFunctions.NvMemoryDecompression is not null;

    internal bool SupportsNvCopyMemoryIndirectCommands
        => Capabilities.Supports(EVulkanDeviceCapability.NvCopyMemoryIndirect)
            && MutableCapabilities._supportsNvCopyMemoryIndirect
            && ExtensionFunctions.NvCopyMemoryIndirect is not null;

    internal MemoryDecompressionMethodFlagsNV PreferredNvMemoryDecompressionMethod
    {
        get
        {
            ulong methods = (ulong)MutableCapabilities._nvMemoryDecompressionMethods;
            ulong firstMethod = methods & (~methods + 1UL);
            return (MemoryDecompressionMethodFlagsNV)firstMethod;
        }
    }

    internal ulong GetBufferDeviceAddress(Buffer buffer)
    {
        if (!Capabilities.Supports(EVulkanDeviceCapability.BufferDeviceAddress) || buffer.Handle == 0)
            return 0;

        BufferDeviceAddressInfo info = new()
        {
            SType = StructureType.BufferDeviceAddressInfo,
            Buffer = buffer,
        };
        return Api.GetBufferDeviceAddress(Device, &info);
    }
}
