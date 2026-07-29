using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        internal readonly record struct RetiredDescriptorSet(
            DescriptorPool DescriptorPool,
            DescriptorSet DescriptorSet,
            VulkanRetirementTicket Ticket);
    }
}
