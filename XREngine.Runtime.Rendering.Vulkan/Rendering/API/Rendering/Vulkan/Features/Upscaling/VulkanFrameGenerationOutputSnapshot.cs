using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable desktop output facts needed to configure one DLSS-G session.</summary>
internal readonly record struct VulkanFrameGenerationOutputSnapshot(
    Extent2D Extent,
    uint ImageCount,
    Format ImageFormat);
