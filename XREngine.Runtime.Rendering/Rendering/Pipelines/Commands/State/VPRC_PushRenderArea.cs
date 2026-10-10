using XREngine.Data.Geometry;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PushRenderArea : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("render-area");

        public required Func<BoundingRectangle> RegionGetter { get; set; }

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PushRenderAreaState(RegionGetter());
    }
}
