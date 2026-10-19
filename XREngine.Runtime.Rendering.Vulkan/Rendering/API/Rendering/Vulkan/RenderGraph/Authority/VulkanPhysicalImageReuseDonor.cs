namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one published planner owner whose internal images can be considered for reuse.</summary>
internal readonly record struct VulkanPhysicalImageReuseDonor(
    VulkanFrameOpPlannerStateKey Key,
    ResourcePlannerRuntimeState State);
