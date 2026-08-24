using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct ResourcePlanningInputs(
    IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
    VulkanCompiledRenderGraph CompiledGraph,
    VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
    ResourcePlannerFastPathKey FastPathKey);
