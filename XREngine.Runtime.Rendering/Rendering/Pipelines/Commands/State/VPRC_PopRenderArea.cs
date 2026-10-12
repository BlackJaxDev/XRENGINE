namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PopRenderArea : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("render-area");

        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PopRenderArea();
            ActivePipelineInstance.RenderState.PopCropArea();
        }
    }
}
