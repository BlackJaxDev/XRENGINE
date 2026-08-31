namespace XREngine.Rendering.Vulkan;

/// <summary>Native descriptor receipt for one directional shadow-atlas consumer binding.</summary>
public readonly record struct VulkanShadowAtlasConsumerReceipt(
    string BindingName,
    ulong ImageHandle,
    ulong ImageGeneration,
    ulong ImageViewGeneration,
    ulong ImageViewHandle,
    uint BaseMipLevel,
    uint LevelCount,
    uint BaseArrayLayer,
    uint LayerCount);
