using XREngine.Rendering.DLSS;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.XeSS;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Attempts to run a vendor-provided upscale pass (Intel XeSS or NVIDIA DLSS) before the final blit.
    /// Falls back to the standard quad blit when no upscaler is available.
    /// </summary>
    public class VPRC_VendorUpscale : VPRC_RenderQuadFBO
    {
        public string? DepthTextureName { get; set; }
        public string? MotionTextureName { get; set; }

        private static bool _reportedDlssFailure;
        private static bool _reportedXessFailure;
        private static bool _reportedXessApiMismatch;
        private static bool _reportedDlssApiMismatch;
        private static bool _reportedXessFrameGenUnavailable;

        protected override void Execute()
        {
            if (TryRunXess())
                return;

            if (TryRunDlss())
                return;

            base.Execute();
        }

        private bool TryRunXess()
        {
            if (!Engine.EffectiveSettings.EnableIntelXess || !IntelXessManager.IsSupported)
                return false;

            if (FrameBufferName is null)
                return false;

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
                return false;

            if (viewport.Window?.Renderer is not VulkanRenderer)
            {
                if (!_reportedXessApiMismatch)
                {
                    _reportedXessApiMismatch = true;
                    Debug.LogWarning("Intel XeSS requires Vulkan. Skipping XeSS upscale on non-Vulkan renderer.");
                }
                return false;
            }

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

            // Keep the internal resolution aligned with XeSS expectations.
            IntelXessManager.ApplyToViewport(viewport, Engine.Rendering.Settings);

            if (Engine.Rendering.Settings.EnableIntelXessFrameGeneration)
            {
                bool frameGenOk = IntelXessNative.TryDispatchFrameGeneration(
                    viewport,
                    sourceFbo,
                    motion,
                    out int frameGenError,
                    out string? frameGenMessage);

                if (!frameGenOk && !_reportedXessFrameGenUnavailable)
                {
                    _reportedXessFrameGenUnavailable = true;
                    string fgReason = frameGenMessage ?? $"errorCode={frameGenError}";
                    Debug.LogWarning($"Intel XeSS frame generation is unavailable ({fgReason}). Continuing without frame generation.");
                }
            }

            bool upscaleOk = IntelXessNative.TryDispatchUpscale(
                viewport,
                sourceFbo,
                destination,
                depth,
                motion,
                Engine.Rendering.Settings.XessSharpness,
                out int errorCode);

            if (upscaleOk)
                return true;

            if (!_reportedXessFailure)
            {
                _reportedXessFailure = true;
                string reason = IntelXessManager.LastError ?? $"errorCode={errorCode}";
                Debug.LogWarning($"Intel XeSS upscale failed ({reason}). Falling back to standard blit.");
            }

            return false;
        }

        private bool TryRunDlss()
        {
            if (!NvidiaDlssManager.IsSupported || !Engine.EffectiveSettings.EnableNvidiaDlss)
                return false;

            if (FrameBufferName is null)
                return false;

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
                return false;

            if (viewport.Window?.Renderer is not VulkanRenderer)
            {
                if (!_reportedDlssApiMismatch)
                {
                    _reportedDlssApiMismatch = true;
                    Debug.LogWarning("NVIDIA DLSS requires Vulkan. Skipping DLSS upscale on non-Vulkan renderer.");
                }
                return false;
            }

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

            bool ok = StreamlineNative.TryDispatchUpscale(
                viewport,
                sourceFbo,
                destination,
                depth,
                motion,
                out int errorCode);

            if (!ok && !_reportedDlssFailure)
            {
                _reportedDlssFailure = true;
                Debug.LogWarning($"Streamline DLSS upscale failed (errorCode={errorCode}). Falling back to standard blit.");
            }

            return ok;
        }
    }
}
