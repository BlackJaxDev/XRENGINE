using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct RetiredCommandBuffer(
    CommandPool CommandPool,
    CommandBuffer CommandBuffer,
    VulkanRenderer.VulkanRetirementTicket Ticket);
