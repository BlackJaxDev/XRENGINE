using System;
using System.Numerics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Applies the engine FXAA shader as a standalone composable fullscreen pass.
/// </summary>
public sealed class VPRC_FXAA : ViewportRenderCommand
{
    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? DestinationFBOName { get; set; }

    public override string GpuProfilingName
        => string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName)
            ? nameof(VPRC_FXAA)
            : $"{nameof(VPRC_FXAA)}:{SourceTextureName ?? SourceFBOName}";

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        _material = new(Array.Empty<XRTexture?>(), XRShader.EngineShader(Path.Combine(SceneShaderPath, "FXAA.fs"), EShaderType.Fragment))
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

        _quad = new XRQuadFrameBuffer(_material);
        _quad.SettingUniforms += Fxaa_SettingUniforms;
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
        {
            _quad.SettingUniforms -= Fxaa_SettingUniforms;
            _quad.Destroy();
            _quad = null;
        }

        _material?.Destroy();
        _material = null;
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (_quad is null ||
            !VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
            || sourceTexture is null)
            return;

        XRFrameBuffer? destination = null;
        if (!string.IsNullOrWhiteSpace(DestinationFBOName))
        {
            destination = instance.GetFBO<XRFrameBuffer>(DestinationFBOName!);
            if (destination is null)
                return;
        }

        _quad.Render(destination);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        string? source = !string.IsNullOrWhiteSpace(SourceTextureName)
            ? MakeTextureResource(SourceTextureName!)
            : !string.IsNullOrWhiteSpace(SourceFBOName)
                ? MakeFboColorResource(SourceFBOName!)
                : null;

        if (source is null)
            return;

        string destination = DestinationFBOName
            ?? context.CurrentRenderTarget?.Name
            ?? RenderGraphResourceNames.OutputRenderTarget;

        context.GetOrCreateSyntheticPass($"Fxaa_{GetSourceDisplayName()}_to_{destination}")
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
    }

    private string GetSourceDisplayName()
        => SourceTextureName ?? SourceFBOName ?? "Output";

    private void Fxaa_SettingUniforms(XRRenderProgram program)
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
            || sourceTexture is null)
            return;

        program.Sampler("Texture0", sourceTexture, 0);

        Vector2 texelStep = ResolveTexelStep(instance);
        program.Uniform("FxaaTexelStep", texelStep);
    }

    private Vector2 ResolveTexelStep(XRRenderPipelineInstance instance)
    {
        float width = 1.0f;
        float height = 1.0f;

        if (!string.IsNullOrWhiteSpace(DestinationFBOName) &&
            instance.GetFBO<XRFrameBuffer>(DestinationFBOName!) is XRFrameBuffer destination)
        {
            width = Math.Max(1u, destination.Width);
            height = Math.Max(1u, destination.Height);
        }
        else if (instance.RenderState.CurrentRenderRegion is { Width: > 0, Height: > 0 } region)
        {
            width = region.Width;
            height = region.Height;
        }
        else if (instance.RenderState.OutputFBO is XRFrameBuffer output)
        {
            width = Math.Max(1u, output.Width);
            height = Math.Max(1u, output.Height);
        }
        else if ((instance.RenderState.WindowViewport ?? instance.LastWindowViewport) is XRViewport viewport)
        {
            width = Math.Max(1, viewport.Width);
            height = Math.Max(1, viewport.Height);
        }

        return new Vector2(1.0f / width, 1.0f / height);
    }
}