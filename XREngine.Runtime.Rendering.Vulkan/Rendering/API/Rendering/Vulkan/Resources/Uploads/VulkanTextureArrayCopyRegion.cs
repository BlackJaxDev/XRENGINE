using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>One exact source generation and mip copied into a destination array layer.</summary>
internal readonly record struct VulkanTextureArrayCopyRegion(
    Image Source,
    ulong SourceGeneration,
    ImageLayout SourceLayout,
    uint MipLevel,
    uint DestinationLayer,
    Extent3D Extent);
