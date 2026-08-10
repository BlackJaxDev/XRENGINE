namespace XREngine.Rendering.Vulkan;

internal sealed record VulkanPublishedDescriptorSetSnapshot(
    ulong Generation,
    ulong ImagePayloadGeneration,
    VulkanResourceLifetimeKey[] References,
    VulkanPublishedDescriptorImageReference[] ImageReferences,
    uint[] ReflectedImageBindings,
    bool HasReflection);
