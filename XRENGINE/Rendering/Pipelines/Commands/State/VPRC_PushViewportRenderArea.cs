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

            var res = UseInternalResolution
                ? vp.InternalResolutionRegion
                : vp.Region;

            ActivePipelineInstance.RenderState.PushRenderArea(res);
            ActivePipelineInstance.RenderState.PushCropArea(res);
        }
    }
}
