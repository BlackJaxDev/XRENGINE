using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Native ABI definition from Vulkan-Headers 1.4.350 for an optional extension
/// not yet represented by the pinned Silk.NET package.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PhysicalDeviceUnifiedImageLayoutsFeaturesKHRNative
{
    internal const StructureType StructureType = (StructureType)1000527000;

    public StructureType SType;
    public void* PNext;
    public Bool32 UnifiedImageLayouts;
    public Bool32 UnifiedImageLayoutsVideo;
}
