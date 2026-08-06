using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct OpenXrEyePreviewCopyRequest(
    Image SourceImage,
    Format SourceFormat,
    Extent2D SourceExtent,
    XRTexture2D? DestinationTexture,
    string DestinationLabel,
    bool FlipY);
