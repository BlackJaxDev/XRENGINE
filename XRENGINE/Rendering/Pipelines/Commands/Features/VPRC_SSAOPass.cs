﻿using Extensions;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Generates the necessary textures and framebuffers for SSAO in the render pipeline depending on the current render area.
    /// </summary>
    /// <param name="pipeline"></param>
    public class VPRC_SSAOPass : ViewportRenderCommand
    {
        private string SSAOBlurShaderName() => 
            Stereo ? "SSAOBlurStereo.fs" : 
            "SSAOBlur.fs";

        private string SSAOGenShaderName() =>
            Stereo ? "SSAOGenStereo.fs" : 
            "SSAOGen.fs";

        public string SSAONoiseTextureName { get; set; } = "SSAONoiseTexture";
        public string SSAOIntensityTextureName { get; set; } = "SSAOFBOTexture";
        public string SSAOFBOName { get; set; } = "SSAOFBO";
        public string SSAOBlurFBOName { get; set; } = "SSAOBlurFBO";
        public string GBufferFBOFBOName { get; set; } = "GBufferFBO";

        public const int DefaultSamples = 128;
        public const uint DefaultNoiseWidth = 4u, DefaultNoiseHeight = 4u;
        public const float DefaultMinSampleDist = 0.1f, DefaultMaxSampleDist = 1.0f;

        public Vector2[]? Noise { get; private set; }
        public Vector3[]? Kernel { get; private set; }

        public int Samples { get; set; } = DefaultSamples;
        public uint NoiseWidth { get; set; } = DefaultNoiseWidth;
        public uint NoiseHeight { get; set; } = DefaultNoiseHeight;
        public float MinSampleDist { get; set; } = DefaultMinSampleDist;
        public float MaxSampleDist { get; set; } = DefaultMaxSampleDist;
        public bool Stereo { get; set; } = false;

        private XRTexture2D? NoiseTexture { get; set; } = null;

        public void GenerateNoiseKernel()
        {
            Random r = new();

            Kernel = new Vector3[Samples];
            Noise = new Vector2[NoiseWidth * NoiseHeight];

            float scale;
            Vector3 sample;

            for (int i = 0; i < Samples; ++i)
            {
                sample = new Vector3(
                    (float)r.NextDouble() * 2.0f - 1.0f,
                    (float)r.NextDouble() * 2.0f - 1.0f,
                    (float)r.NextDouble() + 0.1f).Normalized();
                scale = i / (float)Samples;
                sample *= Interp.Lerp(MinSampleDist, MaxSampleDist, scale * scale);
                Kernel[i] = sample;
            }

            for (int i = 0; i < Noise.Length; ++i)
                Noise[i] = Vector2.Normalize(new Vector2((float)r.NextDouble(), (float)r.NextDouble()));
        }

        private Vector2 NoiseScale;

        private int _lastWidth = 0;
        private int _lastHeight = 0;

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";

        public void SetOptions(int samples, uint noiseWidth, uint noiseHeight, float minSampleDist, float maxSampleDist, bool stereo)
        {
            Samples = samples;
            NoiseWidth = noiseWidth;
            NoiseHeight = noiseHeight;
            MinSampleDist = minSampleDist;
            MaxSampleDist = maxSampleDist;
            Stereo = stereo;
        }
        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmse, string depthStencil)
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSETextureName = rmse;
            DepthStencilTextureName = depthStencil;
        }
        public void SetOutputNames(string noise, string ssaoIntensityTexture, string ssaoFBO, string ssaoBlurFBO, string gBufferFBO)
        {
            SSAONoiseTextureName = noise;
            SSAOIntensityTextureName = ssaoIntensityTexture;
            SSAOFBOName = ssaoFBO;
            SSAOBlurFBOName = ssaoBlurFBO;
            GBufferFBOFBOName = gBufferFBO;
        }

        protected override void Execute()
        {
            XRTexture? normalTex = Pipeline.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = Pipeline.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = Pipeline.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmseTex = Pipeline.GetTexture<XRTexture>(RMSETextureName);
            XRTexture? depthStencilTex = Pipeline.GetTexture<XRTexture>(DepthStencilTextureName);

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmseTex is null ||
                depthStencilTex is null)
                return;

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            if (width == _lastWidth && 
                height == _lastHeight)
                return;

            RegenerateFBOs(
                normalTex,
                depthViewTex,
                albedoTex,
                rmseTex,
                depthStencilTex,
                width,
                height);
        }

        private void RegenerateFBOs(
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            //Debug.Out($"SSAO: Regenerating FBOs for {width}x{height}");
            _lastWidth = width;
            _lastHeight = height;

            GenerateNoiseKernel();

            NoiseScale = new Vector2(
                (float)width / NoiseWidth,
                (float)height / NoiseHeight);

            XRTexture ssaoTex;
            if (Stereo)
            {
                var t = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                t.Resizable = false;
                t.SizedInternalFormat = ESizedInternalFormat.R16f;
                t.OVRMultiViewParameters = new(0, 2u);
                t.Name = SSAOIntensityTextureName;
                t.MinFilter = ETexMinFilter.Nearest;
                t.MagFilter = ETexMagFilter.Nearest;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                ssaoTex = t;
            }
            else
            {
                var t = XRTexture2D.CreateFrameBufferTexture(
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                //t.Resizable = false;
                //t.SizedInternalFormat = ESizedInternalFormat.R16f;
                t.Name = SSAOIntensityTextureName;
                t.MinFilter = ETexMinFilter.Nearest;
                t.MagFilter = ETexMagFilter.Nearest;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                ssaoTex = t;
            }

            Pipeline.SetTexture(ssaoTex);

            RenderingParameters renderParams = new()
            {
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

            var ssaoGenShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, SSAOGenShaderName()), EShaderType.Fragment);
            var ssaoBlurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, SSAOBlurShaderName()), EShaderType.Fragment);

            XRTexture[] ssaoGenTexRefs =
            [
                normalTex,
                GetOrCreateNoiseTexture(),
                depthViewTex,
            ];
            XRTexture[] ssaoBlurTexRefs =
            [
                ssaoTex
            ];

            XRMaterial ssaoGenMat = new(ssaoGenTexRefs, ssaoGenShader) { RenderOptions = renderParams };
            XRMaterial ssaoBlurMat = new(ssaoBlurTexRefs, ssaoBlurShader) { RenderOptions = renderParams };

            if (albedoTex is not IFrameBufferAttachement albedoAttach)
                throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");

            if (normalTex is not IFrameBufferAttachement normalAttach)
                throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");

            if (rmseTex is not IFrameBufferAttachement rmseAttach)
                throw new ArgumentException("RMSI texture must be an IFrameBufferAttachement");

            if (depthStencilTex is not IFrameBufferAttachement depthStencilAttach)
                throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer ssaoGenFBO = new(ssaoGenMat, true,
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = SSAOFBOName
            };
            ssaoGenFBO.SettingUniforms += SSAOGen_SetUniforms;

            if (ssaoTex is not IFrameBufferAttachement ssaoAttach)
                throw new ArgumentException("SSAO texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer ssaoBlurFBO = new(ssaoBlurMat, true, (ssaoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = SSAOBlurFBOName
            };

            XRFrameBuffer gbufferFBO = new((ssaoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = GBufferFBOFBOName
            };

            Pipeline.SetFBO(ssaoGenFBO);
            Pipeline.SetFBO(ssaoBlurFBO);
            Pipeline.SetFBO(gbufferFBO);
        }

        private void SSAOGen_SetUniforms(XRRenderProgram program)
        {
            program.Uniform("NoiseScale", NoiseScale);
            program.Uniform("Samples", Kernel!);

            var rc = Pipeline.RenderState.SceneCamera;
            if (rc is null)
                return;
            
            rc.SetUniforms(program);

            if (Engine.Rendering.State.IsStereoPass)
                Pipeline.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);

            rc.SetAmbientOcclusionUniforms(program);

            var region = Pipeline.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToString(), region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), 0.0f);
        }

        private XRTexture2D GetOrCreateNoiseTexture()
        {
            if (NoiseTexture != null)
                return NoiseTexture;

            XRTexture2D noiseTex = new()
            {
                Name = SSAONoiseTextureName,
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.Repeat,
                VWrap = ETexWrapMode.Repeat,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rg16f,
                Mipmaps =
                [
                    new()
                    {
                        Data = DataSource.FromArray(Noise!.SelectMany(v => new float[] { v.X, v.Y }).ToArray()),
                        PixelFormat = EPixelFormat.Rg,
                        PixelType = EPixelType.Float,
                        InternalFormat = EPixelInternalFormat.RG,
                        Width = NoiseWidth,
                        Height = NoiseHeight
                    }
                ]
            };

            Pipeline.SetTexture(noiseTex);
            noiseTex.PushData();
            return NoiseTexture = noiseTex;
        }
    }
}
