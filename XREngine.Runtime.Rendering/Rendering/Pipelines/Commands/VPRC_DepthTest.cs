namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.State)]
    public class VPRC_DepthTest : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("raster-state");

        [System.ComponentModel.DefaultValue(true)]
        public bool Enable { get; set; } = true;

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.EnableDepthTest(Enable);
        }
    }
}
