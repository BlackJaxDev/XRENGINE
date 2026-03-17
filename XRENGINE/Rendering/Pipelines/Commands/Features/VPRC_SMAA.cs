using System;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Runs a lookup-free SMAA-style three-pass anti-aliasing chain.
/// This implementation uses local edge detection, blend-weight estimation, and neighborhood blending
/// without external area/search textures so it can remain fully self-contained in the command layer.
/// </summary>
public sealed class VPRC_SMAA : ViewportRenderCommand
{
    private const string EdgeDetectionShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform vec2 TexelSize;
uniform float EdgeThreshold;

float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

void main()
{
    vec2 uv = FragPos.xy;

    float center = Luma(texture(SourceTexture, uv).rgb);
    float left = Luma(texture(SourceTexture, uv - vec2(TexelSize.x, 0.0)).rgb);
    float right = Luma(texture(SourceTexture, uv + vec2(TexelSize.x, 0.0)).rgb);
    float top = Luma(texture(SourceTexture, uv + vec2(0.0, TexelSize.y)).rgb);
    float bottom = Luma(texture(SourceTexture, uv - vec2(0.0, TexelSize.y)).rgb);

    float horizontal = step(EdgeThreshold, max(abs(center - top), abs(center - bottom)));
    float vertical = step(EdgeThreshold, max(abs(center - left), abs(center - right)));
    float contrast = max(max(abs(center - left), abs(center - right)), max(abs(center - top), abs(center - bottom)));

    OutColor = vec4(horizontal, vertical, contrast, 1.0);
}
""";

    private const string BlendWeightShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D EdgeTexture;
uniform vec2 TexelSize;
uniform int MaxSearchSteps;
uniform float EdgeSharpness;

float SearchSpan(vec2 uv, vec2 direction, int channelIndex)
{
    float span = 0.0;
    vec2 sampleUv = uv;

    for (int i = 0; i < 32; ++i)
    {
        if (i >= MaxSearchSteps)
            break;

        sampleUv += direction * TexelSize;
        vec4 edge = texture(EdgeTexture, sampleUv);
        float value = channelIndex == 0 ? edge.r : edge.g;
        if (value < 0.5)
            break;

        span += 1.0;
    }

    return span;
}

void main()
{
    vec2 uv = FragPos.xy;
    vec4 edge = texture(EdgeTexture, uv);

    vec4 weights = vec4(0.0);
    float contrast = clamp(edge.b * EdgeSharpness, 0.0, 1.0);

    if (edge.r > 0.5)
    {
        float leftSpan = SearchSpan(uv, vec2(-1.0, 0.0), 0);
        float rightSpan = SearchSpan(uv, vec2(1.0, 0.0), 0);
        float spanFactor = clamp((leftSpan + rightSpan + 1.0) / float(max(MaxSearchSteps, 1)), 0.0, 1.0);
        float weight = 0.5 * contrast * (0.35 + 0.65 * spanFactor);
        weights.z = weight;
        weights.w = weight;
    }

    if (edge.g > 0.5)
    {
        float upSpan = SearchSpan(uv, vec2(0.0, 1.0), 1);
        float downSpan = SearchSpan(uv, vec2(0.0, -1.0), 1);
        float spanFactor = clamp((upSpan + downSpan + 1.0) / float(max(MaxSearchSteps, 1)), 0.0, 1.0);
        float weight = 0.5 * contrast * (0.35 + 0.65 * spanFactor);
        weights.x = weight;
        weights.y = weight;
    }

    OutColor = clamp(weights, 0.0, 1.0);
}
""";

    private const string NeighborhoodBlendShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform sampler2D BlendTexture;
uniform vec2 TexelSize;

void main()
{
    vec2 uv = FragPos.xy;
    vec4 center = texture(SourceTexture, uv);
    vec4 blend = texture(BlendTexture, uv);

    vec4 left = texture(SourceTexture, uv - vec2(TexelSize.x, 0.0));
    vec4 right = texture(SourceTexture, uv + vec2(TexelSize.x, 0.0));
    vec4 top = texture(SourceTexture, uv + vec2(0.0, TexelSize.y));
    vec4 bottom = texture(SourceTexture, uv - vec2(0.0, TexelSize.y));

    float total = clamp(blend.x + blend.y + blend.z + blend.w, 0.0, 1.0);
    vec4 neighborContribution =
        left * blend.x +
        right * blend.y +
        top * blend.z +
        bottom * blend.w;

    OutColor = center * (1.0 - total) + neighborContribution;
}
""";

    private XRTexture2D? _edgeTexture;
    private XRTexture2D? _blendTexture;
    private XRTexture2D? _outputTexture;
    private XRFrameBuffer? _edgeFbo;
    private XRFrameBuffer? _blendFbo;
    private XRFrameBuffer? _outputFbo;
    private XRMaterial? _edgeMaterial;
    private XRMaterial? _blendMaterial;
    private XRMaterial? _neighborhoodMaterial;
    private XRQuadFrameBuffer? _edgeQuad;
    private XRQuadFrameBuffer? _blendQuad;
    private XRQuadFrameBuffer? _neighborhoodQuad;
    private uint _cachedWidth;
    private uint _cachedHeight;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string OutputTextureName { get; set; } = "SmaaOutputTexture";
    public string? OutputFBOName { get; set; }
    public float EdgeThreshold { get; set; } = 0.08f;
    public int MaxSearchSteps { get; set; } = 8;
    public float EdgeSharpness { get; set; } = 1.5f;

    private string ResolvedOutputFboName
        => string.IsNullOrWhiteSpace(OutputFBOName) ? $"{OutputTextureName}FBO" : OutputFBOName!;

    public override string GpuProfilingName
        => string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName)
            ? nameof(VPRC_SMAA)
            : $"{nameof(VPRC_SMAA)}:{SourceTextureName ?? SourceFBOName}";

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_edgeQuad is not null && _blendQuad is not null && _neighborhoodQuad is not null)
            return;

        _edgeMaterial = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, EdgeDetectionShaderCode))
        {
            RenderOptions = CreateRenderOptions()
        };
        _blendMaterial = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, BlendWeightShaderCode))
        {
            RenderOptions = CreateRenderOptions()
        };
        _neighborhoodMaterial = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, NeighborhoodBlendShaderCode))
        {
            RenderOptions = CreateRenderOptions()
        };

        _edgeQuad = new XRQuadFrameBuffer(_edgeMaterial);
        _blendQuad = new XRQuadFrameBuffer(_blendMaterial);
        _neighborhoodQuad = new XRQuadFrameBuffer(_neighborhoodMaterial);

        _edgeQuad.SettingUniforms += Edge_SettingUniforms;
        _blendQuad.SettingUniforms += Blend_SettingUniforms;
        _neighborhoodQuad.SettingUniforms += Neighborhood_SettingUniforms;
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        RemoveRegisteredOutput(instance);

        _edgeFbo?.Destroy();
        _edgeFbo = null;
        _blendFbo?.Destroy();
        _blendFbo = null;

        _edgeTexture?.Destroy();
        _edgeTexture = null;
        _blendTexture?.Destroy();
        _blendTexture = null;

        if (_edgeQuad is not null)
        {
            _edgeQuad.SettingUniforms -= Edge_SettingUniforms;
            _edgeQuad.Destroy();
            _edgeQuad = null;
        }

        if (_blendQuad is not null)
        {
            _blendQuad.SettingUniforms -= Blend_SettingUniforms;
            _blendQuad.Destroy();
            _blendQuad = null;
        }

        if (_neighborhoodQuad is not null)
        {
            _neighborhoodQuad.SettingUniforms -= Neighborhood_SettingUniforms;
            _neighborhoodQuad.Destroy();
            _neighborhoodQuad = null;
        }

        _edgeMaterial?.Destroy();
        _edgeMaterial = null;
        _blendMaterial?.Destroy();
        _blendMaterial = null;
        _neighborhoodMaterial?.Destroy();
        _neighborhoodMaterial = null;

        _cachedWidth = 0;
        _cachedHeight = 0;
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (_edgeQuad is null ||
            _blendQuad is null ||
            _neighborhoodQuad is null ||
            !VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _) ||
            sourceTexture is not XRTexture2D source2D)
            return;

        EnsureResources(instance, source2D);
        if (_edgeFbo is null || _blendFbo is null || _outputFbo is null)
            return;

        _edgeQuad.Render(_edgeFbo);
        _blendQuad.Render(_blendFbo);
        _neighborhoodQuad.Render(_outputFbo);
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

        context.GetOrCreateSyntheticPass($"Smaa_{GetSourceDisplayName()}_to_{OutputTextureName}")
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(MakeFboColorResource(ResolvedOutputFboName), ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
    }

    private static RenderingParameters CreateRenderOptions() => new()
    {
        DepthTest =
        {
            Enabled = ERenderParamUsage.Disabled,
            UpdateDepth = false,
            Function = EComparison.Always,
        }
    };

    private void EnsureResources(XRRenderPipelineInstance instance, XRTexture2D sourceTexture)
    {
        uint width = Math.Max(1u, sourceTexture.Width);
        uint height = Math.Max(1u, sourceTexture.Height);

        if (_cachedWidth == width &&
            _cachedHeight == height &&
            _edgeTexture is not null &&
            _blendTexture is not null &&
            _outputTexture is not null &&
            _edgeFbo is not null &&
            _blendFbo is not null &&
            _outputFbo is not null)
            return;

        RemoveRegisteredOutput(instance);

        _edgeFbo?.Destroy();
        _blendFbo?.Destroy();
        _edgeTexture?.Destroy();
        _blendTexture?.Destroy();

        (EPixelInternalFormat sourceInternalFormat, ESizedInternalFormat sourceSizedFormat, EPixelFormat sourcePixelFormat, EPixelType sourcePixelType) = ResolveSourceFormat(sourceTexture);

        _edgeTexture = XRTexture2D.CreateFrameBufferTexture(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        _edgeTexture.Name = $"{ResolvedOutputFboName}_EdgeTexture";
        _edgeTexture.SamplerName = _edgeTexture.Name;
        _edgeTexture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        _edgeTexture.Resizable = false;
        _edgeTexture.MagFilter = ETexMagFilter.Linear;
        _edgeTexture.MinFilter = ETexMinFilter.Linear;

        _blendTexture = XRTexture2D.CreateFrameBufferTexture(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        _blendTexture.Name = $"{ResolvedOutputFboName}_BlendTexture";
        _blendTexture.SamplerName = _blendTexture.Name;
        _blendTexture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        _blendTexture.Resizable = false;
        _blendTexture.MagFilter = ETexMagFilter.Linear;
        _blendTexture.MinFilter = ETexMinFilter.Linear;

        _outputTexture = XRTexture2D.CreateFrameBufferTexture(width, height, sourceInternalFormat, sourcePixelFormat, sourcePixelType);
        _outputTexture.Name = OutputTextureName;
        _outputTexture.SamplerName = OutputTextureName;
        _outputTexture.SizedInternalFormat = sourceSizedFormat;
        _outputTexture.Resizable = false;
        _outputTexture.MagFilter = ETexMagFilter.Linear;
        _outputTexture.MinFilter = ETexMinFilter.Linear;

        _edgeFbo = new XRFrameBuffer((_edgeTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = $"{ResolvedOutputFboName}_EdgeFBO"
        };
        _blendFbo = new XRFrameBuffer((_blendTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = $"{ResolvedOutputFboName}_BlendFBO"
        };
        _outputFbo = new XRFrameBuffer((_outputTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = ResolvedOutputFboName
        };

        instance.SetTexture(_outputTexture);
        instance.SetFBO(_outputFbo);

        _cachedWidth = width;
        _cachedHeight = height;
    }

    private static (EPixelInternalFormat, ESizedInternalFormat, EPixelFormat, EPixelType) ResolveSourceFormat(XRTexture2D sourceTexture)
    {
        if (sourceTexture.Mipmaps.Length > 0)
        {
            var mip = sourceTexture.Mipmaps[0];
            return (mip.InternalFormat, sourceTexture.SizedInternalFormat, mip.PixelFormat, mip.PixelType);
        }

        if (Engine.Rendering.Settings.OutputHDR)
            return (EPixelInternalFormat.Rgba16f, ESizedInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat);

        return (EPixelInternalFormat.Rgba8, ESizedInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
    }

    private void RemoveRegisteredOutput(XRRenderPipelineInstance instance)
    {
        if (_outputFbo is not null)
        {
            instance.Resources.RemoveFrameBuffer(ResolvedOutputFboName);
            _outputFbo = null;
        }

        if (_outputTexture is not null)
        {
            instance.Resources.RemoveTexture(OutputTextureName);
            _outputTexture = null;
        }
    }

    private string GetSourceDisplayName()
        => SourceTextureName ?? SourceFBOName ?? "Output";

    private void Edge_SettingUniforms(XRRenderProgram program)
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _) ||
            sourceTexture is null)
            return;

        Vector3 sourceSize = sourceTexture.WidthHeightDepth;
        program.Sampler("SourceTexture", sourceTexture, 0);
        program.Uniform("TexelSize", new Vector2(
            1.0f / Math.Max(1.0f, sourceSize.X),
            1.0f / Math.Max(1.0f, sourceSize.Y)));
        program.Uniform("EdgeThreshold", EdgeThreshold);
    }

    private void Blend_SettingUniforms(XRRenderProgram program)
    {
        if (_edgeTexture is null)
            return;

        Vector3 edgeSize = _edgeTexture.WidthHeightDepth;
        program.Sampler("EdgeTexture", _edgeTexture, 0);
        program.Uniform("TexelSize", new Vector2(
            1.0f / Math.Max(1.0f, edgeSize.X),
            1.0f / Math.Max(1.0f, edgeSize.Y)));
        program.Uniform("MaxSearchSteps", Math.Clamp(MaxSearchSteps, 1, 32));
        program.Uniform("EdgeSharpness", EdgeSharpness);
    }

    private void Neighborhood_SettingUniforms(XRRenderProgram program)
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (_blendTexture is null ||
            !VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _) ||
            sourceTexture is null)
            return;

        Vector3 sourceSize = sourceTexture.WidthHeightDepth;
        program.Sampler("SourceTexture", sourceTexture, 0);
        program.Sampler("BlendTexture", _blendTexture, 1);
        program.Uniform("TexelSize", new Vector2(
            1.0f / Math.Max(1.0f, sourceSize.X),
            1.0f / Math.Max(1.0f, sourceSize.Y)));
    }
}