using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Descriptor Pool Retirement ===========

        /// <summary>
        /// Per-frame-slot retirement queue for descriptor pools whose descriptor
        /// sets may still be referenced by previously recorded command buffers.
        /// </summary>
        private readonly record struct RetiredDescriptorPool(
            DescriptorPool DescriptorPool,
            VulkanRetirementTicket Ticket);
    }
}
