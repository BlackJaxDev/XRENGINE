namespace XREngine.Rendering.Pipelines.Commands
{
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

            ActivePipelineInstance.RenderState.PushRenderArea((int)fbo.Width, (int)fbo.Height);
        }
    }
}
