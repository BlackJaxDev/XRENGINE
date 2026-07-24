using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrEyeMirrorPublishRequest(
        XRTexture2D? SourceTexture,
        Image SwapchainImage,
        Format SwapchainFormat,
        Extent2D Extent,
        XRTexture2D? PreviewTexture,
        string DestinationLabel,
        bool FlipPreviewY);
}
