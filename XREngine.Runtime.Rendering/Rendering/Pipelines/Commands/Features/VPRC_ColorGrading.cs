using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Applies standalone color grading using either authored settings or the active camera's color grading stage.
    /// </summary>
    [RenderPipelineScriptCommand]
    public sealed class VPRC_ColorGrading : ViewportRenderCommand
    {
        private const string ColorGradingShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform sampler2D AutoExposureTex;
uniform bool UseGpuAutoExposure;
uniform bool OutputHDR;

struct ColorGradeStruct
{
    vec3 Tint;

    float Exposure;
    float Contrast;
    float Gamma;

    float Hue;
    float Saturation;
    float Brightness;
};
uniform ColorGradeStruct ColorGrade;

float GetExposure()
{
    if (UseGpuAutoExposure)
    {
        float e = texelFetch(AutoExposureTex, ivec2(0, 0), 0).r;
        if (!(isnan(e) || isinf(e)) && e > 0.0)
            return e;
    }

    return ColorGrade.Exposure;
}

vec3 RGBtoHSV(vec3 c)
{
    vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
    vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 HSVtoRGB(vec3 c)
{
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

vec3 ApplyHsvColorGrade(vec3 sceneColor)
{
    if (ColorGrade.Hue == 1.0 && ColorGrade.Saturation == 1.0 && ColorGrade.Brightness == 1.0)
        return sceneColor;

    vec3 hsv = RGBtoHSV(max(sceneColor, vec3(0.0)));
    hsv.x = fract(hsv.x * ColorGrade.Hue);
    hsv.y = clamp(hsv.y * ColorGrade.Saturation, 0.0, 1.0);
    hsv.z = max(hsv.z * ColorGrade.Brightness, 0.0);
    return HSVtoRGB(hsv);
}

void main()
{
    vec2 uv = FragPos.xy;
    vec4 source = texture(SourceTexture, uv);
    vec3 sceneColor = source.rgb * GetExposure();

    sceneColor *= ColorGrade.Tint;

    sceneColor = ApplyHsvColorGrade(sceneColor);

    sceneColor = (sceneColor - 0.5) * ColorGrade.Contrast + 0.5;

    if (!OutputHDR)
        sceneColor = pow(max(sceneColor, vec3(0.0)), vec3(1.0 / max(ColorGrade.Gamma, 0.0001)));

    OutColor = vec4(sceneColor, source.a);
}
""";

        private XRTexture2D? _outputTexture;
        private XRFrameBuffer? _outputFbo;
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _quad;
        private uint _cachedWidth;
        private uint _cachedHeight;

        public string InputTextureName { get; set; } = string.Empty;
        public string OutputTextureName { get; set; } = "ColorGradingOutputTexture";
        public string? OutputFBOName { get; set; }
        public string AutoExposureTextureName { get; set; } = DefaultRenderPipeline.AutoExposureTextureName;
        public bool OutputHDR { get; set; }
        public bool UseSceneCameraSettings { get; set; } = true;
        public ColorGradingSettings Settings { get; set; } = new();

        private string ResolvedOutputFboName
            => string.IsNullOrWhiteSpace(OutputFBOName) ? $"{OutputTextureName}FBO" : OutputFBOName!;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(InputTextureName)
                ? nameof(VPRC_ColorGrading)
                : $"{nameof(VPRC_ColorGrading)}:{InputTextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_quad is not null)
                return;

            _material = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, ColorGradingShaderCode))
            {
                RenderOptions = CreateRenderOptions()
            };

            _quad = new XRQuadFrameBuffer(_material);
            _quad.SettingUniforms += Quad_SettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            RemoveRegisteredOutput(instance);

            if (_quad is not null)
            {
                _quad.SettingUniforms -= Quad_SettingUniforms;
                _quad.Destroy();
                _quad = null;
            }

            _material?.Destroy();
            _material = null;

            _cachedWidth = 0;
            _cachedHeight = 0;
        }

        protected override void Execute()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (_quad is null ||
                string.IsNullOrWhiteSpace(InputTextureName) ||
                !instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) ||
                sourceTexture is not XRTexture2D source2D)
            {
                return;
            }

            EnsureResources(instance, source2D);
            if (_outputFbo is null)
                return;

            _quad.Render(_outputFbo);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(InputTextureName) || string.IsNullOrWhiteSpace(OutputTextureName))
                return;

            var pass = context.GetOrCreateSyntheticPass($"ColorGrading_{InputTextureName}_to_{OutputTextureName}");
            pass.WithStage(ERenderGraphPassStage.Graphics);
            pass.SampleTexture(MakeTextureResource(InputTextureName));
            if (!string.IsNullOrWhiteSpace(AutoExposureTextureName))
                pass.SampleTexture(MakeTextureResource(AutoExposureTextureName));
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

        private ColorGradingSettings ResolveSettings()
        {
            if (!UseSceneCameraSettings)
                return Settings;

            var stage = ActivePipelineInstance.RenderState.SceneCamera?.GetPostProcessStageState<ColorGradingSettings>();
            if (stage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
                return grading;

            return Settings;
        }

        private void Quad_SettingUniforms(XRRenderProgram program)
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(InputTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
                return;

            ColorGradingSettings settings = ResolveSettings();
            bool hasExposureTexture = instance.TryGetTexture(AutoExposureTextureName, out XRTexture? exposureTexture) && exposureTexture is not null;
            bool useGpuAutoExposure = settings.UseGpuAutoExposureThisFrame && hasExposureTexture;

            program.Sampler("SourceTexture", sourceTexture, 0);
            program.Sampler("AutoExposureTex", useGpuAutoExposure ? exposureTexture! : sourceTexture, 1);
            settings.SetUniforms(program);
            program.Uniform("UseGpuAutoExposure", useGpuAutoExposure);
            program.Uniform("OutputHDR", OutputHDR || DefaultRenderPipeline.ResolveOutputHDR());
        }
    }
}
