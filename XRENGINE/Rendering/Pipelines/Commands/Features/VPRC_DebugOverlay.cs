using System;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public enum EDebugOverlayChannelMode
    {
        RGBA = 0,
        Red = 1,
        Green = 2,
        Blue = 3,
        Alpha = 4,
        Depth = 5,
        Normals = 6,
        MotionVectors = 7,
    }

    /// <summary>
    /// Renders a named texture into a normalized overlay region for in-game inspection.
    /// </summary>
    public sealed class VPRC_DebugOverlay : ViewportRenderCommand
    {
        private const string OverlayShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform int ChannelMode;
uniform vec4 Multiply;
uniform vec4 Bias;
uniform float OverlayAlpha;

vec4 Visualize(vec4 sampleColor)
{
    switch (ChannelMode)
    {
        case 1:
            return vec4(sampleColor.rrr, 1.0);
        case 2:
            return vec4(sampleColor.ggg, 1.0);
        case 3:
            return vec4(sampleColor.bbb, 1.0);
        case 4:
            return vec4(sampleColor.aaa, 1.0);
        case 5:
            return vec4(vec3(sampleColor.r), 1.0);
        case 6:
            return vec4(sampleColor.xyz * 0.5 + 0.5, 1.0);
        case 7:
            return vec4(sampleColor.xy * 0.5 + 0.5, 0.0, 1.0);
        default:
            return sampleColor;
    }
}

void main()
{
    vec4 sampleColor = texture(SourceTexture, FragPos.xy);
    vec4 outColor = Visualize(sampleColor) * Multiply + Bias;
    outColor.a *= OverlayAlpha;
    OutColor = outColor;
}
""";

        private XRMaterial? _material;
        private XRQuadFrameBuffer? _quad;

        public string SourceTextureName { get; set; } = string.Empty;
        public string? DestinationFBOName { get; set; }
        public Vector4 NormalizedRegion { get; set; } = new(0.72f, 0.72f, 0.25f, 0.25f);
        public EDebugOverlayChannelMode ChannelMode { get; set; } = EDebugOverlayChannelMode.RGBA;
        public Vector4 Multiply { get; set; } = Vector4.One;
        public Vector4 Bias { get; set; } = Vector4.Zero;
        public float Alpha { get; set; } = 1.0f;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(SourceTextureName)
                ? nameof(VPRC_DebugOverlay)
                : $"{nameof(VPRC_DebugOverlay)}:{SourceTextureName}";

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
            var instance = ActivePipelineInstance;
            if (_quad is null ||
                string.IsNullOrWhiteSpace(SourceTextureName) ||
                !instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) ||
                sourceTexture is null)
                return;

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

            if (string.IsNullOrWhiteSpace(SourceTextureName))
                return;

            string destination = DestinationFBOName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass($"DebugOverlay_{SourceTextureName}_to_{destination}");
            builder.WithStage(ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(SourceTextureName));
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
            var instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
                return;

            program.Sampler("SourceTexture", sourceTexture, 0);
            program.Uniform("ChannelMode", (int)ChannelMode);
            program.Uniform("Multiply", Multiply);
            program.Uniform("Bias", Bias);
            program.Uniform("OverlayAlpha", Alpha);
        }
    }
}