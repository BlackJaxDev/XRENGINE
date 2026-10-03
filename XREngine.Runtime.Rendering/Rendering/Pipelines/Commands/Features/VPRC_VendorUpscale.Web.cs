using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Pipelines.Commands;

public partial class VPRC_VendorUpscale
{
    public override void DescribeRequirements(RenderPipelineRequirements requirements)
    {
        base.DescribeRequirements(requirements);
        if (requirements.Backend != RendererBackendId.WebGPU)
            return;
        requirements.RequireRasterProgram(ParentPipeline is AdvancedRenderPipeline
            ? "advanced::vendor-fallback" : "vendor-fallback");
        if (requirements.OutputProfile.Stereo || requirements.OutputProfile.ViewCount != 1)
            requirements.Diagnostics.Add("The cooked no-vendor presentation operation requires mono output.");
        if (requirements.OutputProfile.AntiAliasingMode == EAntiAliasingMode.Dlaa)
            requirements.Diagnostics.Add("NVIDIA DLAA requires a native vendor service; the WebGPU presentation operation cannot replace it.");
        if (string.IsNullOrWhiteSpace(FrameBufferName))
            requirements.Diagnostics.Add("The cooked no-vendor presentation operation requires an explicit source framebuffer.");
    }

    private XRQuadFrameBuffer? _webFallbackQuad;
    private XRTexture? _webFallbackSource;
    private bool _webFallbackEncodeOutputSrgb;

    /// <summary>Executes only the explicitly selected no-vendor presentation operation.</summary>
    private void ExecuteWebFallback()
    {
        if (IsVendorFeatureRequested())
            throw new NotSupportedException("WebGPU.VendorUpscale.NativeUnsupported: NVIDIA DLSS/DLAA, Intel XeSS, and vendor frame generation have no WebGPU implementation.");

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (instance.RenderState.StereoPass)
            throw new NotSupportedException("WebGPU.VendorUpscale.StereoUnsupported: the cooked fallback presentation requires mono output.");
        if (FrameBufferName is null)
            throw new InvalidOperationException("WebGPU.VendorUpscale.SourceMissing: fallback presentation requires a named source framebuffer.");
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, FrameBufferName,
            out XRTexture? source, out string failure) || source is null)
            throw new InvalidOperationException($"WebGPU.VendorUpscale.SourceMissing: {failure}");
        if (source is not (XRTexture2D or XRTexture2DView))
            throw new NotSupportedException("WebGPU.VendorUpscale.SourceUnsupported: fallback presentation requires a two-dimensional color texture.");

        XRQuadFrameBuffer? sourceFbo = instance.GetFBO<XRQuadFrameBuffer>(FrameBufferName);
        XRFrameBuffer? destination = ResolveDestinationFbo(instance, sourceFbo);
        if (TargetFrameBufferName is not null && destination is null)
            throw new InvalidOperationException($"WebGPU.VendorUpscale.TargetMissing: destination '{TargetFrameBufferName}' was not declared.");
        if (RenderToSourceFrameBuffer && destination is null)
            throw new InvalidOperationException("WebGPU.VendorUpscale.TargetMissing: the selected source framebuffer cannot be resolved as a destination.");

        string passName = BuildQuadBlitPassName(FrameBufferName, ResolveDestinationLabel(instance), RenderGraphPassVariant);
        int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
        if (passIndex == int.MinValue && hasRenderGraphMetadata)
            throw new InvalidOperationException($"WebGPU.VendorUpscale.PassMissing: selected fallback presentation pass '{passName}' is absent from the render graph.");

        EnsureWebFallbackResources(instance);
        _webFallbackSource = source;
        // The default tonemap already applies its authored display gamma. Advanced
        // keeps post-processing linear until the non-sRGB canvas attachment.
        _webFallbackEncodeOutputSrgb = destination is null && instance.Pipeline is AdvancedRenderPipeline;
        try
        {
            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;
            using var areaScope = destination is { Width: > 0, Height: > 0 }
                ? instance.RenderState.PushRenderArea((int)destination.Width, (int)destination.Height)
                : default;
            if (destination is not null)
                VPRCFullscreenPassContract.ValidateAndLog(instance, passName, destination, source, stereo: false);
            _webFallbackQuad!.Render(destination);
        }
        finally
        {
            _webFallbackSource = null;
            _webFallbackEncodeOutputSrgb = false;
        }
    }

    private void EnsureWebFallbackResources(XRRenderPipelineInstance instance)
    {
        if (_webFallbackQuad is not null)
            return;
        RenderPipeline pipeline = instance.Pipeline
            ?? throw new InvalidOperationException("WebGPU.VendorUpscale.PipelineMissing: fallback presentation requires an owning pipeline.");
        XRShader[] shaders = WebPipelineRasterProgram.CreateShaders(
            pipeline, pipeline is AdvancedRenderPipeline ? "advanced::vendor-fallback" : "vendor-fallback");
        XRMaterial material = new(Array.Empty<XRTexture?>(), shaders)
        {
            RenderOptions = new RenderingParameters
            {
                DepthTest = { Enabled = ERenderParamUsage.Disabled, Function = EComparison.Always, UpdateDepth = false },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
            },
        };
        try
        {
            _webFallbackQuad = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false, prepareForInitialRendering: false);
            _webFallbackQuad.SettingUniforms += SetWebFallbackUniforms;
            WebPipelineRasterProgram.OwnMaterial(_webFallbackQuad);
        }
        catch
        {
            _webFallbackQuad?.Destroy(true);
            _webFallbackQuad = null;
            material.Destroy(true);
            foreach (XRShader shader in shaders)
                shader.Destroy(true);
            throw;
        }
    }

    private void SetWebFallbackUniforms(XRRenderProgram program)
    {
        if (_webFallbackSource is null)
            throw new InvalidOperationException("WebGPU.VendorUpscale.SourceMissing: fallback bindings require the selected presentation source.");
        program.Sampler("SourceTexture", _webFallbackSource, 0);
        program.Uniform("ApplySharpen", 0.0f);
        program.Uniform("FlipSourceYOnVulkanFallback", FlipSourceYOnVulkanFallback ? 1.0f : 0.0f);
        program.Uniform("EncodeOutputSrgb", _webFallbackEncodeOutputSrgb ? 1.0f : 0.0f);
        program.Uniform("SharpenStrength", 0.0f);
    }

    private void ReleaseWebFallbackResources()
    {
        if (_webFallbackQuad is not null)
        {
            _webFallbackQuad.SettingUniforms -= SetWebFallbackUniforms;
            _webFallbackQuad.Destroy();
            _webFallbackQuad = null;
        }
        _webFallbackSource = null;
        _webFallbackEncodeOutputSrgb = false;
    }
}
