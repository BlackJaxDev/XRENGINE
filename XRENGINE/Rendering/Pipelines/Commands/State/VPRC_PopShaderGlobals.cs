namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PopShaderGlobals : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopShaderGlobals();
    }
}