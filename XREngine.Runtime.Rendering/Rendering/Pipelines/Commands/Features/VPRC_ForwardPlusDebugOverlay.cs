using System;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Visualizes the Forward+ tiled-light-culling buckets on top of the current render target.
/// Reads the per-tile light counts SSBO published by VPRC_ForwardPlusLightCullingPass and
/// renders a configurable heatmap / grid / overflow indicator. Mode and parameters are read
/// from the active <see cref="XRCamera"/> so the user can toggle the overlay live from the
/// camera component UI without rebuilding the command chain.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_ForwardPlusDebugOverlay : ViewportRenderCommand
{
    // GLSL fragment shader. Uses one input vertex attribute (FragPos) like the other
    // VPRC_Debug* overlays so it composes cleanly with XRQuadFrameBuffer.
    private const string OverlayShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

layout(std430, binding = 29) readonly buffer ForwardPlusTileLightCountsBuffer
{
    uint data[];
} fpTileCounts;

uniform ivec2 ScreenSize;
uniform int TileSize;
uniform int TileCountX;
uniform int TileCountY;
uniform int MaxLightsPerTile;
uniform int DebugMode;          // matches XRCamera.EForwardPlusDebugMode
uniform int MaxCount;           // count that saturates the heatmap ramp
uniform float OverlayAlpha;
uniform vec3 GridColor;
uniform float GridThickness;    // pixels
uniform vec3 OverflowColor;

vec3 HeatRamp(float t)
{
    t = clamp(t, 0.0, 1.0);
    if (t < 0.25)
        return mix(vec3(0.0, 0.0, 0.25), vec3(0.0, 0.55, 1.0), t / 0.25);
    if (t < 0.5)
        return mix(vec3(0.0, 0.55, 1.0), vec3(0.0, 1.0, 0.3), (t - 0.25) / 0.25);
    if (t < 0.75)
        return mix(vec3(0.0, 1.0, 0.3), vec3(1.0, 0.92, 0.0), (t - 0.5) / 0.25);
    return mix(vec3(1.0, 0.92, 0.0), vec3(1.0, 0.08, 0.0), (t - 0.75) / 0.25);
}

void main()
{
    if (DebugMode == 0 || ScreenSize.x <= 0 || ScreenSize.y <= 0 || TileSize <= 0)
        discard;

    vec2 uv = FragPos.xy * 0.5 + 0.5;
    vec2 pixel = uv * vec2(ScreenSize);
    ivec2 tileXY = ivec2(pixel) / TileSize;
    if (tileXY.x < 0 || tileXY.y < 0 || tileXY.x >= TileCountX || tileXY.y >= TileCountY)
        discard;

    int tileLinear = tileXY.y * TileCountX + tileXY.x;
    uint count = fpTileCounts.data[tileLinear];

    bool overflowed = (MaxLightsPerTile > 0) && (count > uint(MaxLightsPerTile));

    // Grid lines: distance to nearest tile boundary in pixel units.
    vec2 inTile = mod(pixel, vec2(float(TileSize)));
    vec2 distToEdge = min(inTile, vec2(float(TileSize)) - inTile);
    float edge = min(distToEdge.x, distToEdge.y);
    bool onGrid = edge < max(GridThickness, 0.5);

    vec3 color = vec3(0.0);
    float alpha = OverlayAlpha;

    if (DebugMode == 4) // GridOnly
    {
        if (!onGrid)
            discard;
        color = GridColor;
    }
    else if (DebugMode == 3) // OverflowOnly
    {
        if (!overflowed)
            discard;
        color = OverflowColor;
    }
    else // Heatmap or HeatmapWithGrid
    {
        float denom = max(float(MaxCount), 1.0);
        float t = clamp(float(count) / denom, 0.0, 1.0);

        if (count == 0u)
        {
            // Faintly mark empty tiles so the user can still see culling extent.
            color = vec3(0.05, 0.05, 0.08);
            alpha *= 0.45;
        }
        else
        {
            color = HeatRamp(t);
        }

        if (overflowed)
            color = mix(color, OverflowColor, 0.6);

        if (DebugMode == 2 && onGrid) // HeatmapWithGrid
            color = GridColor;
    }

    OutColor = vec4(color, alpha);
}
""";

    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;

    public string? DestinationFBOName { get; set; }

    public override string GpuProfilingName
        => nameof(VPRC_ForwardPlusDebugOverlay);

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        _material = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, OverlayShaderCode))
        {
            RenderOptions = CreateRenderOptions()
        };

        _quad = new XRQuadFrameBuffer(_material);
        _quad.SettingUniforms += Overlay_SettingUniforms;
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
        {
            _quad.SettingUniforms -= Overlay_SettingUniforms;
            _quad.Destroy();
            _quad = null;
        }

        _material?.Destroy();
        _material = null;
    }

    protected override void Execute()
    {
        if (_quad is null)
            return;

        if (Engine.Rendering.State.ForwardPlusTileLightCountsBuffer is null)
            return;

        if (Engine.Rendering.State.ForwardPlusTileCountX <= 0 ||
            Engine.Rendering.State.ForwardPlusTileCountY <= 0 ||
            Engine.Rendering.State.ForwardPlusTileSize <= 0)
        {
            return;
        }

        var cam = ResolveDebugCamera();
        if (cam is null || cam.ForwardPlusDebugMode == XRCamera.EForwardPlusDebugMode.None)
            return;

        var instance = ActivePipelineInstance;
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

        using var renderArea = instance.RenderState.PushRenderArea(baseRegion);
        _quad.Render(destination);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        string destination = DestinationFBOName
            ?? context.CurrentRenderTarget?.Name
            ?? RenderGraphResourceNames.OutputRenderTarget;

        var builder = context.GetOrCreateSyntheticPass($"ForwardPlusDebugOverlay_to_{destination}");
        builder.WithStage(ERenderGraphPassStage.Graphics);
        builder.ReadWriteBuffer("ForwardPlusTileLightCounts");
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

    private void Overlay_SettingUniforms(XRRenderProgram program)
    {
        var cam = ResolveDebugCamera();
        var countsBuffer = Engine.Rendering.State.ForwardPlusTileLightCountsBuffer;
        if (cam is null || countsBuffer is null)
            return;

        countsBuffer.BindTo(program, VPRC_ForwardPlusLightCullingPass.TileLightCountsBinding);

        var screen = Engine.Rendering.State.ForwardPlusScreenSize;
        program.Uniform("ScreenSize", new XREngine.Data.Vectors.IVector2((int)screen.X, (int)screen.Y));
        program.Uniform("TileSize", Engine.Rendering.State.ForwardPlusTileSize);
        program.Uniform("TileCountX", Engine.Rendering.State.ForwardPlusTileCountX);
        program.Uniform("TileCountY", Engine.Rendering.State.ForwardPlusTileCountY);
        program.Uniform("MaxLightsPerTile", Engine.Rendering.State.ForwardPlusMaxLightsPerTile);
        program.Uniform("DebugMode", (int)cam.ForwardPlusDebugMode);
        program.Uniform("MaxCount", Math.Max(1, cam.ForwardPlusDebugMaxCount));
        program.Uniform("OverlayAlpha", cam.ForwardPlusDebugOpacity);
        program.Uniform("GridColor", new System.Numerics.Vector3(0.0f, 0.95f, 1.0f));
        program.Uniform("GridThickness", 1.0f);
        program.Uniform("OverflowColor", new System.Numerics.Vector3(1.0f, 0.0f, 1.0f));
    }

    private static XRCamera? ResolveDebugCamera()
    {
        XRRenderPipelineInstance? pipeline = ActivePipelineInstance;
        return pipeline?.RenderState.SceneCamera
            ?? pipeline?.LastSceneCamera
            ?? Engine.Rendering.State.RenderingPipelineState?.SceneCamera
            ?? Engine.Rendering.State.CurrentRenderingPipeline?.LastSceneCamera;
    }
}
