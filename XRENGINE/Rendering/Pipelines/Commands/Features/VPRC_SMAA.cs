using System;
using System.Diagnostics.CodeAnalysis;
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
[RenderPipelineScriptCommand]
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
    vec2 uv = FragPos.xy * 0.5 + 0.5;

    float center = Luma(texture(SourceTexture, uv).rgb);
    // SMAA convention: compare only with top and left neighbors.
    // This stores each edge at exactly one pixel, enabling proper
    // asymmetric blending in the neighborhood pass.
    float top  = Luma(texture(SourceTexture, uv + vec2(0.0, TexelSize.y)).rgb);
    float left = Luma(texture(SourceTexture, uv - vec2(TexelSize.x, 0.0)).rgb);

    float deltaH = abs(center - top);
    float deltaV = abs(center - left);

    float edgeH = step(EdgeThreshold, deltaH);
    float edgeV = step(EdgeThreshold, deltaV);

    // R = horizontal edge, G = vertical edge
    // B = horizontal luma delta, A = vertical luma delta
    OutColor = vec4(edgeH, edgeV, deltaH, deltaV);
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
    vec2 uv = FragPos.xy * 0.5 + 0.5;

    vec4 edge = texture(EdgeTexture, uv);
    vec4 weights = vec4(0.0);

    // Horizontal edge: between this pixel and its top neighbor.
    // Store blend-toward-top weight in R channel ONLY.
    // The complementary (blend-toward-bottom) is read from the pixel
    // below in the neighborhood blend pass.
    if (edge.r > 0.5)
    {
        float leftSpan = SearchSpan(uv, vec2(-1.0, 0.0), 0);
        float rightSpan = SearchSpan(uv, vec2(1.0, 0.0), 0);
        float totalSpan = leftSpan + rightSpan + 1.0;

        // Shorter edge spans (staircase steps) need stronger blending;
        // long straight edges need less. Inverse-sqrt gives a smooth ramp.
        float spanWeight = 1.0 / sqrt(max(totalSpan, 1.0));
        float contrast = min(edge.b * EdgeSharpness, 1.0);
        weights.r = 0.5 * contrast * spanWeight;
    }

    // Vertical edge: between this pixel and its left neighbor.
    // Store blend-toward-left weight in G channel ONLY.
    if (edge.g > 0.5)
    {
        float upSpan = SearchSpan(uv, vec2(0.0, 1.0), 1);
        float downSpan = SearchSpan(uv, vec2(0.0, -1.0), 1);
        float totalSpan = upSpan + downSpan + 1.0;

        float spanWeight = 1.0 / sqrt(max(totalSpan, 1.0));
        float contrast = min(edge.a * EdgeSharpness, 1.0);
        weights.g = 0.5 * contrast * spanWeight;
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
    vec2 uv = FragPos.xy * 0.5 + 0.5;

    vec4 center = texture(SourceTexture, uv);

    // This pixel's own blend weights.
    vec4 blend = texture(BlendTexture, uv);

    // Read complementary blend weights from adjacent pixels.
    // The pixel BELOW us may have a horizontal edge with us (we are its top),
    // so its R channel tells us how much to blend downward.
    float blendDown  = texture(BlendTexture, uv - vec2(0.0, TexelSize.y)).r;
    // The pixel to the RIGHT may have a vertical edge with us (we are its left),
    // so its G channel tells us how much to blend rightward.
    float blendRight = texture(BlendTexture, uv + vec2(TexelSize.x, 0.0)).g;

    // Compose per-direction weights:
    float bTop    = blend.r;      // this pixel has horizontal edge with top → blend up
    float bBottom = blendDown;    // pixel below has horizontal edge with us → blend down
    float bLeft   = blend.g;      // this pixel has vertical edge with left → blend left
    float bRight  = blendRight;   // pixel right has vertical edge with us → blend right

    float total = bTop + bBottom + bLeft + bRight;
    if (total < 1e-5)
    {
        OutColor = center;
        return;
    }
    total = min(total, 1.0);

    vec4 top    = texture(SourceTexture, uv + vec2(0.0, TexelSize.y));
    vec4 bottom = texture(SourceTexture, uv - vec2(0.0, TexelSize.y));
    vec4 left   = texture(SourceTexture, uv - vec2(TexelSize.x, 0.0));
    vec4 right  = texture(SourceTexture, uv + vec2(TexelSize.x, 0.0));

    vec4 blended = top * bTop + bottom * bBottom + left * bLeft + right * bRight;
    OutColor = center * (1.0 - total) + blended;
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
    private EPixelInternalFormat _cachedOutputInternalFormat;
    private ESizedInternalFormat _cachedOutputSizedFormat;
    private EPixelFormat _cachedOutputPixelFormat;
    private EPixelType _cachedOutputPixelType;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string OutputTextureName { get; set; } = "SmaaOutputTexture";
    public string? OutputFBOName { get; set; }
    public float EdgeThreshold { get; set; } = 0.08f;
    public int MaxSearchSteps { get; set; } = 16;
    public float EdgeSharpness { get; set; } = 2.0f;

    private string ResolvedOutputFboName
        => string.IsNullOrWhiteSpace(OutputFBOName) ? $"{OutputTextureName}FBO" : OutputFBOName!;
    private string ResolvedEdgeTextureName => $"{ResolvedOutputFboName}_EdgeTexture";
    private string ResolvedBlendTextureName => $"{ResolvedOutputFboName}_BlendTexture";
    private string ResolvedEdgeFboName => $"{ResolvedOutputFboName}_EdgeFBO";
    private string ResolvedBlendFboName => $"{ResolvedOutputFboName}_BlendFBO";

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
        RemoveRegisteredResources(instance);

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
        _cachedOutputInternalFormat = default;
        _cachedOutputSizedFormat = default;
        _cachedOutputPixelFormat = default;
        _cachedOutputPixelType = default;
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (_edgeQuad is null ||
            _blendQuad is null ||
            _neighborhoodQuad is null ||
            !VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _) ||
            sourceTexture is null)
            return;

        (uint targetWidth, uint targetHeight) = ResolveTargetSize(instance, sourceTexture);
        EnsureResources(instance, sourceTexture, targetWidth, targetHeight);
        if (_outputFbo is null)
            return;

        if (sourceTexture is XRTexture2D)
        {
            if (_edgeFbo is null || _blendFbo is null)
                return;

            _edgeQuad.Render(_edgeFbo);
            _blendQuad.Render(_blendFbo);
            _neighborhoodQuad.Render(_outputFbo);
            return;
        }

        if (TryResolveSourceFrameBuffer(instance, sourceTexture, out XRFrameBuffer? sourceFbo))
        {
            AbstractRenderer.Current?.BlitFBOToFBO(
                sourceFbo,
                _outputFbo,
                EReadBufferMode.ColorAttachment0,
                true,
                false,
                false,
                true);
        }
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

        context.GetOrCreateSyntheticPass($"SmaaEdge_{GetSourceDisplayName()}_to_{ResolvedEdgeFboName}")
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(MakeFboColorResource(ResolvedEdgeFboName), ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);

        context.GetOrCreateSyntheticPass($"SmaaBlend_{ResolvedEdgeTextureName}_to_{ResolvedBlendFboName}")
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(MakeTextureResource(ResolvedEdgeTextureName))
            .UseColorAttachment(MakeFboColorResource(ResolvedBlendFboName), ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);

        context.GetOrCreateSyntheticPass($"SmaaNeighborhood_{GetSourceDisplayName()}_to_{OutputTextureName}")
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .SampleTexture(MakeTextureResource(ResolvedBlendTextureName))
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

    private void EnsureResources(XRRenderPipelineInstance instance, XRTexture sourceTexture, uint width, uint height)
    {
        (EPixelInternalFormat sourceInternalFormat, ESizedInternalFormat sourceSizedFormat, EPixelFormat sourcePixelFormat, EPixelType sourcePixelType) = ResolveSourceFormat(sourceTexture);

        bool resourcesRegistered = instance.Resources.TryGetTexture(ResolvedEdgeTextureName, out XRTexture? registeredEdgeTexture)
            && ReferenceEquals(registeredEdgeTexture, _edgeTexture)
            && instance.Resources.TryGetTexture(ResolvedBlendTextureName, out XRTexture? registeredBlendTexture)
            && ReferenceEquals(registeredBlendTexture, _blendTexture)
            && instance.Resources.TryGetTexture(OutputTextureName, out XRTexture? registeredOutputTexture)
            && ReferenceEquals(registeredOutputTexture, _outputTexture)
            && instance.Resources.TryGetFrameBuffer(ResolvedEdgeFboName, out XRFrameBuffer? registeredEdgeFbo)
            && ReferenceEquals(registeredEdgeFbo, _edgeFbo)
            && instance.Resources.TryGetFrameBuffer(ResolvedBlendFboName, out XRFrameBuffer? registeredBlendFbo)
            && ReferenceEquals(registeredBlendFbo, _blendFbo)
            && instance.Resources.TryGetFrameBuffer(ResolvedOutputFboName, out XRFrameBuffer? registeredOutputFbo)
            && ReferenceEquals(registeredOutputFbo, _outputFbo);

        if (_cachedWidth == width &&
            _cachedHeight == height &&
            _cachedOutputInternalFormat == sourceInternalFormat &&
            _cachedOutputSizedFormat == sourceSizedFormat &&
            _cachedOutputPixelFormat == sourcePixelFormat &&
            _cachedOutputPixelType == sourcePixelType &&
            resourcesRegistered &&
            _edgeTexture is not null &&
            _blendTexture is not null &&
            _outputTexture is not null &&
            _edgeFbo is not null &&
            _blendFbo is not null &&
            _outputFbo is not null)
            return;

        RemoveRegisteredResources(instance);

        _edgeFbo?.Destroy();
        _blendFbo?.Destroy();
        _edgeTexture?.Destroy();
        _blendTexture?.Destroy();

        _edgeTexture = XRTexture2D.CreateFrameBufferTexture(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        _edgeTexture.Name = ResolvedEdgeTextureName;
        _edgeTexture.SamplerName = ResolvedEdgeTextureName;
        _edgeTexture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        _edgeTexture.Resizable = false;
        _edgeTexture.MagFilter = ETexMagFilter.Linear;
        _edgeTexture.MinFilter = ETexMinFilter.Linear;

        _blendTexture = XRTexture2D.CreateFrameBufferTexture(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        _blendTexture.Name = ResolvedBlendTextureName;
        _blendTexture.SamplerName = ResolvedBlendTextureName;
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
            Name = ResolvedEdgeFboName
        };
        _blendFbo = new XRFrameBuffer((_blendTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = ResolvedBlendFboName
        };
        _outputFbo = new XRFrameBuffer((_outputTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = ResolvedOutputFboName
        };

        instance.SetTexture(_edgeTexture);
        instance.SetTexture(_blendTexture);
        instance.SetTexture(_outputTexture);
        instance.SetFBO(_edgeFbo);
        instance.SetFBO(_blendFbo);
        instance.SetFBO(_outputFbo);

        // Populate the material texture arrays so the rendering backend
        // sets up the correct texture units for the shader programs.
        // The SettingUniforms callbacks still override per frame, but
        // having textures in the arrays ensures the backend doesn't
        // skip texture-unit activation for materials with empty arrays.
        if (_edgeMaterial is not null)
        {
            _edgeMaterial.Textures.Clear();
            _edgeMaterial.Textures.Add(sourceTexture);
        }
        if (_blendMaterial is not null)
        {
            _blendMaterial.Textures.Clear();
            _blendMaterial.Textures.Add(_edgeTexture);
        }
        if (_neighborhoodMaterial is not null)
        {
            _neighborhoodMaterial.Textures.Clear();
            _neighborhoodMaterial.Textures.Add(sourceTexture);
            _neighborhoodMaterial.Textures.Add(_blendTexture);
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedOutputInternalFormat = sourceInternalFormat;
        _cachedOutputSizedFormat = sourceSizedFormat;
        _cachedOutputPixelFormat = sourcePixelFormat;
        _cachedOutputPixelType = sourcePixelType;
    }

    private static (EPixelInternalFormat, ESizedInternalFormat, EPixelFormat, EPixelType) ResolveSourceFormat(XRTexture sourceTexture)
    {
        if (sourceTexture is XRTexture2D source2D)
        {
            if (source2D.Mipmaps.Length > 0)
            {
                var mip = source2D.Mipmaps[0];
                return (mip.InternalFormat, source2D.SizedInternalFormat, mip.PixelFormat, mip.PixelType);
            }
        }
        else if (sourceTexture is XRTexture2DView view)
        {
            return ResolveSourceFormat(view.ViewedTexture);
        }
        else if (sourceTexture is XRTexture2DArray array && array.Textures.Length > 0)
        {
            return ResolveSourceFormat(array.Textures[0]);
        }
        else if (sourceTexture is XRTexture2DArrayView arrayView && arrayView.ViewedTexture.Textures.Length > 0)
        {
            return ResolveSourceFormat(arrayView.ViewedTexture.Textures[0]);
        }

        if (Engine.Rendering.Settings.OutputHDR)
            return (EPixelInternalFormat.Rgba16f, ESizedInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat);

        return (EPixelInternalFormat.Rgba8, ESizedInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
    }

    private void RemoveRegisteredResources(XRRenderPipelineInstance instance)
    {
        if (_edgeFbo is not null)
        {
            instance.Resources.RemoveFrameBuffer(ResolvedEdgeFboName);
            _edgeFbo = null;
        }

        if (_blendFbo is not null)
        {
            instance.Resources.RemoveFrameBuffer(ResolvedBlendFboName);
            _blendFbo = null;
        }

        if (_outputFbo is not null)
        {
            instance.Resources.RemoveFrameBuffer(ResolvedOutputFboName);
            _outputFbo = null;
        }

        if (_edgeTexture is not null)
        {
            instance.Resources.RemoveTexture(ResolvedEdgeTextureName);
            _edgeTexture = null;
        }

        if (_blendTexture is not null)
        {
            instance.Resources.RemoveTexture(ResolvedBlendTextureName);
            _blendTexture = null;
        }

        if (_outputTexture is not null)
        {
            instance.Resources.RemoveTexture(OutputTextureName);
            _outputTexture = null;
        }
    }

    private (uint Width, uint Height) ResolveTargetSize(XRRenderPipelineInstance instance, XRTexture sourceTexture)
    {
        if (instance.RenderState.CurrentRenderRegion is { Width: > 0, Height: > 0 } region)
            return ((uint)region.Width, (uint)region.Height);

        if (instance.RenderState.OutputFBO is XRFrameBuffer output && output.Width > 0 && output.Height > 0)
            return (output.Width, output.Height);

        if ((instance.RenderState.WindowViewport ?? instance.LastWindowViewport) is XRViewport viewport &&
            viewport.Width > 0 &&
            viewport.Height > 0)
        {
            return ((uint)viewport.Width, (uint)viewport.Height);
        }

        Vector3 sourceSize = sourceTexture.WidthHeightDepth;
        return ((uint)Math.Max(1.0f, sourceSize.X), (uint)Math.Max(1.0f, sourceSize.Y));
    }

    private bool TryResolveSourceFrameBuffer(XRRenderPipelineInstance instance, XRTexture sourceTexture, [NotNullWhen(true)] out XRFrameBuffer? frameBuffer)
    {
        if (!string.IsNullOrWhiteSpace(SourceFBOName))
        {
            frameBuffer = instance.GetFBO<XRFrameBuffer>(SourceFBOName!);
            return frameBuffer is not null;
        }

        foreach (XRFrameBuffer candidate in instance.Resources.EnumerateFrameBufferInstances())
        {
            if (candidate.Targets is null)
                continue;

            foreach (var (target, attachment, _, _) in candidate.Targets)
            {
                if (attachment is < EFrameBufferAttachment.ColorAttachment0 or > EFrameBufferAttachment.ColorAttachment7)
                    continue;

                if (ReferenceEquals(target, sourceTexture))
                {
                    frameBuffer = candidate;
                    return true;
                }
            }
        }

        frameBuffer = null;
        return false;
    }

    private string GetSourceDisplayName()
        => SourceTextureName ?? SourceFBOName ?? "Output";

    private void Edge_SettingUniforms(XRRenderProgram program)
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _) ||
            sourceTexture is null)
            return;

        Vector3 sourceSize = (_edgeTexture ?? sourceTexture).WidthHeightDepth;
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

        Vector3 sourceSize = (_outputTexture ?? _blendTexture).WidthHeightDepth;
        program.Sampler("SourceTexture", sourceTexture, 0);
        program.Sampler("BlendTexture", _blendTexture, 1);
        program.Uniform("TexelSize", new Vector2(
            1.0f / Math.Max(1.0f, sourceSize.X),
            1.0f / Math.Max(1.0f, sourceSize.Y)));
    }
}
