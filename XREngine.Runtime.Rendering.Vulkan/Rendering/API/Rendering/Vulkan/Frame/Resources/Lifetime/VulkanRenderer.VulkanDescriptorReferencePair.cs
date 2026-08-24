namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanDescriptorReferencePair(
    VulkanResourceLifetimeKey First,
    VulkanResourceLifetimeKey Second);
