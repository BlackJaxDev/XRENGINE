namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanOpenXrResourcePlannerThreadData(
    VulkanOpenXrResourcePlannerSessionToken Session,
    VulkanCommandThreadContext ThreadContext,
    VulkanCommandRuntime Owner);
