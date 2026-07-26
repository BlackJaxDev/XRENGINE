using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Final state exported by the graph for a logical resource version.</summary>
internal sealed record RenderGraphPlanOutputContract(
    string ResourceName,
    int LogicalVersion,
    RenderGraphSubresourceRange SubresourceRange,
    int LastUsePassIndex,
    RenderGraphSyncState FinalState,
    bool Imported);
