namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct RetiredImageResourceEntry(
            RetiredImageResources Resources,
            VulkanRetirementTicket Ticket,
            ulong ImageGeneration,
            ulong SamplerGeneration);
    }
}
