using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct RetiredCommandBuffer(
            CommandPool CommandPool,
            CommandBuffer CommandBuffer,
            VulkanRetirementTicket Ticket);
    }
}
