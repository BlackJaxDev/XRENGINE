using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>A published Vulkan plan and the diagnostics snapshot built from it.</summary>
internal sealed record VulkanPlanDiagnosticsEntry(VulkanRenderGraphPlan Plan, RenderGraphPlanSnapshot Snapshot);
