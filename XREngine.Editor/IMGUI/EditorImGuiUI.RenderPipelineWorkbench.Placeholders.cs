using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool IsWorkbenchPipelineEditable(RenderPipeline pipeline)
        => pipeline is CustomRenderPipeline;
}
