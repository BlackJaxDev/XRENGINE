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

        private static bool _reportedFailure;

        protected override void Execute()
        {
            if (!TryRunStreamline())
                base.Execute();
        }

        private bool TryRunStreamline()
        {
            if (!NvidiaDlssManager.IsSupported || !Engine.Rendering.Settings.EnableNvidiaDlss)
                return false;

            if (FrameBufferName is null)
                return false;

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
                return false;

            var sourceFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (sourceFbo is null)
                return false;

            XRFrameBuffer? destination = null;
            if (TargetFrameBufferName is not null)
                destination = ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName);

            var depth = DepthTextureName is not null
                ? ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName)
                : null;
            var motion = MotionTextureName is not null
                ? ActivePipelineInstance.GetTexture<XRTexture>(MotionTextureName)
                : null;

            bool ok = NvidiaDlssManager.Native.TryDispatchUpscale(
                viewport,
                sourceFbo,
                destination,
                depth,
                motion,
                out int errorCode);

            if (!ok && !_reportedFailure)
            {
                _reportedFailure = true;
                Debug.LogWarning($"Streamline DLSS upscale failed (errorCode={errorCode}). Falling back to standard blit.");
            }

            return ok;
        }
    }
}
