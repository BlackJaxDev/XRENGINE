using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrEyePreviewCopyPlan(
        Image SourceImage,
        Format SourceFormat,
        Extent2D SourceExtent,
        ImageLayout SourceOldLayout,
        IVkImageDescriptorSource DestinationSource,
        Image DestinationImage,
        Extent2D DestinationExtent,
        ImageLayout DestinationOldLayout,
        ImageAspectFlags DestinationAspect,
        string DestinationLabel,
        bool FlipY);
}
