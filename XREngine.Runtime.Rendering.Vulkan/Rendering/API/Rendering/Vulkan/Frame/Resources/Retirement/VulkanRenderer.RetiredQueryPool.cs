using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal readonly record struct RetiredQueryPool(
        QueryPool QueryPool,
        VulkanRetirementTicket Ticket);
}
