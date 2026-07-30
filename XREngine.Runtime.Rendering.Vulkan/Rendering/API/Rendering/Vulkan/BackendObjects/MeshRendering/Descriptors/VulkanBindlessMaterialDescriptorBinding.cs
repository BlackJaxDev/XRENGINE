namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanBindlessMaterialDescriptorBinding(
    VkRenderProgram Program,
    string Consumer);
