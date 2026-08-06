using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact published descriptor-set payload selected for a recorded command packet.</summary>
internal readonly record struct VulkanRecordedDescriptorSetIdentity(
    uint SetIndex,
    ulong DescriptorSetHandle,
    ulong DescriptorSetLifetimeGeneration,
    ulong PayloadGeneration,
    ulong PublicationGeneration,
    VulkanRecordedDescriptorResourceIdentityBuffer Resources)
{
    public bool IsComplete => DescriptorSetHandle != 0UL &&
        DescriptorSetLifetimeGeneration != 0UL && PayloadGeneration != 0UL &&
        PublicationGeneration != 0UL && Resources.IsComplete;
}

/// <summary>One concrete native resource referenced by a descriptor payload.</summary>
internal readonly record struct VulkanRecordedDescriptorResourceIdentity(
    ObjectType Type,
    ulong Handle,
    ulong Generation,
    ImageLayout Layout)
{
    public bool IsComplete => Handle != 0UL && Generation != 0UL;
}
