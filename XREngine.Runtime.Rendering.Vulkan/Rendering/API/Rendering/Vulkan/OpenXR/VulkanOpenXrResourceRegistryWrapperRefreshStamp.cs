namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanOpenXrResourceRegistryWrapperRefreshStamp(
    int InstanceRevision,
    int DescriptorRevision,
    ulong ResourcePlannerRevision,
    int ResourceAllocatorIdentity);
