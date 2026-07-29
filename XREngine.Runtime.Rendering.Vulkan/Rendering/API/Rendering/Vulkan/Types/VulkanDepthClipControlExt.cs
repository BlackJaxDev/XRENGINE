using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanDepthClipControlExt
{
    public const string ExtensionName = "VK_EXT_depth_clip_control";

    public static readonly StructureType PhysicalDeviceFeaturesSType = (StructureType)1000355000;
    public static readonly StructureType PipelineViewportCreateInfoSType = (StructureType)1000355001;
}
