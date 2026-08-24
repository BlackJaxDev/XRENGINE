namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanOpenXrThreadRenderStateData(
    VulkanCommandThreadContext ThreadContext,
    VulkanCommandRuntime Owner);
