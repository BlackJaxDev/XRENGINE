using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_DepthFunc : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("raster-state");

        [System.ComponentModel.DefaultValue(EComparison.Lequal)]
        public EComparison Comp { get; set; } = EComparison.Lequal;

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.DepthFunc(Comp);
        }
    }
}
