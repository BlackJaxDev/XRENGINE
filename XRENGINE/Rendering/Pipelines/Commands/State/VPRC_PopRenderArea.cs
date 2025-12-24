namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PopRenderArea : ViewportPopStateRenderCommand
    {
        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PopRenderArea();
            ActivePipelineInstance.RenderState.PopCropArea();
        }
    }
}
