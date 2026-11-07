namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.State)]
    public class VPRC_PopBufferBinding : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopBufferBinding();
    }
}
