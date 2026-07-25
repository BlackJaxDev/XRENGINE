namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PushOutputFBORenderArea : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        protected override void Execute()
        {
            var fbo = ActivePipelineInstance.RenderState.OutputFBO;
            if (fbo is null)
            {
                PopCommand.ShouldExecute = false;
                return;
            }

            var region = new XREngine.Data.Geometry.BoundingRectangle(
                0,
                0,
                (int)fbo.Width,
                (int)fbo.Height);
            ActivePipelineInstance.RenderState.PushRenderAreaState(region);
            ActivePipelineInstance.RenderState.PushCropAreaState(region);
        }
    }
}
