using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_GTAOPass : ViewportRenderCommand
    {
        private static void Log(string message)
            => Debug.Out(EOutputVerbosity.Normal, false, "[AO][GTAO] {0}", message);

        private string GTAOGenShaderName()
            => Stereo ? "GTAOGenStereo.fs" : "GTAOGen.fs";

        private string GTAOBlurShaderName()
            => Stereo ? "GTAOBlurStereo.fs" : "GTAOBlur.fs";

        public string FinalIntensityTextureName { get; set; } = "AmbientOcclusionTexture";
        public string RawIntensityTextureName { get; set; } = "GTAORawTexture";
        public string IntermediateIntensityTextureName { get; set; } = "GTAOBlurIntermediateTexture";
        public string InputSamplerName { get; set; } = "GTAOInputTexture";

        public string GenerationFBOName { get; set; } = "AmbientOcclusionFBO";
        public string BlurFBOName { get; set; } = "AmbientOcclusionBlurFBO";
        public string BlurIntermediateFBOName { get; set; } = "GTAOBlurIntermediateFBO";
        public string OutputFBOName { get; set; } = "GBufferFBO";

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string TransformIdTextureName { get; set; } = "TransformId";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public IReadOnlyList<string> DependentFboNames { get; set; } = Array.Empty<string>();

        public bool Stereo { get; set; }

        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public XRTexture? RawAoTexture;
            public XRTexture? HorizontalBlurTexture;
            public XRTexture? FinalAoTexture;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();

        private InstanceState GetInstanceState(XRRenderPipelineInstance instance)
            => _instanceStates.GetValue(instance, _ => new InstanceState());

        public void SetOptions(bool stereo)
            => Stereo = stereo;

        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmse, string depthStencil, string transformId = "TransformId")
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSETextureName = rmse;
            DepthStencilTextureName = depthStencil;
            TransformIdTextureName = transformId;
        }

        public void SetOutputNames(
            string finalIntensityTexture,
            string generationFbo,
            string blurFbo,
            string blurIntermediateFbo,
            string outputFbo,
            string rawIntensityTexture,
            string intermediateIntensityTexture)
        {
            FinalIntensityTextureName = finalIntensityTexture;
            GenerationFBOName = generationFbo;
            BlurFBOName = blurFbo;
            BlurIntermediateFBOName = blurIntermediateFbo;
            OutputFBOName = outputFbo;
            RawIntensityTextureName = rawIntensityTexture;
            IntermediateIntensityTextureName = intermediateIntensityTexture;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var state = GetInstanceState(instance);

            XRTexture? normalTex = instance.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = instance.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = instance.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmseTex = instance.GetTexture<XRTexture>(RMSETextureName);
            XRTexture? transformIdTex = instance.GetTexture<XRTexture>(TransformIdTextureName);
            XRTexture? depthStencilTex = instance.GetTexture<XRTexture>(DepthStencilTextureName);

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmseTex is null ||
                transformIdTex is null ||
                depthStencilTex is null)
            {
                Log("Missing required GBuffer textures; skipping GTAO resource refresh.");
                return;
            }

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            if (!forceRebuild)
            {
                XRTexture? registeredAo = instance.GetTexture<XRTexture>(FinalIntensityTextureName);
                forceRebuild = state.RawAoTexture is null
                    || state.HorizontalBlurTexture is null
                    || state.FinalAoTexture is null
                    || registeredAo is null
                    || !ReferenceEquals(state.FinalAoTexture, registeredAo);
            }

            if (!forceRebuild && width == state.LastWidth && height == state.LastHeight)
                return;

            RegenerateFBOs(
                instance,
                state,
                normalTex,
                depthViewTex,
                albedoTex,
                rmseTex,
                transformIdTex,
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
            XRTexture transformIdTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            state.LastWidth = width;
            state.LastHeight = height;

            state.RawAoTexture?.Destroy();
            state.HorizontalBlurTexture?.Destroy();
            state.FinalAoTexture?.Destroy();

            state.RawAoTexture = CreateAoTexture(width, height, RawIntensityTextureName, InputSamplerName);
            state.HorizontalBlurTexture = CreateAoTexture(width, height, IntermediateIntensityTextureName, InputSamplerName);
            state.FinalAoTexture = CreateAoTexture(width, height, FinalIntensityTextureName, FinalIntensityTextureName);

            instance.SetTexture(state.RawAoTexture);
            instance.SetTexture(state.HorizontalBlurTexture);
            instance.SetTexture(state.FinalAoTexture);
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

            XRShader genShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, GTAOGenShaderName()), EShaderType.Fragment);
            XRShader blurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, GTAOBlurShaderName()), EShaderType.Fragment);

            XRMaterial genMaterial = new([normalTex, depthViewTex], genShader) { RenderOptions = renderParams };
            XRMaterial horizontalBlurMaterial = new([state.RawAoTexture, depthViewTex, normalTex], blurShader) { RenderOptions = renderParams };
            XRMaterial verticalBlurMaterial = new([state.HorizontalBlurTexture, depthViewTex, normalTex], blurShader) { RenderOptions = renderParams };

            if (albedoTex is not IFrameBufferAttachement albedoAttach)
                throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");

            if (normalTex is not IFrameBufferAttachement normalAttach)
                throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");

            if (rmseTex is not IFrameBufferAttachement rmseAttach)
                throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");

            if (transformIdTex is not IFrameBufferAttachement transformIdAttach)
                throw new ArgumentException("TransformId texture must be an IFrameBufferAttachement");

            if (depthStencilTex is not IFrameBufferAttachement depthStencilAttach)
                throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");

            if (state.RawAoTexture is not IFrameBufferAttachement rawAoAttach)
                throw new ArgumentException("Raw GTAO texture must be an IFrameBufferAttachement");

            if (state.HorizontalBlurTexture is not IFrameBufferAttachement horizontalAoAttach)
                throw new ArgumentException("Horizontal GTAO blur texture must be an IFrameBufferAttachement");

            if (state.FinalAoTexture is not IFrameBufferAttachement finalAoAttach)
                throw new ArgumentException("Final GTAO texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer genFbo = new(genMaterial, true,
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = GenerationFBOName
            };
            genFbo.SettingUniforms += GTAOGen_SetUniforms;

            XRQuadFrameBuffer horizontalBlurFbo = new(horizontalBlurMaterial, true,
                (rawAoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = BlurFBOName
            };
            horizontalBlurFbo.SettingUniforms += GTAOHorizontalBlur_SetUniforms;

            XRQuadFrameBuffer verticalBlurFbo = new(verticalBlurMaterial, true,
                (horizontalAoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = BlurIntermediateFBOName
            };
            verticalBlurFbo.SettingUniforms += GTAOVerticalBlur_SetUniforms;

            XRFrameBuffer outputFbo = new((finalAoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = OutputFBOName
            };

            instance.SetFBO(genFbo);
            instance.SetFBO(horizontalBlurFbo);
            instance.SetFBO(verticalBlurFbo);
            instance.SetFBO(outputFbo);
        }

        private XRTexture CreateAoTexture(int width, int height, string textureName, string samplerName)
        {
            if (Stereo)
            {
                var texture = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                texture.Resizable = false;
                texture.SizedInternalFormat = ESizedInternalFormat.R16f;
                texture.OVRMultiViewParameters = new(0, 2u);
                texture.Name = textureName;
                texture.SamplerName = samplerName;
                texture.MinFilter = ETexMinFilter.Nearest;
                texture.MagFilter = ETexMagFilter.Nearest;
                texture.UWrap = ETexWrapMode.ClampToEdge;
                texture.VWrap = ETexWrapMode.ClampToEdge;
                return texture;
            }

            var aoTexture = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            aoTexture.Name = textureName;
            aoTexture.SamplerName = samplerName;
            aoTexture.MinFilter = ETexMinFilter.Nearest;
            aoTexture.MagFilter = ETexMagFilter.Nearest;
            aoTexture.UWrap = ETexWrapMode.ClampToEdge;
            aoTexture.VWrap = ETexWrapMode.ClampToEdge;
            return aoTexture;
        }

        private void GTAOGen_SetUniforms(XRRenderProgram program)
        {
            var camera = GetCurrentCamera();
            if (camera is null)
                return;

            camera.SetUniforms(program);

            if (Engine.Rendering.State.IsStereoPass)
                ActivePipelineInstance.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);

            camera.SetAmbientOcclusionUniforms(program, AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion);

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToString(), region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), 0.0f);
        }

        private void GTAOHorizontalBlur_SetUniforms(XRRenderProgram program)
            => SetBlurUniforms(program, new Vector2(1.0f, 0.0f));

        private void GTAOVerticalBlur_SetUniforms(XRRenderProgram program)
            => SetBlurUniforms(program, new Vector2(0.0f, 1.0f));

        private void SetBlurUniforms(XRRenderProgram program, Vector2 direction)
        {
            var camera = GetCurrentCamera();
            if (camera is not null)
                program.Uniform(EEngineUniform.DepthMode.ToString(), (int)camera.DepthMode);

            var settings = GetCurrentSettings();
            program.Uniform("BlurDirection", direction);
            program.Uniform("DenoiseRadius", Math.Clamp(settings?.GTAODenoiseRadius ?? 4, 0, 16));
            program.Uniform("DenoiseSharpness", settings?.GTAODenoiseSharpness is > 0.0f ? settings.GTAODenoiseSharpness : 4.0f);
            program.Uniform("DenoiseEnabled", settings?.GTAODenoiseEnabled ?? true);
            program.Uniform("UseInputNormals", settings?.GTAOUseInputNormals ?? true);
        }

        private AmbientOcclusionSettings? GetCurrentSettings()
        {
            var stage = GetCurrentCamera()?.GetPostProcessStageState<AmbientOcclusionSettings>();
            return stage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true ? backing : null;
        }

        private XRCamera? GetCurrentCamera()
            => ActivePipelineInstance.RenderState.SceneCamera
                ?? ActivePipelineInstance.RenderState.RenderingCamera
                ?? ActivePipelineInstance.LastSceneCamera
                ?? ActivePipelineInstance.LastRenderingCamera;

        private void InvalidateDependentFbos(XRRenderPipelineInstance instance)
        {
            if (DependentFboNames.Count == 0)
                return;

            foreach (string name in DependentFboNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                instance.Resources.RemoveFrameBuffer(name);
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
                state.RawAoTexture?.Destroy();
                state.RawAoTexture = null;
                state.HorizontalBlurTexture?.Destroy();
                state.HorizontalBlurTexture = null;
                state.FinalAoTexture?.Destroy();
                state.FinalAoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}