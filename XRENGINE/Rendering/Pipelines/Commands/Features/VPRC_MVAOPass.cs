using Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Configures the GBuffer and ambient occlusion frame buffers for the multi-view ambient occlusion pass.
    /// </summary>
    public class VPRC_MVAOPass : ViewportRenderCommand
    {
        private const int MaxKernelSize = 128;

        public const int DefaultSamples = 64;
        public const uint DefaultNoiseWidth = 4u;
        public const uint DefaultNoiseHeight = 4u;
        public const float DefaultMinSampleDist = 0.1f;
        public const float DefaultMaxSampleDist = 1.0f;

        private static void Log(string message)
            => Debug.Out(EOutputVerbosity.Normal, false, "[AO][MVAO] {0}", message);

        private string MVAOGenShaderName() =>
            "MVAOGen.fs";

        private string MVAOBlurShaderName() =>
            "MVAOBlur.fs";

        public string NoiseTextureName { get; set; } = "SSAONoiseTexture";
        public string IntensityTextureName { get; set; } = "SSAOIntensityTexture";
        public string GenerationFBOName { get; set; } = "SSAOFBO";
        public string BlurFBOName { get; set; } = "SSAOBlurFBO";
        public string OutputFBOName { get; set; } = "GBufferFBO";

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public IReadOnlyList<string> DependentFboNames { get; set; } = Array.Empty<string>();

        public int Samples { get; set; } = DefaultSamples;
        public uint NoiseWidth { get; set; } = DefaultNoiseWidth;
        public uint NoiseHeight { get; set; } = DefaultNoiseHeight;
        public float MinSampleDistance { get; set; } = DefaultMinSampleDist;
        public float MaxSampleDistance { get; set; } = DefaultMaxSampleDist;
        public bool Stereo { get; set; } = false;

        private Vector2[]? Noise { get; set; }
        private Vector3[]? Kernel { get; set; }
        private int _kernelSize;
        // Per-instance state tracking
        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public XRTexture2D? NoiseTexture;
            public Vector2 NoiseScale;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();

        private InstanceState GetInstanceState(XRRenderPipelineInstance instance)
            => _instanceStates.GetValue(instance, _ => new InstanceState());

        public void SetOptions(int samples, uint noiseWidth, uint noiseHeight, float minSampleDist, float maxSampleDist, bool stereo)
        {
            Samples = samples;
            NoiseWidth = noiseWidth;
            NoiseHeight = noiseHeight;
            MinSampleDistance = minSampleDist;
            MaxSampleDistance = maxSampleDist;
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

        public void SetOutputNames(string noise, string intensity, string generationFbo, string blurFbo, string outputFbo)
        {
            NoiseTextureName = noise;
            IntensityTextureName = intensity;
            GenerationFBOName = generationFbo;
            BlurFBOName = blurFbo;
            OutputFBOName = outputFbo;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var state = GetInstanceState(instance);

            XRTexture? normalTex = instance.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = instance.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = instance.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmseTex = instance.GetTexture<XRTexture>(RMSETextureName);
            XRTexture? depthStencilTex = instance.GetTexture<XRTexture>(DepthStencilTextureName);

            if (normalTex is null)
            {
                Log($"Missing normal texture '{NormalTextureName}', skipping");
                return;
            }

            if (depthViewTex is null)
            {
                Log($"Missing depth view texture '{DepthViewTextureName}', skipping");
                return;
            }

            if (albedoTex is null)
            {
                Log($"Missing albedo texture '{AlbedoTextureName}', skipping");
                return;
            }

            if (rmseTex is null)
            {
                Log($"Missing RMSE texture '{RMSETextureName}', skipping");
                return;
            }

            if (depthStencilTex is null)
            {
                Log($"Missing depth-stencil texture '{DepthStencilTextureName}', skipping");
                return;
            }

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;
            bool sizeChanged = width != state.LastWidth || height != state.LastHeight;

            if (!forceRebuild && !sizeChanged)
            {
                Log($"Skipping MVAO regen; size unchanged at {width}x{height}");
                return;
            }

            Log($"Executing MVAO regen force={forceRebuild} size={width}x{height} last={state.LastWidth}x{state.LastHeight}");

            RegenerateFBOs(
                instance,
                state,
                normalTex,
                depthViewTex,
                albedoTex,
                rmseTex,
                depthStencilTex,
                width,
                height);
        }

        private void RegenerateFBOs(
            XRRenderPipelineInstance instance,
            InstanceState state,
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            state.LastWidth = width;
            state.LastHeight = height;

            Log($"Regenerating resources: size={width}x{height}, stereo={Stereo}");

            GenerateNoiseKernel();

            state.NoiseScale = new Vector2(
                width / (float)NoiseWidth,
                height / (float)NoiseHeight);

            XRTexture aoTexture;
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
                t.Name = IntensityTextureName;
                t.SamplerName = IntensityTextureName;
                t.MinFilter = ETexMinFilter.Nearest;
                t.MagFilter = ETexMagFilter.Nearest;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                aoTexture = t;
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
                t.Name = IntensityTextureName;
                    t.SamplerName = IntensityTextureName;
                t.MinFilter = ETexMinFilter.Nearest;
                t.MagFilter = ETexMagFilter.Nearest;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                aoTexture = t;
            }

            instance.SetTexture(aoTexture);
            InvalidateDependentFbos(instance);

            RenderingParameters renderParams = new()
            {
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

            XRShader mvaoGenShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, MVAOGenShaderName()), EShaderType.Fragment);
            XRShader mvaoBlurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, MVAOBlurShaderName()), EShaderType.Fragment);

            XRTexture[] mvaoGenTextures =
            [
                normalTex,
                GetOrCreateNoiseTexture(instance, state),
                depthViewTex,
            ];

            XRTexture[] mvaoBlurTextures =
            [
                aoTexture,
                depthViewTex,
                normalTex,
            ];

            XRMaterial mvaoGenMat = new(mvaoGenTextures, mvaoGenShader) { RenderOptions = renderParams };
            XRMaterial mvaoBlurMat = new(mvaoBlurTextures, mvaoBlurShader) { RenderOptions = renderParams };

            if (albedoTex is not IFrameBufferAttachement albedoAttach)
                throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");

            if (normalTex is not IFrameBufferAttachement normalAttach)
                throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");

            if (rmseTex is not IFrameBufferAttachement rmseAttach)
                throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");

            if (depthStencilTex is not IFrameBufferAttachement depthStencilAttach)
                throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer mvaoGenFbo = new(mvaoGenMat, true,
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = GenerationFBOName
            };
            mvaoGenFbo.SettingUniforms += MVAOGen_SetUniforms;

            if (aoTexture is not IFrameBufferAttachement aoAttach)
                throw new ArgumentException("Ambient occlusion texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer mvaoBlurFbo = new(mvaoBlurMat, true, (aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = BlurFBOName
            };
            mvaoBlurFbo.SettingUniforms += MVAOBlur_SetUniforms;

            XRFrameBuffer outputFbo = new((aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = OutputFBOName
            };

            instance.SetFBO(mvaoGenFbo);
            instance.SetFBO(mvaoBlurFbo);
            instance.SetFBO(outputFbo);
            Log("Registered AO FBOs (gen/blur/output)");
        }

        private void GenerateNoiseKernel()
        {
            Random random = new();
            _kernelSize = Math.Min(Math.Max(Samples, 1), MaxKernelSize);
            Kernel = new Vector3[MaxKernelSize];
            Noise = new Vector2[NoiseWidth * NoiseHeight];

            for (int i = 0; i < _kernelSize; ++i)
            {
                Vector3 sample = new(
                    (float)random.NextDouble() * 2.0f - 1.0f,
                    (float)random.NextDouble() * 2.0f - 1.0f,
                    (float)random.NextDouble());
                sample = sample.Normalized();

                float scale = i / (float)_kernelSize;
                float magnitude = Interp.Lerp(MinSampleDistance, MaxSampleDistance, scale * scale);
                Kernel[i] = sample * magnitude;
            }

            for (int i = _kernelSize; i < MaxKernelSize; ++i)
                Kernel[i] = Vector3.Zero;

            for (int i = 0; i < Noise!.Length; ++i)
            {
                Vector2 noise = new(
                    (float)random.NextDouble() * 2.0f - 1.0f,
                    (float)random.NextDouble() * 2.0f - 1.0f);
                Noise[i] = Vector2.Normalize(noise);
            }
        }

        private void MVAOGen_SetUniforms(XRRenderProgram program)
        {
            if (Kernel is null || Noise is null)
                return;

            var state = GetInstanceState(ActivePipelineInstance);
            program.Uniform("NoiseScale", state.NoiseScale);
            program.Uniform("Samples", Kernel);
            program.Uniform("KernelSize", _kernelSize);

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            if (camera is null)
                return;

            camera.SetUniforms(program);

            if (Engine.Rendering.State.IsStereoPass)
                ActivePipelineInstance.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);

            camera.SetAmbientOcclusionUniforms(program, AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion);

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToString(), region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), 0.0f);
        }

        private void MVAOBlur_SetUniforms(XRRenderProgram program)
        {
            var stage = ActivePipelineInstance.RenderState.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();
            var settings = stage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true ? backing : null;
            float depthPhi = settings?.DepthPhi is > 0.0f ? settings.DepthPhi : 4.0f;
            float normalPhi = settings?.NormalPhi is > 0.0f ? settings.NormalPhi : 64.0f;
            program.Uniform("DepthPhi", depthPhi);
            program.Uniform("NormalPhi", normalPhi);
        }

        private XRTexture2D GetOrCreateNoiseTexture(XRRenderPipelineInstance instance, InstanceState state)
        {
            if (state.NoiseTexture != null)
            {
                Log($"Reusing noise texture {state.NoiseTexture.Name} {NoiseWidth}x{NoiseHeight}");
                return state.NoiseTexture;
            }

            XRTexture2D noiseTexture = new()
            {
                Name = NoiseTextureName,
                SamplerName = NoiseTextureName,
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
                        Height = NoiseHeight,
                    }
                ]
            };

            instance.SetTexture(noiseTexture);
            noiseTexture.PushData();
            Log($"Created noise texture {noiseTexture.Name} {NoiseWidth}x{NoiseHeight}");
            return state.NoiseTexture = noiseTexture;
        }

        private void InvalidateDependentFbos(XRRenderPipelineInstance instance)
        {
            if (DependentFboNames.Count == 0)
                return;

            foreach (string name in DependentFboNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                instance.Resources.RemoveFrameBuffer(name);
                Log($"Invalidated dependent FBO '{name}'");
            }
        }

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            GetInstanceState(instance).ResourcesDirty = true;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_instanceStates.TryGetValue(instance, out var state))
            {
                state.ResourcesDirty = true;
                state.NoiseTexture?.Destroy();
                state.NoiseTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}
