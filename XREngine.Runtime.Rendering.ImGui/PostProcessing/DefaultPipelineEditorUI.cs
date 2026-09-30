namespace XREngine.Rendering.PostProcessing;

/// <summary>Provides the standard pipeline's camera and debugging controls.</summary>
internal sealed class DefaultPipelineEditorUI : IRenderPipelineEditorUIProvider
{
    public void DrawCameraSettings(PipelineEditorContext context) => StandardPipelineEditorControls.DrawCameraSettings(context);
    public void DrawDebug(PipelineEditorContext context) => StandardPipelineEditorControls.DrawForwardPlusDebug(context);
}
