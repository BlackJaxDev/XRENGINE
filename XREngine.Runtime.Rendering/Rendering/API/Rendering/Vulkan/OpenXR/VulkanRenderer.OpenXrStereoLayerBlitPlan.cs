using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrStereoLayerBlitPlan(
        IVkImageDescriptorSource Source,
        Image SourceImage,
        Format SourceFormat,
        ImageAspectFlags SourceAspect,
        Extent2D LeftSourceExtent,
        ImageLayout LeftSourceOldLayout,
        Extent2D RightSourceExtent,
        ImageLayout RightSourceOldLayout,
        Image LeftDestinationImage,
        Format LeftDestinationFormat,
        Extent2D LeftDestinationExtent,
        Image RightDestinationImage,
        Format RightDestinationFormat,
        Extent2D RightDestinationExtent,
        bool FlipY);
}
