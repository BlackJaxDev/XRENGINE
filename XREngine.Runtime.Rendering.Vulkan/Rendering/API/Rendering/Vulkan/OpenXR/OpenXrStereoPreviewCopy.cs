using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One eye preview texture that the stereo publish command buffer also fills from its
/// source array layer. A default value means that eye has no preview copy.
/// </summary>
internal readonly record struct OpenXrStereoPreviewCopy(
    IVkImageDescriptorSource? Destination,
    Image Image,
    Format Format,
    Extent2D Extent,
    ImageLayout OldLayout,
    ImageAspectFlags Aspect)
{
    /// <summary>True when the copy has a live destination image.</summary>
    public bool IsValid => Destination is not null && Image.Handle != 0;
}
