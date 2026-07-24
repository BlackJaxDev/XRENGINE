using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed record RenderGraphPlanEdge(
    int ProducerPassIndex,
    int ConsumerPassIndex,
    string ResourceName,
    ERenderPassResourceType ResourceType,
    int ResourceVersion,
    RenderGraphSubresourceRange SubresourceRange,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState,
    bool DependencyOnly);
