namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.State)]
    public class VPRC_DepthWrite : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("raster-state");

        [System.ComponentModel.DefaultValue(true)]
        public bool Allow { get; set; } = true;

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.AllowDepthWrite(Allow);
        }
    }
}
