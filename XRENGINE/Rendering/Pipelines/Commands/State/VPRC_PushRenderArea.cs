using XREngine.Data.Geometry;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PushRenderArea : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        public required Func<BoundingRectangle> RegionGetter { get; set; }

        protected override void Execute()
            => ActivePipelineInstance.RenderState.PushRenderArea(RegionGetter());
    }
}
