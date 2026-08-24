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
    private readonly VulkanRecordedDescriptorResourceIdentityBuffer _resources =
        Resources;

    public VulkanRecordedDescriptorResourceIdentityBuffer Resources
    {
        get => _resources;
        init => _resources = value;
    }

    internal static ref readonly VulkanRecordedDescriptorResourceIdentityBuffer
        GetResourcesReference(in VulkanRecordedDescriptorSetIdentity identity)
        => ref identity._resources;

    public bool IsComplete
    {
        get
        {
            ref readonly VulkanRecordedDescriptorResourceIdentityBuffer resources =
                ref GetResourcesReference(in this);
            return DescriptorSetHandle != 0UL &&
                DescriptorSetLifetimeGeneration != 0UL && PayloadGeneration != 0UL &&
                PublicationGeneration != 0UL && resources.IsComplete;
        }
    }

    internal bool Matches(in VulkanRecordedDescriptorSetIdentity other)
    {
        ref readonly VulkanRecordedDescriptorResourceIdentityBuffer resources =
            ref GetResourcesReference(in this);
        ref readonly VulkanRecordedDescriptorResourceIdentityBuffer otherResources =
            ref GetResourcesReference(in other);
        return SetIndex == other.SetIndex &&
            DescriptorSetHandle == other.DescriptorSetHandle &&
            DescriptorSetLifetimeGeneration == other.DescriptorSetLifetimeGeneration &&
            PayloadGeneration == other.PayloadGeneration &&
            PublicationGeneration == other.PublicationGeneration &&
            resources.Equals(in otherResources);
    }
}
