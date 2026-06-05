using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanDepthClipControlExt
{
    public const string ExtensionName = "VK_EXT_depth_clip_control";

    public static readonly StructureType PhysicalDeviceFeaturesSType = (StructureType)1000355000;
    public static readonly StructureType PipelineViewportCreateInfoSType = (StructureType)1000355001;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PhysicalDeviceDepthClipControlFeaturesEXTNative
{
    public StructureType SType;
    public void* PNext;
    public Bool32 DepthClipControl;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipelineViewportDepthClipControlCreateInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public Bool32 NegativeOneToOne;
}
