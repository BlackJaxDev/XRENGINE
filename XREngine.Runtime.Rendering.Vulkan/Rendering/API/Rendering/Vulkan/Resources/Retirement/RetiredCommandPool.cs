using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct RetiredCommandPool(
    CommandPool CommandPool,
    VulkanRetirementTicket Ticket);
