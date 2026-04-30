namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PopBufferBinding : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopBufferBinding();
    }
}
