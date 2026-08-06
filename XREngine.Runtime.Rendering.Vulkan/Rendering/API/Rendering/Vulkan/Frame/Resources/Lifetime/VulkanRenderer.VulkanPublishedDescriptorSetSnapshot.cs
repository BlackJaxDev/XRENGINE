namespace XREngine.Rendering.Vulkan;

internal sealed record VulkanPublishedDescriptorSetSnapshot(
    ulong Generation,
    VulkanResourceLifetimeKey[] References,
    VulkanPublishedDescriptorImageReference[] ImageReferences,
    uint[] ReflectedImageBindings,
    bool HasReflection);
