using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed record RenderGraphPlanResourceUse(
    string Name,
    ERenderPassResourceType ResourceType,
    ERenderGraphAccess Access,
    RenderGraphStageMask StageMask,
    RenderGraphAccessMask AccessMask,
    RenderGraphImageLayout? Layout,
    RenderGraphSubresourceRange SubresourceRange,
    int LogicalVersion,
    bool Imported,
    RenderGraphSyncState? ImportedInitialState);
