using System;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

public enum EDebugHeatmapSourceChannel
{
    Luminance = 0,
    Red = 1,
    Green = 2,
    Blue = 3,
    Alpha = 4,
    MaxRgb = 5,
}

/// <summary>
/// Visualizes a scalar texture channel as a false-color heatmap.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DebugHeatmap : ViewportRenderCommand
{
    private const string HeatmapShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform int ChannelMode;
uniform float ValueMin;
uniform float ValueMax;
uniform float ValueScale;
uniform float ValueBias;
uniform float OverlayAlpha;
uniform int UseLogScale;
uniform int ClampToRange;
uniform int InvertHeatmap;

float ResolveValue(vec4 sampleColor)
{
    switch (ChannelMode)
    {
        case 1:
            return sampleColor.r;
        case 2:
            return sampleColor.g;
        case 3:
            return sampleColor.b;
        case 4:
            return sampleColor.a;
        case 5:
            return max(max(sampleColor.r, sampleColor.g), sampleColor.b);
        default:
            return dot(sampleColor.rgb, vec3(0.2126, 0.7152, 0.0722));
    }
}

vec3 HeatRamp(float t)
{
    t = clamp(t, 0.0, 1.0);
    if (t < 0.25)
        return mix(vec3(0.0, 0.0, 0.2), vec3(0.0, 0.55, 1.0), t / 0.25);
    if (t < 0.5)
        return mix(vec3(0.0, 0.55, 1.0), vec3(0.0, 1.0, 0.3), (t - 0.25) / 0.25);
    if (t < 0.75)
        return mix(vec3(0.0, 1.0, 0.3), vec3(1.0, 0.92, 0.0), (t - 0.5) / 0.25);
    return mix(vec3(1.0, 0.92, 0.0), vec3(1.0, 0.08, 0.0), (t - 0.75) / 0.25);
}

void main()
{
    vec4 sampleColor = texture(SourceTexture, FragPos.xy);
    float value = ResolveValue(sampleColor) * ValueScale + ValueBias;

    if (UseLogScale != 0)
        value = log2(max(value, 1e-6));

    float denom = max(abs(ValueMax - ValueMin), 1e-6);
    float t = (value - ValueMin) / denom;
    if (ClampToRange != 0)
        t = clamp(t, 0.0, 1.0);
    if (InvertHeatmap != 0)
        t = 1.0 - t;

    vec3 heat = HeatRamp(t);
    OutColor = vec4(heat, OverlayAlpha);
}
""";

    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? DestinationFBOName { get; set; }
    public Vector4 NormalizedRegion { get; set; } = new(0.0f, 0.0f, 1.0f, 1.0f);
    public EDebugHeatmapSourceChannel ChannelMode { get; set; } = EDebugHeatmapSourceChannel.Luminance;
    public float ValueMin { get; set; } = 0.0f;
    public float ValueMax { get; set; } = 1.0f;
    public float ValueScale { get; set; } = 1.0f;
    public float ValueBias { get; set; }
    public float Alpha { get; set; } = 0.85f;
    public bool UseLogScale { get; set; }
    public bool ClampToRange { get; set; } = true;
    public bool InvertHeatmap { get; set; }

    public override string GpuProfilingName
        => nameof(VPRC_DebugHeatmap);

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        _material = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, HeatmapShaderCode))
        {
            RenderOptions = CreateRenderOptions()
        };

        _quad = new XRQuadFrameBuffer(_material);
        _quad.SettingUniforms += Heatmap_SettingUniforms;
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
        {
            _quad.SettingUniforms -= Heatmap_SettingUniforms;
            _quad.Destroy();
            _quad = null;
        }

        _material?.Destroy();
        _material = null;
    }

    protected override void Execute()
    {
        var instance = ActivePipelineInstance;
        if (_quad is null)
            return;

        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
            || sourceTexture is null)
        {
            return;
        }

        XRFrameBuffer? destination = null;
        if (!string.IsNullOrWhiteSpace(DestinationFBOName))
        {
            destination = instance.GetFBO<XRFrameBuffer>(DestinationFBOName!);
            if (destination is null)
                return;
        }

        BoundingRectangle baseRegion = ResolveBaseRegion(instance, destination);
        if (baseRegion.Width <= 0 || baseRegion.Height <= 0)
            return;

        BoundingRectangle overlayRegion = new(
            baseRegion.X + (int)MathF.Round(baseRegion.Width * NormalizedRegion.X),
            baseRegion.Y + (int)MathF.Round(baseRegion.Height * NormalizedRegion.Y),
            Math.Max(1, (int)MathF.Round(baseRegion.Width * NormalizedRegion.Z)),
            Math.Max(1, (int)MathF.Round(baseRegion.Height * NormalizedRegion.W)));

        using var renderArea = instance.RenderState.PushRenderArea(overlayRegion);
        _quad.Render(destination);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        if (string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName))
            return;

        string source = SourceTextureName ?? SourceFBOName ?? "Heatmap";
        string destination = DestinationFBOName
            ?? context.CurrentRenderTarget?.Name
            ?? RenderGraphResourceNames.OutputRenderTarget;

        var builder = context.GetOrCreateSyntheticPass($"DebugHeatmap_{source}_to_{destination}");
        builder.WithStage(ERenderGraphPassStage.Graphics);

        if (!string.IsNullOrWhiteSpace(SourceTextureName))
            builder.SampleTexture(MakeTextureResource(SourceTextureName!));
        else
            builder.SampleTexture(MakeFboColorResource(SourceFBOName!));

        builder.UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);
    }

    private static RenderingParameters CreateRenderOptions() => new()
    {
        DepthTest =
        {
            Enabled = ERenderParamUsage.Disabled,
            UpdateDepth = false,
            Function = EComparison.Always,
        },
        BlendModeAllDrawBuffers = new BlendMode()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.SrcAlpha,
            RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
            AlphaSrcFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd,
        }
    };

    private static BoundingRectangle ResolveBaseRegion(XRRenderPipelineInstance instance, XRFrameBuffer? destination)
    {
        BoundingRectangle region = instance.RenderState.CurrentRenderRegion;
        if (region.Width > 0 && region.Height > 0)
            return region;

        if (destination is not null)
            return new BoundingRectangle(0, 0, (int)destination.Width, (int)destination.Height);

        if (instance.RenderState.OutputFBO is XRFrameBuffer output)
            return new BoundingRectangle(0, 0, (int)output.Width, (int)output.Height);

        return BoundingRectangle.Empty;
    }

    private void Heatmap_SettingUniforms(XRRenderProgram program)
    {
        var instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
            || sourceTexture is null)
        {
            return;
        }

        program.Sampler("SourceTexture", sourceTexture, 0);
        program.Uniform("ChannelMode", (int)ChannelMode);
        program.Uniform("ValueMin", ValueMin);
        program.Uniform("ValueMax", ValueMax);
        program.Uniform("ValueScale", ValueScale);
        program.Uniform("ValueBias", ValueBias);
        program.Uniform("OverlayAlpha", Alpha);
        program.Uniform("UseLogScale", UseLogScale ? 1 : 0);
        program.Uniform("ClampToRange", ClampToRange ? 1 : 0);
        program.Uniform("InvertHeatmap", InvertHeatmap ? 1 : 0);
    }
}
