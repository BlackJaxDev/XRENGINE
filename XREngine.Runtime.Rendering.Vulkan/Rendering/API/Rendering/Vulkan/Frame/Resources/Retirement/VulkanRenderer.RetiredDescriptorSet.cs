using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal readonly record struct RetiredDescriptorSet(
        DescriptorPool DescriptorPool,
        DescriptorSet DescriptorSet,
        VulkanRetirementTicket Ticket);
}
