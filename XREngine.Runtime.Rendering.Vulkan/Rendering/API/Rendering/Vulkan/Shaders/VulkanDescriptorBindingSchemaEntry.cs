namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable resource-binding identity compiled from one reflected descriptor.
/// Resource content and descriptor topology deliberately remain separate
/// dependencies.
/// </summary>
internal readonly record struct VulkanDescriptorBindingSchemaEntry(
    DescriptorBindingInfo Reflection,
    EVulkanDescriptorOwner Owner,
    EVulkanDescriptorArrayPolicy ArrayPolicy,
    bool DependsOnTopologyGeneration,
    bool DependsOnContentGeneration)
{
    internal string ResourceIdentity => Reflection.Name;
}
