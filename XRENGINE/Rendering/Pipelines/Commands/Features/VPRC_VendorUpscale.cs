using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.XeSS;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Attempts to run a vendor-provided upscale pass (Intel XeSS or NVIDIA DLSS) before the final blit.
    /// Falls back to the standard quad blit when no upscaler is available.
    /// When the source FBO is a plain <see cref="XRFrameBuffer"/> (not <see cref="XRQuadFrameBuffer"/>),
    /// resolves its first color texture and presents it via a passthrough quad.
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

        private XRMaterial? _fallbackMaterial;
        private XRQuadFrameBuffer? _fallbackQuad;
        private XRTexture? _fallbackSourceTexture;

        private const string FallbackShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = clipXY * 0.5 + 0.5;
    OutColor = texture(SourceTexture, uv);
}
""";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            base.AllocateContainerResources(instance);
            if (_fallbackQuad is not null)
                return;

            _fallbackMaterial = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, FallbackShaderCode))
            {
                RenderOptions = new RenderingParameters()
                {
                    DepthTest = new DepthTest()
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        Function = EComparison.Always,
                        UpdateDepth = false,
                    }
                }
            };

            _fallbackQuad = new XRQuadFrameBuffer(_fallbackMaterial);
            _fallbackQuad.SettingUniforms += FallbackSettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_fallbackQuad is not null)
            {
                _fallbackQuad.SettingUniforms -= FallbackSettingUniforms;
                _fallbackQuad.Destroy();
                _fallbackQuad = null;
            }

            _fallbackMaterial?.Destroy();
            _fallbackMaterial = null;
            _fallbackSourceTexture = null;

            base.ReleaseContainerResources(instance);
        }

        protected override void Execute()
        {
            if (TryRunXess())
                return;

            if (TryRunDlss())
                return;

            // Standard quad blit requires XRQuadFrameBuffer.
            if (FrameBufferName is not null &&
                ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName) is not null)
            {
                base.Execute();
                return;
            }

            // Source is a plain XRFrameBuffer (e.g., SMAA output).
            // Resolve its first color texture and present via passthrough quad.
            FallbackBlit();
        }

        private void FallbackBlit()
        {
            if (FrameBufferName is null || _fallbackQuad is null)
                return;

            if (!VPRCSourceTextureHelpers.TryResolveColorTexture(
                    ActivePipelineInstance, null, FrameBufferName, out XRTexture? colorTexture, out _)
                || colorTexture is null)
                return;

            _fallbackSourceTexture = colorTexture;
            _fallbackQuad.Render(
                TargetFrameBufferName is not null
                    ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName)
                    : null);
        }

        private void FallbackSettingUniforms(XRRenderProgram program)
        {
            if (_fallbackSourceTexture is not null)
                program.Sampler("SourceTexture", _fallbackSourceTexture, 0);
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
                bool frameGenOk = IntelXessManager.Native.TryDispatchFrameGeneration(
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

            bool upscaleOk = IntelXessManager.Native.TryDispatchUpscale(
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

            bool ok = NvidiaDlssManager.Native.TryDispatchUpscale(
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
