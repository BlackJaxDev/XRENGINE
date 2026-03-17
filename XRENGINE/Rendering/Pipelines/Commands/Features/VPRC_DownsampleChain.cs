using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public sealed class VPRC_DownsampleChain : ViewportRenderCommand
    {
        private const string CopyShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;

void main()
{
    OutColor = texture(SourceTexture, FragPos.xy);
}
""";

        private const string DownsampleShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform int SourceLOD;
uniform vec2 TexelSize;
uniform bool UseThreshold;
uniform float Threshold;
uniform float SoftKnee;

vec3 ApplyThresholdCurve(vec3 color)
{
    float brightness = max(max(color.r, color.g), color.b);
    float knee = max(Threshold * SoftKnee, 1e-5);
    float soft = clamp((brightness - Threshold + knee) / (2.0 * knee), 0.0, 1.0);
    float contribution = max(brightness - Threshold, 0.0) + soft * soft * knee;
    if (brightness <= 1e-5)
        return vec3(0.0);
    return color * (contribution / brightness);
}

void main()
{
    vec2 uv = FragPos.xy;
    vec2 texel = TexelSize;

    vec4 accum = vec4(0.0);
    accum += textureLod(SourceTexture, uv + texel * vec2(-0.5, -0.5), SourceLOD);
    accum += textureLod(SourceTexture, uv + texel * vec2( 0.5, -0.5), SourceLOD);
    accum += textureLod(SourceTexture, uv + texel * vec2(-0.5,  0.5), SourceLOD);
    accum += textureLod(SourceTexture, uv + texel * vec2( 0.5,  0.5), SourceLOD);
    accum *= 0.25;

    vec3 color = accum.rgb;
    if (UseThreshold)
        color = ApplyThresholdCurve(color);

    OutColor = vec4(color, accum.a);
}
""";

        private XRTexture2D? _outputTexture;
        private XRMaterial? _copyMaterial;
        private XRMaterial? _downsampleMaterial;
        private XRQuadFrameBuffer? _copyQuad;
        private readonly List<XRQuadFrameBuffer> _downsampleQuads = [];
        private uint _cachedWidth;
        private uint _cachedHeight;
        private int _cachedMaxMip = -1;

        public string InputTextureName { get; set; } = string.Empty;
        public string OutputTextureName { get; set; } = "DownsampleChainTexture";
        public int MaxMipLevel { get; set; } = 4;
        public bool UseThreshold { get; set; }
        public float Threshold { get; set; } = 1.0f;
        public float SoftKnee { get; set; } = 0.5f;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(InputTextureName)
                ? nameof(VPRC_DownsampleChain)
                : $"{nameof(VPRC_DownsampleChain)}:{InputTextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            _copyMaterial ??= new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, CopyShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };
            _downsampleMaterial ??= new(
                [
                    new ShaderInt(0, "SourceLOD"),
                    new ShaderVector2(Vector2.One, "TexelSize"),
                    new ShaderBool(false, "UseThreshold"),
                    new ShaderFloat(1.0f, "Threshold"),
                    new ShaderFloat(0.5f, "SoftKnee"),
                ],
                Array.Empty<XRTexture?>(),
                new XRShader(EShaderType.Fragment, DownsampleShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };

            _copyQuad ??= new XRQuadFrameBuffer(_copyMaterial);
            _copyQuad.SettingUniforms -= Copy_SettingUniforms;
            _copyQuad.SettingUniforms += Copy_SettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            RemoveRegisteredOutput(instance);
            DestroyQuads();

            if (_copyQuad is not null)
            {
                _copyQuad.SettingUniforms -= Copy_SettingUniforms;
                _copyQuad.Destroy();
                _copyQuad = null;
            }

            _copyMaterial?.Destroy();
            _copyMaterial = null;
            _downsampleMaterial?.Destroy();
            _downsampleMaterial = null;
            _cachedWidth = 0;
            _cachedHeight = 0;
            _cachedMaxMip = -1;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            if (_copyQuad is null || _downsampleMaterial is null || string.IsNullOrWhiteSpace(InputTextureName))
                return;

            if (!instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) || sourceTexture is not XRTexture2D source2D)
                return;

            EnsureResources(instance, source2D);
            if (_outputTexture is null || _copyQuad is null)
                return;

            using (instance.RenderState.PushRenderArea((int)Math.Max(1u, source2D.Width), (int)Math.Max(1u, source2D.Height)))
            using (_copyQuad.BindForWritingState())
                _copyQuad.Render();

            for (int mip = 1; mip <= _cachedMaxMip; ++mip)
            {
                XRQuadFrameBuffer quad = _downsampleQuads[mip - 1];
                uint sourceWidth = Math.Max(1u, source2D.Width >> (mip - 1));
                uint sourceHeight = Math.Max(1u, source2D.Height >> (mip - 1));
                uint targetWidth = Math.Max(1u, source2D.Width >> mip);
                uint targetHeight = Math.Max(1u, source2D.Height >> mip);

                _downsampleMaterial.SetInt("SourceLOD", mip - 1);
                _downsampleMaterial.SetVector2("TexelSize", new Vector2(1.0f / sourceWidth, 1.0f / sourceHeight));
                _downsampleMaterial.Parameter<ShaderBool>("UseThreshold")!.Value = UseThreshold && mip == 1;
                _downsampleMaterial.SetFloat("Threshold", Threshold);
                _downsampleMaterial.SetFloat("SoftKnee", SoftKnee);

                using (instance.RenderState.PushRenderArea((int)targetWidth, (int)targetHeight))
                using (quad.BindForWritingState())
                    quad.Render();
            }
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(InputTextureName) || string.IsNullOrWhiteSpace(OutputTextureName))
                return;

            context.GetOrCreateSyntheticPass($"DownsampleChain_{InputTextureName}_to_{OutputTextureName}")
                .WithStage(ERenderGraphPassStage.Graphics)
                .SampleTexture(MakeTextureResource(InputTextureName))
                .ReadWriteTexture(MakeTextureResource(OutputTextureName));
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
            int maxMip = ResolveMaxMip(width, height);

            if (_outputTexture is not null &&
                _cachedWidth == width &&
                _cachedHeight == height &&
                _cachedMaxMip == maxMip &&
                _downsampleQuads.Count == maxMip)
            {
                return;
            }

            RemoveRegisteredOutput(instance);
            DestroyQuads();

            (EPixelInternalFormat internalFormat, ESizedInternalFormat sizedInternalFormat, EPixelFormat pixelFormat, EPixelType pixelType) = ResolveFormat(sourceTexture);

            _outputTexture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat, pixelFormat, pixelType);
            _outputTexture.Name = OutputTextureName;
            _outputTexture.SamplerName = OutputTextureName;
            _outputTexture.SizedInternalFormat = sizedInternalFormat;
            _outputTexture.Resizable = false;
            _outputTexture.MagFilter = ETexMagFilter.Linear;
            _outputTexture.MinFilter = ETexMinFilter.LinearMipmapLinear;
            _outputTexture.LargestMipmapLevel = 0;
            _outputTexture.SmallestAllowedMipmapLevel = maxMip;
            instance.SetTexture(_outputTexture);

            if (_copyQuad is not null)
                _copyQuad.SetRenderTargets((_outputTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));

            for (int mip = 1; mip <= maxMip; ++mip)
            {
                XRQuadFrameBuffer quad = new(_downsampleMaterial!);
                quad.SettingUniforms += Downsample_SettingUniforms;
                quad.SetRenderTargets((_outputTexture, EFrameBufferAttachment.ColorAttachment0, mip, -1));
                _downsampleQuads.Add(quad);
            }

            _cachedWidth = width;
            _cachedHeight = height;
            _cachedMaxMip = maxMip;
        }

        private int ResolveMaxMip(uint width, uint height)
        {
            int smallest = XRTexture.GetSmallestMipmapLevel(width, height);
            if (MaxMipLevel <= 0)
                return Math.Max(1, smallest);
            return Math.Max(1, Math.Min(MaxMipLevel, smallest));
        }

        private void RemoveRegisteredOutput(XRRenderPipelineInstance instance)
        {
            if (_outputTexture is not null)
            {
                instance.Resources.RemoveTexture(OutputTextureName);
                _outputTexture = null;
            }
        }

        private void DestroyQuads()
        {
            foreach (XRQuadFrameBuffer quad in _downsampleQuads)
            {
                quad.SettingUniforms -= Downsample_SettingUniforms;
                quad.Destroy();
            }

            _downsampleQuads.Clear();
        }

        private static (EPixelInternalFormat, ESizedInternalFormat, EPixelFormat, EPixelType) ResolveFormat(XRTexture2D sourceTexture)
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

        private void Copy_SettingUniforms(XRRenderProgram program)
        {
            if (ActivePipelineInstance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) && sourceTexture is not null)
                program.Sampler("SourceTexture", sourceTexture, 0);
        }

        private void Downsample_SettingUniforms(XRRenderProgram program)
        {
            if (_outputTexture is not null)
                program.Sampler("SourceTexture", _outputTexture, 0);
        }
    }
}