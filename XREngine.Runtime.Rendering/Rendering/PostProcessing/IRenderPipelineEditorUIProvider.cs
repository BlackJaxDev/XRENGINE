namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Pipeline-owned controls for the camera's Settings, Post Processing and Debug tabs.
/// </summary>
public interface IRenderPipelineEditorUIProvider
{
    /// <summary>
    /// Draws controls applicable to this pipeline. Camera-wide overrides must be labelled
    /// and must not be changed while inspecting a different, inactive target pipeline.
    /// </summary>
    void DrawCameraSettings(PipelineEditorContext context) { }

    /// <summary>
    /// Draws pipeline-wide image processing controls before the effect selector.
    /// </summary>
    void DrawPostProcessingHeader(PipelineEditorContext context) { }

    /// <summary>
    /// Draws supplementary image processing controls after the selected effect.
    /// </summary>
    void DrawPostProcessingFooter(PipelineEditorContext context) { }

    /// <summary>
    /// Draws pipeline-specific diagnostic controls in the camera Debug tab.
    /// </summary>
    void DrawDebug(PipelineEditorContext context) { }
}
