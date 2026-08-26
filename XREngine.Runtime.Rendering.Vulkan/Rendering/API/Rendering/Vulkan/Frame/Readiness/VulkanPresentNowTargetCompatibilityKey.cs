using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Swapchain properties needed for format-compatible resource and pipeline
/// readiness without owning a WSI image.
/// </summary>
internal readonly record struct VulkanPresentNowTargetCompatibilityKey(
    ulong OutputGeneration,
    Format ColorFormat,
    Format DepthFormat,
    Extent2D Extent,
    bool DynamicRendering,
    bool StreamlineFrameGeneration);
