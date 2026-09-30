using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private IRenderPipelineEditorUIProvider? _editorUIProvider;

    /// <inheritdoc />
    public override IRenderPipelineEditorUIProvider? EditorUIProvider
        => _editorUIProvider ??= PipelineEditorUiServices.Create(this);
}
