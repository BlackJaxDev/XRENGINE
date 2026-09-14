using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Native ABI definition from Vulkan-Headers 1.4.350 for capability reporting.
/// Command recording remains deliberately deferred.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PhysicalDeviceDeviceAddressCommandsFeaturesKHRNative
{
    internal const StructureType StructureType = (StructureType)1000318006;

    public StructureType SType;
    public void* PNext;
    public Bool32 DeviceAddressCommands;
}
