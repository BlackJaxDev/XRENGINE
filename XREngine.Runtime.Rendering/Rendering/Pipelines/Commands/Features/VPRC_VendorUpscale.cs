using System;
using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.RenderGraph;
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
    public class VPRC_VendorUpscale : VPRC_RenderQuadToFBO
    {
        private static bool _diagEnabled => RenderDiagnosticsFlags.DiagVendorUpscale;

        public string? SourceTextureName { get; set; }
        public string? DepthTextureName { get; set; }
        public string? DepthStencilTextureName { get; set; }
        public string? MotionTextureName { get; set; }
        public string? MotionFrameBufferName { get; set; }
        public bool ForceFallbackBlit { get; set; }
        public bool FlipSourceYOnVulkanFallback { get; set; } = true;
        public string AutoExposureTextureName { get; set; } = DefaultRenderPipeline.AutoExposureTextureName;

        private static bool _reportedXessFailure;
        private static bool _reportedXessApiMismatch;
        private static bool _reportedDlssApiMismatch;
        private static bool _reportedDlssUnavailable;
        private static bool _reportedXessUnavailable;
        private static bool _reportedXessFrameGenUnavailable;
        private static bool _reportedDlssFrameGenUnavailable;

        private XRMaterial? _fallbackMaterial;
        private XRQuadFrameBuffer? _fallbackQuad;
        private XRTexture? _fallbackSourceTexture;
        private XRFrameBuffer? _bridgeSourceTextureFbo;
        private XRTexture? _bridgeSourceTexture;
        private XRFrameBuffer? _bridgeDepthTextureFbo;
        private XRTexture? _bridgeDepthTexture;
        private XRFrameBuffer? _bridgeMotionTextureFbo;
        private XRTexture? _bridgeMotionTexture;
        private XRFrameBuffer? _bridgeExposureTextureFbo;
        private XRTexture? _bridgeExposureTexture;
        private NvidiaDlssManager.Native.NativeVulkanSession? _nativeDlssSession;
        private VulkanRenderer? _nativeDlssRenderer;
        private XRTexture2D? _nativeDlssOutputTexture;
        private XRFrameBuffer? _nativeDlssOutputFbo;
        private uint _nativeDlssViewportId;
        private uint _nativeDlssOutputWidth;
        private uint _nativeDlssOutputHeight;
        private bool _nativeDlssOutputHdr;
        private bool _nativeDlssDispatchHistoryValid;
        private XRCamera? _lastNativeDlssCamera;
        private object? _lastNativeDlssScene;
        private bool _lastNativeDlssOutputHdr;
        private bool _lastNativeDlssReverseDepth;
        private uint _lastNativeDlssInputWidth;
        private uint _lastNativeDlssInputHeight;
        private uint _lastNativeDlssOutputWidth;
        private uint _lastNativeDlssOutputHeight;
        private Vector3 _lastNativeDlssCameraPosition;
        private Vector3 _lastNativeDlssCameraForward;
        private bool _bridgeVendorHistoryValid;
        private EVulkanUpscaleBridgeVendor _lastBridgeVendor;
        private bool _fallbackApplySharpen;
        private float _fallbackSharpenStrength;
        private bool _bridgeDispatchHistoryValid;
        private XRCamera? _lastBridgeCamera;
        private object? _lastBridgeScene;
        private uint _lastBridgeGeneration;
        private bool _lastBridgeOutputHdr;
        private bool _lastBridgeReverseDepth;
        private uint _lastBridgeInputWidth;
        private uint _lastBridgeInputHeight;
        private uint _lastBridgeOutputWidth;
        private uint _lastBridgeOutputHeight;
        private Vector3 _lastBridgeCameraPosition;
        private Vector3 _lastBridgeCameraForward;

        // MotionVectors.fs writes current-minus-previous clip-space delta in the engine's NDC convention.
        // DLSS/XeSS bridge dispatch expects a normalized clip delta, so halve the authored scale.
        private const float BridgeMotionVectorNormalizationScale = 0.5f;

        private const string FallbackShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform bool ApplySharpen;
uniform bool FlipSourceYOnVulkanFallback;
uniform float SharpenStrength;

vec2 ResolvePresentTextureUv(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5 + 0.5;
#ifdef XRENGINE_VULKAN
    if (FlipSourceYOnVulkanFallback)
        uv.y = 1.0 - uv.y;
#endif
    return uv;
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = ResolvePresentTextureUv(clipXY);
    vec4 source = texture(SourceTexture, uv);
    if (ApplySharpen && SharpenStrength > 0.0)
    {
        vec2 texelSize = 1.0 / vec2(textureSize(SourceTexture, 0));
        vec3 north = texture(SourceTexture, uv + vec2(0.0, texelSize.y)).rgb;
        vec3 south = texture(SourceTexture, uv - vec2(0.0, texelSize.y)).rgb;
        vec3 east = texture(SourceTexture, uv + vec2(texelSize.x, 0.0)).rgb;
        vec3 west = texture(SourceTexture, uv - vec2(texelSize.x, 0.0)).rgb;
        vec3 sharpened = source.rgb * (1.0 + 4.0 * SharpenStrength) - (north + south + east + west) * SharpenStrength;
        source.rgb = max(sharpened, vec3(0.0));
    }

    OutColor = source;
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
                    },
                    BlendModeAllDrawBuffers = BlendMode.Disabled()
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
            DestroyBridgeHelperFrameBuffer(ref _bridgeExposureTextureFbo, ref _bridgeExposureTexture);
            DestroyNativeDlssResources();
            _bridgeVendorHistoryValid = false;
            _lastBridgeVendor = default;
            _bridgeDispatchHistoryValid = false;
            _lastBridgeCamera = null;
            _lastBridgeScene = null;
            _nativeDlssDispatchHistoryValid = false;
            _lastNativeDlssCamera = null;
            _lastNativeDlssScene = null;
            _fallbackApplySharpen = false;
            _fallbackSharpenStrength = 0.0f;

            base.ReleaseContainerResources(instance);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceQuadFBOName is null)
                return;

            string destination = DestinationFBOName
                ?? (RenderToSourceFrameBuffer ? SourceQuadFBOName : null)
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass(BuildQuadBlitPassName(SourceQuadFBOName, destination));
            if (!string.IsNullOrWhiteSpace(SourceTextureName))
            {
                builder.SampleTexture(MakeTextureResource(SourceTextureName));
                return;
            }

            if (!TryDescribeActualFboColorInputs(builder, SourceQuadFBOName))
                builder.SampleTexture(MakeFboColorResource(SourceQuadFBOName));
        }

        protected override void Execute()
        {
            _fallbackApplySharpen = false;
            _fallbackSharpenStrength = 0.0f;

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

            bool requestedVendorFeature = IsVendorFeatureRequested();
            if (ForceFallbackBlit && requestedVendorFeature)
                FailRequestedVendorFeature("vendor upscale/frame generation", "vendor upscale bypass is active while a vendor feature is requested");

            if (!ForceFallbackBlit && viewport?.Window?.Renderer is VulkanRenderer)
            {
                if (TryRunNativeVulkanVendor(out string nativeFailure))
                    return;

                if (requestedVendorFeature)
                    FailRequestedVendorFeature("native Vulkan vendor upscale/frame generation", nativeFailure);

                ReportNativeFallback();
            }
            else if (!ForceFallbackBlit &&
                viewport?.Window?.Renderer is OpenGLRenderer openGlRenderer &&
                IsBridgePathRequested() &&
                hasColorTexture &&
                resolvedColorTexture is not null)
            {
                if (TryRunBridge(openGlRenderer, viewport, sourceFrameBuffer, resolvedColorTexture, out string bridgeFailure))
                    return;

                if (requestedVendorFeature)
                    FailRequestedVendorFeature("OpenGL-to-Vulkan vendor upscale bridge", bridgeFailure);

                ReportBridgeFallback(viewport, bridgeFailure);
            }
            else if (!ForceFallbackBlit && viewport is not null && requestedVendorFeature)
            {
                string failureReason =
                    hasColorTexture
                        ? RuntimeEngine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(viewport, ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false)
                        : resolveFailure;
                FailRequestedVendorFeature("vendor upscale/frame generation", failureReason);
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
                _fallbackApplySharpen = false;
                _fallbackSharpenStrength = 0.0f;
                RenderFallbackQuad();
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
            _fallbackApplySharpen = false;
            _fallbackSharpenStrength = 0.0f;
            RenderFallbackQuad();
        }

        private void RenderFallbackQuad()
        {
            if (_fallbackQuad is null || FrameBufferName is null)
                return;

            XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (activeInstance is null)
            {
                Debug.RenderingWarningEvery(
                    $"VendorUpscale.FallbackMissingPipeline.{FrameBufferName}.{TargetFrameBufferName}",
                    TimeSpan.FromSeconds(5),
                    "[RenderDiag] Skipping vendor-upscale fallback blit from '{0}' to '{1}': no active render pipeline instance.",
                    FrameBufferName,
                    TargetFrameBufferName ?? "<current>");
                return;
            }

            string destination = ResolveDestinationLabel(activeInstance);
            string passName = BuildQuadBlitPassName(FrameBufferName, destination);
            int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
            if (passIndex == int.MinValue && hasRenderGraphMetadata)
            {
                Debug.RenderingWarningEvery(
                    $"VendorUpscale.FallbackMissingRenderGraphPass.{passName}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDiag] Skipping vendor-upscale fallback blit '{0}': no matching render-graph pass metadata was generated.",
                    passName);
                return;
            }

            XRQuadFrameBuffer? sourceFbo = activeInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            XRFrameBuffer? destFbo = ResolveDestinationFbo(activeInstance, sourceFbo);
            if (_diagEnabled && TargetFrameBufferName is not null && destFbo is null)
                Debug.RenderingWarning($"[VendorUpscaleDiag] Dest FBO '{TargetFrameBufferName}' not found.");

            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            _fallbackQuad.Render(destFbo);
        }

        private void FallbackSettingUniforms(XRRenderProgram program)
        {
            if (_fallbackSourceTexture is not null)
            {
                program.Sampler("SourceTexture", _fallbackSourceTexture, 0);
            }

            program.Uniform("ApplySharpen", _fallbackApplySharpen);
            program.Uniform(
                "FlipSourceYOnVulkanFallback",
                FlipSourceYOnVulkanFallback);
            program.Uniform("SharpenStrength", _fallbackSharpenStrength);
        }

        private bool TryRunBridge(
            OpenGLRenderer renderer,
            XRViewport viewport,
            XRFrameBuffer? sourceFrameBuffer,
            XRTexture resolvedColorTexture,
            out string failureReason)
        {
            failureReason = string.Empty;

            VulkanUpscaleBridge? bridge = RuntimeEngine.Rendering.GetVulkanUpscaleBridge(viewport);
            if (bridge is null || !bridge.TryResolveCurrentFrameSlot(out VulkanUpscaleBridgeFrameSlot? slot) || slot is null)
            {
                failureReason = RuntimeEngine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(
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

            if (!TryResolveBridgeCamera(out XRCamera? camera, out string cameraFailure) || camera is null)
            {
                failureReason = cameraFailure;
                return false;
            }

            ColorGradingSettings? colorGrading = ResolveBridgeColorGradingSettings(camera);
            if (!TryResolveBridgeExposureSource(renderer, colorGrading, out XRFrameBuffer? sourceExposureFbo, out string exposureFailure))
            {
                failureReason = exposureFailure;
                return false;
            }

            if (!ValidateBridgeInputSizes(sourceColorFbo, sourceDepthFbo, sourceMotionFbo, slot, out string sizeFailure))
            {
                failureReason = sizeFailure;
                return false;
            }

            if (!TryResolveBridgeVendor(out EVulkanUpscaleBridgeVendor vendor, out string vendorFailure))
            {
                failureReason = vendorFailure;
                return false;
            }

            if (!TryCreateBridgeDispatchParameters(
                    renderer,
                    viewport,
                    bridge,
                    vendor,
                    camera,
                    colorGrading,
                    sourceColorFbo,
                    sourceExposureFbo,
                    out VulkanUpscaleBridgeDispatchParameters dispatchParameters,
                    out string dispatchFailure))
            {
                failureReason = dispatchFailure;
                return false;
            }

            bool ok = bridge.TryExecuteVendorUpscale(
                renderer,
                sourceColorFbo,
                sourceDepthFbo,
                sourceMotionFbo,
                sourceExposureFbo,
                in dispatchParameters,
                out XRTexture? bridgeOutput,
                out TimeSpan dispatchDuration,
                out string bridgeFailure);

            if (!ok || bridgeOutput is null)
            {
                if (ShouldRecreateBridgeAfterDispatchFailure(bridgeFailure))
                {
                    bridge.MarkNeedsRecreate(string.IsNullOrWhiteSpace(bridgeFailure)
                        ? $"{vendor} bridge dispatch failed"
                        : bridgeFailure);
                }

                failureReason = string.IsNullOrWhiteSpace(bridgeFailure)
                    ? $"bridge {vendor} submission failed"
                    : bridgeFailure;
                return false;
            }

            if (_diagEnabled)
            {
                Debug.Log(
                    ELogCategory.Rendering,
                    $"[VendorUpscaleDiag] BridgeVendor path. Vendor='{vendor}' SourceFBO='{DescribeFrameBuffer(sourceColorFbo)}' DepthFBO='{DescribeFrameBuffer(sourceDepthFbo)}' MotionFBO='{DescribeFrameBuffer(sourceMotionFbo)}' OutputTexture='{DescribeTexture(bridgeOutput)}' Slot={slot.SlotIndex} State='{bridge.State}' Recreate='{bridge.PendingRecreateReason ?? "<none>"}' DispatchMs={dispatchDuration.TotalMilliseconds:F3}");
            }

            _fallbackSourceTexture = bridgeOutput;
            _fallbackApplySharpen = vendor == EVulkanUpscaleBridgeVendor.Xess && RuntimeEngine.Rendering.Settings.XessSharpness > 0.0f;
            _fallbackSharpenStrength = _fallbackApplySharpen
                ? Math.Clamp(RuntimeEngine.Rendering.Settings.XessSharpness, 0.0f, 1.0f) * 0.35f
                : 0.0f;
            _lastBridgeVendor = vendor;
            _bridgeVendorHistoryValid = true;
            RememberBridgeDispatch(camera, bridge, dispatchParameters);
            _fallbackQuad?.Render(
                TargetFrameBufferName is not null
                    ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName)
                    : null);
            return true;
        }

        private static bool TryResolveBridgeVendor(out EVulkanUpscaleBridgeVendor vendor, out string failureReason)
        {
            vendor = default;
            failureReason = string.Empty;

            bool dlssEnabled = RuntimeEngine.EffectiveSettings.EnableNvidiaDlss;
            bool xessEnabled = RuntimeEngine.EffectiveSettings.EnableIntelXess;
            bool dlssSupported = dlssEnabled && NvidiaDlssManager.IsSupported;
            bool xessSupported = xessEnabled && IntelXessManager.IsSupported;

            if (RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot.DlssFirst)
            {
                if (dlssSupported)
                {
                    vendor = EVulkanUpscaleBridgeVendor.Dlss;
                    return true;
                }

                if (xessSupported)
                {
                    vendor = EVulkanUpscaleBridgeVendor.Xess;
                    return true;
                }
            }
            else
            {
                if (xessSupported)
                {
                    vendor = EVulkanUpscaleBridgeVendor.Xess;
                    return true;
                }

                if (dlssSupported)
                {
                    vendor = EVulkanUpscaleBridgeVendor.Dlss;
                    return true;
                }
            }

            failureReason = dlssEnabled && !string.IsNullOrWhiteSpace(NvidiaDlssManager.LastError)
                ? NvidiaDlssManager.LastError!
                : xessEnabled && !string.IsNullOrWhiteSpace(IntelXessManager.LastError)
                    ? IntelXessManager.LastError!
                    : "No supported bridge vendor runtime is currently available.";
            return false;
        }

        private bool TryCreateBridgeDispatchParameters(
            OpenGLRenderer renderer,
            XRViewport viewport,
            VulkanUpscaleBridge bridge,
            EVulkanUpscaleBridgeVendor vendor,
            XRCamera camera,
            ColorGradingSettings? colorGrading,
            XRFrameBuffer sourceColorFbo,
            XRFrameBuffer? sourceExposureFbo,
            out VulkanUpscaleBridgeDispatchParameters parameters,
            out string failureReason)
        {
            parameters = default;
            failureReason = string.Empty;

            Matrix4x4 cameraViewToClip = camera.ProjectionMatrixUnjittered;
            Matrix4x4 clipToCameraView = camera.InverseProjectionMatrixUnjittered;
            Matrix4x4 currentViewProjectionUnjittered = camera.ViewProjectionMatrixUnjittered;
            Matrix4x4 previousViewProjectionUnjittered = currentViewProjectionUnjittered;
            Vector2 jitter = Vector2.Zero;
            bool vendorChanged = _bridgeVendorHistoryValid && _lastBridgeVendor != vendor;
            bool resetHistory = !_bridgeDispatchHistoryValid || vendorChanged;

            if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
            {
                currentViewProjectionUnjittered = temporalData.CurrViewProjectionUnjittered;
                previousViewProjectionUnjittered = temporalData.HistoryReady
                    ? temporalData.PrevViewProjectionUnjittered
                    : temporalData.CurrViewProjectionUnjittered;
                jitter = temporalData.CurrentJitter;
                resetHistory |= !temporalData.HistoryReady;
            }
            else
            {
                resetHistory = true;
            }

            if (!Matrix4x4.Invert(currentViewProjectionUnjittered, out Matrix4x4 currentInverseViewProjectionUnjittered))
            {
                failureReason = "Failed to invert the current unjittered view-projection matrix for bridge vendor dispatch.";
                return false;
            }

            if (!Matrix4x4.Invert(previousViewProjectionUnjittered, out Matrix4x4 previousInverseViewProjectionUnjittered))
                previousInverseViewProjectionUnjittered = currentInverseViewProjectionUnjittered;

            var frameResources = bridge.CurrentFrameResources;
            float aspectRatio = camera.Parameters.GetApproximateAspectRatio();
            if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
                aspectRatio = Math.Max(1, viewport.Width) / (float)Math.Max(1, viewport.Height);

            float verticalFovRadians = camera.Parameters.GetApproximateVerticalFov() * (MathF.PI / 180.0f);
            if (!float.IsFinite(verticalFovRadians) || verticalFovRadians <= 0.0f)
                verticalFovRadians = 60.0f * (MathF.PI / 180.0f);

            bool outputHdr = ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? bridge.CurrentFrameResources.OutputHdr;
            float exposureScale = ResolveBridgeExposureScale(colorGrading);
            bool hasExposureTexture = sourceExposureFbo is not null;
            resetHistory |= ShouldResetBridgeHistory(
                camera,
                bridge,
                sourceColorFbo,
                outputHdr);

            parameters = new VulkanUpscaleBridgeDispatchParameters
            {
                Vendor = vendor,
                InputWidth = (uint)Math.Max(1, frameResources.InternalWidth),
                InputHeight = (uint)Math.Max(1, frameResources.InternalHeight),
                OutputWidth = (uint)Math.Max(1, frameResources.DisplayWidth),
                OutputHeight = (uint)Math.Max(1, frameResources.DisplayHeight),
                FrameIndex = unchecked((uint)Math.Max(0L, renderer._frameCounter)),
                ResetHistory = resetHistory,
                ReverseDepth = camera.IsReversedDepth,
                IsOrthographic = camera.Parameters is XROrthographicCameraParameters,
                OutputHdr = outputHdr,
                DlssQuality = frameResources.DlssQuality,
                XessQuality = frameResources.XessQuality,
                DlssSharpness = RuntimeEngine.Rendering.Settings.DlssSharpness,
                XessSharpness = RuntimeEngine.Rendering.Settings.XessSharpness,
                JitterOffsetX = jitter.X,
                JitterOffsetY = jitter.Y,
                HasExposureTexture = hasExposureTexture,
                ExposureScale = exposureScale,
                MotionVectorScaleX = BridgeMotionVectorNormalizationScale,
                MotionVectorScaleY = BridgeMotionVectorNormalizationScale,
                CameraViewToClip = cameraViewToClip,
                ClipToCameraView = clipToCameraView,
                ClipToPrevClip = currentInverseViewProjectionUnjittered * previousViewProjectionUnjittered,
                PrevClipToClip = previousInverseViewProjectionUnjittered * currentViewProjectionUnjittered,
                CameraPosition = camera.Transform?.RenderTranslation ?? Vector3.Zero,
                CameraUp = camera.Transform?.RenderUp ?? Vector3.UnitY,
                CameraRight = camera.Transform?.RenderRight ?? Vector3.UnitX,
                CameraForward = camera.Transform?.RenderForward ?? -Vector3.UnitZ,
                CameraNear = camera.NearZ,
                CameraFar = camera.FarZ,
                CameraFovRadians = verticalFovRadians,
                CameraAspectRatio = aspectRatio,
            };

            return true;
        }

        private bool TryResolveBridgeCamera(out XRCamera? camera, out string failureReason)
        {
            camera = ActivePipelineInstance.RenderState.SceneCamera
                ?? ActivePipelineInstance.RenderState.RenderingCamera
                ?? ActivePipelineInstance.LastSceneCamera
                ?? ActivePipelineInstance.LastRenderingCamera;
            if (camera is not null)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = "Bridge vendor upscale requires an active scene camera.";
            return false;
        }

        private static ColorGradingSettings? ResolveBridgeColorGradingSettings(XRCamera camera)
        {
            var stage = camera.GetPostProcessStageState<ColorGradingSettings>();
            if (stage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
                return grading;

            return null;
        }

        private bool TryResolveBridgeExposureSource(
            OpenGLRenderer renderer,
            ColorGradingSettings? colorGrading,
            out XRFrameBuffer? bridgeExposureFbo,
            out string failureReason)
        {
            bridgeExposureFbo = null;
            failureReason = string.Empty;

            if (colorGrading is null || !colorGrading.UseGpuAutoExposureThisFrame || string.IsNullOrWhiteSpace(AutoExposureTextureName))
                return true;

            XRTexture? exposureTexture = ActivePipelineInstance.GetTexture<XRTexture>(AutoExposureTextureName);
            if (exposureTexture is null)
                return true;

            return TryEnsureBridgeHelperFrameBuffer(
                renderer,
                exposureTexture,
                EFrameBufferAttachment.ColorAttachment0,
                "VendorUpscale.Bridge.ExposureSourceFBO",
                ref _bridgeExposureTextureFbo,
                ref _bridgeExposureTexture,
                out bridgeExposureFbo,
                out failureReason);
        }

        private static float ResolveBridgeExposureScale(ColorGradingSettings? colorGrading)
        {
            if (colorGrading is null)
                return 1.0f;

            float exposure = colorGrading.Exposure;
            bool useGpuExposure = colorGrading.UseGpuAutoExposureThisFrame;
            if (colorGrading.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical)
            {
                float physicalBase = colorGrading.ComputePhysicalExposureMultiplier();
                if (!colorGrading.AutoExposure || useGpuExposure)
                    exposure = physicalBase;
            }

            if (!float.IsFinite(exposure) || exposure <= 0.0f)
                return 1.0f;

            return exposure;
        }

        private bool ShouldResetBridgeHistory(
            XRCamera camera,
            VulkanUpscaleBridge bridge,
            XRFrameBuffer sourceColorFbo,
            bool outputHdr)
        {
            if (!_bridgeDispatchHistoryValid)
                return true;

            if (!ReferenceEquals(_lastBridgeCamera, camera)
                || !ReferenceEquals(_lastBridgeScene, ActivePipelineInstance.RenderState.Scene)
                || _lastBridgeGeneration != bridge.ResourceGeneration
                || _lastBridgeOutputHdr != outputHdr
                || _lastBridgeReverseDepth != camera.IsReversedDepth
                || _lastBridgeInputWidth != (uint)Math.Max(1, sourceColorFbo.Width)
                || _lastBridgeInputHeight != (uint)Math.Max(1, sourceColorFbo.Height)
                || _lastBridgeOutputWidth != (uint)Math.Max(1, bridge.CurrentFrameResources.DisplayWidth)
                || _lastBridgeOutputHeight != (uint)Math.Max(1, bridge.CurrentFrameResources.DisplayHeight))
            {
                return true;
            }

            if (IsLikelyCameraCut(camera))
                return true;

            return false;
        }

        private bool IsLikelyCameraCut(XRCamera camera)
        {
            Vector3 position = camera.Transform?.RenderTranslation ?? Vector3.Zero;
            Vector3 forward = NormalizeSafe(camera.Transform?.RenderForward ?? -Vector3.UnitZ, -Vector3.UnitZ);
            float positionDelta = Vector3.DistanceSquared(position, _lastBridgeCameraPosition);
            float forwardDot = Vector3.Dot(forward, _lastBridgeCameraForward);
            return positionDelta > 25.0f || forwardDot < 0.9f;
        }

        private void RememberBridgeDispatch(
            XRCamera camera,
            VulkanUpscaleBridge bridge,
            in VulkanUpscaleBridgeDispatchParameters dispatchParameters)
        {
            _bridgeDispatchHistoryValid = true;
            _lastBridgeCamera = camera;
            _lastBridgeScene = ActivePipelineInstance.RenderState.Scene;
            _lastBridgeGeneration = bridge.ResourceGeneration;
            _lastBridgeOutputHdr = dispatchParameters.OutputHdr;
            _lastBridgeReverseDepth = dispatchParameters.ReverseDepth;
            _lastBridgeInputWidth = dispatchParameters.InputWidth;
            _lastBridgeInputHeight = dispatchParameters.InputHeight;
            _lastBridgeOutputWidth = dispatchParameters.OutputWidth;
            _lastBridgeOutputHeight = dispatchParameters.OutputHeight;
            _lastBridgeCameraPosition = camera.Transform?.RenderTranslation ?? Vector3.Zero;
            _lastBridgeCameraForward = NormalizeSafe(camera.Transform?.RenderForward ?? -Vector3.UnitZ, -Vector3.UnitZ);
        }

        private static Vector3 NormalizeSafe(Vector3 value, Vector3 fallback)
            => value.LengthSquared() > 1.0e-8f ? Vector3.Normalize(value) : fallback;

        private static void ReportNativeFallback()
        {
            if (RuntimeEngine.EffectiveSettings.EnableIntelXess && !IntelXessManager.IsSupported && !_reportedXessUnavailable)
            {
                _reportedXessUnavailable = true;
                string reason = string.IsNullOrWhiteSpace(IntelXessManager.LastError)
                    ? "runtime unavailable"
                    : IntelXessManager.LastError!;
                Debug.RenderingError($"Intel XeSS is enabled but unavailable ({reason}). No fallback blit will be rendered for an explicit vendor request.");
            }

            if (RuntimeEngine.EffectiveSettings.EnableNvidiaDlss && !NvidiaDlssManager.IsSupported && !_reportedDlssUnavailable)
            {
                _reportedDlssUnavailable = true;
                string reason = string.IsNullOrWhiteSpace(NvidiaDlssManager.LastError)
                    ? "runtime unavailable"
                    : NvidiaDlssManager.LastError!;
                Debug.RenderingError($"NVIDIA DLSS is enabled but unavailable ({reason}). No fallback blit will be rendered for an explicit vendor request.");
            }
        }

        private void ReportBridgeFallback(XRViewport viewport, string failureReason)
        {
            string reason = string.IsNullOrWhiteSpace(failureReason)
                ? RuntimeEngine.Rendering.DescribeVulkanUpscaleBridgeUnavailability(
                    viewport,
                    ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false)
                : failureReason;

            if (RuntimeEngine.EffectiveSettings.EnableIntelXess && !_reportedXessApiMismatch)
            {
                _reportedXessApiMismatch = true;
                Debug.RenderingError($"Intel XeSS requires Vulkan or the OpenGL->Vulkan upscale bridge. {reason}. No fallback blit will be rendered for an explicit vendor request.");
            }

            if (RuntimeEngine.EffectiveSettings.EnableNvidiaDlss && !_reportedDlssApiMismatch)
            {
                _reportedDlssApiMismatch = true;
                Debug.RenderingError($"NVIDIA DLSS requires Vulkan or the OpenGL->Vulkan upscale bridge. {reason}. No fallback blit will be rendered for an explicit vendor request.");
            }
        }

        private bool TryResolveBridgeColorSource(
            OpenGLRenderer renderer,
            XRFrameBuffer? sourceFrameBuffer,
            XRTexture resolvedColorTexture,
            out XRFrameBuffer? bridgeSourceFbo,
            out string failureReason)
        {
            if (!string.IsNullOrWhiteSpace(SourceTextureName))
            {
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
            => RuntimeEngine.EffectiveSettings.EnableIntelXess || RuntimeEngine.EffectiveSettings.EnableNvidiaDlss;

        private static bool IsVendorFeatureRequested()
            => RuntimeEngine.EffectiveSettings.EnableIntelXess
            || RuntimeEngine.EffectiveSettings.EnableNvidiaDlss
            || RuntimeEngine.Rendering.Settings.EnableIntelXessFrameGeneration
            || NvidiaDlssManager.IsFrameGenerationRequested;

        private static void FailRequestedVendorFeature(string path, string failureReason)
        {
            string reason = string.IsNullOrWhiteSpace(failureReason)
                ? "no compatible vendor path completed"
                : failureReason;
            string message = $"Requested {path} failed: {reason}. No fallback blit will be rendered because a vendor upscaler or frame-generation mode was explicitly requested.";
            Debug.RenderingError(message);
            throw new InvalidOperationException(message);
        }

        private static bool ShouldRecreateBridgeAfterDispatchFailure(string failureReason)
            => !NvidiaDlssManager.Native.IsTerminalBridgeFailureMessage(failureReason);

        private static void DestroyBridgeHelperFrameBuffer(ref XRFrameBuffer? frameBuffer, ref XRTexture? cachedTexture)
        {
            frameBuffer?.Destroy();
            frameBuffer = null;
            cachedTexture = null;
        }

        private void DestroyNativeDlssResources()
        {
            _nativeDlssSession?.Dispose();
            _nativeDlssSession = null;
            _nativeDlssRenderer = null;
            _nativeDlssViewportId = 0;

            _nativeDlssOutputFbo?.Destroy();
            _nativeDlssOutputFbo = null;
            _nativeDlssOutputTexture?.Destroy();
            _nativeDlssOutputTexture = null;
            _nativeDlssOutputWidth = 0;
            _nativeDlssOutputHeight = 0;
            _nativeDlssOutputHdr = false;
            _nativeDlssDispatchHistoryValid = false;
        }

        private bool TryResolveNativeDlssDepthTexture(out XRTexture? depthTexture, out string failureReason)
        {
            depthTexture = null;
            failureReason = string.Empty;

            string? depthName = !string.IsNullOrWhiteSpace(DepthStencilTextureName)
                ? DepthStencilTextureName
                : DepthTextureName;
            if (string.IsNullOrWhiteSpace(depthName))
            {
                failureReason = "NVIDIA DLSS native dispatch requires a depth texture.";
                return false;
            }

            depthTexture = ActivePipelineInstance.GetTexture<XRTexture>(depthName);
            if (depthTexture is not null)
                return true;

            failureReason = $"NVIDIA DLSS depth texture '{depthName}' was not found.";
            return false;
        }

        private bool TryResolveNativeDlssMotionTexture(out XRTexture? motionTexture, out string failureReason)
        {
            motionTexture = null;
            failureReason = string.Empty;

            if (!string.IsNullOrWhiteSpace(MotionFrameBufferName))
            {
                if (VPRCSourceTextureHelpers.TryResolveColorTexture(
                        ActivePipelineInstance,
                        null,
                        MotionFrameBufferName,
                        out motionTexture,
                        out failureReason)
                    && motionTexture is not null)
                {
                    return true;
                }

                failureReason = string.IsNullOrWhiteSpace(failureReason)
                    ? $"NVIDIA DLSS motion framebuffer '{MotionFrameBufferName}' did not expose a color texture."
                    : failureReason;
                return false;
            }

            if (string.IsNullOrWhiteSpace(MotionTextureName))
            {
                failureReason = "NVIDIA DLSS native dispatch requires a motion-vector texture.";
                return false;
            }

            motionTexture = ActivePipelineInstance.GetTexture<XRTexture>(MotionTextureName);
            if (motionTexture is not null)
                return true;

            failureReason = $"NVIDIA DLSS motion texture '{MotionTextureName}' was not found.";
            return false;
        }

        private XRTexture? ResolveNativeDlssExposureTexture(ColorGradingSettings? colorGrading)
        {
            if (colorGrading is null || !colorGrading.UseGpuAutoExposureThisFrame || string.IsNullOrWhiteSpace(AutoExposureTextureName))
                return null;

            return ActivePipelineInstance.GetTexture<XRTexture>(AutoExposureTextureName);
        }

        private (uint Width, uint Height) ResolveNativeDlssOutputExtent(XRViewport viewport)
        {
            XRFrameBuffer? target = TargetFrameBufferName is not null
                ? ActivePipelineInstance.GetFBO<XRFrameBuffer>(TargetFrameBufferName)
                : null;

            uint width = target is not null
                ? Math.Max(1u, target.Width)
                : (uint)Math.Max(1, viewport.Width);
            uint height = target is not null
                ? Math.Max(1u, target.Height)
                : (uint)Math.Max(1, viewport.Height);
            return (width, height);
        }

        private static bool ValidateNativeDlssInputSizes(
            XRTexture sourceColor,
            XRTexture depth,
            XRTexture motion,
            uint outputWidth,
            uint outputHeight,
            out string failureReason)
        {
            (uint sourceWidth, uint sourceHeight) = ResolveTextureExtent(sourceColor);
            (uint depthWidth, uint depthHeight) = ResolveTextureExtent(depth);
            (uint motionWidth, uint motionHeight) = ResolveTextureExtent(motion);

            if (depthWidth != sourceWidth || depthHeight != sourceHeight)
            {
                failureReason = $"NVIDIA DLSS depth size mismatch: expected {sourceWidth}x{sourceHeight}, got {depthWidth}x{depthHeight}.";
                return false;
            }

            if (motionWidth != sourceWidth || motionHeight != sourceHeight)
            {
                failureReason = $"NVIDIA DLSS motion size mismatch: expected {sourceWidth}x{sourceHeight}, got {motionWidth}x{motionHeight}.";
                return false;
            }

            if (outputWidth == 0 || outputHeight == 0)
            {
                failureReason = $"NVIDIA DLSS output extent is invalid: {outputWidth}x{outputHeight}.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        private bool TryEnsureNativeDlssOutputTexture(
            uint width,
            uint height,
            bool outputHdr,
            out XRTexture2D? outputTexture,
            out string failureReason)
        {
            outputTexture = null;
            failureReason = string.Empty;

            if (_nativeDlssOutputTexture is not null
                && _nativeDlssOutputWidth == width
                && _nativeDlssOutputHeight == height
                && _nativeDlssOutputHdr == outputHdr)
            {
                outputTexture = _nativeDlssOutputTexture;
                return true;
            }

            _nativeDlssOutputFbo?.Destroy();
            _nativeDlssOutputFbo = null;
            _nativeDlssOutputTexture?.Destroy();
            _nativeDlssOutputTexture = null;

            EPixelInternalFormat internalFormat = outputHdr
                ? EPixelInternalFormat.Rgba16f
                : EPixelInternalFormat.Rgba8;
            EPixelType pixelType = outputHdr
                ? EPixelType.HalfFloat
                : EPixelType.UnsignedByte;
            ESizedInternalFormat sizedFormat = outputHdr
                ? ESizedInternalFormat.Rgba16f
                : ESizedInternalFormat.Rgba8;

            XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
                width,
                height,
                internalFormat,
                EPixelFormat.Rgba,
                pixelType,
                EFrameBufferAttachment.ColorAttachment0);
            texture.Name = "VendorUpscale.NativeDlss.Output";
            texture.SamplerName = "VendorUpscale.NativeDlss.OutputTexture";
            texture.Resizable = false;
            texture.SizedInternalFormat = sizedFormat;
            texture.RequiresStorageUsage = true;
            texture.AutoGenerateMipmaps = false;

            _nativeDlssOutputFbo = new XRFrameBuffer((texture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = "VendorUpscale.NativeDlss.OutputFBO",
            };

            _nativeDlssOutputTexture = texture;
            _nativeDlssOutputWidth = width;
            _nativeDlssOutputHeight = height;
            _nativeDlssOutputHdr = outputHdr;
            _nativeDlssSession?.ResetResources();
            _nativeDlssDispatchHistoryValid = false;
            outputTexture = texture;
            return true;
        }

        private bool TryEnsureNativeDlssSession(
            VulkanRenderer renderer,
            XRViewport viewport,
            out string failureReason)
        {
            failureReason = string.Empty;
            uint viewportId = unchecked((uint)viewport.GetHashCode());
            if (viewportId == 0)
                viewportId = 1;

            if (_nativeDlssSession is not null
                && ReferenceEquals(_nativeDlssRenderer, renderer)
                && _nativeDlssViewportId == viewportId)
            {
                return true;
            }

            _nativeDlssSession?.Dispose();
            _nativeDlssSession = null;
            _nativeDlssRenderer = null;
            _nativeDlssViewportId = 0;
            _nativeDlssDispatchHistoryValid = false;

            if (!NvidiaDlssManager.Native.TryCreateNativeVulkanSession(
                    renderer,
                    viewportId,
                    out NvidiaDlssManager.Native.NativeVulkanSession? session,
                    out failureReason)
                || session is null)
            {
                return false;
            }

            _nativeDlssSession = session;
            _nativeDlssRenderer = renderer;
            _nativeDlssViewportId = viewportId;
            return true;
        }

        private bool TryCreateNativeDlssDispatchParameters(
            VulkanRenderer renderer,
            XRViewport viewport,
            XRCamera camera,
            ColorGradingSettings? colorGrading,
            XRTexture sourceColorTexture,
            uint outputWidth,
            uint outputHeight,
            bool outputHdr,
            bool hasExposureTexture,
            out VulkanUpscaleBridgeDispatchParameters parameters,
            out string failureReason)
        {
            parameters = default;
            failureReason = string.Empty;

            Matrix4x4 cameraViewToClip = camera.ProjectionMatrixUnjittered;
            Matrix4x4 clipToCameraView = camera.InverseProjectionMatrixUnjittered;
            Matrix4x4 currentViewProjectionUnjittered = camera.ViewProjectionMatrixUnjittered;
            Matrix4x4 previousViewProjectionUnjittered = currentViewProjectionUnjittered;
            Vector2 jitter = Vector2.Zero;
            bool resetHistory = !_nativeDlssDispatchHistoryValid;

            if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
            {
                currentViewProjectionUnjittered = temporalData.CurrViewProjectionUnjittered;
                previousViewProjectionUnjittered = temporalData.HistoryReady
                    ? temporalData.PrevViewProjectionUnjittered
                    : temporalData.CurrViewProjectionUnjittered;
                jitter = temporalData.CurrentJitter;
                resetHistory |= !temporalData.HistoryReady;
            }
            else
            {
                resetHistory = true;
            }

            if (!Matrix4x4.Invert(currentViewProjectionUnjittered, out Matrix4x4 currentInverseViewProjectionUnjittered))
            {
                failureReason = "Failed to invert the current unjittered view-projection matrix for native NVIDIA DLSS dispatch.";
                return false;
            }

            if (!Matrix4x4.Invert(previousViewProjectionUnjittered, out Matrix4x4 previousInverseViewProjectionUnjittered))
                previousInverseViewProjectionUnjittered = currentInverseViewProjectionUnjittered;

            float aspectRatio = camera.Parameters.GetApproximateAspectRatio();
            if (!float.IsFinite(aspectRatio) || aspectRatio <= 0.0f)
                aspectRatio = Math.Max(1, viewport.Width) / (float)Math.Max(1, viewport.Height);

            float verticalFovRadians = camera.Parameters.GetApproximateVerticalFov() * (MathF.PI / 180.0f);
            if (!float.IsFinite(verticalFovRadians) || verticalFovRadians <= 0.0f)
                verticalFovRadians = 60.0f * (MathF.PI / 180.0f);

            (uint inputWidth, uint inputHeight) = ResolveTextureExtent(sourceColorTexture);
            resetHistory |= ShouldResetNativeDlssHistory(
                camera,
                sourceColorTexture,
                outputWidth,
                outputHeight,
                outputHdr);

            parameters = new VulkanUpscaleBridgeDispatchParameters
            {
                Vendor = EVulkanUpscaleBridgeVendor.Dlss,
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                FrameIndex = unchecked((uint)Math.Min(uint.MaxValue, renderer.VulkanFrameCounter)),
                ResetHistory = resetHistory,
                ReverseDepth = camera.IsReversedDepth,
                IsOrthographic = camera.Parameters is XROrthographicCameraParameters,
                OutputHdr = outputHdr,
                DlssQuality = RuntimeEngine.EffectiveSettings.DlssQuality,
                XessQuality = RuntimeEngine.EffectiveSettings.XessQuality,
                DlssSharpness = RuntimeEngine.Rendering.Settings.DlssSharpness,
                XessSharpness = RuntimeEngine.Rendering.Settings.XessSharpness,
                JitterOffsetX = jitter.X,
                JitterOffsetY = jitter.Y,
                HasExposureTexture = hasExposureTexture,
                ExposureScale = ResolveBridgeExposureScale(colorGrading),
                MotionVectorScaleX = BridgeMotionVectorNormalizationScale,
                MotionVectorScaleY = BridgeMotionVectorNormalizationScale,
                CameraViewToClip = cameraViewToClip,
                ClipToCameraView = clipToCameraView,
                ClipToPrevClip = currentInverseViewProjectionUnjittered * previousViewProjectionUnjittered,
                PrevClipToClip = previousInverseViewProjectionUnjittered * currentViewProjectionUnjittered,
                CameraPosition = camera.Transform?.RenderTranslation ?? Vector3.Zero,
                CameraUp = camera.Transform?.RenderUp ?? Vector3.UnitY,
                CameraRight = camera.Transform?.RenderRight ?? Vector3.UnitX,
                CameraForward = camera.Transform?.RenderForward ?? -Vector3.UnitZ,
                CameraNear = camera.NearZ,
                CameraFar = camera.FarZ,
                CameraFovRadians = verticalFovRadians,
                CameraAspectRatio = aspectRatio,
            };

            return true;
        }

        private bool ShouldResetNativeDlssHistory(
            XRCamera camera,
            XRTexture sourceColorTexture,
            uint outputWidth,
            uint outputHeight,
            bool outputHdr)
        {
            if (!_nativeDlssDispatchHistoryValid)
                return true;

            (uint inputWidth, uint inputHeight) = ResolveTextureExtent(sourceColorTexture);
            if (!ReferenceEquals(_lastNativeDlssCamera, camera)
                || !ReferenceEquals(_lastNativeDlssScene, ActivePipelineInstance.RenderState.Scene)
                || _lastNativeDlssOutputHdr != outputHdr
                || _lastNativeDlssReverseDepth != camera.IsReversedDepth
                || _lastNativeDlssInputWidth != inputWidth
                || _lastNativeDlssInputHeight != inputHeight
                || _lastNativeDlssOutputWidth != outputWidth
                || _lastNativeDlssOutputHeight != outputHeight)
            {
                return true;
            }

            if (IsLikelyNativeDlssCameraCut(camera))
                return true;

            return false;
        }

        private bool IsLikelyNativeDlssCameraCut(XRCamera camera)
        {
            Vector3 position = camera.Transform?.RenderTranslation ?? Vector3.Zero;
            Vector3 forward = NormalizeSafe(camera.Transform?.RenderForward ?? -Vector3.UnitZ, -Vector3.UnitZ);
            float positionDelta = Vector3.DistanceSquared(position, _lastNativeDlssCameraPosition);
            float forwardDot = Vector3.Dot(forward, _lastNativeDlssCameraForward);
            return positionDelta > 25.0f || forwardDot < 0.9f;
        }

        private void RememberNativeDlssDispatch(
            XRCamera camera,
            in VulkanUpscaleBridgeDispatchParameters dispatchParameters)
        {
            _nativeDlssDispatchHistoryValid = true;
            _lastNativeDlssCamera = camera;
            _lastNativeDlssScene = ActivePipelineInstance.RenderState.Scene;
            _lastNativeDlssOutputHdr = dispatchParameters.OutputHdr;
            _lastNativeDlssReverseDepth = dispatchParameters.ReverseDepth;
            _lastNativeDlssInputWidth = dispatchParameters.InputWidth;
            _lastNativeDlssInputHeight = dispatchParameters.InputHeight;
            _lastNativeDlssOutputWidth = dispatchParameters.OutputWidth;
            _lastNativeDlssOutputHeight = dispatchParameters.OutputHeight;
            _lastNativeDlssCameraPosition = camera.Transform?.RenderTranslation ?? Vector3.Zero;
            _lastNativeDlssCameraForward = NormalizeSafe(camera.Transform?.RenderForward ?? -Vector3.UnitZ, -Vector3.UnitZ);
        }

        private static (uint Width, uint Height) ResolveTextureExtent(XRTexture texture)
        {
            Vector3 size = texture.WidthHeightDepth;
            return ((uint)Math.Max(1.0f, size.X), (uint)Math.Max(1.0f, size.Y));
        }

        private bool TryRunNativeVulkanVendor(out string failureReason)
        {
            failureReason = string.Empty;

            bool preferDlss = RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot.DlssFirst;
            if (preferDlss)
            {
                if (TryRunDlss(out failureReason))
                    return true;

                if (TryRunXess(out string xessFailure))
                    return true;

                if (string.IsNullOrWhiteSpace(failureReason))
                    failureReason = xessFailure;
                return false;
            }

            if (TryRunXess(out failureReason))
                return true;

            if (TryRunDlss(out string dlssFailure))
                return true;

            if (string.IsNullOrWhiteSpace(failureReason))
                failureReason = dlssFailure;
            return false;
        }

        private bool TryRunXess(out string failureReason)
        {
            failureReason = string.Empty;

            if (!RuntimeEngine.EffectiveSettings.EnableIntelXess && !RuntimeEngine.Rendering.Settings.EnableIntelXessFrameGeneration)
                return false;

            if (FrameBufferName is null)
            {
                failureReason = "Intel XeSS requires a source framebuffer.";
                return false;
            }

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
            {
                failureReason = "Intel XeSS requires an active viewport.";
                return false;
            }

            if (viewport.Window?.Renderer is not VulkanRenderer)
            {
                failureReason = "Intel XeSS native dispatch requires the Vulkan renderer.";
                return false;
            }

            if (!RuntimeEngine.EffectiveSettings.EnableIntelXess)
            {
                failureReason = "Intel XeSS frame generation requires Intel XeSS upscaling to be enabled.";
                return false;
            }

            if (!IntelXessManager.IsSupported)
            {
                failureReason = IntelXessManager.LastError ?? "Intel XeSS support probe failed.";
                return false;
            }

            var sourceFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
            if (sourceFbo is null)
            {
                failureReason = $"Intel XeSS source framebuffer '{FrameBufferName}' was not found or is not an XRQuadFrameBuffer.";
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

            // Keep the internal resolution aligned with XeSS expectations.
            IntelXessManager.ApplyToViewport(viewport, RuntimeEngine.Rendering.Settings);

            if (RuntimeEngine.Rendering.Settings.EnableIntelXessFrameGeneration)
            {
                bool frameGenOk = IntelXessManager.Native.TryDispatchFrameGeneration(
                    viewport,
                    sourceFbo,
                    motion,
                    out int frameGenError,
                    out string? frameGenMessage);

                if (!frameGenOk)
                {
                    failureReason = $"Intel XeSS frame generation failed ({frameGenMessage ?? $"errorCode={frameGenError}"}).";
                    if (!_reportedXessFrameGenUnavailable)
                    {
                        _reportedXessFrameGenUnavailable = true;
                        Debug.RenderingError(failureReason);
                    }
                    return false;
                }
            }

            bool upscaleOk = IntelXessManager.Native.TryDispatchUpscale(
                viewport,
                sourceFbo,
                destination,
                depth,
                motion,
                RuntimeEngine.Rendering.Settings.XessSharpness,
                out int errorCode);

            if (upscaleOk)
                return true;

            failureReason = IntelXessManager.LastError ?? $"errorCode={errorCode}";
            if (!_reportedXessFailure)
            {
                _reportedXessFailure = true;
                Debug.RenderingError($"Intel XeSS upscale failed ({failureReason}).");
            }

            return false;
        }

        private bool TryRunDlss(out string failureReason)
        {
            failureReason = string.Empty;
            bool dlssRequested = RuntimeEngine.EffectiveSettings.EnableNvidiaDlss;
            bool frameGenRequested = NvidiaDlssManager.IsFrameGenerationRequested;

            if (!dlssRequested && !frameGenRequested)
                return false;

            if (FrameBufferName is null)
            {
                failureReason = "NVIDIA DLSS requires a source framebuffer.";
                return false;
            }

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
            {
                failureReason = "NVIDIA DLSS requires an active viewport.";
                return false;
            }

            if (viewport.Window?.Renderer is not VulkanRenderer renderer)
            {
                failureReason = "NVIDIA DLSS native dispatch requires the Vulkan renderer.";
                return false;
            }

            if (frameGenRequested && !dlssRequested)
            {
                failureReason = "NVIDIA DLSS frame generation requires NVIDIA DLSS upscaling to be enabled.";
                return false;
            }

            if (!NvidiaDlssManager.IsSupported)
            {
                failureReason = NvidiaDlssManager.LastError ?? "NVIDIA DLSS support probe failed.";
                return false;
            }

            if (!VPRCSourceTextureHelpers.TryResolveColorTexture(
                    ActivePipelineInstance,
                    SourceTextureName,
                    FrameBufferName,
                    out XRTexture? sourceColorTexture,
                    out string colorFailure)
                || sourceColorTexture is null)
            {
                failureReason = colorFailure;
                return false;
            }

            if (!TryResolveNativeDlssDepthTexture(out XRTexture? depthTexture, out string depthFailure)
                || depthTexture is null)
            {
                failureReason = depthFailure;
                return false;
            }

            if (!TryResolveNativeDlssMotionTexture(out XRTexture? motionTexture, out string motionFailure)
                || motionTexture is null)
            {
                failureReason = motionFailure;
                return false;
            }

            if (!TryResolveBridgeCamera(out XRCamera? camera, out string cameraFailure) || camera is null)
            {
                failureReason = cameraFailure;
                return false;
            }

            ColorGradingSettings? colorGrading = ResolveBridgeColorGradingSettings(camera);
            XRTexture? exposureTexture = ResolveNativeDlssExposureTexture(colorGrading);

            bool outputHdr = ActivePipelineInstance.EffectiveOutputHDRThisFrame ?? false;
            (uint outputWidth, uint outputHeight) = ResolveNativeDlssOutputExtent(viewport);

            if (!ValidateNativeDlssInputSizes(sourceColorTexture, depthTexture, motionTexture, outputWidth, outputHeight, out string sizeFailure))
            {
                failureReason = sizeFailure;
                return false;
            }

            if (!TryEnsureNativeDlssOutputTexture(outputWidth, outputHeight, outputHdr, out XRTexture2D? outputTexture, out string outputFailure)
                || outputTexture is null)
            {
                failureReason = outputFailure;
                return false;
            }

            if (!TryEnsureNativeDlssSession(renderer, viewport, out string sessionFailure))
            {
                failureReason = sessionFailure;
                return false;
            }

            if (!TryCreateNativeDlssDispatchParameters(
                    renderer,
                    viewport,
                    camera,
                    colorGrading,
                    sourceColorTexture,
                    outputWidth,
                    outputHeight,
                    outputHdr,
                    exposureTexture is not null,
                    out VulkanUpscaleBridgeDispatchParameters dispatchParameters,
                    out string dispatchFailure))
            {
                failureReason = dispatchFailure;
                return false;
            }

            if (!renderer.TryResolveStreamlineImage(sourceColorTexture, depthOnly: false, out VulkanRenderer.VulkanStreamlineImage sourceColorImage, out string sourceImageFailure))
            {
                failureReason = sourceImageFailure;
                return false;
            }

            if (!renderer.TryResolveStreamlineImage(depthTexture, depthOnly: true, out VulkanRenderer.VulkanStreamlineImage depthImage, out string depthImageFailure))
            {
                failureReason = depthImageFailure;
                return false;
            }

            if (!renderer.TryResolveStreamlineImage(motionTexture, depthOnly: false, out VulkanRenderer.VulkanStreamlineImage motionImage, out string motionImageFailure))
            {
                failureReason = motionImageFailure;
                return false;
            }

            if (!renderer.TryResolveStreamlineImage(outputTexture, depthOnly: false, out VulkanRenderer.VulkanStreamlineImage outputImage, out string outputImageFailure))
            {
                failureReason = outputImageFailure;
                return false;
            }

            VulkanRenderer.VulkanStreamlineImage? exposureImage = null;
            if (exposureTexture is not null)
            {
                if (!renderer.TryResolveStreamlineImage(exposureTexture, depthOnly: false, out VulkanRenderer.VulkanStreamlineImage resolvedExposure, out string exposureFailure))
                {
                    failureReason = exposureFailure;
                    return false;
                }

                exposureImage = resolvedExposure;
            }

            if (frameGenRequested)
            {
                bool frameGenOk = NvidiaDlssManager.Native.TryDispatchFrameGeneration(
                    viewport,
                    in dispatchParameters,
                    in depthImage,
                    in motionImage,
                    in outputImage,
                    NvidiaDlssManager.ResolveFrameGenerationMode(),
                    out int frameGenError,
                    out string? frameGenMessage);

                if (!frameGenOk)
                {
                    failureReason = frameGenMessage ?? NvidiaDlssManager.Native.LastError ?? $"errorCode={frameGenError}";
                    if (!_reportedDlssFrameGenUnavailable)
                    {
                        _reportedDlssFrameGenUnavailable = true;
                        Debug.RenderingError($"NVIDIA DLSS frame generation failed ({failureReason}).");
                    }
                    return false;
                }
            }

            string destination = ResolveDestinationLabel(ActivePipelineInstance);
            string passName = BuildQuadBlitPassName(FrameBufferName, destination);
            int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
            if (passIndex == int.MinValue && hasRenderGraphMetadata)
            {
                failureReason = $"NVIDIA DLSS native dispatch could not resolve render-graph pass '{passName}'.";
                return false;
            }

            renderer.EnqueueDlssUpscale(
                passIndex,
                _nativeDlssSession!,
                sourceColorImage,
                depthImage,
                motionImage,
                outputImage,
                exposureImage,
                dispatchParameters);

            _fallbackSourceTexture = outputTexture;
            _fallbackApplySharpen = false;
            _fallbackSharpenStrength = 0.0f;
            RememberNativeDlssDispatch(camera, dispatchParameters);

            XRFrameBuffer? destFbo = ResolveDestinationFbo(
                ActivePipelineInstance,
                ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(FrameBufferName));

            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            _fallbackQuad?.Render(destFbo);

            if (_diagEnabled)
            {
                Debug.Log(
                    ELogCategory.Rendering,
                    $"[VendorUpscaleDiag] NativeVulkanDLSS path. Source='{DescribeTexture(sourceColorTexture)}' Depth='{DescribeTexture(depthTexture)}' Motion='{DescribeTexture(motionTexture)}' Output='{DescribeTexture(outputTexture)}' Pass='{passName}' Frame={dispatchParameters.FrameIndex} Reset={dispatchParameters.ResetHistory}");
            }

            return true;
        }
    }
}
