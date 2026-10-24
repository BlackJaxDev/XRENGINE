namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one native set payload at the bind command.</summary>
internal readonly record struct VulkanWorldUiDescriptorSetWitness(
    uint Index, ulong Handle, ulong LifetimeGeneration,
    ulong PayloadGeneration,
    VulkanPublishedDescriptorSetSnapshot? Publication,
    int OffsetStart, int OffsetCount);
