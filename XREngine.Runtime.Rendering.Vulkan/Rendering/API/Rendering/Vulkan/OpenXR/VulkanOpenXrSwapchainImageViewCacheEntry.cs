using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanOpenXrSwapchainImageViewCacheEntry(
    ImageView View,
    Format Format);
