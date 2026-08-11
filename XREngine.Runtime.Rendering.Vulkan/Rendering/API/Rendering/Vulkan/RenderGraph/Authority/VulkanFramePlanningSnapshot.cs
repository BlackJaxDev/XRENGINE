namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>Immutable planning values safe to pass from planning into command scheduling.</summary>
internal readonly record struct VulkanFramePlanningSnapshot(
    VulkanRenderGraphPlan RenderGraphPlan,
    ulong FrozenResourcePlanRevision,
    bool IsResourcePlanFrozen);
