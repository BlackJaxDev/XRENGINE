using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Pipeline Retirement ===========

        /// <summary>
        /// Per-frame-slot retirement queue for pipelines whose handles may still
        /// be referenced by command buffers recorded earlier in the same frame or
        /// by previously submitted frame slots.
        /// </summary>
        private readonly record struct RetiredPipeline(
            Pipeline Pipeline,
            VulkanRetirementTicket Ticket);
    }
}
