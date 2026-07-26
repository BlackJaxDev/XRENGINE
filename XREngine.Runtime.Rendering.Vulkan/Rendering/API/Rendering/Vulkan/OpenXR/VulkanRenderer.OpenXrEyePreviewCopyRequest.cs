using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrEyePreviewCopyRequest(
        Image SourceImage,
        Format SourceFormat,
        Extent2D SourceExtent,
        XRTexture2D? DestinationTexture,
        string DestinationLabel,
        bool FlipY);
}
