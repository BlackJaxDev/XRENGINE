using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public sealed class VPRC_UpsampleChain : ViewportRenderCommand
    {
        private const string UpsampleShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform int SourceLOD;
uniform vec2 TexelSize;
uniform float Radius;

void main()
{
    vec2 uv = FragPos.xy;
    vec2 r = TexelSize * Radius;

    vec4 sum = vec4(0.0);
    sum += textureLod(SourceTexture, uv + vec2(-r.x, -r.y), SourceLOD);
    sum += textureLod(SourceTexture, uv + vec2( 0.0, -r.y), SourceLOD) * 2.0;
    sum += textureLod(SourceTexture, uv + vec2( r.x, -r.y), SourceLOD);
    sum += textureLod(SourceTexture, uv + vec2(-r.x,  0.0), SourceLOD) * 2.0;
    sum += textureLod(SourceTexture, uv, SourceLOD) * 4.0;
    sum += textureLod(SourceTexture, uv + vec2( r.x,  0.0), SourceLOD) * 2.0;
    sum += textureLod(SourceTexture, uv + vec2(-r.x,  r.y), SourceLOD);
    sum += textureLod(SourceTexture, uv + vec2( 0.0,  r.y), SourceLOD) * 2.0;
    sum += textureLod(SourceTexture, uv + vec2( r.x,  r.y), SourceLOD);
    OutColor = sum / 16.0;
}
""";

        private XRMaterial? _upsampleMaterial;
        private readonly List<XRQuadFrameBuffer> _upsampleQuads = [];
        private uint _cachedWidth;
        private uint _cachedHeight;
        private int _cachedHighestMip = -1;
        private int _cachedLowestMip = -1;
        private bool _cachedAdditiveBlend;

        public string TextureName { get; set; } = string.Empty;
        public int HighestOutputMipLevel { get; set; } = 0;
        public int LowestSourceMipLevel { get; set; } = -1;
        public float Radius { get; set; } = 1.0f;
        public bool AdditiveBlend { get; set; } = true;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(TextureName)
                ? nameof(VPRC_UpsampleChain)
                : $"{nameof(VPRC_UpsampleChain)}:{TextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            _upsampleMaterial ??= new(
                [
                    new ShaderInt(0, "SourceLOD"),
                    new ShaderVector2(Vector2.One, "TexelSize"),
                    new ShaderFloat(1.0f, "Radius"),
                ],
                Array.Empty<XRTexture?>(),
                new XRShader(EShaderType.Fragment, UpsampleShaderCode))
            {
                RenderOptions = CreateRenderOptions(AdditiveBlend)
            };
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            DestroyQuads();
            _upsampleMaterial?.Destroy();
            _upsampleMaterial = null;
            _cachedWidth = 0;
            _cachedHeight = 0;
            _cachedHighestMip = -1;
            _cachedLowestMip = -1;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            if (_upsampleMaterial is null || string.IsNullOrWhiteSpace(TextureName))
                return;

            if (!instance.TryGetTexture(TextureName, out XRTexture? sourceTexture) || sourceTexture is not XRTexture2D texture)
                return;

            int highestMip = Math.Max(texture.LargestMipmapLevel, HighestOutputMipLevel);
            int lowestMip = LowestSourceMipLevel >= 0
                ? Math.Min(texture.SmallestMipmapLevel, LowestSourceMipLevel)
                : texture.SmallestMipmapLevel;

            if (lowestMip <= highestMip)
                return;

            EnsureResources(texture, highestMip, lowestMip);
            if (_upsampleQuads.Count == 0)
                return;

            for (int sourceMip = lowestMip; sourceMip > highestMip; --sourceMip)
            {
                int targetMip = sourceMip - 1;
                XRQuadFrameBuffer quad = _upsampleQuads[lowestMip - sourceMip];
                uint sourceWidth = Math.Max(1u, texture.Width >> sourceMip);
                uint sourceHeight = Math.Max(1u, texture.Height >> sourceMip);
                uint targetWidth = Math.Max(1u, texture.Width >> targetMip);
                uint targetHeight = Math.Max(1u, texture.Height >> targetMip);

                _upsampleMaterial.SetInt("SourceLOD", sourceMip);
                _upsampleMaterial.SetVector2("TexelSize", new Vector2(1.0f / sourceWidth, 1.0f / sourceHeight));
                _upsampleMaterial.SetFloat("Radius", Radius);

                using (instance.RenderState.PushRenderArea((int)targetWidth, (int)targetHeight))
                using (quad.BindForWritingState())
                    quad.Render();
            }
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(TextureName))
                return;

            context.GetOrCreateSyntheticPass($"UpsampleChain_{TextureName}")
                .WithStage(ERenderGraphPassStage.Graphics)
                .ReadWriteTexture(MakeTextureResource(TextureName));
        }

        private void EnsureResources(XRTexture2D texture, int highestMip, int lowestMip)
        {
            uint width = Math.Max(1u, texture.Width);
            uint height = Math.Max(1u, texture.Height);

            if (_cachedWidth == width &&
                _cachedHeight == height &&
                _cachedHighestMip == highestMip &&
                _cachedLowestMip == lowestMip &&
                _cachedAdditiveBlend == AdditiveBlend &&
                _upsampleQuads.Count == (lowestMip - highestMip))
            {
                return;
            }

            DestroyQuads();
            _upsampleMaterial!.RenderOptions = CreateRenderOptions(AdditiveBlend);

            for (int sourceMip = lowestMip; sourceMip > highestMip; --sourceMip)
            {
                XRQuadFrameBuffer quad = new(_upsampleMaterial);
                quad.SettingUniforms += Upsample_SettingUniforms;
                quad.SetRenderTargets((texture, EFrameBufferAttachment.ColorAttachment0, sourceMip - 1, -1));
                _upsampleQuads.Add(quad);
            }

            _cachedWidth = width;
            _cachedHeight = height;
            _cachedHighestMip = highestMip;
            _cachedLowestMip = lowestMip;
            _cachedAdditiveBlend = AdditiveBlend;
        }

        private void DestroyQuads()
        {
            foreach (XRQuadFrameBuffer quad in _upsampleQuads)
            {
                quad.SettingUniforms -= Upsample_SettingUniforms;
                quad.Destroy();
            }

            _upsampleQuads.Clear();
        }

        private static RenderingParameters CreateRenderOptions(bool additiveBlend)
            => additiveBlend
                ? new RenderingParameters
                {
                    DepthTest =
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        UpdateDepth = false,
                        Function = EComparison.Always,
                    },
                    BlendModeAllDrawBuffers = new BlendMode
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        RgbSrcFactor = EBlendingFactor.One,
                        RgbDstFactor = EBlendingFactor.One,
                        AlphaSrcFactor = EBlendingFactor.One,
                        AlphaDstFactor = EBlendingFactor.One,
                        RgbEquation = EBlendEquationMode.FuncAdd,
                        AlphaEquation = EBlendEquationMode.FuncAdd,
                    }
                }
                : new RenderingParameters
                {
                    DepthTest =
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        UpdateDepth = false,
                        Function = EComparison.Always,
                    }
                };

        private void Upsample_SettingUniforms(XRRenderProgram program)
        {
            if (ActivePipelineInstance.TryGetTexture(TextureName, out XRTexture? texture) && texture is not null)
                program.Sampler("SourceTexture", texture, 0);
        }
    }
}
