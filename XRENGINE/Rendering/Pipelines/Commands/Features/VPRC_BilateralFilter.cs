using System;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Runs a separable bilateral filter over a named input texture with optional depth and normal guidance.
    /// </summary>
    [RenderPipelineScriptCommand]
    public sealed class VPRC_BilateralFilter : ViewportRenderCommand
    {
        private const string BilateralFilterShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform sampler2D DepthTexture;
uniform sampler2D NormalTexture;
uniform vec2 TexelSize;
uniform vec2 Direction;
uniform int KernelRadius;
uniform float SpatialSigma;
uniform float DepthSharpness;
uniform float NormalSharpness;
uniform bool UseDepthGuide;
uniform bool UseNormalGuide;

float Gaussian(float x, float sigma)
{
    float safeSigma = max(sigma, 0.0001);
    return exp(-(x * x) / (2.0 * safeSigma * safeSigma));
}

vec3 DecodeNormal(vec3 encoded)
{
    vec3 normal = encoded * 2.0 - 1.0;
    float lenSq = dot(normal, normal);
    return lenSq > 0.000001 ? normalize(normal) : vec3(0.0, 0.0, 1.0);
}

void main()
{
    vec2 uv = FragPos.xy;
    vec4 centerColor = texture(SourceTexture, uv);
    float centerDepth = texture(DepthTexture, uv).r;
    vec3 centerNormal = DecodeNormal(texture(NormalTexture, uv).xyz);

    vec4 accum = vec4(0.0);
    float totalWeight = 0.0;

    for (int i = -32; i <= 32; ++i)
    {
        if (abs(i) > KernelRadius)
            continue;

        vec2 sampleUv = uv + Direction * TexelSize * float(i);
        vec4 sampleColor = texture(SourceTexture, sampleUv);
        float weight = Gaussian(float(i), SpatialSigma);

        if (UseDepthGuide)
        {
            float sampleDepth = texture(DepthTexture, sampleUv).r;
            weight *= exp(-abs(sampleDepth - centerDepth) * max(DepthSharpness, 0.0));
        }

        if (UseNormalGuide)
        {
            vec3 sampleNormal = DecodeNormal(texture(NormalTexture, sampleUv).xyz);
            float normalWeight = max(dot(centerNormal, sampleNormal), 0.0);
            weight *= pow(normalWeight, max(NormalSharpness, 0.0001));
        }

        accum += sampleColor * weight;
        totalWeight += weight;
    }

    OutColor = totalWeight > 0.0 ? accum / totalWeight : centerColor;
}
""";

        private XRTexture2D? _tempTexture;
        private XRTexture2D? _outputTexture;
        private XRFrameBuffer? _tempFbo;
        private XRFrameBuffer? _outputFbo;
        private XRMaterial? _horizontalMaterial;
        private XRMaterial? _verticalMaterial;
        private XRQuadFrameBuffer? _horizontalQuad;
        private XRQuadFrameBuffer? _verticalQuad;
        private uint _cachedWidth;
        private uint _cachedHeight;

        public string InputTextureName { get; set; } = string.Empty;
        public string OutputTextureName { get; set; } = "BilateralFilterOutputTexture";
        public string? OutputFBOName { get; set; }
        public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
        public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
        public int KernelRadius { get; set; } = 4;
        public float SpatialSigma { get; set; } = 2.0f;
        public float DepthSharpness { get; set; } = 64.0f;
        public float NormalSharpness { get; set; } = 16.0f;
        public bool UseDepthGuide { get; set; } = true;
        public bool UseNormalGuide { get; set; } = true;

        private string ResolvedOutputFboName
            => string.IsNullOrWhiteSpace(OutputFBOName) ? $"{OutputTextureName}FBO" : OutputFBOName!;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(InputTextureName)
                ? nameof(VPRC_BilateralFilter)
                : $"{nameof(VPRC_BilateralFilter)}:{InputTextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_horizontalQuad is not null && _verticalQuad is not null)
                return;

            _horizontalMaterial = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, BilateralFilterShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };
            _verticalMaterial = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, BilateralFilterShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };

            _horizontalQuad = new XRQuadFrameBuffer(_horizontalMaterial);
            _verticalQuad = new XRQuadFrameBuffer(_verticalMaterial);
            _horizontalQuad.SettingUniforms += Horizontal_SettingUniforms;
            _verticalQuad.SettingUniforms += Vertical_SettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            RemoveRegisteredOutput(instance);

            _tempFbo?.Destroy();
            _tempFbo = null;

            _tempTexture?.Destroy();
            _tempTexture = null;

            if (_horizontalQuad is not null)
            {
                _horizontalQuad.SettingUniforms -= Horizontal_SettingUniforms;
                _horizontalQuad.Destroy();
                _horizontalQuad = null;
            }

            if (_verticalQuad is not null)
            {
                _verticalQuad.SettingUniforms -= Vertical_SettingUniforms;
                _verticalQuad.Destroy();
                _verticalQuad = null;
            }

            _horizontalMaterial?.Destroy();
            _horizontalMaterial = null;
            _verticalMaterial?.Destroy();
            _verticalMaterial = null;

            _cachedWidth = 0;
            _cachedHeight = 0;
        }

        protected override void Execute()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (_horizontalQuad is null ||
                _verticalQuad is null ||
                string.IsNullOrWhiteSpace(InputTextureName) ||
                !instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) ||
                sourceTexture is not XRTexture2D source2D)
            {
                return;
            }

            EnsureResources(instance, source2D);
            if (_tempFbo is null || _outputFbo is null)
                return;

            _horizontalQuad.Render(_tempFbo);
            _verticalQuad.Render(_outputFbo);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(InputTextureName) || string.IsNullOrWhiteSpace(OutputTextureName))
                return;

            var pass = context.GetOrCreateSyntheticPass($"BilateralFilter_{InputTextureName}_to_{OutputTextureName}");
            pass.WithStage(ERenderGraphPassStage.Graphics);
            pass.SampleTexture(MakeTextureResource(InputTextureName));
            if (UseDepthGuide && !string.IsNullOrWhiteSpace(DepthTextureName))
                pass.SampleTexture(MakeTextureResource(DepthTextureName));
            if (UseNormalGuide && !string.IsNullOrWhiteSpace(NormalTextureName))
                pass.SampleTexture(MakeTextureResource(NormalTextureName));
            pass.UseColorAttachment(MakeFboColorResource(ResolvedOutputFboName), ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
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
                _tempTexture is not null &&
                _tempFbo is not null &&
                _outputTexture is not null &&
                _outputFbo is not null)
            {
                return;
            }

            RemoveRegisteredOutput(instance);

            _tempFbo?.Destroy();
            _tempTexture?.Destroy();

            (EPixelInternalFormat internalFormat, ESizedInternalFormat sizedInternalFormat, EPixelFormat pixelFormat, EPixelType pixelType) = ResolveFormat(sourceTexture);

            _tempTexture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat, pixelFormat, pixelType);
            _tempTexture.Name = $"{ResolvedOutputFboName}_IntermediateTexture";
            _tempTexture.SamplerName = _tempTexture.Name;
            _tempTexture.SizedInternalFormat = sizedInternalFormat;
            _tempTexture.Resizable = false;
            _tempTexture.MagFilter = ETexMagFilter.Linear;
            _tempTexture.MinFilter = ETexMinFilter.Linear;

            _outputTexture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat, pixelFormat, pixelType);
            _outputTexture.Name = OutputTextureName;
            _outputTexture.SamplerName = OutputTextureName;
            _outputTexture.SizedInternalFormat = sizedInternalFormat;
            _outputTexture.Resizable = false;
            _outputTexture.MagFilter = ETexMagFilter.Linear;
            _outputTexture.MinFilter = ETexMinFilter.Linear;

            _tempFbo = new XRFrameBuffer((_tempTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            _outputFbo = new XRFrameBuffer((_outputTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = ResolvedOutputFboName
            };

            instance.SetTexture(_outputTexture);
            instance.SetFBO(_outputFbo);

            _cachedWidth = width;
            _cachedHeight = height;
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

        private void Horizontal_SettingUniforms(XRRenderProgram program)
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
                return;

            BindGuides(instance, program, sourceTexture);
            program.Sampler("SourceTexture", sourceTexture, 0);
            program.Uniform("TexelSize", new Vector2(
                1.0f / Math.Max(1.0f, sourceTexture.WidthHeightDepth.X),
                1.0f / Math.Max(1.0f, sourceTexture.WidthHeightDepth.Y)));
            SetFilterUniforms(program, Vector2.UnitX);
        }

        private void Vertical_SettingUniforms(XRRenderProgram program)
        {
            if (_tempTexture is null)
                return;

            XRRenderPipelineInstance instance = ActivePipelineInstance;
            BindGuides(instance, program, _tempTexture);
            program.Sampler("SourceTexture", _tempTexture, 0);
            program.Uniform("TexelSize", new Vector2(1.0f / Math.Max(1u, _tempTexture.Width), 1.0f / Math.Max(1u, _tempTexture.Height)));
            SetFilterUniforms(program, Vector2.UnitY);
        }

        private void BindGuides(XRRenderPipelineInstance instance, XRRenderProgram program, XRTexture fallbackTexture)
        {
            XRTexture depthTexture = fallbackTexture;
            XRTexture normalTexture = fallbackTexture;

            if (UseDepthGuide && instance.TryGetTexture(DepthTextureName, out XRTexture? resolvedDepth) && resolvedDepth is not null)
                depthTexture = resolvedDepth;
            if (UseNormalGuide && instance.TryGetTexture(NormalTextureName, out XRTexture? resolvedNormal) && resolvedNormal is not null)
                normalTexture = resolvedNormal;

            program.Sampler("DepthTexture", depthTexture, 1);
            program.Sampler("NormalTexture", normalTexture, 2);
        }

        private void SetFilterUniforms(XRRenderProgram program, Vector2 direction)
        {
            program.Uniform("Direction", direction);
            program.Uniform("KernelRadius", Math.Clamp(KernelRadius, 0, 32));
            program.Uniform("SpatialSigma", Math.Max(0.0001f, SpatialSigma));
            program.Uniform("DepthSharpness", Math.Max(0.0f, DepthSharpness));
            program.Uniform("NormalSharpness", Math.Max(0.0001f, NormalSharpness));
            program.Uniform("UseDepthGuide", UseDepthGuide);
            program.Uniform("UseNormalGuide", UseNormalGuide);
        }
    }
}
