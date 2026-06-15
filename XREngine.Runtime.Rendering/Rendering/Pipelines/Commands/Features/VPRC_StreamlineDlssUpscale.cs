using System;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Attempts to run a Streamline DLSS upscale pass before the final blit.
    /// Explicit DLSS requests fail loudly when Streamline cannot run.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_StreamlineDlssUpscale : VPRC_RenderQuadToFBO
    {
        public string? DepthTextureName { get; set; }
        public string? MotionTextureName { get; set; }

        private static bool _reportedFailure;

        protected override void Execute()
        {
            if (TryRunStreamline(out string failureReason))
                return;

            if (RuntimeEngine.EffectiveSettings.EnableNvidiaDlss)
            {
                string reason = string.IsNullOrWhiteSpace(failureReason)
                    ? "Streamline DLSS did not complete"
                    : failureReason;
                string message = $"Requested Streamline DLSS upscale failed: {reason}. No fallback blit will be rendered because NVIDIA DLSS was explicitly requested.";
                Debug.RenderingError(message);
                throw new InvalidOperationException(message);
            }

            base.Execute();
        }

        private bool TryRunStreamline(out string failureReason)
        {
            failureReason = string.Empty;

            if (!RuntimeEngine.EffectiveSettings.EnableNvidiaDlss)
                return false;

            if (!NvidiaDlssManager.IsSupported)
            {
                failureReason = NvidiaDlssManager.LastError ?? "NVIDIA DLSS support probe failed.";
                return false;
            }

            if (FrameBufferName is null)
            {
                failureReason = "Streamline DLSS requires a source framebuffer.";
                return false;
            }

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
            {
                failureReason = "Streamline DLSS requires an active viewport.";
                return false;
            }

            var sourceFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (sourceFbo is null)
            {
                failureReason = $"Streamline DLSS source framebuffer '{FrameBufferName}' was not found or is not an XRQuadFrameBuffer.";
                return false;
            }

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
                failureReason = NvidiaDlssManager.Native.LastError ?? $"errorCode={errorCode}";
                Debug.RenderingError($"Streamline DLSS upscale failed ({failureReason}).");
            }

            return ok;
        }
    }
}
