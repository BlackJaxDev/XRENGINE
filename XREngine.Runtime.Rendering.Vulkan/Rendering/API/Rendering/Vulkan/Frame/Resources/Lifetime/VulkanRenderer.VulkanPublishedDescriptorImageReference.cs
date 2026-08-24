namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanPublishedDescriptorImageReference(
    uint Binding,
    uint Element,
    VulkanDescriptorImageReference Reference);
