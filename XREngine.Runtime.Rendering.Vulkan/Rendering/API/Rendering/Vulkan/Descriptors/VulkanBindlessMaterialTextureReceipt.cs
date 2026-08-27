using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact immutable descriptor element leased by one accepted foreground frame.
/// </summary>
internal readonly record struct VulkanBindlessMaterialTextureReceipt(
    XRTexture Texture,
    long StreamingGeneration,
    uint DescriptorIndex,
    uint SlotGeneration,
    ulong WrapperDescriptorGeneration,
    ImageView ImageView,
    ulong ImageViewGeneration,
    Sampler Sampler,
    ulong SamplerGeneration,
    ImageLayout ImageLayout);
