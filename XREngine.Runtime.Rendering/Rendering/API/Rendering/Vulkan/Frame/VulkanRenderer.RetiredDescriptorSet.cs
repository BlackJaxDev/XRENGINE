using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct RetiredDescriptorSet(
            DescriptorPool DescriptorPool,
            DescriptorSet DescriptorSet,
            VulkanRetirementTicket Ticket);
    }
}
