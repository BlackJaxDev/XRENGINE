namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PopTextureBinding : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopTextureBinding();
    }
}