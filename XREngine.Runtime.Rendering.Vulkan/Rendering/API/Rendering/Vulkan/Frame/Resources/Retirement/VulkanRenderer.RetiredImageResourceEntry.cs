namespace XREngine.Rendering.Vulkan
{
    internal readonly record struct RetiredImageResourceEntry(
        RetiredImageResources Resources,
        VulkanRetirementTicket Ticket,
        ulong ImageGeneration,
        ulong PrimaryViewGeneration,
        ulong[] AttachmentViewGenerations,
        ulong SamplerGeneration);
}
