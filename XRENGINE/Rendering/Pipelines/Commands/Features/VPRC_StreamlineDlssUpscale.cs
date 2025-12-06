using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Attempts to run a Streamline DLSS upscale pass before the final blit. Falls back
    /// to the standard quad blit when DLSS is unavailable or errors out.
    /// </summary>
    public class VPRC_StreamlineDlssUpscale : VPRC_RenderQuadFBO
    {
        public string? DepthTextureName { get; set; }
        public string? MotionTextureName { get; set; }

        protected override void Execute()
        {
            TryRunStreamline();
            base.Execute();
        }

        private void TryRunStreamline()
        {
            if (!NvidiaDlssManager.IsSupported || !Engine.Rendering.Settings.EnableNvidiaDlss)
                return;

            if (FrameBufferName is null)
                return;

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
                return;

            var sourceFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (sourceFbo is null)
                return;

            XRFrameBuffer? destination = null;
            if (TargetFrameBufferName is not null)
                destination = ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName);

            var depth = DepthTextureName is not null
                ? ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName)
                : null;
            var motion = MotionTextureName is not null
                ? ActivePipelineInstance.GetTexture<XRTexture>(MotionTextureName)
                : null;

            StreamlineNative.TryDispatchUpscale(
                viewport,
                sourceFbo,
                destination,
                depth,
                motion,
                out _);
        }
    }
}
