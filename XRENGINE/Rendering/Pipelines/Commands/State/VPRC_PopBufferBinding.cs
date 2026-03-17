namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PopBufferBinding : ViewportPopStateRenderCommand
    {
        protected override void Execute()
            => ActivePipelineInstance.RenderState.PopBufferBinding();
    }
}