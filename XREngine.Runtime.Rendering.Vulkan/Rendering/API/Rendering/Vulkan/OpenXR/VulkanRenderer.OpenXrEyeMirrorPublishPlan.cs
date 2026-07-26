using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrEyeMirrorPublishPlan(
        IVkImageDescriptorSource Source,
        Image SourceImage,
        Format SourceFormat,
        Extent2D SourceExtent,
        ImageLayout SourceOldLayout,
        ImageAspectFlags SourceAspect,
        Image SwapchainImage,
        Format SwapchainFormat,
        Extent2D SwapchainExtent,
        IVkImageDescriptorSource? PreviewSource,
        Image PreviewImage,
        Extent2D PreviewExtent,
        ImageLayout PreviewOldLayout,
        ImageAspectFlags PreviewAspect,
        string DestinationLabel,
        bool FlipPreviewY);
}
