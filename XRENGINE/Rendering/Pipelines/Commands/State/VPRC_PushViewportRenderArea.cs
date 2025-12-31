using XREngine.Data.Geometry;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PushViewportRenderArea : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        /// <summary>
        /// If true, the internal resolution region of the viewport is used.
        /// Otherwise, the region of the viewport is used.
        /// Defaults to true.
        /// </summary>
        public bool UseInternalResolution { get; set; } = true;

        protected override void Execute()
        {
            var vp = ActivePipelineInstance.RenderState.WindowViewport;
            if (vp is null)
            {
                PopCommand.ShouldExecute = false;
                return;
            }

            BoundingRectangle res;
            if (UseInternalResolution)
            {
                res = vp.InternalResolutionRegion;
            }
            else
            {
                // For the final output pass, apply the viewport panel offset if available.
                // The viewport's Region already contains the offset from ApplyViewportPanelRegion.
                res = vp.Region;
            }

            ActivePipelineInstance.RenderState.PushRenderArea(res);
            ActivePipelineInstance.RenderState.PushCropArea(res);
        }
    }
}
