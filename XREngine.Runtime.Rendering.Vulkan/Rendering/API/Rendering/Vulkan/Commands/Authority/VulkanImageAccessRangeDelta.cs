using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImageAccessRangeDelta(
    ulong ImageHandle,
    ImageSubresourceRange Range,
    VulkanImageAccessState State);
