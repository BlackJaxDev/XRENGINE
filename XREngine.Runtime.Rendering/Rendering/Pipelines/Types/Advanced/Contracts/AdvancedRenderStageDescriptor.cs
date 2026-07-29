using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral identity and primary execution domain for one advanced frame stage.
/// </summary>
public readonly record struct AdvancedRenderStageDescriptor(
    EAdvancedRenderStage Stage,
    string PassName,
    string GpuLabel,
    ERenderGraphPassStage RenderGraphStage);
