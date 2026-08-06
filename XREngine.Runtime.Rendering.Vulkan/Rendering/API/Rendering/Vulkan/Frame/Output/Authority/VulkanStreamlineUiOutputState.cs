using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns the DLSS-G UI output images for one desktop-output generation.</summary>
internal sealed class VulkanStreamlineUiOutputState
{
    internal Image[]? Images;
    internal DeviceMemory[]? ImageMemories;
    internal ImageView[]? ImageViews;
    internal bool[]? ImagesInitialized;
}
