using System;
using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Context passed to pipeline post-processing UI extension methods when drawing in the Camera Component editor.
/// </summary>
public sealed record PipelineEditorContext(
    XRCamera Camera,
    CameraComponent? Component,
    RenderPipeline? ActiveViewportPipeline,
    RenderPipeline SelectedPipeline,
    PipelinePostProcessState? State,
    RenderPipelinePostProcessSchema Schema);

/// <summary>
/// Interface implemented by render pipelines or dedicated post-processing UI providers
/// to render custom header and footer controls in the Camera Component post-processing inspector.
/// </summary>
public interface IRenderPipelinePostProcessUIProvider
{
    /// <summary>
    /// Invoked before standard post-processing stage selection.
    /// Useful for pipeline-level debug views (e.g. ShadingDebugView) or pipeline-wide overrides.
    /// </summary>
    void DrawPipelineHeader(PipelineEditorContext context);

    /// <summary>
    /// Invoked after standard post-processing stages have been rendered.
    /// Useful for pipeline diagnostics, timing breakdowns, or supplementary information.
    /// </summary>
    void DrawPipelineFooter(PipelineEditorContext context);
}
