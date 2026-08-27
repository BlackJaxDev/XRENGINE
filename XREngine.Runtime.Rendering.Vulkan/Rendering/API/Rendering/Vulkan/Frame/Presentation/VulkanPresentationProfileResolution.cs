using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Native present-mode selection paired with its immutable policy identity.</summary>
internal readonly record struct VulkanPresentationProfileResolution(
    PresentModeKHR NativePresentMode,
    VulkanPresentationProfileSnapshot Snapshot);
