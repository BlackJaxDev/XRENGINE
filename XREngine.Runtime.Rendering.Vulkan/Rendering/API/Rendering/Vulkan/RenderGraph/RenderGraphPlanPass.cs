using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal sealed record RenderGraphPlanPass(
    int PassIndex,
    int Order,
    string Name,
    ERenderGraphPassStage Stage,
    bool RequiresPipelineReady,
    string AttachmentSignature,
    ReadOnlyCollection<RenderGraphPlanResourceUse> Resources);
