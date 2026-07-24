using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Identifies a render-graph pass together with the pipeline that declared it.
/// Nested pipelines may execute while a parent pass remains active, so the
/// numeric pass index alone is not sufficient to select matching metadata.
/// </summary>
internal readonly record struct RenderGraphPassScopeState(
    int PassIndex,
    XRRenderPipelineInstance? OwnerPipeline);
