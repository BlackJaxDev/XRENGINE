using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

    /// <summary>
    /// Identifies one independently tracked Vulkan image aspect, mip level, and
    /// array layer.
    /// </summary>
    /// <param name="ImageHandle">The native Vulkan image handle.</param>
    /// <param name="MipLevel">The mip level within the image.</param>
    /// <param name="ArrayLayer">The array layer within the image.</param>
    /// <param name="Aspect">The single image aspect represented by the key.</param>
internal readonly record struct VulkanTrackedImageSubresource(
    ulong ImageHandle,
    uint MipLevel,
    uint ArrayLayer,
    ImageAspectFlags Aspect);
