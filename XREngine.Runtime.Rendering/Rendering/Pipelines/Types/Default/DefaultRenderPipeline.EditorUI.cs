using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private static readonly DefaultPipelineEditorUI DefaultEditorUI = new();

    /// <inheritdoc />
    public override IRenderPipelineEditorUIProvider? EditorUIProvider => DefaultEditorUI;

    private sealed class DefaultPipelineEditorUI : IRenderPipelineEditorUIProvider
    {
        public void DrawCameraSettings(PipelineEditorContext context)
            => StandardPipelineEditorControls.DrawCameraSettings(context);

        public void DrawDebug(PipelineEditorContext context)
            => StandardPipelineEditorControls.DrawForwardPlusDebug(context);
    }
}
