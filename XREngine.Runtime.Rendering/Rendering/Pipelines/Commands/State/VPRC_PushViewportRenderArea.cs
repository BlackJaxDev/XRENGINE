using XREngine.Data.Geometry;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PushViewportRenderArea : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        /// <summary>
        /// If true, the internal resolution region of the viewport is used.
        /// Otherwise, the full-resolution active output target is used when one
        /// is bound, falling back to the viewport region.
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

            BoundingRectangle? externalRegion = null;
            BoundingRectangle? outputRegion = null;
            var renderer = AbstractRenderer.Current;
            if (renderer?.IsRenderingExternalSwapchainTarget == true &&
                renderer.TryGetExternalSwapchainTargetRegion(out BoundingRectangle activeExternalRegion))
            {
                externalRegion = activeExternalRegion;
            }
            else if (VPRC_RenderQuadToFBO.TryResolveDestinationRenderArea(
                ActivePipelineInstance.RenderState.OutputFBO,
                out int outputWidth,
                out int outputHeight))
            {
                outputRegion = new BoundingRectangle(0, 0, outputWidth, outputHeight);
            }

            BoundingRectangle res = ResolveRenderArea(
                UseInternalResolution,
                vp.InternalResolutionRegion,
                vp.Region,
                externalRegion,
                outputRegion);

            ActivePipelineInstance.RenderState.PushRenderArea(res);
            ActivePipelineInstance.RenderState.PushCropArea(res);
        }

        internal static BoundingRectangle ResolveRenderArea(
            bool useInternalResolution,
            BoundingRectangle internalRegion,
            BoundingRectangle viewportRegion,
            BoundingRectangle? externalRegion,
            BoundingRectangle? outputRegion)
        {
            if (useInternalResolution)
                return internalRegion;
            if (externalRegion.HasValue)
                return externalRegion.Value;
            if (outputRegion.HasValue)
                return outputRegion.Value;

            // The viewport region already contains any editor-panel offset.
            return viewportRegion;
        }
    }
}
