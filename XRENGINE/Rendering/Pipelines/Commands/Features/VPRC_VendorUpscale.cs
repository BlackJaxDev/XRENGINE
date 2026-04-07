using System;
using System.Diagnostics;
using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.XeSS;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Attempts to run a vendor-provided upscale pass (Intel XeSS or NVIDIA DLSS) before the final blit.
    /// When no upscaler is available, resolves the source FBO's first color texture and presents it
    /// via a passthrough quad instead of re-running the source quad shader directly to the backbuffer.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_VendorUpscale : VPRC_RenderQuadFBO
    {
        private static readonly bool _diagEnabled =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRE_DIAG_VENDOR_UPSCALE"));

        public string? SourceTextureName { get; set; }
        public string? DepthTextureName { get; set; }
        public string? DepthStencilTextureName { get; set; }
        public string? MotionTextureName { get; set; }
        public string? MotionFrameBufferName { get; set; }

        private static bool _reportedDlssFailure;
        private static bool _reportedXessFailure;
        private static bool _reportedXessApiMismatch;
        private static bool _reportedDlssApiMismatch;
        private static bool _reportedXessFrameGenUnavailable;

        private XRMaterial? _fallbackMaterial;
        private XRQuadFrameBuffer? _fallbackQuad;
        private XRTexture? _fallbackSourceTexture;
        private XRFrameBuffer? _bridgeSourceTextureFbo;
        private XRTexture? _bridgeSourceTexture;
        private XRFrameBuffer? _bridgeDepthTextureFbo;
        private XRTexture? _bridgeDepthTexture;
        private XRFrameBuffer? _bridgeMotionTextureFbo;
        private XRTexture? _bridgeMotionTexture;

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

            DestroyBridgeHelperFrameBuffer(ref _bridgeSourceTextureFbo, ref _bridgeSourceTexture);
            DestroyBridgeHelperFrameBuffer(ref _bridgeDepthTextureFbo, ref _bridgeDepthTexture);
            DestroyBridgeHelperFrameBuffer(ref _bridgeMotionTextureFbo, ref _bridgeMotionTexture);

            base.ReleaseContainerResources(instance);
        }

        protected override void Execute()
        {
            XRViewport? viewport = ActivePipelineInstance.RenderState.WindowViewport;
            XRFrameBuffer? sourceFrameBuffer = FrameBufferName is not null
                ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(FrameBufferName)
                : null;

            bool hasColorTexture = VPRCSourceTextureHelpers.TryResolveColorTexture(
                ActivePipelineInstance,
                SourceTextureName,
                FrameBufferName,
                out XRTexture? resolvedColorTexture,
                out string resolveFailure)
                && resolvedColorTexture is not null;

            if (viewport?.Window?.Renderer is VulkanRenderer)
            {
                if (TryRunXess())
                    return;

                if (TryRunDlss())
                    return;
            }
            else if (viewport?.Window?.Renderer is OpenGLRenderer openGlRenderer &&
                IsBridgePathRequested() &&
                hasColorTexture &&
                resolvedColorTexture is not null)
            {
                if (TryRunBridge(openGlRenderer, viewport, sourceFrameBuffer, resolvedColorTexture, out string bridgeFailure))
                    return;

                ReportBridgeFallback(viewport, bridgeFailure);
            }
            else if (viewport is not null && IsBridgePathRequested())
            {
                ReportBridgeFallback(
                    viewport,
                    hasColorTexture
                        ? Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(viewport, ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false)
                        : resolveFailure);
            }

            if (FrameBufferName is null)
            {
                Debug.RenderingWarningEvery(
                    $"VendorUpscale.NoSource.{ActivePipelineInstance.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] VendorUpscale skipped: no source FBO. Target={0} OutputFBO={1} AA={2}",
                    TargetFrameBufferName ?? "<current>",
                    ActivePipelineInstance.RenderState.OutputFBO?.Name ?? "<null>",
                    ActivePipelineInstance.EffectiveAntiAliasingModeThisFrame?.ToString() ?? "<null>");
                return;
            }

            XRQuadFrameBuffer? quadFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);

            string outputTarget = TargetFrameBufferName
                ?? ActivePipelineInstance.RenderState.OutputFBO?.Name
                ?? "<backbuffer>";

            if (quadFbo is not null && !hasColorTexture)
            {
/*
                Debug.RenderingEvery(
                    $"VendorUpscale.Path.ShaderOnly.{ActivePipelineInstance.GetHashCode()}.{FrameBufferName}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] VendorUpscale path=QuadShader Source={0} Target={1} AA={2} HDR={3}",
                    FrameBufferName,
                    outputTarget,
                    ActivePipelineInstance.EffectiveAntiAliasingModeThisFrame?.ToString() ?? "<null>",
                    ActivePipelineInstance.EffectiveOutputHDRThisFrame?.ToString() ?? "<null>");
*/
                if (_diagEnabled)
                    Debug.Log(ELogCategory.Rendering, $"[VendorUpscaleDiag] QuadBlit path. Source='{FrameBufferName}' Target='{TargetFrameBufferName ?? "<current>"}' OutputFBO='{ActivePipelineInstance.RenderState.OutputFBO?.Name ?? "<null>"}'");

                base.Execute();
                return;
            }

            if (hasColorTexture)
            {
                XRTexture colorTexture = resolvedColorTexture!;
/*
                Debug.RenderingEvery(
                    $"VendorUpscale.Path.ResolvedColor.{ActivePipelineInstance.GetHashCode()}.{FrameBufferName}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] VendorUpscale path=ResolvedColor Source={0} Target={1} Texture={2} QuadSource={3} AA={4} HDR={5}",
                    FrameBufferName,
                    outputTarget,
                    resolvedColorTexture!.Name ?? resolvedColorTexture.SamplerName ?? "<unnamed>",
                    quadFbo is not null,
                    ActivePipelineInstance.EffectiveAntiAliasingModeThisFrame?.ToString() ?? "<null>",
                    ActivePipelineInstance.EffectiveOutputHDRThisFrame?.ToString() ?? "<null>");
*/

                if (_diagEnabled)
                    Debug.Log(ELogCategory.Rendering, $"[VendorUpscaleDiag] FallbackBlit path. Source='{FrameBufferName}' Target='{TargetFrameBufferName ?? "<current>"}' Texture='{colorTexture.Name ?? colorTexture.SamplerName ?? "<unnamed>"}'");

                _fallbackSourceTexture = colorTexture;
                _fallbackQuad?.Render(
                    TargetFrameBufferName is not null
                        ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName)
                        : null);
                return;
            }

            Debug.RenderingWarningEvery(
                $"VendorUpscale.NoPresentableSource.{ActivePipelineInstance.GetHashCode()}.{FrameBufferName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] VendorUpscale skipped: no presentable source for FBO={0}. Reason={1} Target={2} AA={3}",
                FrameBufferName,
                resolveFailure,
                outputTarget,
                ActivePipelineInstance.EffectiveAntiAliasingModeThisFrame?.ToString() ?? "<null>");
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

        private bool TryRunBridge(
            OpenGLRenderer renderer,
            XRViewport viewport,
            XRFrameBuffer? sourceFrameBuffer,
            XRTexture resolvedColorTexture,
            out string failureReason)
        {
            failureReason = string.Empty;

            VulkanUpscaleBridge? bridge = Engine.Rendering.GetVulkanUpscaleBridge(viewport);
            if (bridge is null || !bridge.TryResolveCurrentFrameSlot(out VulkanUpscaleBridgeFrameSlot? slot) || slot is null)
            {
                failureReason = Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(
                    viewport,
                    ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false);
                return false;
            }

            if (!TryResolveBridgeColorSource(renderer, sourceFrameBuffer, resolvedColorTexture, out XRFrameBuffer? sourceColorFbo, out string colorFailure)
                || sourceColorFbo is null)
            {
                failureReason = colorFailure;
                return false;
            }

            if (!TryResolveBridgeDepthSource(renderer, out XRFrameBuffer? sourceDepthFbo, out string depthFailure)
                || sourceDepthFbo is null)
            {
                failureReason = depthFailure;
                return false;
            }

            if (!TryResolveBridgeMotionSource(renderer, out XRFrameBuffer? sourceMotionFbo, out string motionFailure)
                || sourceMotionFbo is null)
            {
                failureReason = motionFailure;
                return false;
            }

            if (!ValidateBridgeInputSizes(sourceColorFbo, sourceDepthFbo, sourceMotionFbo, slot, out string sizeFailure))
            {
                failureReason = sizeFailure;
                return false;
            }

            bool ok = bridge.TryExecutePassthrough(
                renderer,
                sourceColorFbo,
                sourceDepthFbo,
                sourceMotionFbo,
                out XRTexture? bridgeOutput,
                out TimeSpan dispatchDuration,
                out string bridgeFailure);

            if (!ok || bridgeOutput is null)
            {
                failureReason = string.IsNullOrWhiteSpace(bridgeFailure)
                    ? "bridge passthrough submission failed"
                    : bridgeFailure;
                return false;
            }

            if (_diagEnabled)
            {
                Debug.Log(
                    ELogCategory.Rendering,
                    $"[VendorUpscaleDiag] BridgePassthrough path. SourceFBO='{DescribeFrameBuffer(sourceColorFbo)}' DepthFBO='{DescribeFrameBuffer(sourceDepthFbo)}' MotionFBO='{DescribeFrameBuffer(sourceMotionFbo)}' OutputTexture='{DescribeTexture(bridgeOutput)}' Slot={slot.SlotIndex} State='{bridge.State}' Recreate='{bridge.PendingRecreateReason ?? "<none>"}' DispatchMs={dispatchDuration.TotalMilliseconds:F3}");
            }

            _fallbackSourceTexture = bridgeOutput;
            _fallbackQuad?.Render(
                TargetFrameBufferName is not null
                    ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName)
                    : null);
            return true;
        }

        private void ReportBridgeFallback(XRViewport viewport, string failureReason)
        {
            string reason = string.IsNullOrWhiteSpace(failureReason)
                ? Engine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(
                    viewport,
                    ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false)
                : failureReason;

            if (Engine.EffectiveSettings.EnableIntelXess && !_reportedXessApiMismatch)
            {
                _reportedXessApiMismatch = true;
                Debug.LogWarning($"Intel XeSS requires Vulkan or the experimental OpenGL->Vulkan bridge. {reason}. Falling back to standard blit.");
            }

            if (Engine.EffectiveSettings.EnableNvidiaDlss && !_reportedDlssApiMismatch)
            {
                _reportedDlssApiMismatch = true;
                Debug.LogWarning($"NVIDIA DLSS requires Vulkan or the experimental OpenGL->Vulkan bridge. {reason}. Falling back to standard blit.");
            }
        }

        private bool TryResolveBridgeColorSource(
            OpenGLRenderer renderer,
            XRFrameBuffer? sourceFrameBuffer,
            XRTexture resolvedColorTexture,
            out XRFrameBuffer? bridgeSourceFbo,
            out string failureReason)
        {
            if (sourceFrameBuffer is not null && FrameBufferHasColorAttachment(sourceFrameBuffer))
            {
                bridgeSourceFbo = sourceFrameBuffer;
                failureReason = string.Empty;
                return true;
            }

            return TryEnsureBridgeHelperFrameBuffer(
                renderer,
                resolvedColorTexture,
                EFrameBufferAttachment.ColorAttachment0,
                "VendorUpscale.Bridge.SourceColorFBO",
                ref _bridgeSourceTextureFbo,
                ref _bridgeSourceTexture,
                out bridgeSourceFbo,
                out failureReason);
        }

        private bool TryResolveBridgeDepthSource(
            OpenGLRenderer renderer,
            out XRFrameBuffer? bridgeDepthFbo,
            out string failureReason)
        {
            XRTexture? depthTexture = !string.IsNullOrWhiteSpace(DepthStencilTextureName)
                ? ActivePipelineInstance.GetTexture<XRTexture>(DepthStencilTextureName!)
                : !string.IsNullOrWhiteSpace(DepthTextureName)
                    ? ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName!)
                    : null;

            if (depthTexture is null)
            {
                bridgeDepthFbo = null;
                failureReason = !string.IsNullOrWhiteSpace(DepthStencilTextureName)
                    ? $"Depth texture '{DepthStencilTextureName}' was not found."
                    : !string.IsNullOrWhiteSpace(DepthTextureName)
                        ? $"Depth texture '{DepthTextureName}' was not found."
                        : "No bridge depth texture source was configured.";
                return false;
            }

            return TryEnsureBridgeHelperFrameBuffer(
                renderer,
                depthTexture,
                EFrameBufferAttachment.DepthStencilAttachment,
                "VendorUpscale.Bridge.DepthSourceFBO",
                ref _bridgeDepthTextureFbo,
                ref _bridgeDepthTexture,
                out bridgeDepthFbo,
                out failureReason);
        }

        private bool TryResolveBridgeMotionSource(
            OpenGLRenderer renderer,
            out XRFrameBuffer? bridgeMotionFbo,
            out string failureReason)
        {
            if (!string.IsNullOrWhiteSpace(MotionFrameBufferName))
            {
                bridgeMotionFbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(MotionFrameBufferName!);
                if (bridgeMotionFbo is not null)
                {
                    failureReason = string.Empty;
                    return true;
                }
            }

            XRTexture? motionTexture = !string.IsNullOrWhiteSpace(MotionTextureName)
                ? ActivePipelineInstance.GetTexture<XRTexture>(MotionTextureName!)
                : null;
            if (motionTexture is null)
            {
                bridgeMotionFbo = null;
                failureReason = !string.IsNullOrWhiteSpace(MotionFrameBufferName)
                    ? $"Motion framebuffer '{MotionFrameBufferName}' was not found."
                    : !string.IsNullOrWhiteSpace(MotionTextureName)
                        ? $"Motion texture '{MotionTextureName}' was not found."
                        : "No bridge motion source was configured.";
                return false;
            }

            return TryEnsureBridgeHelperFrameBuffer(
                renderer,
                motionTexture,
                EFrameBufferAttachment.ColorAttachment0,
                "VendorUpscale.Bridge.MotionSourceFBO",
                ref _bridgeMotionTextureFbo,
                ref _bridgeMotionTexture,
                out bridgeMotionFbo,
                out failureReason);
        }

        private bool TryEnsureBridgeHelperFrameBuffer(
            OpenGLRenderer renderer,
            XRTexture texture,
            EFrameBufferAttachment attachment,
            string frameBufferName,
            ref XRFrameBuffer? cachedFrameBuffer,
            ref XRTexture? cachedTexture,
            out XRFrameBuffer? frameBuffer,
            out string failureReason)
        {
            frameBuffer = null;
            failureReason = string.Empty;

            if (texture is not IFrameBufferAttachement attachmentTarget)
            {
                failureReason = $"Texture '{texture.Name ?? texture.SamplerName ?? "<unnamed>"}' cannot be attached to a helper framebuffer for bridge upload.";
                return false;
            }

            if (cachedFrameBuffer is null || !ReferenceEquals(cachedTexture, texture))
            {
                DestroyBridgeHelperFrameBuffer(ref cachedFrameBuffer, ref cachedTexture);

                cachedFrameBuffer = new XRFrameBuffer((attachmentTarget, attachment, 0, -1))
                {
                    Name = frameBufferName,
                };
                cachedTexture = texture;

                if (renderer.GenericToAPI<GLFrameBuffer>(cachedFrameBuffer) is not GLFrameBuffer glFrameBuffer)
                {
                    DestroyBridgeHelperFrameBuffer(ref cachedFrameBuffer, ref cachedTexture);
                    failureReason = $"Failed to create the OpenGL framebuffer wrapper for bridge helper '{frameBufferName}'.";
                    return false;
                }

                glFrameBuffer.Generate();
            }

            frameBuffer = cachedFrameBuffer;
            return true;
        }

        private static bool ValidateBridgeInputSizes(
            XRFrameBuffer sourceColorFbo,
            XRFrameBuffer sourceDepthFbo,
            XRFrameBuffer sourceMotionFbo,
            VulkanUpscaleBridgeFrameSlot slot,
            out string failureReason)
        {
            if (sourceColorFbo.Width != slot.SourceColorFrameBuffer.Width || sourceColorFbo.Height != slot.SourceColorFrameBuffer.Height)
            {
                failureReason = $"bridge source color size mismatch: expected {slot.SourceColorFrameBuffer.Width}x{slot.SourceColorFrameBuffer.Height}, got {sourceColorFbo.Width}x{sourceColorFbo.Height}.";
                return false;
            }

            if (sourceDepthFbo.Width != slot.SourceDepthFrameBuffer.Width || sourceDepthFbo.Height != slot.SourceDepthFrameBuffer.Height)
            {
                failureReason = $"bridge source depth size mismatch: expected {slot.SourceDepthFrameBuffer.Width}x{slot.SourceDepthFrameBuffer.Height}, got {sourceDepthFbo.Width}x{sourceDepthFbo.Height}.";
                return false;
            }

            if (sourceMotionFbo.Width != slot.SourceMotionFrameBuffer.Width || sourceMotionFbo.Height != slot.SourceMotionFrameBuffer.Height)
            {
                failureReason = $"bridge source motion size mismatch: expected {slot.SourceMotionFrameBuffer.Width}x{slot.SourceMotionFrameBuffer.Height}, got {sourceMotionFbo.Width}x{sourceMotionFbo.Height}.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        private static bool FrameBufferHasColorAttachment(XRFrameBuffer frameBuffer)
        {
            var targets = frameBuffer.Targets;
            if (targets is null)
                return false;

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i].Attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment7)
                    return true;
            }

            return false;
        }

        private static string DescribeFrameBuffer(XRFrameBuffer frameBuffer)
            => $"{frameBuffer.Name ?? "<unnamed>"}({frameBuffer.Width}x{frameBuffer.Height})";

        private static string DescribeTexture(XRTexture texture)
        {
            var size = texture.WidthHeightDepth;
            string descriptor = texture is XRTexture2D texture2D
                ? texture2D.SizedInternalFormat.ToString()
                : texture.GetType().Name;
            return $"{texture.Name ?? texture.SamplerName ?? "<unnamed>"}({size.X}x{size.Y}, {descriptor})";
        }

        private static bool IsBridgePathRequested()
            => Engine.EffectiveSettings.EnableIntelXess || Engine.EffectiveSettings.EnableNvidiaDlss;

        private static void DestroyBridgeHelperFrameBuffer(ref XRFrameBuffer? frameBuffer, ref XRTexture? cachedTexture)
        {
            frameBuffer?.Destroy();
            frameBuffer = null;
            cachedTexture = null;
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

            if (!ok && !_reportedDlssFailure)
            {
                _reportedDlssFailure = true;
                Debug.LogWarning($"Streamline DLSS upscale failed (errorCode={errorCode}). Falling back to standard blit.");
            }

            return ok;
        }
    }
}
