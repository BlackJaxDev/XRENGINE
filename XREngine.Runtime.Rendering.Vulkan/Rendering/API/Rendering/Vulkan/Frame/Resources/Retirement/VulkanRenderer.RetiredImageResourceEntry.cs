namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        internal readonly record struct RetiredImageResourceEntry(
            RetiredImageResources Resources,
            VulkanRetirementTicket Ticket,
            ulong ImageGeneration,
            ulong PrimaryViewGeneration,
            ulong[] AttachmentViewGenerations,
            ulong SamplerGeneration);
    }
}
