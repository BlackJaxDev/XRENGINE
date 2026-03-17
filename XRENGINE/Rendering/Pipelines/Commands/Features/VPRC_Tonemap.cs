using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Applies a standalone tonemapping pass from a named input texture into the current or specified output framebuffer.
    /// </summary>
    public sealed class VPRC_Tonemap : ViewportRenderCommand
    {
        private const string TonemapShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform sampler2D BloomTexture;
uniform bool UseBloom;
uniform float BloomStrength;
uniform float Exposure;
uniform float Gamma;
uniform int TonemapType;

vec3 LinearTM(vec3 c)
{
    return c * Exposure;
}

vec3 GammaTM(vec3 c)
{
    return pow(max(c * Exposure, vec3(0.0)), vec3(1.0 / max(Gamma, 0.0001)));
}

vec3 ClipTM(vec3 c)
{
    return clamp(c * Exposure, 0.0, 1.0);
}

vec3 ReinhardTM(vec3 c)
{
    vec3 x = c * Exposure;
    return x / (x + vec3(1.0));
}

vec3 HableTM(vec3 c)
{
    const float A = 0.15;
    const float B = 0.50;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.02;
    const float F = 0.30;
    vec3 x = max(c * Exposure - E, vec3(0.0));
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

vec3 MobiusTM(vec3 c)
{
    float a = 0.6;
    vec3 x = c * Exposure;
    return (x * (a + 1.0)) / (x + a);
}

vec3 ACESTM(vec3 c)
{
    vec3 x = c * Exposure;
    return (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14);
}

vec3 NeutralTM(vec3 c)
{
    vec3 x = c * Exposure;
    return (x * (x + 0.0245786)) / (x * (0.983729 * x + 0.432951) + 0.238081);
}

vec3 FilmicTM(vec3 c)
{
    return NeutralTM(c);
}

vec3 ApplyTonemap(vec3 c)
{
    switch (TonemapType)
    {
        case 0:
            return LinearTM(c);
        case 1:
            return GammaTM(c);
        case 2:
            return ClipTM(c);
        case 3:
            return ReinhardTM(c);
        case 4:
            return HableTM(c);
        case 5:
            return MobiusTM(c);
        case 6:
            return ACESTM(c);
        case 7:
            return NeutralTM(c);
        case 8:
            return FilmicTM(c);
        default:
            return ReinhardTM(c);
    }
}

void main()
{
    vec2 uv = FragPos.xy;
    vec4 src = texture(SourceTexture, uv);
    vec3 color = src.rgb;

    if (UseBloom)
        color += texture(BloomTexture, uv).rgb * BloomStrength;

    OutColor = vec4(ApplyTonemap(color), src.a);
}
""";

        private XRMaterial? _material;
        private XRQuadFrameBuffer? _quad;

        public string SourceTextureName { get; set; } = string.Empty;
        public string? BloomTextureName { get; set; }
        public string? DestinationFBOName { get; set; }
        public ETonemappingType TonemapType { get; set; } = ETonemappingType.Reinhard;
        public float Exposure { get; set; } = 1.0f;
        public float Gamma { get; set; } = 2.2f;
        public float BloomStrength { get; set; } = 0.0f;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(SourceTextureName)
                ? nameof(VPRC_Tonemap)
                : $"{nameof(VPRC_Tonemap)}:{SourceTextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_quad is not null)
                return;

            _material = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, TonemapShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };

            _quad = new XRQuadFrameBuffer(_material);
            _quad.SettingUniforms += Tonemap_SettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_quad is not null)
            {
                _quad.SettingUniforms -= Tonemap_SettingUniforms;
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

            var builder = context.GetOrCreateSyntheticPass($"Tonemap_{SourceTextureName}_to_{destination}");
            builder.WithStage(ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(SourceTextureName));
            if (!string.IsNullOrWhiteSpace(BloomTextureName))
                builder.SampleTexture(MakeTextureResource(BloomTextureName!));
            builder.UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
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

        private void Tonemap_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
                return;

            XRTexture? bloomTexture = null;
            bool useBloom = !string.IsNullOrWhiteSpace(BloomTextureName) &&
                instance.TryGetTexture(BloomTextureName!, out bloomTexture) &&
                bloomTexture is not null;

            program.Sampler("SourceTexture", sourceTexture, 0);
            if (useBloom)
                program.Sampler("BloomTexture", bloomTexture!, 1);

            program.Uniform("UseBloom", useBloom);
            program.Uniform("BloomStrength", BloomStrength);
            program.Uniform("Exposure", Exposure);
            program.Uniform("Gamma", Gamma);
            program.Uniform("TonemapType", (int)TonemapType);
        }
    }
}