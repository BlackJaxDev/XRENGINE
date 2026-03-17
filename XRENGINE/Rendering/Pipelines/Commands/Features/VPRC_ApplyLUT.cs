using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Applies a standalone LUT texture to a named input texture, supporting either 3D LUTs or 2D strip LUTs.
    /// </summary>
    public sealed class VPRC_ApplyLUT : ViewportRenderCommand
    {
        private const string Lut3DShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform sampler3D LutTexture3D;
uniform float LutStrength;
uniform float InputMin;
uniform float InputMax;

vec3 NormalizeInput(vec3 color)
{
    float range = max(InputMax - InputMin, 0.000001);
    return clamp((color - vec3(InputMin)) / range, 0.0, 1.0);
}

void main()
{
    vec2 uv = FragPos.xy;
    vec4 source = texture(SourceTexture, uv);
    vec3 normalized = NormalizeInput(source.rgb);
    vec3 graded = texture(LutTexture3D, normalized).rgb;
    OutColor = vec4(mix(source.rgb, graded, clamp(LutStrength, 0.0, 1.0)), source.a);
}
""";

        private const string Lut2DShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform sampler2D LutTexture2D;
uniform float LutStrength;
uniform float InputMin;
uniform float InputMax;
uniform bool VerticalStrip;

vec3 NormalizeInput(vec3 color)
{
    float range = max(InputMax - InputMin, 0.000001);
    return clamp((color - vec3(InputMin)) / range, 0.0, 1.0);
}

vec3 Sample2DStripLut(vec3 color)
{
    ivec2 lutSizePx = textureSize(LutTexture2D, 0);
    float lutSize = VerticalStrip ? float(lutSizePx.x) : float(lutSizePx.y);
    lutSize = max(lutSize, 2.0);

    float redIndex = color.r * (lutSize - 1.0);
    float greenIndex = color.g * (lutSize - 1.0);
    float blueIndex = color.b * (lutSize - 1.0);

    float slice0 = floor(blueIndex);
    float slice1 = min(slice0 + 1.0, lutSize - 1.0);
    float sliceBlend = blueIndex - slice0;

    vec2 uv0;
    vec2 uv1;
    if (VerticalStrip)
    {
        float denom = lutSize * lutSize;
        uv0 = vec2((redIndex + 0.5) / lutSize, (slice0 * lutSize + greenIndex + 0.5) / denom);
        uv1 = vec2((redIndex + 0.5) / lutSize, (slice1 * lutSize + greenIndex + 0.5) / denom);
    }
    else
    {
        float denom = lutSize * lutSize;
        uv0 = vec2((slice0 * lutSize + redIndex + 0.5) / denom, (greenIndex + 0.5) / lutSize);
        uv1 = vec2((slice1 * lutSize + redIndex + 0.5) / denom, (greenIndex + 0.5) / lutSize);
    }

    vec3 sample0 = texture(LutTexture2D, uv0).rgb;
    vec3 sample1 = texture(LutTexture2D, uv1).rgb;
    return mix(sample0, sample1, sliceBlend);
}

void main()
{
    vec2 uv = FragPos.xy;
    vec4 source = texture(SourceTexture, uv);
    vec3 normalized = NormalizeInput(source.rgb);
    vec3 graded = Sample2DStripLut(normalized);
    OutColor = vec4(mix(source.rgb, graded, clamp(LutStrength, 0.0, 1.0)), source.a);
}
""";

        private XRTexture2D? _outputTexture;
        private XRFrameBuffer? _outputFbo;
        private XRMaterial? _material3D;
        private XRMaterial? _material2D;
        private XRQuadFrameBuffer? _quad3D;
        private XRQuadFrameBuffer? _quad2D;
        private uint _cachedWidth;
        private uint _cachedHeight;

        public string InputTextureName { get; set; } = string.Empty;
        public string LutTextureName { get; set; } = string.Empty;
        public string OutputTextureName { get; set; } = "ApplyLUTOutputTexture";
        public string? OutputFBOName { get; set; }
        public float Strength { get; set; } = 1.0f;
        public float InputMin { get; set; } = 0.0f;
        public float InputMax { get; set; } = 1.0f;
        public bool Treat2DLutAsVerticalStrip { get; set; }

        private string ResolvedOutputFboName
            => string.IsNullOrWhiteSpace(OutputFBOName) ? $"{OutputTextureName}FBO" : OutputFBOName!;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(InputTextureName)
                ? nameof(VPRC_ApplyLUT)
                : $"{nameof(VPRC_ApplyLUT)}:{InputTextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_quad3D is not null && _quad2D is not null)
                return;

            _material3D = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, Lut3DShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };
            _material2D = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, Lut2DShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };

            _quad3D = new XRQuadFrameBuffer(_material3D);
            _quad2D = new XRQuadFrameBuffer(_material2D);
            _quad3D.SettingUniforms += Quad3D_SettingUniforms;
            _quad2D.SettingUniforms += Quad2D_SettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            RemoveRegisteredOutput(instance);

            if (_quad3D is not null)
            {
                _quad3D.SettingUniforms -= Quad3D_SettingUniforms;
                _quad3D.Destroy();
                _quad3D = null;
            }

            if (_quad2D is not null)
            {
                _quad2D.SettingUniforms -= Quad2D_SettingUniforms;
                _quad2D.Destroy();
                _quad2D = null;
            }

            _material3D?.Destroy();
            _material3D = null;
            _material2D?.Destroy();
            _material2D = null;

            _cachedWidth = 0;
            _cachedHeight = 0;
        }

        protected override void Execute()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (string.IsNullOrWhiteSpace(InputTextureName) ||
                string.IsNullOrWhiteSpace(LutTextureName) ||
                !instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) ||
                sourceTexture is not XRTexture2D source2D ||
                !instance.TryGetTexture(LutTextureName, out XRTexture? lutTexture) ||
                lutTexture is null)
            {
                return;
            }

            EnsureResources(instance, source2D);
            if (_outputFbo is null)
                return;

            if (lutTexture is XRTexture3D)
            {
                _quad3D?.Render(_outputFbo);
                return;
            }

            if (lutTexture is XRTexture2D)
                _quad2D?.Render(_outputFbo);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(InputTextureName) ||
                string.IsNullOrWhiteSpace(LutTextureName) ||
                string.IsNullOrWhiteSpace(OutputTextureName))
            {
                return;
            }

            var pass = context.GetOrCreateSyntheticPass($"ApplyLUT_{InputTextureName}_to_{OutputTextureName}");
            pass.WithStage(ERenderGraphPassStage.Graphics);
            pass.SampleTexture(MakeTextureResource(InputTextureName));
            pass.SampleTexture(MakeTextureResource(LutTextureName));
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
                _outputTexture is not null &&
                _outputFbo is not null)
            {
                return;
            }

            RemoveRegisteredOutput(instance);

            (EPixelInternalFormat internalFormat, ESizedInternalFormat sizedInternalFormat, EPixelFormat pixelFormat, EPixelType pixelType) = ResolveFormat(sourceTexture);

            _outputTexture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat, pixelFormat, pixelType);
            _outputTexture.Name = OutputTextureName;
            _outputTexture.SamplerName = OutputTextureName;
            _outputTexture.SizedInternalFormat = sizedInternalFormat;
            _outputTexture.Resizable = false;
            _outputTexture.MagFilter = ETexMagFilter.Linear;
            _outputTexture.MinFilter = ETexMinFilter.Linear;

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

        private void Quad3D_SettingUniforms(XRRenderProgram program)
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) || sourceTexture is null ||
                !instance.TryGetTexture(LutTextureName, out XRTexture? lutTexture) || lutTexture is not XRTexture3D)
            {
                return;
            }

            program.Sampler("SourceTexture", sourceTexture, 0);
            program.Sampler("LutTexture3D", lutTexture, 1);
            program.Uniform("LutStrength", Math.Clamp(Strength, 0.0f, 1.0f));
            program.Uniform("InputMin", InputMin);
            program.Uniform("InputMax", InputMax);
        }

        private void Quad2D_SettingUniforms(XRRenderProgram program)
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) || sourceTexture is null ||
                !instance.TryGetTexture(LutTextureName, out XRTexture? lutTexture) || lutTexture is not XRTexture2D)
            {
                return;
            }

            program.Sampler("SourceTexture", sourceTexture, 0);
            program.Sampler("LutTexture2D", lutTexture, 1);
            program.Uniform("LutStrength", Math.Clamp(Strength, 0.0f, 1.0f));
            program.Uniform("InputMin", InputMin);
            program.Uniform("InputMax", InputMax);
            program.Uniform("VerticalStrip", Treat2DLutAsVerticalStrip);
        }
    }
}